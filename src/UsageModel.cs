using System;
using System.Collections.Generic;
using System.Linq;

namespace UsageBars;

/// <summary>What the clickable icon on the pill offers, if anything.</summary>
internal enum PillAction
{
    None,

    /// <summary>No Claude Code CLI, so there's no fallback source: link to the install docs.</summary>
    InstallClaudeCode,

    /// <summary>No Codex CLI, so the app-server can't be reached: link to the install docs.</summary>
    InstallCodex,
}

internal sealed record UsageBar(string Label, double? RemainingPct)
{
    public bool ShowInPill { get; init; } = true;
    /// <summary>When this window next resets, when that's known.</summary>
    public DateTimeOffset? ResetsAt { get; init; }

    /// <summary>The provider's nominal window length, used to normalize the reset pointer.</summary>
    public TimeSpan? WindowDuration { get; init; }

    /// <summary>Provider-reported reset text when no timestamp has been parsed.</summary>
    public string? ReportedResetText { get; init; }

    /// <summary>
    /// When this bar is projected to reach 0 % at the rate it is currently moving, or null when
    /// there isn't enough history to say.
    ///
    /// Distinct from <see cref="ResetsAt"/>, and the interesting question is which of the two lands
    /// first: a window that refills before you empty it needs no attention, and one that doesn't
    /// needs it now.
    /// </summary>
    public DateTimeOffset? EmptyAt { get; init; }

    /// <summary>Measured burn rate behind <see cref="EmptyAt"/>, in points per hour.</summary>
    public double? PercentPerHour { get; init; }
}

/// <summary>
/// Merges the two possible sources into what the overlay draws. The local JSON file wins when it
/// carries fresh values; the scraped Claude Code meters are the fallback when the file
/// is empty or stops updating.
/// </summary>
internal sealed record UsageModel
{
    public IReadOnlyList<UsageBar> Bars { get; init; } = Array.Empty<UsageBar>();

    public bool Stale { get; init; }

    /// <summary>Time of the displayed sample, or of our successful live reading.</summary>
    public DateTimeOffset? ReadingAt { get; init; }

    public PillAction Action { get; init; }

    public string? ActionUrl { get; init; }

    /// <summary>Human-readable source description, for the tray tooltip and diagnostics.</summary>
    public string SourceText { get; init; } = "";

    public string StatusText { get; init; } = "";

    /// <summary>
    /// Per-bar projected time to 0 % at the current burn rate, or empty when nothing can be
    /// projected yet.
    ///
    /// Retained for diagnostics; the tray only shows the current remaining percentages.
    /// </summary>
    public string ProjectionText { get; init; } = "";

    /// <summary>Burn rate / projection behind the warning decision, when one was computed.</summary>
    public VelocityEstimate? Velocity { get; init; }

    /// <summary>The legacy low-usage estimate, for diagnostics.</summary>
    public string? WarnReason { get; init; }

    /// <summary>
    /// Whether the diagnostic low-usage estimate warns. Pill colours are determined by the
    /// distance from the reset pointer, independently of this estimate.
    /// </summary>
    public bool Warned { get; init; }

    /// <summary>Soonest known reset across the bars, or null when it isn't knowable.</summary>
    public DateTimeOffset? NextReset { get; init; }

    /// <summary>How <see cref="NextReset"/> was arrived at — measured, configured, or unknown.</summary>
    public string ResetBasis { get; init; } = "unknown";

    public bool HasValues
    {
        get
        {
            foreach (var bar in Bars)
                if (bar.RemainingPct is not null) return true;
            return false;
        }
    }

