namespace DshShell.Tests;

/// <summary>
/// Parsing npm's http log into a package count. This is the piece most likely to
/// be wrong, and the progress bar is only as honest as this count.
/// </summary>
[TestClass]
public sealed class NpmProgressTrackerTests
{
    /// <summary>Real line shapes captured from npm 11.6.2 against the configured registry.</summary>
    private const string TarballFetch =
        "npm http fetch GET 200 https://registry.npmjs.org/is-number/-/is-number-7.0.0.tgz 105ms (cache revalidated)";
    private const string TarballCacheHit =
        "npm http cache is-number@https://registry.npmjs.org/is-number/-/is-number-7.0.0.tgz 0ms (cache hit)";
    private const string MetadataFetch =
        "npm http fetch GET 200 https://registry.npmjs.org/is-number 105ms (cache revalidated)";
    private const string AdvisoryFetch =
        "npm http fetch POST 404 https://registry.npmjs.org/-/npm/v1/security/advisories/bulk 18ms";

    private static NpmProgressTracker MakeTracker(int expected, List<EngineProgress> reports) =>
        new(expected, reports.Add);

    [TestMethod]
    public void CountsEachTarballOnce()
    {
        var reports = new List<EngineProgress>();
        var tracker = MakeTracker(100, reports);

        tracker.Observe(TarballFetch);
        tracker.Observe(TarballFetch);          // same package again
        tracker.Observe(TarballCacheHit);       // same URL via the cache route

        Assert.AreEqual(1, tracker.Seen, "a repeated package must not inflate the count");
    }

    [TestMethod]
    public void IgnoresMetadataAndAdvisoryRequests()
    {
        var reports = new List<EngineProgress>();
        var tracker = MakeTracker(100, reports);

        tracker.Observe(MetadataFetch);
        tracker.Observe(AdvisoryFetch);
        tracker.Observe("added 584 packages in 3m");
        tracker.Observe("");
        tracker.Observe("npm http fetch GET 200 https://registry.npmjs.org/pkg 12ms");

        Assert.AreEqual(0, tracker.Seen,
            "only tarball fetches count; metadata would push the bar past its total");
    }

    [TestMethod]
    public void FailedFetchesDoNotCountAsProgress()
    {
        var reports = new List<EngineProgress>();
        var tracker = MakeTracker(100, reports);

        tracker.Observe("npm http fetch GET 404 https://registry.npmjs.org/gone/-/gone-1.0.0.tgz 40ms");
        tracker.Observe("npm http fetch GET 500 https://registry.npmjs.org/bad/-/bad-1.0.0.tgz 90ms");

        Assert.AreEqual(0, tracker.Seen, "npm retries failed fetches, so they are not progress");
    }

    [TestMethod]
    public void ReportsTheFirstPackageImmediately()
    {
        var reports = new List<EngineProgress>();
        var tracker = MakeTracker(584, reports);

        tracker.Observe(TarballFetch);

        Assert.AreEqual(1, reports.Count, "the first package should report at once, not after a delay");
        Assert.AreEqual(EnginePhase.Downloading, reports[0].Phase);
        Assert.AreEqual(1, reports[0].Done);
        Assert.AreEqual(584, reports[0].Total);
        Assert.IsTrue(reports[0].IsDeterminate);
    }

    [TestMethod]
    public void ThrottlesWhenNoClockAdvances()
    {
        var reports = new List<EngineProgress>();
        // Frozen clock: only the package-count jump may trigger a report.
        var tracker = new NpmProgressTracker(1000, reports.Add, () => TimeSpan.Zero);

        for (var i = 0; i < 7; i++)
            tracker.Observe($"npm http fetch GET 200 https://r.test/p{i}/-/p{i}-1.0.0.tgz 10ms");

        Assert.AreEqual(1, reports.Count, "7 packages is below the jump threshold; only the first reports");
        Assert.AreEqual(7, tracker.Seen, "but every package is still counted");
    }

    [TestMethod]
    public void ReportsAgainOnceEnoughPackagesArrive()
    {
        var reports = new List<EngineProgress>();
        var tracker = new NpmProgressTracker(1000, reports.Add, () => TimeSpan.Zero);

        for (var i = 0; i < 10; i++)
            tracker.Observe($"npm http fetch GET 200 https://r.test/p{i}/-/p{i}-1.0.0.tgz 10ms");

        // The jump is measured from the last *reported* count (1), and the
        // threshold is ">= 8", so the second report lands at 9 rather than 10.
        Assert.AreEqual(2, reports.Count, "crossing the jump threshold should report again");
        Assert.AreEqual(9, reports[^1].Done, "the second report lands where the threshold is crossed");
    }

    [TestMethod]
    public void FlushReportsTheFinalCountEvenWhenThrottled()
    {
        var reports = new List<EngineProgress>();
        var tracker = new NpmProgressTracker(1000, reports.Add, () => TimeSpan.Zero);
        for (var i = 0; i < 5; i++)
            tracker.Observe($"npm http fetch GET 200 https://r.test/p{i}/-/p{i}-1.0.0.tgz 10ms");

        tracker.Flush();

        Assert.AreEqual(5, reports[^1].Done, "the bar must land on the real count");
    }

