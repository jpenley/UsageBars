using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace UsageBars;

/// <summary>One utilization reading, used for deriving when a window resets.</summary>
internal readonly record struct UsagePoint(DateTimeOffset At, double UsedPct);

/// <summary>
/// Works out when a usage window actually resets, by watching the data rather than trusting a
/// configured day.
///
/// Claude's 5h and 7d windows are rolling sessions: the clock starts on first use after the
/// previous window expired, and the window closes exactly one window-length later. Neither the
/// history file nor the app reports that deadline, but it is recoverable — a reset shows up as
/// utilization dropping to (near) zero, and the next deadline is one window-length after usage
/// resumed. plan-usage-history.json keeps several days of five-minute samples, which is more than
/// enough to see it.
///
/// This is why <see cref="Config.ResetDayOfWeek"/> is only ever a fallback: a measured deadline is
/// correct by construction, and a configured weekday is a guess that is wrong the moment a window
/// starts at a different time of day.
/// </summary>
internal static class ResetEstimator
{
    /// <summary>Claude's short rolling window.</summary>
    public static readonly TimeSpan FiveHourWindow = TimeSpan.FromHours(5);

    /// <summary>Claude's long rolling window.</summary>
    public static readonly TimeSpan SevenDayWindow = TimeSpan.FromDays(7);

    /// <summary>
    /// A fall of at least this many points counts as a reset rather than noise. The observed
    /// pattern is a clean drop to zero, so this is deliberately generous.
    /// </summary>
    private const double ResetDropPoints = 5;

    /// <summary>Utilization at or below this is treated as "the window has not started".</summary>
    private const double IdleThreshold = 0.5;

    /// <summary>
    /// When the current window is next due to reset, or null when that can't be established.
    ///
    /// Returns null rather than guessing in three cases, each of which is a real "unknown": no
    /// usage recorded yet, history that doesn't reach back to the start of the current window, and
    /// a deadline that has already passed without the drop being observed (which happens when
    /// Claude Desktop was closed over the boundary and simply stopped writing samples).
    /// </summary>
    public static DateTimeOffset? NextReset(
        IReadOnlyList<UsagePoint> series, TimeSpan window, DateTimeOffset now)
    {
        var start = CurrentWindowStart(series, now);
        if (start is null) return null;

        var next = start.Value + window;
        return next > now ? next : null;
    }

    /// <summary>
    /// When the current window started: the first non-idle reading after the most recent reset.
    /// Exposed separately because it is the interesting value for diagnostics — it explains the
    /// deadline rather than just asserting it.
    /// </summary>
    public static DateTimeOffset? CurrentWindowStart(
        IReadOnlyList<UsagePoint> series, DateTimeOffset now)
    {
        if (series is null || series.Count == 0) return null;

        // Walk back to the last reset. Index 0 is a valid answer only if the series happens to
        // begin at a reset; otherwise a start earlier than the data is unknowable.
        var resetIndex = -1;
        for (var i = series.Count - 1; i > 0; i--)
        {
            if (series[i].UsedPct <= series[i - 1].UsedPct - ResetDropPoints)
            {
                resetIndex = i;
                break;
            }
        }

        if (resetIndex < 0)
        {
            // No reset in the retained history. If the series has been idle from its very first
            // sample, the window demonstrably started within it and we can still date it.
            if (series[0].UsedPct > IdleThreshold) return null;
            resetIndex = 0;
        }

        // The window's clock starts when usage resumes, not at the reset itself.
        for (var i = resetIndex; i < series.Count; i++)
        {
            if (series[i].UsedPct > IdleThreshold) return series[i].At;
        }

        // Reset observed but no usage since: no window is running, so nothing is pending.
        return null;
    }

    /// <summary>
    /// Reset or expiry date mentioned in the Claude Code <c>/usage</c> meter's secondary line,
    /// such as "One-time credit · Expires October 14" or "Resets 3pm".
    ///
    /// Only used for the scraped source, where there is no sample history to derive from. Times
    /// without a date are resolved to the next occurrence, dates without a year to the next
    /// occurrence, so a line read in December that says "Expires January 4" lands in the new year.
    /// </summary>
    public static DateTimeOffset? ParseMeterDetail(string? detail, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(detail)) return null;

        // "Resets 3pm" / "Resets at 15:30"
        var time = Regex.Match(
            detail,
            @"\bresets?\b(?:\s+at)?\s+(\d{1,2})(?::(\d{2}))?\s*(am|pm)?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (time.Success && TryBuildTime(time, now, out var atTime)) return atTime;

        // "Expires October 14" / "Resets on 14 October"
        var monthFirst = Regex.Match(
            detail,
            @"\b(?:expires?|resets?)\b(?:\s+on)?\s+([A-Za-z]{3,9})\s+(\d{1,2})\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (monthFirst.Success
            && TryMonth(monthFirst.Groups[1].Value, out var m1)
            && int.TryParse(monthFirst.Groups[2].Value, out var d1))
        {
            return BuildDate(m1, d1, now);
        }

        var dayFirst = Regex.Match(
            detail,
            @"\b(?:expires?|resets?)\b(?:\s+on)?\s+(\d{1,2})\s+([A-Za-z]{3,9})\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (dayFirst.Success
            && int.TryParse(dayFirst.Groups[1].Value, out var d2)
            && TryMonth(dayFirst.Groups[2].Value, out var m2))
        {
            return BuildDate(m2, d2, now);
        }

        return null;
    }