    public static UsageModel Build(
        UsageSnapshot file,
        ClaudeCodeUsage? scraped,
        UsageHistory history,
        Config config)
    {
        var buildNow = DateTimeOffset.Now;

        // Preferred: the local file, when it actually has utilization values.
        if (file.HasValues && (!NeedsClaudeFallback(file, config) ||
            scraped is not { HasValue: true } || scraped.ReadAt is null ||
            (file.SampleTime is { } sampled && scraped.ReadAt <= sampled)))
        {
            // The file's own sample series is what makes these two projectable: several days of
            // five-minute readings, already on disk.
            var bars = new List<UsageBar>();
            if (file.FiveHourUsedPct is { } fh)
                bars.Add(WithProjection(
                    new UsageBar("5h", 100 - fh) { ResetsAt = file.FiveHourResetsAt, WindowDuration = ResetEstimator.FiveHourWindow },
                    file.FiveHourSeries, config, buildNow));
            if (file.SevenDayUsedPct is { } sd)
                bars.Add(WithProjection(
                    new UsageBar("7d", 100 - sd) { ResetsAt = file.SevenDayResetsAt, WindowDuration = ResetEstimator.SevenDayWindow },
                    file.SevenDaySeries, config, buildNow));

            var lowest = double.MaxValue;
            foreach (var bar in bars)
                if (bar.RemainingPct is { } r && r < lowest) lowest = r;

            // The 5h/7d windows roll continuously and carry their own horizon, so the weekly
            // velocity model doesn't apply here — the static floor is the right trigger.
            var low = lowest <= config.LowThresholdPct;
            var stale = file.IsStaleAfter(TimeSpan.FromMinutes(config.FileStaleMinutes));

            return new UsageModel
            {
                Bars = bars,
                Stale = stale,
                Warned = low,
                SourceText = file.SourceLabel ?? "local file",
                ReadingAt = file.SampleTime,
                StatusText = Describe(bars, stale),
                ProjectionText = DescribeProjections(bars, buildNow),
                NextReset = file.NextReset,
                ResetBasis = file.NextReset is null
                    ? "not derivable from retained samples"
                    : "measured from usage data",
                WarnReason = low
                    ? $"rolling window at or below floor of {config.LowThresholdPct:0}%"
                    : "rolling windows use the static floor, not velocity",
            };
        }

        if (scraped is { HasValue: true, Windows.Count: > 0 })
        {
            var bars = new List<UsageBar>();
            var lowest = 100.0;
            foreach (var window in scraped.Windows)
            {
                var remaining = 100 - window.UsedPct;
                lowest = Math.Min(lowest, remaining);
                var duration = window.WindowDuration ?? (window.Label switch
                {
                    "5h" => ResetEstimator.FiveHourWindow,
                    "7d" => ResetEstimator.SevenDayWindow,
                    _ => (TimeSpan?)null,
                });
                bars.Add(new UsageBar(window.Label, remaining)
                {
                    ReportedResetText = window.Detail,
                    ResetsAt = duration is { } length
                        ? ResetEstimator.ParseWindowReset(window.Detail, scraped.ReadAt ?? buildNow, length) : null,
                    WindowDuration = duration,
                    ShowInPill = !window.Label.Equals("Fable", StringComparison.OrdinalIgnoreCase),
                });
            }
            var stale = scraped.ReadAt is null ||
                buildNow - scraped.ReadAt.Value > TimeSpan.FromHours(config.ReadingStaleHours);
            var low = lowest <= config.LowThresholdPct;
            return new UsageModel
            {
                Bars = bars,
                Stale = stale,
                Warned = low,
                SourceText = "claude-code /usage",
                ReadingAt = scraped.ReadAt,
                StatusText = Describe(bars, stale),
                NextReset = bars.Where(b => b.ResetsAt is not null).Select(b => b.ResetsAt).Min(),
                ResetBasis = bars.Any(b => b.ResetsAt is not null) ? "parsed from reported window reset" : "reported in meter text; timestamp not parsed",
                WarnReason = low ? "rolling window at or below floor" : "rolling windows use the static floor",
            };
        }

        // Fallback: the scraped Claude Code credit meter, with a velocity-based warning.
        if (scraped is { HasValue: true })
        {
            var now = buildNow;
            var remaining = scraped.RemainingPct!.Value;

            // There is no sample history behind the scrape, so the meter's own secondary line is
            // the only place a reset or expiry can come from.
            var scrapedReset = ResetEstimator.ParseMeterDetail(scraped.MeterDetail, now);
            var bars = new List<UsageBar>
            {
                WithProjection(
                    new UsageBar(scraped.ShortLabel, remaining) { ResetsAt = scrapedReset },
                    history.Points(), config, now),
            };

            // The scrape is only as fresh as its last successful run.
            var stale = scraped.ReadAt is null
                || now - scraped.ReadAt.Value > TimeSpan.FromHours(config.ReadingStaleHours);

            var velocity = UsageVelocity.Estimate(history, remaining, config, scrapedReset);
            var warn = UsageVelocity.ShouldWarn(velocity, remaining, config, out var why);

            return new UsageModel
            {
                Bars = bars,
                Stale = stale,
                Warned = warn,
                SourceText = scraped.MeterLabel is null
                    ? "claude-code /usage"
                    : $"claude-code /usage · {scraped.MeterLabel}",
                ReadingAt = scraped.ReadAt,
                StatusText = Describe(bars, stale),
                ProjectionText = DescribeProjections(bars, now),
                Velocity = velocity,
                NextReset = velocity.NextReset,
                ResetBasis = velocity.ResetBasis,
                WarnReason = why,
            };
        }

        // Nothing to show. Offer the install link when that's the actual reason.
        var notInstalled = scraped is { ClaudeInstalled: false };
        return new UsageModel
        {
            Bars = new List<UsageBar> { new("usage", null) },
            Stale = true,
            Action = notInstalled ? PillAction.InstallClaudeCode : PillAction.None,
            ActionUrl = notInstalled ? ClaudeCli.InstallUrl : null,
            SourceText = notInstalled ? "Claude Code not installed" : "no data",
            StatusText = notInstalled
                ? "Claude Code not installed — click ! to install"
                : scraped?.Error ?? file.Error ?? "no usage data available",
        };
    }

