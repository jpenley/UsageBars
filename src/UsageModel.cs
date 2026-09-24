using System;
using System.Collections.Generic;

namespace UsageBars;

/// <summary>What the clickable icon on the pill offers, if anything.</summary>
internal enum PillAction
{
    None,

    /// <summary>Meter is nearly empty: link out to request more credit.</summary>
    RequestCredit,

    /// <summary>No Claude Code CLI, so there's no fallback source: link to the install docs.</summary>
    InstallClaudeCode,

    /// <summary>No Codex CLI, so the app-server can't be reached: link to the install docs.</summary>
    InstallCodex,
}

internal sealed record UsageBar(string Label, double? RemainingPct)
{
    /// <summary>When this window next resets, when that's known. Shown in the tray tooltip.</summary>
    public DateTimeOffset? ResetsAt { get; init; }

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
/// carries values (free and updated every ~5 min); the scraped Claude Code meter is the fallback
/// for accounts where that file stays empty.
/// </summary>
internal sealed record UsageModel
{
    public IReadOnlyList<UsageBar> Bars { get; init; } = Array.Empty<UsageBar>();

    public bool Stale { get; init; }

    public PillAction Action { get; init; }

    public string? ActionUrl { get; init; }

    /// <summary>Human-readable source description, for the tray tooltip and diagnostics.</summary>
    public string SourceText { get; init; } = "";

    public string StatusText { get; init; } = "";

    /// <summary>
    /// Per-bar projected time to 0 % at the current burn rate, or empty when nothing can be
    /// projected yet.
    ///
    /// Held apart from <see cref="StatusText"/> and given its own line in the tray menu because it
    /// is a prediction rather than a reading. Folding it into the status line made an already long
    /// line longer while hiding exactly the distinction that matters.
    /// </summary>
    public string ProjectionText { get; init; } = "";

    /// <summary>Burn rate / projection behind the warning decision, when one was computed.</summary>
    public VelocityEstimate? Velocity { get; init; }

    /// <summary>Why the icon is or isn't showing, for diagnostics.</summary>
    public string? WarnReason { get; init; }

    /// <summary>
    /// Whether usage is low enough to warrant a warning, independently of whether an action icon
    /// is offered.
    ///
    /// Kept separate from <see cref="Action"/> because the icon can be withheld — off-domain users
    /// aren't offered a workplace-internal link — and the warning itself must survive that. The
    /// bar still turns red; only the clickable affordance goes away.
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
        var requestCreditUrl = config.RequestCreditUrl;

        // Only offered when a link is configured, and — since a configured link may be
        // workplace-internal — only to users on the configured domain. The warning itself is never
        // withheld — see UsageModel.Warned.
        var hasUrl = !string.IsNullOrWhiteSpace(requestCreditUrl);
        var onDomain = UserDomain.Matches(config.RequestCreditDomain);
        var offerCredit = hasUrl && onDomain;
        var withheld = !hasUrl
            ? "; no RequestCreditUrl configured"
            : !onDomain ? $"; icon withheld off {config.RequestCreditDomain} domain" : "";

        var buildNow = DateTimeOffset.Now;