    [TestMethod]
    public void FractionIsClampedWhenNpmFetchesMoreThanExpected()
    {
        // npm can legitimately exceed the estimate (optional deps, retries),
        // which must not render a bar wider than its track.
        var over = new EngineProgress(EnginePhase.Downloading, 700, 584);
        Assert.AreEqual(1.0, over.Fraction, 0.0001);

        var none = new EngineProgress(EnginePhase.Downloading, 0, 584);
        Assert.AreEqual(0.0, none.Fraction, 0.0001);
    }

    [TestMethod]
    public void NonDownloadPhasesAreIndeterminate()
    {
        Assert.IsFalse(new EngineProgress(EnginePhase.Resolving, 0, 584).IsDeterminate);
        Assert.IsFalse(new EngineProgress(EnginePhase.Finishing, 584, 584).IsDeterminate);
    }

    [TestMethod]
    public void ZeroExpectedTotalFallsBackToTheDefault()
    {
        var reports = new List<EngineProgress>();
        var tracker = MakeTracker(0, reports);
        tracker.Observe(TarballFetch);

        Assert.AreEqual(NpmProgressTracker.DefaultExpectedPackages, reports[0].Total);
    }

    [TestMethod]
    public void DoesNotReportStalledBeforeAnyPackageArrives()
    {
        var clock = TimeSpan.Zero;
        var reports = new List<EngineProgress>();
        var tracker = new NpmProgressTracker(584, reports.Add, () => clock);

        clock = TimeSpan.FromSeconds(30); // a long silent "resolving" phase

        Assert.IsFalse(tracker.IsStalled(),
            "nothing has been counted yet, so there is no count to distrust");
    }

    [TestMethod]
    public void ReportsStalledOncePackagesStopArriving()
    {
        var clock = TimeSpan.Zero;
        var reports = new List<EngineProgress>();
        var tracker = new NpmProgressTracker(584, reports.Add, () => clock);

        // A cache-served update: three packages, then silence.
        for (var i = 0; i < 3; i++)
            tracker.Observe($"npm http fetch GET 200 https://r.test/p{i}/-/p{i}-1.0.0.tgz 10ms");

        Assert.IsFalse(tracker.IsStalled(), "the count is still fresh");

        clock = TimeSpan.FromSeconds(3);

        Assert.IsTrue(tracker.IsStalled(),
            "3 / 584 sitting still means the expected total does not describe this run");
    }

    [TestMethod]
    public void ANewPackageClearsTheStalledState()
    {
        var clock = TimeSpan.Zero;
        var reports = new List<EngineProgress>();
        var tracker = new NpmProgressTracker(584, reports.Add, () => clock);

        tracker.Observe("npm http fetch GET 200 https://r.test/a/-/a-1.0.0.tgz 10ms");
        clock = TimeSpan.FromSeconds(3);
        Assert.IsTrue(tracker.IsStalled());

        tracker.Observe("npm http fetch GET 200 https://r.test/b/-/b-1.0.0.tgz 10ms");

        Assert.IsFalse(tracker.IsStalled(), "a fresh package means downloads are still moving");
    }

    [TestMethod]
    public void ApplyingPhaseIsIndeterminate()
    {
        var progress = new EngineProgress(EnginePhase.Applying, 3, 584);
        Assert.IsFalse(progress.IsDeterminate,
            "the bar must go indeterminate rather than freeze at a wrong fraction");
    }

    [TestMethod]
    public void FlushDoesNotUndoTheIndeterminateState()
    {
        // Observed in a real run: the final flush arrived after the stall notice
        // and reported "downloading 3 / 584" again, yanking the bar back to a
        // fraction already known to be wrong.
        var clock = TimeSpan.Zero;
        var reports = new List<EngineProgress>();
        var tracker = new NpmProgressTracker(584, reports.Add, () => clock);

        tracker.Observe("npm http fetch GET 200 https://r.test/a/-/a-1.0.0.tgz 10ms");
        clock = TimeSpan.FromSeconds(3);
        tracker.ReportApplying();
        tracker.Flush();

        Assert.AreEqual(EnginePhase.Applying, reports[^1].Phase,
            "once the count is abandoned, the final report must stay indeterminate");
        Assert.IsFalse(reports[^1].IsDeterminate);
    }

    [TestMethod]
    public void FlushStaysDeterminateWhenTheCounterWasTrustworthy()
    {
        var reports = new List<EngineProgress>();
        var tracker = new NpmProgressTracker(584, reports.Add, () => TimeSpan.Zero);
        for (var i = 0; i < 4; i++)
            tracker.Observe($"npm http fetch GET 200 https://r.test/p{i}/-/p{i}-1.0.0.tgz 10ms");

        tracker.Flush();

        Assert.AreEqual(EnginePhase.Downloading, reports[^1].Phase,
            "a healthy download should end on its real count");
        Assert.AreEqual(4, reports[^1].Done);
    }
}