    internal static bool NeedsClaudeFallback(UsageSnapshot file, Config config) =>
        !file.HasValues || file.SampleTime is null ||
        file.IsStaleAfter(TimeSpan.FromMinutes(config.FileStaleMinutes));

    /// <summary>
    /// Key under which a Codex window's readings are accumulated in <see cref="BarHistory"/>.
    /// Namespaced by provider so a Claude window of the same name could never share a series.
    /// </summary>
    public static string CodexHistoryKey(string label) => "codex:" + label;

    /// <summary>
    /// Builds the Codex pill from an app-server snapshot.
    ///
    /// The app-server reports a real <c>resetsAt</c> for every window, so nothing here has to
    /// *derive* a deadline the way the Claude side does. The burn-rate projection is a different
    /// question and still applies: when a window refills is not the same as when you empty it, and
    /// only the second one tells you to slow down. Codex reports no history of its own, so that
    /// series comes from <paramref name="history"/> — readings UsageBars has watched go by.
    /// </summary>
    public static UsageModel BuildCodex(
        CodexSnapshot? snapshot,
        bool codexInstalled,
        string? error,
        Config config,
        BarHistory? history = null)
    {
        if (!codexInstalled)
        {
            return new UsageModel
            {
                Bars = new List<UsageBar> { new("codex", null) },
                Stale = true,
                Action = PillAction.InstallCodex,
                ActionUrl = CodexCli.InstallUrl,
                SourceText = "Codex CLI not installed",
                StatusText = "Codex CLI not installed — click ! to install",
            };
        }

        if (snapshot is not { HasValues: true })
        {
            return new UsageModel
            {
                Bars = new List<UsageBar> { new("codex", null) },
                Stale = true,
                SourceText = "codex app-server",
                StatusText = error ?? "no Codex usage data available",
            };
        }

        var now = DateTimeOffset.Now;
        var bars = new List<UsageBar>();

        UsageBar Projected(UsageBar bar) =>
            WithProjection(bar, history?.Points(CodexHistoryKey(bar.Label)), config, now);

        if (snapshot.Primary is { } primary)
            bars.Add(Projected(
                new UsageBar(primary.Label, primary.RemainingPct)
                {
                    ResetsAt = primary.ResetsAt,
                    WindowDuration = primary.WindowMinutes is > 0 ? TimeSpan.FromMinutes(primary.WindowMinutes.Value) : null,
                }));
        if (snapshot.Secondary is { } secondary)
            bars.Add(Projected(
                new UsageBar(secondary.Label, secondary.RemainingPct)
                {
                    ResetsAt = secondary.ResetsAt,
                    WindowDuration = secondary.WindowMinutes is > 0 ? TimeSpan.FromMinutes(secondary.WindowMinutes.Value) : null,
                }));

        // On usage-based and enterprise seats the spend budget is the binding constraint, exactly
        // as the credit meter is on the Claude side.
        if (snapshot.SpendLimit is { } spend)
            bars.Add(Projected(new UsageBar("spend", spend.RemainingPct) { ResetsAt = spend.ResetsAt }));

        var lowest = double.MaxValue;
        foreach (var bar in bars)
            if (bar.RemainingPct is { } r && r < lowest) lowest = r;

        var low = lowest <= config.LowThresholdPct;
        var stale = now - snapshot.ReadAt > TimeSpan.FromHours(config.ReadingStaleHours);

        var status = Describe(bars, stale);
        if (snapshot.LimitReached is { Length: > 0 } reached)
            status += $" · {reached.Replace('_', ' ')}";

        return new UsageModel
        {
            Bars = bars,
            Stale = stale,
            Warned = low,
            SourceText = snapshot.PlanType is { Length: > 0 } plan
                ? $"codex app-server · {plan}"
                : "codex app-server",
            ReadingAt = snapshot.ReadAt,
            StatusText = status,
            ProjectionText = DescribeProjections(bars, now),
            NextReset = snapshot.NextReset,
            ResetBasis = snapshot.NextReset is null
                ? "not reported by the app-server"
                : "reported by the app-server",
            WarnReason = low
                ? $"at or below floor of {config.LowThresholdPct:0}%"
                : $"lowest window at {(lowest is double.MaxValue ? 0 : lowest):0}% remaining",
        };
    }