        // Preferred: the local file, when it actually has utilization values.
        if (file.HasValues)
        {
            // The file's own sample series is what makes these two projectable: several days of
            // five-minute readings, already on disk.
            var bars = new List<UsageBar>();
            if (file.FiveHourUsedPct is { } fh)
                bars.Add(WithProjection(
                    new UsageBar("5h", 100 - fh) { ResetsAt = file.FiveHourResetsAt },
                    file.FiveHourSeries, config, buildNow));
            if (file.SevenDayUsedPct is { } sd)
                bars.Add(WithProjection(
                    new UsageBar("7d", 100 - sd) { ResetsAt = file.SevenDayResetsAt },
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
                Action = low && offerCredit ? PillAction.RequestCredit : PillAction.None,
                ActionUrl = low && offerCredit ? requestCreditUrl : null,
                SourceText = file.SourceLabel ?? "local file",
                StatusText = Describe(bars, stale) + DescribeResets(bars),
                ProjectionText = DescribeProjections(bars, buildNow),
                NextReset = file.NextReset,
                ResetBasis = file.NextReset is null
                    ? "not derivable from retained samples"
                    : "measured from usage data",
                WarnReason = low
                    ? $"rolling window at or below floor of {config.LowThresholdPct:0}%" + withheld
                    : "rolling windows use the static floor, not velocity",
            };
        }

        // Fallback: the scraped Claude Code meter, with a velocity-based warning.
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

            var status = Describe(bars, stale);
            var rate = velocity.Describe();
            if (!string.IsNullOrEmpty(rate)) status += $" · {rate}";

            if (velocity.NextReset is { } reset)
                status += $" · resets {ResetEstimator.Describe(reset, now)}";

            return new UsageModel
            {
                Bars = bars,
                Stale = stale,
                Warned = warn,
                Action = warn && offerCredit ? PillAction.RequestCredit : PillAction.None,
                ActionUrl = warn && offerCredit ? requestCreditUrl : null,
                SourceText = scraped.MeterLabel is null
                    ? "claude-code /usage"
                    : $"claude-code /usage · {scraped.MeterLabel}",
                StatusText = status +
                             (scraped.MeterDetail is null ? "" : $" · {scraped.MeterDetail}"),
                ProjectionText = DescribeProjections(bars, now),
                Velocity = velocity,
                NextReset = velocity.NextReset,
                ResetBasis = velocity.ResetBasis,
                WarnReason = warn ? why + withheld : why,
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
                ? "Claude Code not installed — click + to install"
                : scraped?.Error ?? file.Error ?? "no usage data available",
        };
    }

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
                new UsageBar(primary.Label, primary.RemainingPct) { ResetsAt = primary.ResetsAt }));
        if (snapshot.Secondary is { } secondary)
            bars.Add(Projected(
                new UsageBar(secondary.Label, secondary.RemainingPct) { ResetsAt = secondary.ResetsAt }));

        // On usage-based and enterprise seats the spend budget is the binding constraint, exactly
        // as the credit meter is on the Claude side.
        if (snapshot.SpendLimit is { } spend)
            bars.Add(Projected(new UsageBar("spend", spend.RemainingPct) { ResetsAt = spend.ResetsAt }));

        var lowest = double.MaxValue;
        foreach (var bar in bars)
            if (bar.RemainingPct is { } r && r < lowest) lowest = r;

        var low = lowest <= config.LowThresholdPct;
        var stale = now - snapshot.ReadAt > TimeSpan.FromHours(config.ReadingStaleHours);

        // Only offer a link if one is configured — unlike Claude's credit runbook there is no
        // universal Codex "get more" URL, and an icon that goes nowhere is worse than no icon.
        // The same domain restriction applies, for the same reason.
        var url = string.IsNullOrWhiteSpace(config.CodexRequestCreditUrl)
                  || !UserDomain.Matches(config.RequestCreditDomain)
            ? null
            : config.CodexRequestCreditUrl;

        var status = Describe(bars, stale) + DescribeResets(bars);
        if (snapshot.ResetCreditsAvailable > 0)
            status += $" · {snapshot.ResetCreditsAvailable} reset credit(s) available";
        if (snapshot.LimitReached is { Length: > 0 } reached)
            status += $" · {reached.Replace('_', ' ')}";

        return new UsageModel
        {
            Bars = bars,
            Stale = stale,
            Warned = low,
            Action = low && url is not null ? PillAction.RequestCredit : PillAction.None,
            ActionUrl = low ? url : null,
            SourceText = snapshot.PlanType is { Length: > 0 } plan
                ? $"codex app-server · {plan}"
                : "codex app-server",
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
    /// Per-bar projected time to 0 %, for the tray menu's own line. Silent for bars with too little
    /// history — an empty line is better than a row of "unknown", and matches how
    /// <see cref="DescribeResets"/> handles the same situation.
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
    /// Hover text for the pill: one line per bar whose reset is known, or null when none is, so a
    /// pill with nothing to say shows no tooltip rather than a row of "unknown".
    /// </summary>
    public string? ResetTooltip(DateTimeOffset now)
    {
        var lines = new List<string>();
        foreach (var bar in Bars)
        {
            if (bar.ResetsAt is not { } reset) continue;
            lines.Add($"{bar.Label} resets {ResetEstimator.Describe(reset, now)}");
        }
        return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Per-bar reset deadlines, appended to the status line. Silent when nothing is known, so an
    /// account whose history is too short doesn't get a row of "unknown".
    /// </summary>
    private static string DescribeResets(IReadOnlyList<UsageBar> bars)
    {
        var now = DateTimeOffset.Now;
        var parts = new List<string>();
        foreach (var bar in bars)
        {
            if (bar.ResetsAt is not { } reset) continue;
            parts.Add($"{bar.Label} resets {ResetEstimator.Describe(reset, now)}");
        }
        return parts.Count == 0 ? "" : " · " + string.Join(", ", parts);
    }
}
