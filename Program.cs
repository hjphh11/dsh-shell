namespace DshShell;

internal static class Program
{
    private const string MutexName = @"Local\DshShell.SingleInstance.Mutex";
    private const string ActivateEventName = @"Local\DshShell.SingleInstance.Activate";

    /// <summary>
    /// Object names for this process. DSHSHELL_INSTANCE makes a launch use its
    /// own single-instance objects, so an automated run can start a real shell
    /// without colliding with - or signaling - a shell the user is working in.
    /// A secondary launch signals the primary instance to come to the front,
    /// which is disruptive enough to tear down a GUI driving the launch.
    /// </summary>
    private static string InstanceSuffix { get; } = ResolveInstanceSuffix();

    private static string ResolveInstanceSuffix()
    {
        try
        {
            var suffix = Environment.GetEnvironmentVariable("DSHSHELL_INSTANCE");
            return string.IsNullOrWhiteSpace(suffix) ? string.Empty : "." + suffix.Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    [STAThread]
    private static void Main(string[] args)
    {
        Log.Write("========== DshShell starting ==========");
        if (InstanceSuffix.Length > 0)
        {
            Log.Write($"Isolated instance mode (DSHSHELL_INSTANCE='{InstanceSuffix.TrimStart('.')}').");
        }

        var mutexName = MutexName + InstanceSuffix;
        var activateEventName = ActivateEventName + InstanceSuffix;

        EventWaitHandle? activateEvent = null;
        Mutex? mutex = null;
        bool createdNew;
        try
        {
            // Local\ (per session) instead of Global\: avoids needing
            // SeCreateGlobalPrivilege, which standard users may not hold.
            activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, activateEventName, out _);
            mutex = new Mutex(true, mutexName, out createdNew);
        }
        catch (Exception ex)
        {
            // Never let the single-instance guard kill startup without a trace.
            Log.Write($"Single-instance setup failed ({ex.GetType().Name}: {ex.Message}) - continuing without it.");
            createdNew = true;
        }

        if (!createdNew)
        {
            Log.Write("Another instance is running - signaling it and exiting.");
            for (var i = 0; i < 10; i++)
            {
                try { activateEvent?.Set(); } catch { }
                Thread.Sleep(300);
            }
            return;
        }

        // Primary instance: listen for "activate" nudges from secondary launches.
        var listener = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    if (activateEvent is null || !activateEvent.WaitOne()) return;
                    // The form may not exist yet during early startup - keep
                    // trying briefly so a fast double-click is not swallowed.
                    for (var i = 0; i < 40; i++)
                    {
                        var form = MainForm.Instance;
                        if (form is not null)
                        {
                            form.BeginInvoke(form.ActivateToFront);
                            break;
                        }
                        Thread.Sleep(100);
                    }
                }
                catch
                {
                    // form may be disposing
                }
            }
        })
        { IsBackground = true };
        listener.Start();

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        Application.ThreadException += (_, e) => Log.Write($"UI thread exception: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Write($"Unhandled exception: {e.ExceptionObject}");

        var form = new MainForm();
        Application.Run(form);

        mutex?.Dispose();
        // Guarantee teardown even if a WebView2 background thread lingers.
        Log.Write("DshShell exited cleanly.");
        Environment.Exit(0);
    }
}
