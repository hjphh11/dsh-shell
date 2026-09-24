using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DshShell;

/// <summary>
/// Ensures a DSH engine is available, launches `dsh web --port 0 --no-open`
/// in the background, parses the tokenized ready-URL from stdout, polls until
/// the HTTP server answers, and reports status / ready / failure via events.
///
/// Engine resolution order:
///   1. dedicated engine dir: %LOCALAPPDATA%\DshShell\engine (installed/updated here)
///   2. `dsh` shim found on PATH (global npm / npx cache bin)
///   3. any @deepseek-ai/dsh inside the npm npx cache
/// If none exist, DSH is auto-installed with npm into the dedicated dir
/// (requires Node.js + network, first run only). Measured cold boot ~5s.
/// </summary>
internal sealed class BackendLauncher : IDisposable
{
    private static readonly TimeSpan DefaultHardTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan DefaultInstallTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DefaultUpdateTimeout = TimeSpan.FromMinutes(5);

    /// <summary>How long to wait for the engine to print its URL and answer HTTP.</summary>
    private readonly TimeSpan _hardTimeout;
    private readonly TimeSpan _installTimeout;
    private readonly TimeSpan _updateTimeout;

    /// <summary>At most one silent engine update per this interval, so an
    /// offline/slow registry cannot stall every single launch.</summary>
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(24);    private static readonly Regex ReadyUrlRegex =
        new(@"https?://[^\s""']+token=[^\s""']+", RegexOptions.Compiled);

    /// <summary>
    /// The only hosts this shell will ever navigate WebView2 to. The engine is
    /// bound to loopback (it refuses --host 0.0.0.0), so anything else appearing
    /// on its stdout is not a URL we should follow.
    /// </summary>
    private static readonly string[] LoopbackHosts = ["127.0.0.1", "localhost", "::1", "[::1]"];

    private static string StateDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DshShell");
    private static string UpdateStampPath => Path.Combine(StateDir, "last-update-check");

