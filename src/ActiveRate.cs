using System;
using System.Collections.Generic;
using System.Linq;

namespace UsageBars;

/// <summary>
/// What a credit/spend meter is projected to do at the user's rate of use while working.
/// Rates are in percent of the limit; a dollar bar converts them with its limit.
/// </summary>
internal readonly record struct ActiveProjection(
    double PercentPerActiveHour,
    double PercentPerActiveDay,
    double ActiveHoursLeft,
    DateTimeOffset EmptyAt,
    int DaysUsed);

/// <summary>
/// Spend rate measured over the time the user was actually working, not the wall clock.
///
/// A work account sits idle overnight, at weekends and through meetings. Dividing spend by elapsed
/// wall-clock time dilutes the rate by all of that and pushes the projected empty time far into
/// the future. Here each working day contributes only the span between its first and last
/// transcript message (see <see cref="ActivityLog"/>), clipped to the stretch the meter was
/// actually sampled, and the rate is total spend over total span across the most recent days.
/// </summary>
internal static class ActiveRate
{
    private const double MaxProjectionDays = 45;

    /// <param name="days">Per-day activity spans from the transcripts.</param>
    /// <param name="series">The meter's (time, used %) readings.</param>
    /// <param name="remainingPct">Percent of the limit still unspent.</param>
    public static ActiveProjection? Compute(
        IReadOnlyList<ActiveDay> days,
        IReadOnlyList<UsagePoint>? series,
        double remainingPct,
        Config config,
        DateTimeOffset now)
    {
        if (days.Count == 0 || series is null || series.Count < 2) return null;
        if (!double.IsFinite(remainingPct) || remainingPct <= 0) return null;

        // Only readings since the last reset describe this cycle.
        var cycle = UsageVelocity.SinceLastReset(series);
        if (cycle.Count < 2) return null;
        var sampledFrom = cycle[0].At;
        var sampledTo = cycle[^1].At;

        var minSpan = TimeSpan.FromMinutes(Math.Max(1, config.MinVelocitySpanMinutes));
        var todayKey = ActivityLog.DayKey(now, config.ActivityDayStartHour);

        var used = new List<(ActiveDay Day, DateTimeOffset From, DateTimeOffset To, double Spend)>();
        foreach (var day in days.Where(d => d.Key <= todayKey).OrderByDescending(d => d.Key))
        {
            if (used.Count >= Math.Max(1, config.ActivityRateDays)) break;

            // Spend can only be attributed inside the stretch the meter was sampled.
            var from = day.Start < sampledFrom ? sampledFrom : day.Start;
            var to = day.End > sampledTo ? sampledTo : day.End;
            if (to - from < minSpan) continue;

            var spend = UsedAt(cycle, to) - UsedAt(cycle, from);
            if (spend < 0) continue; // a reset or top-up inside the span makes it meaningless
            used.Add((day, from, to, spend));
        }
        if (used.Count == 0) return null;

        var totalSpend = used.Sum(u => u.Spend);
        var totalHours = used.Sum(u => (u.To - u.From).TotalHours);
        if (totalHours <= 0 || totalSpend <= 0) return null;

        var perHour = totalSpend / totalHours;
        var perDay = totalSpend / used.Count;
        var hoursLeft = remainingPct / perHour;

        var empty = EmptyAt(
            remainingPct, perHour, perDay, used.Select(u => u.From).ToList(),
            todaySpend: used.Where(u => u.Day.Key == todayKey).Sum(u => u.Spend), todayKey, now);
        if (empty is null) return null;

        return new ActiveProjection(perHour, perDay, hoursLeft, empty.Value, used.Count);
    }

    /// <summary>
    /// Walks forward day by day: what is left of today's typical spend, then a full typical day
    /// for each following weekday. Weekends are skipped, because the days that set the rate were
    /// working days and a bar that spends nothing on Saturday should not be projected to.
    /// </summary>
    private static DateTimeOffset? EmptyAt(
        double remainingPct, double perHour, double perDay, IReadOnlyList<DateTimeOffset> starts,
        double todaySpend, DateTime todayKey, DateTimeOffset now)
    {
        var typicalStart = TimeSpan.FromTicks(
            (long)starts.Average(s => s.ToLocalTime().TimeOfDay.Ticks));

        var left = remainingPct;
        var firstCapacity = Math.Max(0, perDay - todaySpend);
        if (left <= firstCapacity)
            return now.AddHours(left / perHour);
        left -= firstCapacity;

        var zone = TimeZoneInfo.Local;
        for (var offset = 1; offset <= MaxProjectionDays; offset++)
        {
            var date = todayKey.AddDays(offset);
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;

            if (left <= perDay)
            {
                var local = date.Add(typicalStart).AddHours(left / perHour);
                return new DateTimeOffset(local, zone.GetUtcOffset(local));
            }
            left -= perDay;
        }
        return null;
    }

    /// <summary>The meter's used percent at a moment, interpolated between neighbouring readings.</summary>
    internal static double UsedAt(IReadOnlyList<UsagePoint> series, DateTimeOffset at)
    {
        if (at <= series[0].At) return series[0].UsedPct;
        if (at >= series[^1].At) return series[^1].UsedPct;

        for (var i = 1; i < series.Count; i++)
        {
            if (series[i].At < at) continue;
            var before = series[i - 1];
            var after = series[i];
            var span = (after.At - before.At).TotalSeconds;
            if (span <= 0) return after.UsedPct;
            var fraction = (at - before.At).TotalSeconds / span;
            return before.UsedPct + (after.UsedPct - before.UsedPct) * fraction;
        }
        return series[^1].UsedPct;
    }
}
