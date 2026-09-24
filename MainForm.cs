using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DshShell;

internal sealed class MainForm : Form
{
    public static MainForm? Instance { get; private set; }

    private static readonly TimeSpan MinSplashTime = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan SplashFadeTime = TimeSpan.FromMilliseconds(600);

    private readonly BackendLauncher _backend = new();
    private readonly Stopwatch _splashShownAt = Stopwatch.StartNew();

    private WebView2 _webview = null!;
    private bool _readyUrlPendingNavigation;
    private string? _pendingUrl;
    private bool _splashFaded;
    private bool _navigatedToApp;
    private string? _pendingErrorMessage;

    /// <summary>
    /// Whether the pending error is one a reinstall could fix. Sent with the
    /// error so the splash only offers the destructive action where it helps.
    /// </summary>
    private bool _pendingErrorReinstallable;
    private int _retryCount;

    /// <summary>
    /// Boot attempt counter. Every delayed callback captures the value it was
    /// scheduled under and becomes a no-op once a newer attempt has started,
    /// so a stale timer from an earlier boot can never drive the navigation
    /// handshake. Mirrors BackendLauncher's own generation counter, which does
    /// not reach into this layer.
    /// </summary>
    private int _bootGeneration;

    /// <summary>True while WebView2 is initialised and usable.</summary>
    private bool _webViewReady;

    public MainForm()
    {
        Instance = this;

        Text = "DeepSeek Harness";
        StartPosition = FormStartPosition.CenterScreen;
        // Initial size 1920x1080, clamped to the actual working area.
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        ClientSize = new Size(Math.Min(1920, wa.Width), Math.Min(1080, wa.Height));
        MinimumSize = new Size(960, 640);
        BackColor = Color.White;
        try
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch
        {
            Icon = SystemIcons.Application;
        }

        _webview = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = BackColor,
        };
        Controls.Add(_webview);

        // Backend events are wired exactly once for the lifetime of the form:
        // _backend is a single instance, so re-subscribing on every start would
        // multiply the handlers and post duplicate splash messages.
        _backend.StatusChanged += s => SafePost(new { type = "status", text = s });
        _backend.Progress += p => SafePost(new
        {
            type = "progress",
            // Fields are named explicitly: the splash reads these names, so they
            // must not drift if a C# identifier is renamed.
            progress = new
            {
                phase = p.Phase switch
                {
                    EnginePhase.Resolving => "resolving",
                    EnginePhase.Downloading => "downloading",
                    EnginePhase.Applying => "applying",
                    _ => "finishing",
                },
                done = p.Done,
                total = p.Total,
                determinate = p.IsDeterminate,
            },
        });
        _backend.Ready += url => OnBackendReady(url);
        _backend.Failed += msg => SafePost(new
        {
            type = "error",
            message = msg,
            reinstallable = BackendLauncher.IsEngineRelatedFailure(msg),
        });
        _backend.EngineCrashed += msg => ShowSplashError(msg);

