using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace DshShell.Tests;

/// <summary>
/// The startup failure paths, driven through a scripted
/// <see cref="IProcessRunner"/> so each scenario is deterministic and needs no
/// PATH surgery, network access or real Node.js.
/// </summary>
[TestClass]
public sealed class BackendLauncherFailureTests
{
    private const string EngineDir = @"C:\fake\engine";
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(15);

    /// <summary>
    /// A launcher with no engine on disk and a stubbed PATH. <paramref name="nodeExists"/>
    /// controls only the node/npm lookup, so "engine is missing but node is
    /// fine" (the install path) stays distinct from "node is missing".
    /// </summary>
    private static BackendLauncher MakeLauncher(
        FakeProcessRunner runner,
        bool nodeExists,
        TimeSpan? hardTimeout = null,
        bool engineExists = false)
        => new(
            runner: runner,
            hardTimeout: hardTimeout,
            engineDir: EngineDir,
            fileExists: path => engineExists || nodeExists,
            whereFirst: cmd => nodeExists ? @"C:\fake\" + cmd + ".exe" : null);

    /// <summary>Polls until <paramref name="condition"/> holds, or the deadline passes.</summary>
    private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? Settle);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }
        return condition();
    }

    [TestMethod]
    public async Task NodeMissing_FailsWithInstallNodeMessage_AndNeverStartsNpm()
    {
        var runner = new FakeProcessRunner(_ => new FakeProcess());
        using var launcher = MakeLauncher(runner, nodeExists: false);

        string? failed = null;
        launcher.Failed += m => failed = m;
        launcher.Start();
        await WaitUntil(() => failed is not null, TimeSpan.FromSeconds(10));
        launcher.Kill();

        Assert.IsNotNull(failed, "the launcher should report failure when node is absent");
        StringAssert.Contains(failed, "Node.js");
        Assert.AreEqual(0, runner.Starts.Count,
            "no process may be launched when node cannot be found");
    }

    [TestMethod]
    public async Task NpmMissing_FailsWithInstallNodeMessage()
    {
        var runner = new FakeProcessRunner(_ => new FakeProcess());
        // node resolves, npm does not: resolution stops at the install step,
        // which reports the missing package manager.
        using var launcher = new BackendLauncher(
            runner: runner,
            hardTimeout: Short,
            engineDir: EngineDir,
            // node resolves, but every route to an *engine* is closed: no engine
            // dir, no PATH `dsh` shim, and no npm. (The machine's real npx cache
            // is also scanned by the launcher, so the shim probe is closed too -
            // otherwise resolution stops there and never reaches the install.)
            fileExists: path =>
                !path.EndsWith("bin.js", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith("dsh.exe", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith("dsh.cmd", StringComparison.OrdinalIgnoreCase),
            whereFirst: cmd => cmd is "npm" or "dsh" ? null : @"C:\fake\" + cmd + ".exe",
            npxCacheScan: () => null);

        string? failed = null;
        launcher.Failed += m => failed = m;
        launcher.Start();
        await WaitUntil(() => failed is not null, TimeSpan.FromSeconds(10));
        launcher.Kill();

        Assert.IsNotNull(failed);
        StringAssert.Contains(failed, "npm");
    }

    [TestMethod]
    public async Task InstallFails_DoesNotFallThroughToLaunchingAnEngine()
    {
        var runner = new FakeProcessRunner(_ =>
        {
            var npm = new FakeProcess();
            npm.EmitError("npm ERR! network timeout");
            npm.Exit(1);
            return npm;
        });
        using var launcher = MakeLauncher(runner, nodeExists: true);

        string? failed = null;
        launcher.Failed += m => failed = m;
        launcher.Start();
        await WaitUntil(() => failed is not null, TimeSpan.FromSeconds(15));
        await Task.Delay(200);
        launcher.Kill();

        Assert.IsNotNull(failed, "a non-zero npm exit code must be reported");
        StringAssert.Contains(failed, "退出码 1");
        Assert.AreEqual(1, runner.Starts.Count,
            "a failed install must not fall through to launching an engine");
    }

    [TestMethod]
    public async Task EngineNeverReportsReady_FailsWithStartupTimeout()
    {
        // Alive, silent: no URL line ever arrives.
        var runner = FakeProcessRunner.Single(new FakeProcess());
        using var launcher = MakeLauncher(runner, nodeExists: true, hardTimeout: Short);

        string? failed = null;
        string? ready = null;
        launcher.Failed += m => failed = m;
        launcher.Ready += u => ready = u;
        launcher.Start();
        await WaitUntil(() => failed is not null || ready is not null);
        launcher.Kill();

        Assert.IsNull(ready);
        Assert.IsNotNull(failed);
        StringAssert.Contains(failed, "超时");
    }

    [TestMethod]
    public async Task EngineDiesBeforeReady_ReportsExitCode_AndIsNotReportedAsACrash()
    {
        var runner = new FakeProcessRunner(_ =>
        {
            var engine = new FakeProcess();
            engine.EmitError("Error: task-board ledger is already owned by process 42");
            engine.Exit(1);
            return engine;
        });
        using var launcher = MakeLauncher(runner, nodeExists: true, hardTimeout: Short);

        string? failed = null;
        var crashed = false;
        launcher.Failed += m => failed = m;
        launcher.EngineCrashed += _ => crashed = true;
        launcher.Start();
        await WaitUntil(() => failed is not null);
        launcher.Kill();

        Assert.IsNotNull(failed);
        StringAssert.Contains(failed, "退出码 1");
        Assert.IsFalse(crashed, "a process that never became ready has not crashed");
    }

    [TestMethod]
    public async Task EngineCrashesAfterReady_RaisesEngineCrashedAndNotFailed()
    {
        using var listener = new HttpListener();
        var port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var serving = Task.Run(async () =>
        {
            try
            {
                while (listener.IsListening)
                {
                    var ctx = await listener.GetContextAsync();
                    ctx.Response.StatusCode = 200;
                    ctx.Response.Close();
                }
            }
            catch { /* listener stopped */ }
        });

        try
        {
            FakeProcess? engine = null;
            var runner = new FakeProcessRunner(_ =>
            {
                engine = new FakeProcess();
                engine.Emit($"dsh web: http://127.0.0.1:{port}/?token=TESTTOKEN");
                return engine;
            });
            using var launcher = MakeLauncher(runner, nodeExists: true, hardTimeout: TimeSpan.FromSeconds(10));

            string? failed = null;
            string? ready = null;
            var crashed = false;
            launcher.Failed += m => failed = m;
            launcher.Ready += u => ready = u;
            launcher.EngineCrashed += _ => crashed = true;
            launcher.Start();

            Assert.IsTrue(await WaitUntil(() => ready is not null),
                "a responsive engine on a loopback URL should report ready");

            // Take down the very process the launcher is monitoring.
            Assert.IsNotNull(engine);
            engine.Exit(3);

            Assert.IsTrue(await WaitUntil(() => crashed, TimeSpan.FromSeconds(10)),
                "losing an engine that had been ready is a crash, not a startup failure");
            Assert.IsNull(failed, "a post-ready crash must not be reported as a startup failure");
        }
        finally
        {
            listener.Stop();
            await Task.WhenAny(serving, Task.Delay(1000));
        }
    }

    [TestMethod]
    public async Task MaliciousUrlOnStdout_IsNeverNavigatedTo()
    {
        // The engine prints a non-loopback address carrying a token. Following
        // it would hand WebView2 - and a live token - to a remote host.
        var runner = new FakeProcessRunner(_ =>
        {
            var engine = new FakeProcess();
            engine.Emit("dsh web: http://evil.example.com/?token=STOLEN");
            return engine;
        });
        using var launcher = MakeLauncher(runner, nodeExists: true, hardTimeout: Short, engineExists: true);

        string? failed = null;
        string? ready = null;
        launcher.Failed += m => failed = m;
        launcher.Ready += u => ready = u;
        launcher.Start();
        await WaitUntil(() => failed is not null || ready is not null);
        launcher.Kill();

        Assert.IsNull(ready, "a non-loopback URL must never become the ready target");
        Assert.IsNotNull(failed, "rejecting the only URL should end in a startup timeout");
    }

    [TestMethod]
    public async Task EngineStartedFromDedicatedDir_UsesTheResolvedEngineBinary()
    {
        var runner = new FakeProcessRunner(_ =>
        {
            var engine = new FakeProcess();
            engine.Emit("dsh web: https://evil.example.com/?token=STOLEN"); // rejected
            return engine;
        });
        using var launcher = MakeLauncher(runner, nodeExists: true, hardTimeout: Short, engineExists: true);

        launcher.Start();
        await WaitUntil(() => runner.Starts.Count > 0);
        launcher.Kill();

        Assert.AreEqual(1, runner.Starts.Count);
        StringAssert.Contains(runner.Starts[0].FileName, "node");
        StringAssert.Contains(runner.Starts[0].Arguments, "web --port 0 --no-open");
        StringAssert.Contains(runner.Starts[0].Arguments, "bin.js");
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
