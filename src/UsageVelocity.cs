using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UsageBars;

/// <summary>One recorded observation of the meter.</summary>
internal sealed class UsageSample
{
    [JsonPropertyName("t")]
    public DateTimeOffset At { get; set; }

    /// <summary>Percent consumed at that moment (0-100).</summary>
    [JsonPropertyName("u")]
    public double UsedPct { get; set; }
}

/// <summary>
/// Rolling history of meter readings, persisted so burn rate survives a restart. Also carries the
/// last label/detail so the pill can show a number immediately on start.
/// </summary>
internal sealed class UsageHistory
{
    private const int MaxSamples = 4000;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    /// <summary>A drop of at least this many points is treated as a reset/top-up.</summary>
    private const double ResetDropPoints = 5;

    /// <summary>Don't record more often than this; scrapes are ~15 min apart anyway.</summary>
    private static readonly TimeSpan MinSpacing = TimeSpan.FromMinutes(4);

    [JsonPropertyName("samples")]
    public List<UsageSample> Samples { get; set; } = new();

    [JsonPropertyName("label")]
    public string? LastLabel { get; set; }

    [JsonPropertyName("detail")]
    public string? LastDetail { get; set; }

    private static string FilePath => AppPaths.HistoryFile;

    public static UsageHistory Load()
    {
        var path = File.Exists(FilePath) ? FilePath : LegacyPath();
        if (path is null) return new UsageHistory();

        try
        {
            var loaded = JsonSerializer.Deserialize<UsageHistory>(File.ReadAllText(path));
            if (loaded is not null)
            {
                loaded.Samples ??= new List<UsageSample>();
                loaded.Samples.Sort((a, b) => a.At.CompareTo(b.At));

                // Adopted from the old profile location: persist it under the current data root so
                // burn-rate history survives moving the exe (or switching to portable mode).
                if (!string.Equals(path, FilePath, StringComparison.OrdinalIgnoreCase))
                    loaded.Save();

                return loaded;
            }
        }
        catch (Exception)
        {
            // Corrupt history is not worth failing over; start fresh.
        }
        return new UsageHistory();
    }

    /// <summary>Pre-portable location, checked once so existing history isn't stranded.</summary>
    private static string? LegacyPath()
    {
        try
        {
            var legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UsageBars", "usage-history.json");
            return File.Exists(legacy) ? legacy : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception)
        {
            // History is a nicety; failing to persist only costs accuracy after a restart.
        }
    }

    /// <summary>The most recent reading, for instant display before the first scrape completes.</summary>
    public ClaudeCodeUsage? Latest()
    {
        if (Samples.Count == 0) return null;
        var last = Samples[^1];
        return new ClaudeCodeUsage
        {
            UsedPct = last.UsedPct,
            MeterLabel = LastLabel,
            MeterDetail = LastDetail,
            ReadAt = last.At,
        };
    }

    /// <summary>Adds a reading, then prunes. Returns true when the sample was kept.</summary>
    public bool Record(double usedPct, string? label, string? detail, DateTimeOffset at)
    {
        LastLabel = label ?? LastLabel;
        LastDetail = detail ?? LastDetail;

        if (Samples.Count > 0)
        {
            var last = Samples[^1];
            // Collapse near-duplicates, but always keep a sample that changed the value.
            if (at - last.At < MinSpacing && Math.Abs(usedPct - last.UsedPct) < 0.5) return false;
            // Ignore out-of-order readings.
            if (at < last.At) return false;
        }

        Samples.Add(new UsageSample { At = at, UsedPct = usedPct });

        var cutoff = DateTimeOffset.Now - MaxAge;
        Samples.RemoveAll(s => s.At < cutoff);
        if (Samples.Count > MaxSamples) Samples.RemoveRange(0, Samples.Count - MaxSamples);
        return true;
    }

    /// <summary>
    /// The samples as a plain (time, used %) series, so the scraped meter feeds the same shared
    /// projection as the Claude history file and the Codex windows.
    /// </summary>
    public IReadOnlyList<UsagePoint> Points() =>
        Samples.Select(s => new UsagePoint(s.At, s.UsedPct)).ToList();

    /// <summary>
    /// Samples since the last observed reset. A reset is detected purely from the data — a drop in
    /// usage — never from the configured reset day, which is informational only.
    /// </summary>
    public List<UsageSample> CurrentWindow()
    {
        for (var i = Samples.Count - 1; i > 0; i--)
        {
            if (Samples[i].UsedPct <= Samples[i - 1].UsedPct - ResetDropPoints)
                return Samples.Skip(i).ToList();
        }
        return new List<UsageSample>(Samples);
    }
}