    /// <summary>
    /// Where the shell owns its engine installation. Shared with the splash so
    /// the path - and the version read from it - are resolved in one place.
    /// </summary>
    internal static string EngineDir => Path.Combine(StateDir, "engine");
    private static string EngineBin => Path.Combine(
        EngineDir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");

    public event Action<string>? StatusChanged;
    public event Action<string>? Ready;
    public event Action<string>? Failed;
    /// <summary>Raised when the engine dies after having been ready.</summary>
    public event Action<string>? EngineCrashed;

    /// <summary>
    /// Install/update progress for the splash. Only raised during the npm
    /// phases; the download phase is the only one with a real counter.
    /// </summary>
    public event Action<EngineProgress>? Progress;

    private IChildProcess? _process;      // the engine (dsh web)
    private IChildProcess? _auxProcess;   // npm install / npm update
    private CancellationTokenSource? _cts;
    private readonly object _gate = new();
    private readonly IProcessRunner _runner;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, string?> _whereFirst;
    private readonly Func<string?> _npxCacheScan;
    private readonly string _engineDir;
    private readonly string _engineBin;
    private bool _reported;
    private bool _attemptInFlight;
    private bool _shuttingDown;
    private int _generation;

    /// <param name="runner">
    /// How child processes are started. Defaults to the real thing; tests
    /// inject a scripted runner so failure paths are deterministic.
    /// </param>
    /// <param name="hardTimeout">Overrides the engine startup deadline (tests use a short one).</param>
    /// <param name="installTimeout">Overrides the npm install deadline.</param>
    /// <param name="updateTimeout">Overrides the npm update deadline.</param>
    /// <param name="fileExists">Overrides the filesystem probe used to find the engine.</param>
    /// <param name="whereFirst">Overrides the PATH probe used for node/npm/dsh.</param>
    /// <param name="engineDir">Overrides where the engine is installed.</param>
    internal BackendLauncher(
        IProcessRunner? runner = null,
        TimeSpan? hardTimeout = null,
        TimeSpan? installTimeout = null,
        TimeSpan? updateTimeout = null,
        Func<string, bool>? fileExists = null,
        Func<string, string?>? whereFirst = null,
        string? engineDir = null,
        Func<string?>? npxCacheScan = null)
    {
        _runner = runner ?? ProcessRunner.Instance;
        _hardTimeout = hardTimeout ?? DefaultHardTimeout;
        _installTimeout = installTimeout ?? DefaultInstallTimeout;
        _updateTimeout = updateTimeout ?? DefaultUpdateTimeout;
        _fileExists = fileExists ?? File.Exists;
        _whereFirst = whereFirst ?? WhereFirst;
        _npxCacheScan = npxCacheScan ?? ScanNpxCache;
        _engineDir = engineDir ?? EngineDir;
        _engineBin = Path.Combine(_engineDir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
    }

    /// <summary>True while an engine or an npm child process is alive.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _process is { HasExited: false } || _auxProcess is { HasExited: false };
            }
        }
    }

    /// <summary>True while a start/install/update attempt is still in flight.</summary>
    public bool IsBusy
    {
        get { lock (_gate) return _attemptInFlight; }
    }

    public void Start()
    {
        CancellationToken token;
        int gen;
        lock (_gate)
        {
            // Cancel any previous attempt so the old chain cannot keep
            // installing/launching in parallel with this one.
            try { _cts?.Cancel(); } catch { }
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            token = _cts.Token;
            gen = ++_generation;
            _reported = false;
            _attemptInFlight = true;
            _shuttingDown = false;
            _process = null;
        }
        _ = BootAsync(gen, token);
    }

    private async Task BootAsync(int gen, CancellationToken ct)
    {
        try
        {
            var spec = TryResolveInstalled();
            if (spec is null)
            {
                StatusChanged?.Invoke("未找到 DSH 引擎，准备自动安装…");
                spec = await InstallEngineAsync(gen, ct);
                if (spec is null) return; // failure already reported
            }
            else if (spec.Source == "engine")
            {
                // Only engines we own get silently updated; PATH/npx-cache
                // installs belong to the user and are left untouched.
                await SilentUpdateAsync(gen, ct);
            }
            if (ct.IsCancellationRequested) return;
            spec = ResolveNodeFor(spec, gen);
            if (spec is null) return; // failure already reported
            await LaunchAndMonitorAsync(gen, spec, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shell is closing or a newer attempt replaced this one
        }
        catch (Exception ex)
        {
            Log.Write($"BootAsync unexpected error: {ex}");
            ReportFailed(gen, $"启动过程出现意外错误：{ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                if (gen == _generation) _attemptInFlight = false;
            }
        }
    }

    // ---------------------------------------------------------------- resolve

    private sealed record LaunchSpec(string FileName, string Arguments, string Source);

    private LaunchSpec? TryResolveInstalled()
    {
        // 1. dedicated engine dir (installed/updated by this shell - preferred)
        if (_fileExists(_engineBin))
        {
            Log.Write($"Engine via dedicated dir: {_engineBin}");
            return new LaunchSpec("node.exe", $"\"{_engineBin}\" web --port 0 --no-open", "engine");
        }

        // 2. dsh shim on PATH (global npm install / npx cache bin dir)
        var where = _whereFirst("dsh");
        if (where is not null && _fileExists(where))
        {
            Log.Write($"Engine via PATH: {where}");
            return new LaunchSpec("cmd.exe", $"/c \"\"{where}\" web --port 0 --no-open\"", "PATH");
        }

        // 3. any @deepseek-ai/dsh inside the npx cache (files, not directories!)
        try
        {
            var bin = _npxCacheScan();
            if (bin is not null)
            {
                Log.Write($"Engine via npx cache: {bin}");
                return new LaunchSpec("node.exe", $"\"{bin}\" web --port 0 --no-open", "npx-cache");
            }
        }
        catch (Exception ex)
        {
            Log.Write($"npx cache scan failed: {ex.Message}");
        }

        Log.Write("No installed DSH engine found.");
        return null;
    }

    /// <summary>
    /// Looks for an engine inside the npm npx cache. Injectable so tests can
    /// close this route too: on a developer machine the cache often holds a
    /// usable engine, which would otherwise mask the install path under test.
    /// </summary>
    private static string? ScanNpxCache()
    {
        var npxCache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "npm-cache", "_npx");
        if (!Directory.Exists(npxCache)) return null;

        var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true };
        return Directory.GetFiles(npxCache, "bin.js", opts)
            .FirstOrDefault(p =>
                p.Contains($"{Path.DirectorySeparatorChar}@deepseek-ai{Path.DirectorySeparatorChar}dsh{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}bin.js", StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(p))!, "package.json")));
    }

    /// <summary>
    /// Engines are Node programs: resolve node.exe explicitly so a machine
    /// without Node.js gets a clear "install Node.js" message instead of a raw
    /// "system cannot find the file specified" error.
    /// </summary>
    private LaunchSpec? ResolveNodeFor(LaunchSpec spec, int gen)
    {
        if (!string.Equals(spec.FileName, "node.exe", StringComparison.OrdinalIgnoreCase))
        {
            return spec; // cmd.exe shim path (dsh on PATH) needs no node lookup
        }

        var node = _whereFirst("node");
        if (node is null || !_fileExists(node))
        {
            Log.Write("node.exe not found on PATH - engine cannot start.");
            ReportFailed(gen,
                "未检测到 Node.js。DSH 引擎是 Node 程序，必须先安装 Node.js" +
                "（https://nodejs.org，建议 LTS 版）并重新打开本软件。");
            return null;
        }

        Log.Write($"Using node: {node}");
        return spec with { FileName = node };
    }

    private static string? WhereFirst(string command)
    {
        try
        {
            var psi = new ProcessStartInfo("where.exe", command)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var firstLine = p.StandardOutput.ReadLine();
            p.WaitForExit(3000);
            return string.IsNullOrWhiteSpace(firstLine) ? null : firstLine.Trim();
        }
        catch
        {
            return null;
        }
    }

    // ----------------------------------------------------------------- update

    /// <summary>
    /// Serialises engine installs/updates across processes. The single-instance
    /// guard is per Windows session (Local\), so two sessions can decide the
    /// update is due at the same moment and run npm against the same
    /// %LOCALAPPDATA%\DshShell\engine tree. Concurrent npm writes there can
    /// leave a half-installed engine that every later launch inherits.
    /// </summary>
    private const string UpdateLockName = @"Local\DshShell.EngineUpdate";
    private static readonly TimeSpan UpdateLockTimeout = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Runs <paramref name="work"/> while holding the cross-process update lock.
    /// Returns false without running it when another process holds the lock, or
    /// when the lock times out - the caller then boots the engine as it stands.
    /// </summary>
    /// <remarks>
    /// The mutex is owned by a dedicated thread rather than the caller's:
    /// ReleaseMutex must run on the thread that acquired it, and the await
    /// continuations around this method may resume on a different thread. A
    /// release from the wrong thread throws and would leak the lock forever,
    /// permanently blocking every future engine update.
    /// </remarks>
    private static Task<bool> WithUpdateLockAsync(Func<Task> work)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Mutex? mutex = null;
            var acquired = false;
            try
            {
                try
                {
                    mutex = new Mutex(false, UpdateLockName);
                }
                catch (Exception ex)
                {
                    // No lock is available (unusual ACL/session state). Refusing
                    // to install at all is worse than proceeding unserialised.
                    Log.Write($"Engine update lock unavailable ({ex.GetType().Name}: {ex.Message}) - proceeding without it.");
                    work().GetAwaiter().GetResult();
                    result.TrySetResult(true);
                    return;
                }

                try
                {
                    acquired = mutex.WaitOne(UpdateLockTimeout);
                }
                catch (AbandonedMutexException)
                {
                    // A previous holder died mid-install. The lock is now ours,
                    // but the tree may be half-written; npm install repairs it.
                    Log.Write("Engine update lock was abandoned by a previous holder - taking it over.");
                    acquired = true;
                }

                if (!acquired)
                {
                    Log.Write("Another session is updating the engine - skipping this check.");
                    result.TrySetResult(false);
                    return;
                }

                work().GetAwaiter().GetResult();
                result.TrySetResult(true);
            }
            catch (Exception ex)
            {
                Log.Write($"Engine update lock failed ({ex.GetType().Name}: {ex.Message}) - skipping this check.");
                result.TrySetResult(false);
            }
            finally
            {
                if (acquired)
                {
                    try { mutex!.ReleaseMutex(); } catch { }
                }
                mutex?.Dispose();
            }
        })
        { IsBackground = true, Name = "DshShell.EngineUpdateLock" };
        thread.Start();
        return result.Task;
    }

    /// <summary>
    /// Silently refreshes the owned engine to @latest before launch.
    /// Best-effort: any failure/timeout just logs and boots the current version.
    /// Runs before the engine starts, so no files are locked.
    /// </summary>
    private Task SilentUpdateAsync(int gen, CancellationToken ct) =>
        WithUpdateLockAsync(() => SilentUpdateCoreAsync(gen, ct));

    private async Task SilentUpdateCoreAsync(int gen, CancellationToken ct)
    {
        IChildProcess? npmProcess = null;
        try
        {
            var npm = _whereFirst("npm");
            if (npm is null)
            {
                Log.Write("Silent update skipped: npm not found.");
                return;
            }

            if (!IsUpdateDue())
            {
                Log.Write($"Silent update skipped: checked within the last {UpdateInterval.TotalHours:0}h.");
                return;
            }

            StatusChanged?.Invoke("正在检查引擎更新…");
            Log.Write("Silent update: npm install @deepseek-ai/dsh@latest");

            var psi = new ProcessStartInfo("cmd.exe",
                $"/c \"\"{npm}\" install --prefix \"{_engineDir}\" --loglevel=http \"@deepseek-ai/dsh@latest\"\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            npmProcess = _runner.Start(psi);
            if (npmProcess is null) return;
            RegisterAux(npmProcess);

            var progress = new NpmProgressTracker(NpmProgressTracker.DefaultExpectedPackages, ReportProgress);
            var stallWatch = WatchStallAsync(progress, ct);

            var npmOut = Task.Run(async () =>
            {
                try
                {
                    await foreach (var line in npmProcess.ReadOutputAsync(ct))
                    {
                        Log.Write($"[npm-update] {line}");
                        progress.Observe(line);
                    }
                }
                catch { /* diagnostics only */ }
            }, CancellationToken.None);
            var npmErr = Task.Run(async () =>
            {
                try
                {
                    await foreach (var line in npmProcess.ReadErrorAsync(ct))
                    {
                        Log.Write($"[npm-update!] {line}");
                        // npm writes its http log to stderr, not stdout, so the
                        // progress lines only appear here.
                        progress.Observe(line);
                    }
                }
                catch { /* diagnostics only */ }
            }, CancellationToken.None);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_updateTimeout);
            var done = await WaitForExitAsync(npmProcess, timeoutCts.Token);
            if (!done)
            {
                Kick(npmProcess);
                if (ct.IsCancellationRequested)
                {
                    Log.Write("Silent update cancelled (shell closing).");
                    return;
                }
                Log.Write($"Silent update timed out ({_updateTimeout.TotalSeconds:0}s) - continuing with current version.");
                return;
            }
            Log.Write($"Silent update finished, npm exit code {npmProcess.ExitCode}.");
            progress.Flush();
            _ = Task.WhenAll(npmOut, npmErr);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shell closing or superseded by a newer attempt
        }
        catch (Exception ex)
        {
            Log.Write($"Silent update error (ignored): {ex.Message}");
        }
        finally
        {
            UnregisterAux(npmProcess);
            RecordUpdateAttempt();
        }
    }

    /// <summary>
    /// Watches a download in progress and, once packages stop arriving while npm
    /// is still running, switches the bar to indeterminate. Without this a
    /// cache-served update would sit at "3 / 584" and look hung.
    /// </summary>
    private async Task WatchStallAsync(NpmProgressTracker progress, CancellationToken ct)
    {
        var announced = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (announced || !progress.IsStalled()) continue;
            announced = true;
            Log.Write($"Download count stalled at {progress.Seen} - switching the bar to indeterminate.");
            progress.ReportApplying();
        }
    }

    /// <summary>
    /// Raises a progress observation. Swallows listener faults: the splash is an
    /// observer, and a WebView2 hiccup must never break the install.
    /// </summary>
    private void ReportProgress(EngineProgress progress)
    {
        try
        {
            Progress?.Invoke(progress);
        }
        catch (Exception ex)
        {
            Log.Write($"Progress listener failed: {ex.Message}");
        }
    }

    private static bool IsUpdateDue()
    {
        try
        {
            if (!File.Exists(UpdateStampPath)) return true;
            var text = File.ReadAllText(UpdateStampPath).Trim();
            return !DateTime.TryParse(text, null,
                       System.Globalization.DateTimeStyles.RoundtripKind, out var last)
                   || DateTime.UtcNow - last.ToUniversalTime() >= UpdateInterval;
        }
        catch
        {
            return true; // unreadable stamp: just try the update
        }
    }

    private static void RecordUpdateAttempt()
    {
        try
        {
            Directory.CreateDirectory(StateDir);
            File.WriteAllText(UpdateStampPath, DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            Log.Write($"Could not record update stamp: {ex.Message}");
        }
    }

    // ----------------------------------------------------------------- install

    /// <returns>A launch spec on success, null after reporting a failure.</returns>
    private async Task<LaunchSpec?> InstallEngineAsync(int gen, CancellationToken ct)
    {
        var npm = _whereFirst("npm");
        if (npm is null)
        {
            ReportFailed(gen, "未检测到 Node.js / npm。请先安装 Node.js（https://nodejs.org），然后点击重试。");
            return null;
        }

        StatusChanged?.Invoke("首次运行：正在下载并安装 DSH 引擎…（需联网，请耐心等待）");
        Log.Write($"Auto-installing @deepseek-ai/dsh into {_engineDir}");

        var psi = new ProcessStartInfo("cmd.exe",
            $"/c \"\"{npm}\" install --prefix \"{_engineDir}\" --loglevel=http \"@deepseek-ai/dsh@latest\"\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // npm reports nothing until it finishes at the default log level, so the
        // download phase is counted from its http lines instead.
        var progress = new NpmProgressTracker(NpmProgressTracker.DefaultExpectedPackages, ReportProgress);
        var stallWatch = WatchStallAsync(progress, ct);
        ReportProgress(new EngineProgress(EnginePhase.Resolving, 0, NpmProgressTracker.DefaultExpectedPackages));

        IChildProcess? install = null;
        try
        {
            install = _runner.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null.");
            RegisterAux(install);

            var npmOut = Task.Run(async () =>
            {
                try
                {
                    await foreach (var line in install.ReadOutputAsync(ct))
                    {
                        Log.Write($"[npm] {line}");
                        progress.Observe(line);
                    }
                }
                catch { /* diagnostics only */ }
            }, CancellationToken.None);
            var npmErr = Task.Run(async () =>
            {
                try
                {
                    await foreach (var line in install.ReadErrorAsync(ct))
                    {
                        Log.Write($"[npm!] {line}");
                        // npm writes its http log to stderr, not stdout.
                        progress.Observe(line);
                    }
                }
                catch { /* diagnostics only */ }
            }, CancellationToken.None);

            // Bounded wait: a stalled network/registry must not hang the splash forever.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_installTimeout);
            var done = await WaitForExitAsync(install, timeoutCts.Token);
            if (!done)
            {
                Kick(install);
                if (ct.IsCancellationRequested) return null; // shell closing, no error UI needed
                ReportFailed(gen, $"DSH 引擎安装超时（超过 {_installTimeout.TotalMinutes:0} 分钟），请检查网络后点击重试。");
                return null;
            }
            if (install.ExitCode != 0)
            {
                ReportFailed(gen, $"DSH 引擎安装失败（npm 退出码 {install.ExitCode}），请检查网络后点击重试。详情见日志：{Log.LogPath}");
                return null;
            }
            // Land the bar on the real count: the last few observations may have
            // been throttled away.
            progress.Flush();
            ReportProgress(new EngineProgress(EnginePhase.Finishing, progress.Seen, NpmProgressTracker.DefaultExpectedPackages));
            _ = Task.WhenAll(npmOut, npmErr);
        }
        catch (Exception ex)
        {
            ReportFailed(gen, $"无法启动 npm 安装进程：{ex.Message}");
            return null;
        }
        finally
        {
            UnregisterAux(install);
        }

        if (!_fileExists(_engineBin))
        {
            ReportFailed(gen, $"安装完成但{EngineMissingHint}，请点击重试或重新安装引擎。详情见日志：" + Log.LogPath);
            return null;
        }

        Log.Write($"Engine installed at {_engineBin}");
        return new LaunchSpec("node.exe", $"\"{_engineBin}\" web --port 0 --no-open", "engine");
    }

    private void RegisterAux(IChildProcess p)
    {
        lock (_gate) { _auxProcess = p; }
    }

    private void UnregisterAux(IChildProcess? p)
    {
        if (p is null) return;
        lock (_gate)
        {
            if (ReferenceEquals(_auxProcess, p)) _auxProcess = null;
        }
    }

    private static void Kick(IChildProcess p)
    {
        try
        {
            p.KillTree();
        }
        catch (Exception ex)
        {
            Log.Write($"Kill child process failed: {ex.Message}");
        }
    }

    /// <returns>True if the process exited within the token's lifetime.</returns>
    private static async Task<bool> WaitForExitAsync(IChildProcess p, CancellationToken ct)
    {
        try
        {
            await p.WaitForExitAsync(ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            return p.HasExited;
        }
    }

    // ------------------------------------------------------------------ launch

    /// <summary>
    /// Validates a URL scraped from the engine's stdout before it is handed to
    /// WebView2. The engine binds loopback and prints its own address, but the
    /// regex is deliberately loose, so anything non-loopback, non-http, or
    /// missing a plausible token is rejected rather than navigated to.
    /// </summary>
    internal static bool TryValidateReadyUrl(string candidate, out string url)
    {
        url = string.Empty;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed))
        {
            Log.Write("Rejected ready URL: not an absolute URI.");
            return false;
        }
        if (parsed.Scheme != Uri.UriSchemeHttp)
        {
            Log.Write($"Rejected ready URL: scheme '{parsed.Scheme}' is not http.");
            return false;
        }
        if (!LoopbackHosts.Contains(parsed.Host, StringComparer.OrdinalIgnoreCase))
        {
            Log.Write($"Rejected ready URL: host '{parsed.Host}' is not loopback.");
            return false;
        }
        if (parsed.Port is <= 0 or > 65535)
        {
            Log.Write($"Rejected ready URL: port {parsed.Port} out of range.");
            return false;
        }

        var token = ReadToken(parsed);
        if (token is null || token.Length == 0)
        {
            Log.Write("Rejected ready URL: no token query parameter.");
            return false;
        }
        // Tokens are opaque, but a sane one is printable and not absurdly long.
        // This keeps control characters or a truncated read out of the URL.
        if (token.Length > 4096 || token.Any(c => char.IsControl(c)))
        {
            Log.Write($"Rejected ready URL: token shape is implausible (length {token.Length}).");
            return false;
        }

        url = parsed.AbsoluteUri;
        return true;
    }

    private static string? ReadToken(Uri uri)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            if (pair.AsSpan(0, eq).Equals("token", StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }
        return null;
    }

    private async Task LaunchAndMonitorAsync(int gen, LaunchSpec spec, CancellationToken ct)
    {
        StatusChanged?.Invoke("正在启动引擎…");
        Log.Write($"Launching backend ({spec.Source}): {spec.FileName} {spec.Arguments}");

        var psi = new ProcessStartInfo
        {
            FileName = spec.FileName,
            Arguments = spec.Arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };

        IChildProcess process;
        try
        {
            process = _runner.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception ex)
        {
            Log.Write($"Failed to start backend process: {ex.Message}");
            ReportFailed(gen, $"无法启动 DSH 进程：{ex.Message}");
            return;
        }

        lock (_gate)
        {
            _process = process;
        }

        var readyUrlTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Drain stdout looking for the tokenized URL. Only the current attempt
        // may complete the handshake: a process replaced by a retry is killed,
        // but its buffered output could still surface here.
        var stdoutPump = Task.Run(async () =>
        {
            try
            {
                await foreach (var line in process.ReadOutputAsync(ct))
                {
                    Log.Write($"[dsh stdout] {line}");
                    var m = ReadyUrlRegex.Match(line);
                    if (m.Success && TryValidateReadyUrl(m.Value, out var ready)
                        && gen == Volatile.Read(ref _generation))
                    {
                        readyUrlTcs.TrySetResult(ready);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // shell closing or a newer attempt took over
            }
            catch (Exception ex)
            {
                Log.Write($"Reading engine stdout failed: {ex.Message}");
            }
        }, CancellationToken.None);

        var stderrPump = Task.Run(async () =>
        {
            try
            {
                await foreach (var line in process.ReadErrorAsync(ct))
                {
                    Log.Write($"[dsh stderr] {line}");
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Write($"Reading engine stderr failed: {ex.Message}");
            }
        }, CancellationToken.None);

        // Exit handling. If the engine dies before it was ever ready, that is a
        // startup failure; after ready it is a crash the shell can offer to
        // recover from.
        var exitWatch = Task.Run(async () =>
        {
            int code;
            try
            {
                code = await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Write($"Waiting for engine exit failed: {ex.Message}");
                code = -1;
            }

            Log.Write($"[dsh] process exited, code={code}");
            if (!readyUrlTcs.Task.IsCompleted)
            {
                ReportFailed(gen, $"DSH 进程意外退出（退出码 {code}）。详情见日志：{Log.LogPath}");
                return;
            }

            bool shuttingDown;
            lock (_gate) shuttingDown = _shuttingDown || gen != _generation;
            if (!shuttingDown)
            {
                Log.Write("Engine exited after ready - notifying shell.");
                EngineCrashed?.Invoke($"DSH 引擎已退出（退出码 {code}）。");
            }
        }, CancellationToken.None);

        try
        {
            await RunBootSequenceAsync(gen, process, readyUrlTcs.Task, ct);
        }
        finally
        {
            // Let the pumps observe cancellation before abandoning them; they
            // only touch the log and their own process handle.
            _ = Task.WhenAll(stdoutPump, stderrPump, exitWatch);
        }
    }

    private async Task RunBootSequenceAsync(int gen, IChildProcess process, Task<string> readyUrlTask, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + _hardTimeout;
        string? url = null;

        // Wait for the stdout line that carries the tokenized URL.
        while (true)
        {
            if (ct.IsCancellationRequested) return;
            var urlTask = await Task.WhenAny(readyUrlTask, Task.Delay(200, ct));
            if (urlTask == readyUrlTask)
            {
                url = readyUrlTask.Result;
                Log.Write($"Parsed ready URL: {url}");
                break;
            }
            if (process.HasExited) return; // Exited handler reports the failure
            if (DateTime.UtcNow > deadline)
            {
                ReportFailed(gen, $"启动超时（超过 {_hardTimeout.TotalSeconds:0} 秒仍未就绪）。");
                return;
            }
        }

        StatusChanged?.Invoke("引擎已启动，等待就绪…");

        // Poll until the HTTP server actually answers (any response counts;
        // with a valid token we expect 200/3xx).
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(3) };

        while (true)
        {
            if (ct.IsCancellationRequested) return;
            if (process.HasExited) return;

            try
            {
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                Log.Write($"Probe {new Uri(url).GetLeftPart(UriPartial.Path)} -> {(int)resp.StatusCode}");
                if ((int)resp.StatusCode < 500)
                {
                    ReportReady(gen, url);
                    return;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // server not accepting yet
            }

            if (DateTime.UtcNow > deadline)
            {
                ReportFailed(gen, $"启动超时（超过 {_hardTimeout.TotalSeconds:0} 秒仍未就绪）。");
                return;
            }
            await Task.Delay(250, ct);
        }
    }

    private void ReportReady(int gen, string url)
    {
        lock (_gate)
        {
            if (_reported || gen != _generation) return;
            _reported = true;
        }
        Log.Write("Backend READY.");
        Ready?.Invoke(url);
    }

    private void ReportFailed(int gen, string message)
    {
        lock (_gate)
        {
            if (_reported || gen != _generation) return;
            _reported = true;
        }
        Log.Write($"Backend FAILED: {message}");
        Failed?.Invoke(message);
    }

    public void Kill()
    {
        lock (_gate)
        {
            _shuttingDown = true;
            _cts?.Cancel();
            KillProcessTreeIfAlive(_process, "Backend process tree killed.");
            KillProcessTreeIfAlive(_auxProcess, "npm child process tree killed.");
            _process = null;
            _auxProcess = null;
        }
    }

    private static void KillProcessTreeIfAlive(IChildProcess? p, string logMessage)
    {
        if (p is not { HasExited: false }) return;
        try
        {
            p.KillTree();
            Log.Write(logMessage);
        }
        catch (Exception ex)
        {
            Log.Write($"Kill failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Failure text for problems that match a missing or broken engine
    /// installation. Shared with <see cref="IsEngineRelatedFailure"/> so the
    /// splash only offers a destructive reinstall where it can actually help.
    /// </summary>
    internal const string EngineMissingHint = "引擎入口缺失";

    /// <summary>
    /// True when reinstalling the engine could plausibly fix the failure, as
    /// opposed to a missing runtime or a network problem.
    /// </summary>
    internal static bool IsEngineRelatedFailure(string? message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        return message.Contains(EngineMissingHint, StringComparison.Ordinal)
            || message.Contains("安装失败", StringComparison.Ordinal)
            || message.Contains("安装超时", StringComparison.Ordinal)
            || message.Contains("引擎已退出", StringComparison.Ordinal)
            || message.Contains("DSH 进程意外退出", StringComparison.Ordinal);
    }

    /// <summary>
    /// Deletes the owned engine installation and starts a fresh install.
    ///
    /// Destructive by design and only offered from the failure panel: it removes
    /// several hundred MB and needs a network round trip to restore. Any live
    /// engine or npm child is reclaimed first, because a running node process
    /// holds its own files open and would make the delete fail part-way.
    /// </summary>
    public void Reinstall()
    {
        Kill();
        Log.Write($"Reinstall requested - removing engine at {_engineDir}");

        try
        {
            if (Directory.Exists(_engineDir))
            {
                Directory.Delete(_engineDir, recursive: true);
                Log.Write("Engine directory removed.");
            }
            else
            {
                Log.Write("Engine directory was already absent.");
            }
        }
        catch (Exception ex)
        {
            // Report rather than fail silently: a partial delete leaves the
            // engine present but damaged, which is exactly what the user was
            // trying to escape.
            Log.Write($"Could not remove the engine directory: {ex.Message}");
            StatusChanged?.Invoke("无法完全删除旧引擎，将尝试覆盖安装…");
        }

        Start();
    }

    public void Dispose() => Kill();
}
