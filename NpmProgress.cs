using System.Text.RegularExpressions;

namespace DshShell;

/// <summary>
/// Turns npm's verbose log into download progress.
///
/// Why this is needed at all: the shell installs the engine with npm, which is
/// ~584 packages / ~223 MB on first run. At npm's default log level that
/// produces <em>no output until it finishes</em> - the splash would sit on one
/// static line for minutes with no way to tell progress from a hang. Raising the
/// log level to <c>http</c> yields one line per request, which is the only
/// granular signal npm offers when its output is redirected.
///
/// It is deliberately approximate. npm has no aggregate progress to report, so
/// progress is "tarballs I have seen fetched" over an expected total, and the
/// bar may finish a little short of 100%. Showing a moving number was judged
/// more useful than showing nothing for a 223 MB download.
/// </summary>
internal sealed class NpmProgressTracker
{
    /// <summary>Engine dependency count measured from a real install (lockfile entries).</summary>
    public const int DefaultExpectedPackages = 584;

    /// <summary>At most one report per this interval, so 584 lines do not become 584 UI updates.</summary>
    private static readonly TimeSpan MinReportInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>Larger jumps always report immediately, so a fast install still animates.</summary>
    private const int MinPackageDelta = 8;

    /// <summary>
    /// npm's http log line, e.g.
    /// "npm http fetch GET 200 registry.npmjs.org/foo/-/foo-1.0.0.tgz 105ms (cache revalidated)".
    /// Only a tarball fetch counts: metadata GETs and advisory lookups would
    /// inflate the count past the package total.
    /// </summary>
    private static readonly Regex FetchLine = new(
        @"npm http (?:\w+ )?fetch \w+ (?<status>\d{3}) (?<url>\S+\.tgz)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly HashSet<string> _seenTarballs = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _expected;
    private readonly Action<EngineProgress> _report;
    private readonly Func<TimeSpan> _clock;
    private TimeSpan _lastReportedAt = TimeSpan.MinValue;
    private int _lastReportedCount = -1;
    private int _observed;
    private TimeSpan _lastFetchAt = TimeSpan.MinValue;
    private bool _applying;

    /// <summary>
    /// How long without a new package before the expected total is judged
    /// unusable. A routine `@latest` update often fetches only a handful of
    /// packages (the rest come from npm's cache), so "3 / 584" would sit there
    /// looking stalled while npm is in fact nearly done. Past this quiet period
    /// the bar gives up on the number instead of showing a misleading one.
    /// </summary>
    private static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(2.5);

    public NpmProgressTracker(int expected, Action<EngineProgress> report, Func<TimeSpan>? clock = null)
    {
        _expected = expected > 0 ? expected : DefaultExpectedPackages;
        _report = report;
        _clock = clock ?? (() => TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    /// <summary>Packages counted so far (unique tarballs, not lines).</summary>
    public int Seen => _observed;

    /// <summary>
    /// Feeds one npm output line. Reports only when the count actually moved and
    /// either enough time passed or enough packages arrived.
    /// </summary>
    public void Observe(string line)
    {
        if (string.IsNullOrEmpty(line)) return;

        var match = FetchLine.Match(line);
        if (!match.Success) return;

        // A failed fetch (404/500) must not count as progress; npm retries it.
        if (!int.TryParse(match.Groups["status"].Value, out var status) || status >= 400) return;

        if (!_seenTarballs.Add(match.Groups["url"].Value)) return;

        _observed++;
        _lastFetchAt = _clock();
        var now = _lastFetchAt;
        var due = _lastReportedAt == TimeSpan.MinValue
                  || now - _lastReportedAt >= MinReportInterval
                  || _observed - _lastReportedCount >= MinPackageDelta;
        if (!due) return;

        _lastReportedAt = now;
        _lastReportedCount = _observed;
        _report(new EngineProgress(Phase: EnginePhase.Downloading, Done: _observed, Total: _expected));
    }

    /// <summary>
    /// True once downloads have gone quiet long enough that the expected total
    /// is clearly not the real one - typically an update served from cache.
    /// </summary>
    public bool IsStalled()
    {
        if (_observed == 0) return false;
        var since = _clock() - _lastFetchAt;
        return since >= StallThreshold;
    }

    /// <summary>Reports the indeterminate "applying" phase, used once the counter is untrustworthy.</summary>
    public void ReportApplying()
    {
        _applying = true;
        _report(new EngineProgress(Phase: EnginePhase.Applying, Done: _observed, Total: _expected));
    }

    /// <summary>
    /// Forces a final report, so the bar lands on the real count even if the last
    /// observations were throttled away. Stays indeterminate once the counter has
    /// been abandoned - reporting the download count again would yank the bar
    /// back to a fraction we already know is wrong.
    /// </summary>
    public void Flush() =>
        _report(_applying
            ? new EngineProgress(Phase: EnginePhase.Applying, Done: _observed, Total: _expected)
            : new EngineProgress(Phase: EnginePhase.Downloading, Done: _observed, Total: _expected));
}

/// <summary>Which part of the install/update pipeline is running.</summary>
internal enum EnginePhase
{
    /// <summary>Resolving the dependency graph; npm gives no progress signal here.</summary>
    Resolving,
    /// <summary>Fetching packages; countable.</summary>
    Downloading,
    /// <summary>
    /// Downloads went quiet while npm is still running: the expected total does
    /// not describe this run (an update mostly served from cache), so the bar
    /// goes indeterminate rather than freezing at a wrong fraction.
    /// </summary>
    Applying,
    /// <summary>Writing files into place; not countable.</summary>
    Finishing,
}

/// <summary>
/// One progress observation. <see cref="Done"/>/<see cref="Total"/> are only
/// meaningful for <see cref="EnginePhase.Downloading"/>; the other phases are
/// indeterminate on purpose rather than faking a percentage.
/// </summary>
internal readonly record struct EngineProgress(EnginePhase Phase, int Done, int Total)
{
    /// <summary>True when a determinate bar can be drawn.</summary>
    public bool IsDeterminate => Phase == EnginePhase.Downloading && Total > 0;

    /// <summary>Completion in [0,1], clamped: npm can fetch more or fewer than expected.</summary>
    public double Fraction => IsDeterminate ? Math.Clamp((double)Done / Total, 0, 1) : 0;
}
