using System.Diagnostics;

namespace DshShell;

/// <summary>
/// A child process as <see cref="BackendLauncher"/> needs to see it.
///
/// This exists so the startup failure paths can be exercised deterministically
/// in tests: without it, every scenario (npm exits non-zero, engine never
/// prints a URL, engine dies after ready) would have to be provoked by
/// manipulating PATH, the network or real binaries.
///
/// Streams are surfaced as tasks rather than events on purpose - waiting for
/// output, for exit and for a timeout are all awaits, which keeps the
/// production and fake implementations equally simple.
/// </summary>
internal interface IChildProcess : IDisposable
{
    bool HasExited { get; }
    int ExitCode { get; }

    /// <summary>Completes with each stdout/stderr line; faulted if the stream cannot be read.</summary>
    IAsyncEnumerable<string> ReadOutputAsync(CancellationToken ct);
    IAsyncEnumerable<string> ReadErrorAsync(CancellationToken ct);

    /// <summary>Completes when the process exits, carrying its exit code.</summary>
    Task<int> WaitForExitAsync(CancellationToken ct);

    void KillTree();
}

/// <summary>Starts child processes. The default implementation wraps <see cref="Process"/>.</summary>
internal interface IProcessRunner
{
    IChildProcess? Start(ProcessStartInfo startInfo);
}

/// <summary>Starts real child processes with the lifetime the launcher relies on.</summary>
internal sealed class ProcessRunner : IProcessRunner
{
    public static readonly ProcessRunner Instance = new();

    public IChildProcess? Start(ProcessStartInfo startInfo)
    {
        var process = Process.Start(startInfo);
        if (process is null) return null;
        try
        {
            process.EnableRaisingEvents = true;
            return new ChildProcess(process);
        }
        catch
        {
            // Never leak a started process just because wrapping it failed.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
            throw;
        }
    }
}

internal sealed class ChildProcess : IChildProcess
{
    private readonly Process _process;
    private readonly TaskCompletionSource<int> _exit =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ChildProcess(Process process)
    {
        _process = process;
        _process.Exited += (_, _) => _exit.TrySetResult(SafeExitCode());
        // The process may already be gone before we subscribe.
        if (_process.HasExited) _exit.TrySetResult(SafeExitCode());
    }

    public bool HasExited
    {
        get { try { return _process.HasExited; } catch { return true; } }
    }

    public int ExitCode
    {
        get { try { return _process.ExitCode; } catch { return -1; } }
    }

    private int SafeExitCode()
    {
        try { return _process.ExitCode; } catch { return -1; }
    }

    public async IAsyncEnumerable<string> ReadOutputAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await _process.StandardOutput.ReadLineAsync(ct);
            if (line is null) yield break;
            yield return line;
        }
    }

    public async IAsyncEnumerable<string> ReadErrorAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await _process.StandardError.ReadLineAsync(ct);
            if (line is null) yield break;
            yield return line;
        }
    }

    public async Task<int> WaitForExitAsync(CancellationToken ct)
    {
        try
        {
            return await _exit.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Callers distinguish "cancelled" from "exited" by re-checking
            // HasExited, so surface the concrete code without throwing.
            return HasExited ? ExitCode : -1;
        }
    }

    public void KillTree()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort: the process may have exited between the check and the kill.
        }
    }

    public void Dispose() => _process.Dispose();
}
