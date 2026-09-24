namespace DshShell.Tests;

/// <summary>
/// The rule that decides whether the splash offers "重新安装引擎". That button
/// destroys several hundred MB of the user's disk, so it must not appear for a
/// missing runtime or a network problem.
/// </summary>
[TestClass]
public sealed class ReinstallDecisionTests
{
    [TestMethod]
    [DataRow("安装完成但引擎入口缺失，请点击重试或重新安装引擎。", DisplayName = "engine entry missing")]
    [DataRow("DSH 引擎安装失败（npm 退出码 1），请检查网络后点击重试。", DisplayName = "install failed")]
    [DataRow("DSH 引擎安装超时（超过 15 分钟），请检查网络后点击重试。", DisplayName = "install timed out")]
    [DataRow("DSH 引擎已退出（退出码 1）。", DisplayName = "engine crashed after ready")]
    [DataRow("DSH 进程意外退出（退出码 1）。", DisplayName = "engine died before ready")]
    public void EngineProblemsOfferAReinstall(string message)
    {
        Assert.IsTrue(BackendLauncher.IsEngineRelatedFailure(message),
            $"a reinstall could plausibly fix: {message}");
    }

    [TestMethod]
    [DataRow("未检测到 Node.js。DSH 引擎是 Node 程序，必须先安装 Node.js（https://nodejs.org，建议 LTS 版）。", DisplayName = "node missing - reinstalling changes nothing")]
    [DataRow("未检测到 Node.js / npm。请先安装 Node.js（https://nodejs.org），然后点击重试。", DisplayName = "npm missing - reinstalling changes nothing")]
    [DataRow("启动超时（超过 120 秒仍未就绪）。", DisplayName = "generic startup timeout")]
    [DataRow("界面加载失败（ConnectionAborted）。请点击重试重新启动引擎。", DisplayName = "page load failure")]
    [DataRow("", DisplayName = "empty")]
    public void UnrelatedProblemsDoNotOfferAReinstall(string message)
    {
        Assert.IsFalse(BackendLauncher.IsEngineRelatedFailure(message),
            $"a reinstall would not help: {message}");
    }

    [TestMethod]
    public void NullMessageIsNotEngineRelated() =>
        Assert.IsFalse(BackendLauncher.IsEngineRelatedFailure(null));
}