/// <summary>Where the configured weekly reset falls.</summary>
internal static class ResetSchedule
{
    public static DateTimeOffset Last(DayOfWeek day, int hour, DateTimeOffset now)
    {
        var h = Math.Clamp(hour, 0, 23);
        for (var i = 0; i <= 7; i++)
        {
            var date = now.Date.AddDays(-i);
            if (date.DayOfWeek != day) continue;
            var candidate = new DateTimeOffset(date.AddHours(h), now.Offset);
            if (candidate <= now) return candidate;
        }
        // Only reachable if today is the reset day but before the reset hour.
        return new DateTimeOffset(now.Date.AddDays(-7).AddHours(h), now.Offset);
    }

    public static DateTimeOffset Next(DayOfWeek day, int hour, DateTimeOffset now) =>
        Last(day, hour, now).AddDays(7);
}

/// <summary>Burn rate and projected exhaustion.</summary>
internal sealed record VelocityEstimate
{
    /// <summary>Percentage points of the meter consumed per hour, before padding.</summary>
    public double? PercentPerHour { get; init; }

    /// <summary>Hours until the meter hits zero at the padded rate.</summary>
    public double? HoursToEmpty { get; init; }

    public int SampleCount { get; init; }

    public double SpanHours { get; init; }

    /// <summary>
    /// When the window is next due to reset. Null when it isn't knowable — measured resets are
    /// preferred over the configured weekday, and "unknown" is a more honest answer than a guess.
    /// </summary>
    public DateTimeOffset? NextReset { get; init; }

    /// <summary>How the reset was arrived at, for diagnostics.</summary>
    public string ResetBasis { get; init; } = "unknown";

    public double? HoursToReset { get; init; }

    /// <summary>True when there was enough data to trust the rate.</summary>
    public bool Confident => PercentPerHour is not null;

    /// <summary>Why the estimate is unavailable, for diagnostics.</summary>
    public string? Reason { get; init; }

    public string Describe()
    {
        if (PercentPerHour is null) return Reason ?? "no rate yet";
        var rate = PercentPerHour.Value.ToString("0.0", CultureInfo.InvariantCulture);
        if (HoursToEmpty is null) return $"{rate}%/h";
        var hours = HoursToEmpty.Value;
        var when = hours >= 48
            ? $"{hours / 24:0.0}d"
            : $"{hours:0.0}h";
        return $"{rate}%/h · ~{when} left";
    }
}

/// <summary>
/// When one meter is projected to reach 0 % at the rate it is currently moving.
///
/// Deliberately separate from <see cref="VelocityEstimate"/>. That exists to drive the warning
/// decision, so it inflates the rate by <see cref="Config.VelocityPaddingFactor"/> to fire early.
/// This is the plain answer to "when do I actually run out?", and uses the measured rate unpadded —
/// a padded number is the right thing to warn on and the wrong thing to print as a clock time.
/// </summary>
internal readonly record struct BurnProjection(
    double PercentPerHour, double HoursToEmpty, DateTimeOffset EmptyAt);

internal static class UsageVelocity
{
    /// <summary>
    /// A fall of at least this many points counts as a reset rather than noise, matching
    /// <see cref="ResetEstimator"/> and <see cref="UsageHistory"/>.
    /// </summary>
    private const double ResetDropPoints = 5;

    /// <summary>
    /// Beyond this the projection stops being information. A meter barely moving divides into
    /// years, and "0 % in 2031" is noise dressed as a measurement — better to say nothing.
    /// </summary>
    private const double MaxProjectionHours = 24 * 30;

    /// <summary>
    /// Projects when a meter reaches 0 %, from its own series of utilization readings.
    ///
    /// Shared by every bar rather than living beside one source: Claude's history file, the scraped
    /// credit meter and each Codex window all reduce to the same thing — a list of (time, used %)
    /// points — and "when do I run out" shouldn't mean three different calculations.
    ///
    /// Null whenever the data can't support an answer, under the same guards
    /// <see cref="Estimate"/> applies: too few samples, too short a span, and usage that is flat or
    /// falling. Those are shared because thin data producing a confident-looking time is the one
    /// failure mode that matters here.
    /// </summary>
    public static BurnProjection? Project(
        IReadOnlyList<UsagePoint>? series, double remainingPct, Config config, DateTimeOffset now)
    {
        if (series is null || series.Count < 2) return null;

        var window = SinceLastReset(series);

        // Prefer the recent window so the rate reflects current behaviour, but fall back to the
        // whole window when that is too sparse to say anything.
        var recentCutoff = now - TimeSpan.FromHours(Math.Max(0.5, config.VelocityWindowHours));
        var recent = window.Where(p => p.At >= recentCutoff).ToList();
        if (recent.Count < Math.Max(2, config.MinVelocitySamples)) recent = window;
        if (recent.Count < Math.Max(2, config.MinVelocitySamples)) return null;

        var first = recent[0];
        var last = recent[^1];
        var spanHours = (last.At - first.At).TotalHours;
        if (spanHours < Math.Max(1, config.MinVelocitySpanMinutes) / 60.0) return null;

        var rate = (last.UsedPct - first.UsedPct) / spanHours;
        if (rate <= 0) return null;

        var hours = Math.Max(0, remainingPct) / rate;
        if (!double.IsFinite(hours) || hours > MaxProjectionHours) return null;

        return new BurnProjection(rate, hours, now.AddHours(hours));
    }

