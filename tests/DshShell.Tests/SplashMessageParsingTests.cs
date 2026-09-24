namespace DshShell.Tests;

/// <summary>
/// Reading the splash's messages. This exists because the case-sensitive default
/// of System.Text.Json silently produced a null type for every message, so the
/// splash handshake only ever completed via the fallback timer - and the log
/// looked like the page had sent garbage.
/// </summary>
[TestClass]
public sealed class SplashMessageParsingTests
{
    [TestMethod]
    [DataRow("{\"type\":\"splash-faded\"}", "splash-faded")]
    [DataRow("{\"type\":\"retry\"}", "retry")]
    [DataRow("{\"type\":\"open-log\"}", "open-log")]
    [DataRow("{\"type\":\"reinstall\"}", "reinstall")]
    public void ReadsTheTypeFromThePayloadTheSplashActuallySends(string json, string expected)
    {
        Assert.AreEqual(expected, MainForm.ParseSplashMessageType(json));
    }

    [TestMethod]
    public void ToleratesPascalCaseAndExtraFields()
    {
        // The real payload carries only "type", but the mapping must not depend
        // on how the page spells it.
        Assert.AreEqual("splash-faded", MainForm.ParseSplashMessageType("{\"Type\":\"splash-faded\"}"));
        Assert.AreEqual("retry", MainForm.ParseSplashMessageType("{\"type\":\"retry\",\"extra\":123}"));
    }

    [TestMethod]
    [DataRow("not json at all")]
    [DataRow("")]
    [DataRow("{}")]
    [DataRow("{\"other\":\"retry\"}")]
    [DataRow("null")]
    public void ReturnsNullForAnythingWithoutAType(string json)
    {
        Assert.IsNull(MainForm.ParseSplashMessageType(json),
            "only an explicit, well-formed type may drive the handshake");
    }

    [TestMethod]
    public void DoesNotMistakeNestedTextForAType()
    {
        // The old substring test would have matched "retry" anywhere in the
        // payload, including inside a message body.
        Assert.IsNull(MainForm.ParseSplashMessageType("{\"message\":\"please retry later\"}"));
    }
}