    private static string Describe(IReadOnlyList<UsageBar> bars, bool stale)
    {
        var parts = new List<string>();
        foreach (var bar in bars)
        {
            parts.Add(bar.RemainingPct is { } r
                ? $"{bar.Label} {Math.Round(r)}% left"
                : $"{bar.Label} --");
        }
        var text = string.Join(", ", parts);
        return stale ? text + " (stale)" : text;
    }

    /// <summary>
    /// Attaches a 0 % projection to a bar when its series supports one, and leaves it untouched
    /// when it doesn't. Every source funnels through here so a projection means the same thing —
    /// and is guarded the same way — whichever bar it appears on.
    /// </summary>
    private static UsageBar WithProjection(
        UsageBar bar, IReadOnlyList<UsagePoint>? series, Config config, DateTimeOffset now)
    {
        if (bar.RemainingPct is not { } remaining) return bar;
        if (UsageVelocity.Project(series, remaining, config, now) is not { } projection) return bar;

        return bar with
        {
            EmptyAt = projection.EmptyAt,
            PercentPerHour = projection.PercentPerHour,
        };
    }

    /// <summary>
    /// Per-bar projected time to 0 %, for diagnostics. Silent for bars with too little history.
    /// </summary>
    private static string DescribeProjections(IReadOnlyList<UsageBar> bars, DateTimeOffset now)
    {
        var parts = new List<string>();
        foreach (var bar in bars)
        {
            if (bar.EmptyAt is not { } empty) continue;
            parts.Add($"{bar.Label} 0% at {ResetEstimator.Describe(empty, now)}");
        }
        return string.Join(", ", parts);
    }

    /// <summary>
    /// Hover text for the pill: known resets and the age of the displayed reading.
    /// </summary>
    public string? ResetTooltip(DateTimeOffset now)
    {
        var lines = new List<string>();
        foreach (var bar in Bars)
        {
            if (!bar.ShowInPill) continue;
            if (!string.IsNullOrWhiteSpace(bar.ReportedResetText))
                lines.Add($"{bar.Label} {bar.ReportedResetText}");
            else if (bar.ResetsAt is { } reset)
                lines.Add($"{bar.Label} resets {ResetEstimator.Describe(reset, now)}");
        }
        if (ReadingAt is { } readingAt)
        {
            var age = now - readingAt;
            if (age < TimeSpan.Zero) age = TimeSpan.Zero;
            var text = age.TotalDays >= 1 ? $"{(int)age.TotalDays}d {age.Hours}h"
                : age.TotalHours >= 1 ? $"{(int)age.TotalHours}h {age.Minutes}m"
                : age.TotalMinutes >= 1 ? $"{(int)age.TotalMinutes}m {age.Seconds}s"
                : $"{age.Seconds}s";
            lines.Add($"Reading age: {text}");
        }
        return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    public UsageModel ForPill() => this with { Bars = Bars.Where(bar => bar.ShowInPill).ToArray() };

}
