using System.Text.RegularExpressions;

namespace DshShell;

/// <summary>
/// Append-only diagnostic log with credential masking and size-based rotation.
/// Never throws: logging must not be able to break the shell.
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();

    /// <summary>
    /// Names whose value is a credential regardless of where it appears.
    /// A leading \b keeps a long run of dashes from parsing as a key name, so a
    /// plain "--- token=..." separator line still matches.
    /// </summary>
    private const string SecretKey =
        @"\b(?:access[_-]?token|refresh[_-]?token|id[_-]?token|token|api[_-]?key|apikey|secret|password|credential|authorization)";

    /// <summary>
    /// A value in "key":"value" or "key": "value" JSON form. \\. consumes an
    /// escaped character so a quote inside the value cannot end the match early.
    /// </summary>
    private static readonly Regex JsonSecretPattern = new(
        $@"(""{SecretKey}""\s*:\s*"")[^""\\]*(?:\\.[^""\\]*)*("")",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>An Authorization header value, however it was printed.</summary>
    private static readonly Regex AuthHeaderPattern = new(
        @"\b((?:proxy-)?authorization\s*[:=]\s*)(?:bearer|basic|digest|token)?\s*[^\s,;""']+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// key=value (URL query, form body, argv) and key: value (header style).
    /// </summary>
    private static readonly Regex AssignmentSecretPattern = new(
        $@"({SecretKey}\s*[:=]\s*)[^\s&""';,]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const long MaxBytes = 2 * 1024 * 1024;

    public static string LogPath { get; } = ResolveLogPath();

    /// <summary>
    /// An explicit DSHSHELL_LOG lets a caller (the smoke test) keep its log
    /// completely separate from a production instance's, so neither run has to
    /// truncate or reason about the other's file.
    /// </summary>
    private static string ResolveLogPath()
    {
        try
        {
            var overridden = Environment.GetEnvironmentVariable("DSHSHELL_LOG");
            if (!string.IsNullOrWhiteSpace(overridden)) return overridden;
        }
        catch
        {
            // fall through to the default below
        }
        return Path.Combine(Path.GetTempPath(), "dsh-shell.log");
    }

    public static void Write(string message)
    {
        try
        {
            var safe = MaskSecrets(message);
            lock (Gate)
            {
                RotateIfNeeded();
                File.AppendAllText(LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {safe}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never crash the shell
        }
    }

    /// <summary>
    /// Replaces credential values while keeping enough structure ("which key,
    /// which line") for the log to stay diagnostically useful. Order matters:
    /// JSON first, because its quoted value may contain spaces that the
    /// assignment pattern would stop at.
    /// </summary>
    internal static string MaskSecrets(string message)
    {
        if (string.IsNullOrEmpty(message)) return message;
        try
        {
            var masked = JsonSecretPattern.Replace(message, "$1***$2");
            masked = AuthHeaderPattern.Replace(masked, "$1***");
            return AssignmentSecretPattern.Replace(masked, "$1***");
        }
        catch
        {
            // A masking failure must not cost us the log line entirely.
            return "(unmasked log line withheld: masking failed)";
        }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(LogPath);
            if (!info.Exists || info.Length < MaxBytes) return;
            var old = LogPath + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(LogPath, old);
        }
        catch
        {
            // rotation is best-effort
        }
    }
}