    /// <summary>
    /// Parses the dated clock time and optional IANA zone on a Claude rolling-window reset.
    /// Resolve it at the sample time, so a cached "Resets 3pm" never rolls forward on a repaint.
    /// Unsupported text or an ambiguous daylight-saving time stays unknown.
    /// </summary>
    public static DateTimeOffset? ParseWindowReset(string? detail, DateTimeOffset observedAt, TimeSpan windowDuration)
    {
        if (string.IsNullOrWhiteSpace(detail) || windowDuration <= TimeSpan.Zero) return null;
        var match = Regex.Match(detail,
            @"^\s*resets?\s+(?:(?:on|at)\s+)?(?:(?<month>[A-Za-z]{3,9})\s+(?<day>[0-9]{1,2})\s*,?\s+)?(?<hour>[0-9]{1,2})(?::(?<minute>[0-9]{2}))?\s*(?<meridiem>am|pm)?\s*(?:\((?<zone>[^)]+)\))?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        try
        {
            var zone = match.Groups["zone"].Success
                ? TimeZoneInfo.FindSystemTimeZoneById(match.Groups["zone"].Value.Trim())
                : TimeZoneInfo.Local;
            var localNow = TimeZoneInfo.ConvertTime(observedAt, zone);
            var hour = int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture);
            var minute = match.Groups["minute"].Success
                ? int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture) : 0;
            var meridiem = match.Groups["meridiem"].Value.ToLowerInvariant();
            if (minute > 59 || (meridiem.Length > 0 && hour is < 1 or > 12) || hour > 23) return null;
            if (meridiem == "pm" && hour < 12) hour += 12;
            if (meridiem == "am" && hour == 12) hour = 0;

            var hasDate = match.Groups["month"].Success;
            var month = localNow.Month;
            var day = localNow.Day;
            if (hasDate)
            {
                if (!TryMonth(match.Groups["month"].Value, out month)) return null;
                day = int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture);
            }
            var candidate = new DateTime(localNow.Year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
            // The CLI rounds to minutes and timestamps a read after the panel settles. A reset
            // crossed during that read is expired, not tomorrow's (or next year's) fresh budget.
            // Resolve a genuinely future occurrence only when it fits the provider's window.
            if (candidate < localNow.DateTime.AddMinutes(-1))
            {
                var next = hasDate ? candidate.AddYears(1) : candidate.AddDays(1);
                if (next - localNow.DateTime <= windowDuration) candidate = next;
            }
            if (zone.IsInvalidTime(candidate) || zone.IsAmbiguousTime(candidate)) return null;
            return new DateTimeOffset(candidate, zone.GetUtcOffset(candidate));
        }
        catch (TimeZoneNotFoundException) { return null; }
        catch (InvalidTimeZoneException) { return null; }
        catch (ArgumentException) { return null; }
    }

    private static bool TryBuildTime(Match match, DateTimeOffset now, out DateTimeOffset result)
    {
        result = default;

        if (!int.TryParse(match.Groups[1].Value, out var hour)) return false;
        var minute = match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out var m) ? m : 0;
        if (minute is < 0 or > 59) return false;

        var meridiem = match.Groups[3].Value.ToLowerInvariant();
        if (meridiem == "pm" && hour < 12) hour += 12;
        else if (meridiem == "am" && hour == 12) hour = 0;

        if (hour is < 0 or > 23) return false;

        var candidate = new DateTimeOffset(
            now.Year, now.Month, now.Day, hour, minute, 0, now.Offset);

        // A time that has already passed today refers to tomorrow.
        if (candidate <= now) candidate = candidate.AddDays(1);

        result = candidate;
        return true;
    }

    private static DateTimeOffset? BuildDate(int month, int day, DateTimeOffset now)
    {
        if (month is < 1 or > 12) return null;
        if (day < 1 || day > DateTime.DaysInMonth(now.Year, month)) return null;

        var candidate = new DateTimeOffset(now.Year, month, day, 0, 0, 0, now.Offset);

        // A date already behind us means next year's occurrence.
        if (candidate <= now.AddDays(-1))
        {
            var year = now.Year + 1;
            if (day > DateTime.DaysInMonth(year, month)) return null;
            candidate = new DateTimeOffset(year, month, day, 0, 0, 0, now.Offset);
        }

        return candidate;
    }

    private static bool TryMonth(string name, out int month)
    {
        month = 0;
        if (string.IsNullOrWhiteSpace(name)) return false;

        // Invariant culture only: the meter is rendered in English regardless of system locale.
        var months = DateTimeFormatInfo.InvariantInfo.MonthNames;
        var abbreviations = DateTimeFormatInfo.InvariantInfo.AbbreviatedMonthNames;

        for (var i = 0; i < 12; i++)
        {
            if (name.Equals(months[i], StringComparison.OrdinalIgnoreCase)
                || name.Equals(abbreviations[i], StringComparison.OrdinalIgnoreCase))
            {
                month = i + 1;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Human-readable "resets at X (in Y)", shared by every provider's tooltip and diagnostics.
    /// A reset is a reset however it was arrived at, so the formatting lives here rather than
    /// beside any one source.
    /// </summary>
    public static string Describe(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is null) return "unknown";

        var remaining = resetsAt.Value - now;
        if (remaining <= TimeSpan.Zero) return "due";

        // A same-day deadline only needs a clock time; anything further out needs the date too.
        var when = remaining.TotalHours < 18
            ? resetsAt.Value.ToString("HH:mm", CultureInfo.InvariantCulture)
            : resetsAt.Value.ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture);

        var span = remaining.TotalHours >= 1
            ? $"{remaining.TotalHours:0.#} h"
            : $"{remaining.TotalMinutes:0} min";

        return $"{when} (in {span})";
    }
}