    /// <summary>
    /// The tail of the series since the last observed reset. Consumption from the previous window
    /// would otherwise inflate every projection taken just after a reset.
    /// </summary>
    private static List<UsagePoint> SinceLastReset(IReadOnlyList<UsagePoint> series)
    {
        for (var i = series.Count - 1; i > 0; i--)
        {
            if (series[i].UsedPct <= series[i - 1].UsedPct - ResetDropPoints)
                return series.Skip(i).ToList();
        }
        return series.ToList();
    }

    /// <summary>
    /// Estimates the burn rate from recent samples and projects when the meter empties.
    /// Padding inflates the rate so the warning arrives early rather than late.
    ///
    /// The configured reset day never enters this calculation — it is carried through only so the
    /// UI can say when a reset is expected. Everything driving the bar comes from observed data.
    /// </summary>
    public static VelocityEstimate Estimate(
        UsageHistory history,
        double remainingPct,
        Config config,
        DateTimeOffset? measuredReset = null)
    {
        var now = DateTimeOffset.Now;

        // A reset observed in the data always wins. The configured weekday is a last resort, and
        // is flagged as such so diagnostics never present a guess as a measurement.
        DateTimeOffset? nextReset;
        string basis;
        if (measuredReset is { } measured && measured > now)
        {
            nextReset = measured;
            basis = "measured from usage data";
        }
        else if (config.UseConfiguredResetDay)
        {
            nextReset = ResetSchedule.Next(config.ResetDayOfWeek, config.ResetHour, now);
            basis = $"configured {config.ResetDayOfWeek} {config.ResetHour:00}:00 (not measured)";
        }
        else
        {
            nextReset = null;
            basis = "unknown";
        }

        var hoursToReset = nextReset is null ? (double?)null : (nextReset.Value - now).TotalHours;

        var window = history.CurrentWindow();

        // Restrict to the recent window so a rate reflects current behaviour, but fall back to
        // the whole billing window when that's too sparse.
        var recentCutoff = now - TimeSpan.FromHours(Math.Max(0.5, config.VelocityWindowHours));
        var recent = window.Where(s => s.At >= recentCutoff).ToList();
        if (recent.Count < Math.Max(2, config.MinVelocitySamples)) recent = window;

        var baseline = new VelocityEstimate
        {
            NextReset = nextReset,
            HoursToReset = hoursToReset,
            ResetBasis = basis,
        };

        if (recent.Count < Math.Max(2, config.MinVelocitySamples))
            return baseline with { SampleCount = recent.Count, Reason = "not enough samples yet" };

        var first = recent[0];
        var last = recent[^1];
        var spanHours = (last.At - first.At).TotalHours;

        if (spanHours < Math.Max(1, config.MinVelocitySpanMinutes) / 60.0)
        {
            return baseline with
            {
                SampleCount = recent.Count,
                SpanHours = spanHours,
                Reason = "sample span too short",
            };
        }

        var consumed = last.UsedPct - first.UsedPct;
        var rate = consumed / spanHours;

        if (rate <= 0)
        {
            return baseline with
            {
                SampleCount = recent.Count,
                SpanHours = spanHours,
                PercentPerHour = 0,
                Reason = "usage flat or decreasing",
            };
        }

        var padded = rate * Math.Max(1.0, config.VelocityPaddingFactor);
        var hoursToEmpty = Math.Max(0, remainingPct) / padded;

        return baseline with
        {
            SampleCount = recent.Count,
            SpanHours = spanHours,
            PercentPerHour = rate,
            HoursToEmpty = hoursToEmpty,
        };
    }

    /// <summary>
    /// Decides whether the diagnostic low-usage estimate warns. The velocity projection is the primary
    /// trigger; <see cref="Config.LowThresholdPct"/> stays on as an absolute floor so a nearly
    /// empty meter still warns even when usage has gone quiet.
    /// </summary>
    public static bool ShouldWarn(VelocityEstimate estimate, double remainingPct, Config config, out string why)
    {
        if (remainingPct <= config.LowThresholdPct)
        {
            why = $"at or below floor of {config.LowThresholdPct:0}%";
            return true;
        }

        if (estimate.HoursToEmpty is not { } hours)
        {
            why = estimate.Reason ?? "no projection";
            return false;
        }

        if (hours > config.WarnHoursToEmpty)
        {
            why = $"projected {hours:0.0}h left, over {config.WarnHoursToEmpty:0.#}h horizon";
            return false;
        }

        why = $"projected {hours:0.0}h to empty, within {config.WarnHoursToEmpty:0.#}h horizon";
        return true;
    }
}