        FormClosing += OnFormClosing;
        Load += OnLoadAsync;
    }

    public void ActivateToFront()
    {
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private async void OnLoadAsync(object? sender, EventArgs e)
    {
        Log.Write("Main form loaded - initializing WebView2.");

        if (!await InitializeWebViewWithFallbackAsync())
        {
            // The splash is unavailable, so this is the only surface we have.
            // Close() here means the user must restart the app by hand.
            var answer = MessageBox.Show(this,
                "WebView2 初始化失败。\n\n" +
                "已尝试：默认用户数据目录、重新创建的备用目录、系统临时目录。\n" +
                "常见原因是上一次退出的 WebView2 进程仍占用用户数据目录，或系统未安装 WebView2 Runtime。\n\n" +
                "是否打开 WebView2 Runtime 官方下载页面？\n" +
                "（选择“否”将关闭本程序，可稍后重新打开再试）",
                "DeepSeek Harness", MessageBoxButtons.YesNo, MessageBoxIcon.Error);
            if (answer == DialogResult.Yes)
            {
                OpenWebView2DownloadPage();
            }
            Close();
            return;
        }

        // Backend failures are reported on the splash (retry button), never here.
        Log.Write("Splash shown - starting backend.");

        // Detect the environment now, while the engine boots, so the splash can
        // show a missing prerequisite before it turns into a failure message.
        SafePost(new { type = "env", env = DetectEnvironment() });

        _splashShownAt.Restart();
        StartBackend();
    }

    /// <summary>
    /// The prerequisites shown along the bottom of the splash. Each probe is
    /// cheap (one PATH lookup plus two file reads) and never throws.
    /// </summary>
    private object DetectEnvironment()
    {
        var node = ProbeNode();
        return new
        {
            node,
            engine = ProbeEngineVersion(),
            webView2 = ProbeWebView2Version(),
        };
    }

    /// <summary>Reports "v24.12.0" style text, or null when node is absent.</summary>
    private static string? ProbeNode()
    {
        try
        {
            var node = FindOnPath("node");
            if (node is null) return null;
            var info = FileVersionInfo.GetVersionInfo(node);
            var version = info.ProductVersion ?? info.FileVersion;
            return string.IsNullOrWhiteSpace(version) ? Path.GetFileName(node) : $"v{version.TrimStart('v', 'V')}";
        }
        catch (Exception ex)
        {
            Log.Write($"Node probe failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Reads the installed engine's package version, or null if absent.</summary>
    private static string? ProbeEngineVersion()
    {
        try
        {
            var manifest = Path.Combine(
                BackendLauncher.EngineDir, "node_modules", "@deepseek-ai", "dsh", "package.json");
            if (!File.Exists(manifest)) return null;

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));
            return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch (Exception ex)
        {
            // A damaged manifest is itself diagnostic, so say so rather than
            // showing nothing at all.
            Log.Write($"Engine version probe failed: {ex.Message}");
            return "读取失败";
        }
    }

    private string? ProbeWebView2Version()
    {
        try
        {
            return _webview.CoreWebView2?.Environment.BrowserVersionString;
        }
        catch (Exception ex)
        {
            Log.Write($"WebView2 version probe failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Minimal PATH lookup. BackendLauncher has a richer one but it is tied to
    /// the launch pipeline; this is only for display.
    /// </summary>
    private static string? FindOnPath(string command)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("where.exe", command)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });
            if (p is null) return null;
            var first = p.StandardOutput.ReadLine();
            p.WaitForExit(3000);
            return string.IsNullOrWhiteSpace(first) ? null : first.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static void OpenWebView2DownloadPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(
                "https://developer.microsoft.com/microsoft-edge/webview2/")
            { UseShellExecute = true });
        }
        catch (Exception openEx)
        {
            Log.Write($"Could not open WebView2 download page: {openEx.Message}");
        }
    }

    private static string WebView2UserDataFolder
    {
        get
        {
            // DSHSHELL_WEBVIEW2_DIR lets an automated run keep its own browser
            // profile. Two WebView2 instances must not share one user-data
            // folder (it fails with 0x800700AA), so an isolated run cannot
            // reuse the folder a user-facing instance is holding.
            try
            {
                var overridden = Environment.GetEnvironmentVariable("DSHSHELL_WEBVIEW2_DIR");
                if (!string.IsNullOrWhiteSpace(overridden)) return overridden;
            }
            catch
            {
                // fall through to the default below
            }
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DshShell", "WebView2");
        }
    }

    /// <summary>
    /// Initialises WebView2, retrying with fallback user-data folders.
    /// A previous <em>failed</em> attempt can leave the folder locked by a
    /// still-exiting WebView2 process, which surfaces as
    /// COMException 0x800700AA (ERROR_BUSY) - observed in the wild. Without a
    /// fallback that case is unrecoverable short of restarting the app.
    /// </summary>
    private async Task<bool> InitializeWebViewWithFallbackAsync()
    {
        // Fallback folders get a fresh unique suffix per call. They must NOT be
        // deterministic: recreating a fix-named folder would delete the cookie
        // jar and session state inside, forcing the user to re-authenticate.
        var stamp = $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Environment.ProcessId}";
        var primary = WebView2UserDataFolder;
        var candidates = new List<(string Path, string Label)>
        {
            (primary, "默认目录"),
            (Path.Combine(primary + "-alt", stamp), "备用目录"),
            (Path.Combine(Path.GetTempPath(), "DshShell-WebView2", stamp), "临时目录"),
        };

        // Persist the working folder so a retry after the *backend* failed
        // reuses it (keeping the session) instead of leaking another directory.
        var reuse = TryReadSavedWebView2Folder();
        if (reuse is not null)
        {
            candidates.Insert(0, (reuse, "已保存的可用目录"));
        }

        string? lastError = null;
        foreach (var (path, label) in candidates)
        {
            try
            {
                Directory.CreateDirectory(path);

                if (await TryInitializeWebViewAsync(path))
                {
                    _webViewReady = true;
                    SaveWebView2Folder(path);
                    Log.Write($"WebView2 initialised ({label}): {path}");
                    return true;
                }
                lastError = "EnsureCoreWebView2Async 返回 null 环境";
            }
            catch (Exception ex)
            {
                lastError = $"{ex.GetType().Name}: {ex.Message}";
                Log.Write($"WebView2 init failed ({label} '{path}'): {ex}");
            }

            // Any unsuccessful attempt leaves a control in the form that can
            // never be initialised; remove it before trying the next folder so
            // the failed controls (and their environments) do not accumulate.
            DetachWebView();
        }

        Log.Write($"WebView2 init exhausted all candidates. Last error: {lastError}");
        return false;
    }

    /// <summary>
    /// One WebView2 init attempt against a specific folder. Never throws:
    /// returns false so the caller can move on to the next candidate.
    /// </summary>
    private async Task<bool> TryInitializeWebViewAsync(string userDataFolder)
    {
        // A control instance that already failed to init cannot be reused.
        _webview = CreateWebViewControl();

        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
        await _webview.EnsureCoreWebView2Async(env);
        if (_webview.CoreWebView2 is null) return false;

        _webview.CoreWebView2.NavigateToString(LoadSplashHtml());
        _webview.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        _webview.CoreWebView2.DocumentTitleChanged += (_, _) =>
        {
            if (_navigatedToApp)
            {
                var t = _webview.CoreWebView2.DocumentTitle;
                Text = string.IsNullOrEmpty(t) || t == "DeepSeek Harness"
                    ? "DeepSeek Harness" : $"DeepSeek Harness — {t}";
            }
        };
        _webview.NavigationCompleted += OnNavigationCompleted;
        return true;
    }

    private WebView2 CreateWebViewControl()
    {
        var view = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.White,
        };
        Controls.Add(view);
        view.BringToFront();
        return view;
    }

    /// <summary>Removes the current WebView2 control so a new one can be added.</summary>
    private void DetachWebView()
    {
        var old = _webview;
        _webViewReady = false;
        try
        {
            if (old is null) return;
            Controls.Remove(old);
            old.Dispose();
        }
        catch (Exception ex)
        {
            Log.Write($"Disposing failed WebView2 control: {ex.Message}");
        }
    }

    /// <summary>
    /// Where the "last known-good user-data folder" note is kept. It lives next
    /// to <see cref="WebView2UserDataFolder"/> so an isolated run records its
    /// own choice and never adopts the user-facing profile.
    /// </summary>
    private static string WebView2FolderStatePath =>
        WebView2UserDataFolder.TrimEnd('\\', '/') + "-folder";

    private static string? TryReadSavedWebView2Folder()
    {
        try
        {
            if (!File.Exists(WebView2FolderStatePath)) return null;
            var saved = File.ReadAllText(WebView2FolderStatePath).Trim();
            if (saved.Length == 0 || !Directory.Exists(saved)) return null;
            // Only trust folders this shell manages, never an arbitrary path
            // that a tampered state file might point at.
            var root = Path.GetFullPath(
                Path.GetDirectoryName(WebView2UserDataFolder.TrimEnd('\\', '/'))!);
            var full = Path.GetFullPath(saved);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                Log.Write($"Ignoring saved WebView2 folder outside state dir: {saved}");
                return null;
            }
            return full;
        }
        catch (Exception ex)
        {
            Log.Write($"Could not read saved WebView2 folder: {ex.Message}");
            return null;
        }
    }

    private static void SaveWebView2Folder(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(WebView2FolderStatePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(WebView2FolderStatePath, path);
        }
        catch (Exception ex)
        {
            Log.Write($"Could not save WebView2 folder: {ex.Message}");
        }
    }

    /// <summary>
    /// Loads splash.html + DeepSeek logo from embedded resources and inlines
    /// the logo as a data URI, so the single-file exe needs no extra files.
    /// </summary>
    private static string LoadSplashHtml()
    {
        var asm = typeof(MainForm).Assembly;
        string ReadText(string name)
        {
            using var s = asm.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"missing embedded resource: {name}");
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }
        byte[] ReadBytes(string name)
        {
            using var s = asm.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"missing embedded resource: {name}");
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        var html = ReadText("DshShell.wwwroot.splash.html");
        var dataUri = "data:image/svg+xml;base64," + Convert.ToBase64String(ReadBytes("DshShell.deepseek-color.svg"));
        return html.Replace("https://app.dsh-shell.local/deepseek-color.svg", dataUri);
    }

    /// <summary>
    /// Resets the handshake state and starts a fresh backend attempt, bumping
    /// the boot generation so every callback from the previous attempt is
    /// invalidated. Safe to call for both the initial start and a retry.
    /// </summary>
    private void StartBackend()
    {
        var gen = ++_bootGeneration;
        _readyUrlPendingNavigation = false;
        _navigatedToApp = false;
        _splashFaded = false;
        _pendingUrl = null;
        _pendingErrorMessage = null;
        _splashShownAt.Restart();
        Log.Write($"Starting backend attempt #{gen}.");
        _backend.Start();
    }

    private void OnBackendReady(string url)
    {
        _pendingUrl = url;
        _readyUrlPendingNavigation = true;
        var gen = _bootGeneration;

        // Make sure the splash has been on screen long enough not to flash.
        var remaining = MinSplashTime - _splashShownAt.Elapsed;
        if (remaining <= TimeSpan.Zero) remaining = TimeSpan.Zero;
        RunOnUiThreadAfter(gen, remaining, () =>
        {
            if (_readyUrlPendingNavigation && !_splashFaded)
            {
                SafePost(new { type = "ready" });
            }
        });

        // Fallback: even if the splash JS never acks (e.g. throttled timers),
        // navigate anyway. The ack normally arrives in ~1s. Generation-checked
        // so a timer left over from an earlier boot cannot fire here.
        RunOnUiThreadAfter(gen, MinSplashTime + SplashFadeTime + TimeSpan.FromMilliseconds(3000), () =>
        {
            if (_readyUrlPendingNavigation && !_navigatedToApp)
            {
                Log.Write("Splash ack missing - navigating via fallback timer.");
                NavigateToApp();
            }
        });
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread.
    /// </summary>
    private void RunOnUiThread(Action action)
    {
        try
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired)
            {
                BeginInvoke(action);
                return;
            }
            action();
        }
        catch (Exception ex)
        {
            Log.Write($"UI action failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread after a delay, but only
    /// if <paramref name="generation"/> is still the current boot attempt.
    /// Deliberately does NOT use TaskScheduler.FromCurrentSynchronizationContext():
    /// that throws unless the caller happens to run on a thread with a sync
    /// context, which would silently kill the whole navigation handshake.
    /// </summary>
    private void RunOnUiThreadAfter(int generation, TimeSpan delay, Action action)
    {
        Task.Delay(delay).ContinueWith(_ => RunOnUiThread(() =>
        {
            if (generation != _bootGeneration)
            {
                Log.Write($"Discarding stale timer from boot attempt #{generation} (current #{_bootGeneration}).");
                return;
            }
            action();
        }));
    }

    /// <summary>
    /// Returns to the splash with an error panel (retry button) - used when the
    /// engine dies mid-session or the app page fails to load.
    /// Raised from a process-exit callback, so it must hop to the UI thread.
    /// </summary>
    private void ShowSplashError(string message)
    {
        RunOnUiThread(() =>
        {
            Log.Write($"Showing splash error: {message}");
            // Bump the boot generation: any timer or ready-callback still in
            // flight from the attempt that just failed must not navigate.
            _bootGeneration++;
            _readyUrlPendingNavigation = false;
            _navigatedToApp = false;
            _splashFaded = false;
            _pendingErrorMessage = message;
            _pendingErrorReinstallable = BackendLauncher.IsEngineRelatedFailure(message);
            _splashShownAt.Restart();
            try
            {
                if (!_webViewReady || _webview.CoreWebView2 is null)
                {
                    Log.Write("Cannot show splash error: WebView2 is not ready.");
                    return;
                }
                _webview.DefaultBackgroundColor = Color.White;
                _webview.CoreWebView2.NavigateToString(LoadSplashHtml());
            }
            catch (Exception ex)
            {
                Log.Write($"Could not return to splash: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Only message types the splash is allowed to send. Parsing strictly and
    /// dispatching on an explicit type replaces the previous substring test,
    /// where any payload merely <em>containing</em> "retry" could restart the
    /// engine.
    /// </summary>
    internal sealed class SplashMessage
    {
        public string? Type { get; set; }
    }

    /// <summary>
    /// Options for reading splash messages. Case-insensitive matching is
    /// required, not cosmetic: the splash sends <c>{"type":...}</c> and
    /// System.Text.Json is case-sensitive by default, so without this every
    /// message deserializes to a null Type and is silently ignored - the
    /// navigation then limps along on the fallback timer instead.
    /// </summary>
    private static readonly System.Text.Json.JsonSerializerOptions SplashMessageOptions =
        new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Reads the message type from a splash payload, or null when it is
    /// unparseable or carries no type. Pure, so the mapping is unit-tested
    /// rather than discovered from a log after the fact.
    /// </summary>
    internal static string? ParseSplashMessageType(string json)
    {
        try
        {
            return System.Text.Json.JsonSerializer
                .Deserialize<SplashMessage>(json, SplashMessageOptions)?.Type;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.WebMessageAsJson;
            Log.Write($"[splash -> host] {json}");

            var type = ParseSplashMessageType(json);
            switch (type)
            {
                case "splash-faded":
                    _splashFaded = true;
                    if (_readyUrlPendingNavigation) NavigateToApp();
                    break;
                case "retry":
                    RetryBackend();
                    break;
                case "open-log":
                    OpenLogFile();
                    break;
                case "reinstall":
                    ReinstallEngine();
                    break;
                default:
                    Log.Write($"Ignoring unknown splash message type: {type ?? "<null>"}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"WebMessageReceived error: {ex.Message}");
        }
    }

    /// <summary>Opens the diagnostic log in whatever handles .log files.</summary>
    private void OpenLogFile()
    {
        Log.Write("Open-log requested from the splash.");
        try
        {
            if (!File.Exists(Log.LogPath))
            {
                Log.Write("Log file does not exist yet.");
                return;
            }
            Process.Start(new ProcessStartInfo(Log.LogPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // Never let a failed shell-open take the app down; the path is in
            // the error text on screen anyway.
            Log.Write($"Could not open the log file: {ex.Message}");
        }
    }

    /// <summary>
    /// Rebuilds the engine from scratch. Confirm-then-act happens in the splash;
    /// this side only guards against a concurrent attempt.
    /// </summary>
    private void ReinstallEngine()
    {
        if (_backend.IsBusy)
        {
            Log.Write("Reinstall ignored: a backend attempt is already in flight.");
            return;
        }

        Log.Write("Reinstall requested from the splash.");
        _bootGeneration++;
        _readyUrlPendingNavigation = false;
        _navigatedToApp = false;
        _splashFaded = false;
        _pendingUrl = null;
        _pendingErrorMessage = null;
        _splashShownAt.Restart();

        // Reinstall reclaims the process tree and deletes the engine, so there
        // is no stale process to worry about; it also starts a fresh attempt.
        _backend.Reinstall();
        SafePost(new { type = "reset" });
    }

    private void NavigateToApp()
    {
        if (_navigatedToApp || _pendingUrl is null) return;
        // The splash can only exist while WebView2 is up, but a recovery path
        // may have swapped the control out from under a pending navigation.
        if (!_webViewReady || _webview.CoreWebView2 is null)
        {
            Log.Write("Navigation skipped: WebView2 is not ready.");
            return;
        }
        _navigatedToApp = true;
        _readyUrlPendingNavigation = false;
        // DSH UI is dark - switch the webview background so navigation doesn't flash white.
        _webview.DefaultBackgroundColor = Color.FromArgb(11, 14, 20);
        Log.Write($"Navigating WebView2 to app: {_pendingUrl}");
        _webview.CoreWebView2.Navigate(_pendingUrl);
    }

    private void RetryBackend()
    {
        // A backend attempt is already in flight (typically the npm install /
        // update phase, when no engine process exists yet). Starting a second
        // one would race the same engine directory, so ignore the click.
        if (_backend.IsBusy)
        {
            Log.Write("Retry ignored: a backend attempt is already in flight.");
            return;
        }

        // A live engine process does NOT mean the engine is usable - it may be
        // hung past its startup timeout. Returning early here used to leave the
        // user clicking a retry button that did nothing. Reclaim the process
        // tree first, then start a genuinely fresh attempt.
        if (_backend.IsRunning)
        {
            Log.Write("Retry: reclaiming the previous engine process tree before restarting.");
            _backend.Kill();
        }

        _retryCount++;
        Log.Write($"Retry requested (#{_retryCount}).");
        // Start first, then reset the splash: StartBackend can emit status
        // messages synchronously, and a "reset" posted afterwards would land
        // out of order and blank them out again.
        StartBackend();
        SafePost(new { type = "reset" });
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        // A replaced control can still deliver events for a document it loaded
        // before a WebView2 recovery; ignore anything from a stale instance.
        if (!_webViewReady || !ReferenceEquals(sender, _webview))
        {
            Log.Write("Ignoring navigation event from a stale WebView2 instance.");
            return;
        }

        if (_navigatedToApp)
        {
            if (e.IsSuccess)
            {
                Log.Write("App page loaded - splash flow complete.");
            }
            else
            {
                // App page failed (engine restarted on another port, token
                // expired, ...): go back to the splash so the user can retry.
                ShowSplashError($"界面加载失败（{e.WebErrorStatus}）。请点击重试重新启动引擎。");
            }
            return;
        }

        // Fresh splash page: deliver any pending error once it is listening.
        if (_pendingErrorMessage is { } message)
        {
            var reinstallable = _pendingErrorReinstallable;
            _pendingErrorMessage = null;
            _pendingErrorReinstallable = false;
            SafePost(new { type = "error", message, reinstallable });
        }
    }

    private void SafePost(object message)
    {
        string json;
        try
        {
            json = System.Text.Json.JsonSerializer.Serialize(message);
        }
        catch (Exception ex)
        {
            Log.Write($"Could not serialize UI message: {ex.Message}");
            return;
        }
        Log.Write($"[host -> splash] {json}");

        // Touch WebView2 only on the UI thread: callers may be process-exit
        // callbacks or HTTP polling continuations.
        RunOnUiThread(() =>
        {
            try
            {
                if (!_webViewReady || _webview.CoreWebView2 is null) return;
                _webview.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch (Exception ex)
            {
                Log.Write($"PostWebMessage failed: {ex.Message}");
            }
        });
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        Log.Write("Form closing - killing backend process tree.");
        _backend.Kill();
    }
}
