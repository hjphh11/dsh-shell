namespace DshShell.Tests;

/// <summary>
/// The destructive half of "重新安装引擎": the owned engine directory must be
/// removed, and the attempt must still hand control back to a fresh install.
/// Uses an injected engine directory so it can never touch a real installation.
/// </summary>
[TestClass]
public sealed class ReinstallTests
{
    private static string NewTempEngineDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DshShell-test-engine-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "node_modules", "@deepseek-ai", "dsh", "lib"));
        File.WriteAllText(
            Path.Combine(dir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"),
            "// placeholder");
        return dir;
    }

    [TestMethod]
    public async Task Reinstall_RemovesTheEngineDirectory_AndStartsAFreshAttempt()
    {
        var engineDir = NewTempEngineDir();
        try
        {
            Assert.IsTrue(Directory.Exists(engineDir), "precondition: a fake engine exists");

            // node resolves; there is no npm, so the fresh attempt ends in a
            // reported failure rather than hanging - which is all this asserts.
            var runner = new FakeProcessRunner(_ => new FakeProcess());
            using var launcher = new BackendLauncher(
                runner: runner,
                engineDir: engineDir,
                fileExists: _ => true,
                whereFirst: cmd => @"C:\fake\" + cmd + ".exe",
                npxCacheScan: () => null);

            string? status = null;
            launcher.StatusChanged += s => status = s;

            launcher.Reinstall();

            Assert.IsFalse(Directory.Exists(engineDir),
                "reinstall must delete the owned engine directory");

            // It also kicks off a new attempt; wait for it to say something.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (status is null && DateTime.UtcNow < deadline) await Task.Delay(25);
            launcher.Kill();

            Assert.IsNotNull(status, "reinstall should drive the backend to start again");
        }
        finally
        {
            try { if (Directory.Exists(engineDir)) Directory.Delete(engineDir, true); } catch { }
        }
    }

    [TestMethod]
    public void Reinstall_SurvivesAMissingEngineDirectory()
    {
        // Nothing installed: the delete must not throw, and a fresh install
        // attempt is still the right outcome.
        var missing = Path.Combine(Path.GetTempPath(), "DshShell-test-engine-absent-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.IsFalse(Directory.Exists(missing));

        var runner = new FakeProcessRunner(_ => new FakeProcess());
        using var launcher = new BackendLauncher(
            runner: runner,
            engineDir: missing,
            fileExists: _ => false,
            whereFirst: cmd => cmd is "npm" or "dsh" ? null : @"C:\fake\" + cmd + ".exe",
            npxCacheScan: () => null);

        launcher.Reinstall(); // must not throw
        launcher.Kill();

        Assert.IsFalse(Directory.Exists(missing));
    }
}
