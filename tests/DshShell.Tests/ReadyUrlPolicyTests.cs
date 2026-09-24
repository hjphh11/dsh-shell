namespace DshShell.Tests;

/// <summary>
/// The ready-URL policy. Pure function, so these cover the security boundary
/// exhaustively without any process plumbing.
/// </summary>
[TestClass]
public sealed class ReadyUrlPolicyTests
{
    private static bool Accepts(string candidate) =>
        BackendLauncher.TryValidateReadyUrl(candidate, out _);

    private static bool Accepts(string candidate, out string url) =>
        BackendLauncher.TryValidateReadyUrl(candidate, out url);

    [TestMethod]
    [DataRow("http://127.0.0.1:8045/?token=abc")]
    [DataRow("http://127.0.0.1:1/?token=x")]
    [DataRow("http://localhost:8045/?token=abc")]
    [DataRow("http://127.0.0.1:8045/?foo=1&token=abc")]
    [DataRow("http://[::1]:8045/?token=abc")]
    [DataRow("http://127.0.0.1/?token=abc")]
    public void AcceptsLoopbackHttpUrlsCarryingAToken(string candidate)
    {
        Assert.IsTrue(Accepts(candidate), $"should accept: {candidate}");
    }

    [TestMethod]
    [DataRow("http://evil.example.com/?token=STOLEN", DisplayName = "remote host")]
    [DataRow("http://192.168.1.10:8045/?token=abc", DisplayName = "private LAN address")]
    [DataRow("http://0.0.0.0:8045/?token=abc", DisplayName = "all-interfaces bind")]
    [DataRow("http://127.0.0.1.evil.com/?token=abc", DisplayName = "loopback-lookalike hostname")]
    [DataRow("https://127.0.0.1:8045/?token=abc", DisplayName = "https is not the engine's scheme")]
    [DataRow("file:///C:/Windows/System32/calc.exe", DisplayName = "file scheme")]
    [DataRow("javascript:alert(1)", DisplayName = "javascript scheme")]
    [DataRow("ftp://127.0.0.1/?token=abc", DisplayName = "ftp scheme")]
    [DataRow("http://127.0.0.1:8045/", DisplayName = "no token at all")]
    [DataRow("http://127.0.0.1:8045/?other=1", DisplayName = "token under another name")]
    [DataRow("not a url", DisplayName = "not a URI")]
    [DataRow("", DisplayName = "empty")]
    public void RejectsAnythingThatIsNotALoopbackUrlWithAToken(string candidate)
    {
        Assert.IsFalse(Accepts(candidate), $"should reject: {candidate}");
    }

    [TestMethod]
    public void RejectsTokenWithControlCharacters()
    {
        // A truncated read or a hostile engine could otherwise smuggle control
        // characters into a URL that is handed to WebView2.
        Assert.IsFalse(Accepts("http://127.0.0.1:8045/?token=abc\u0000def"));
        Assert.IsFalse(Accepts("http://127.0.0.1:8045/?token=abc\u001b[31m"));
    }

    [TestMethod]
    public void NormalizesTheAcceptedUrl()
    {
        Assert.IsTrue(Accepts("http://127.0.0.1:8045/?token=abc", out var url));
        Assert.AreEqual("http://127.0.0.1:8045/?token=abc", url);
    }

    [TestMethod]
    public void PortOutOfRangeIsRejected() =>
        Assert.IsFalse(Accepts("http://127.0.0.1:99999/?token=abc"));
}
