using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace UsageBars.Tests;

public class ActiveRateTests
{
    private static DateTimeOffset Local(int month, int day, int hour, int minute = 0) =>
        new(new DateTime(2026, month, day, hour, minute, 0, DateTimeKind.Local));

    private static ActiveDay Day(DateTimeOffset start, DateTimeOffset end) =>
        new(ActivityLog.DayKey(start, 6), start, end);

    // ---- Which working day a moment belongs to --------------------------------------

    [Fact]
    public void DaysStartAtSixLocalSoLateNightWorkBelongsToTheDayItBeganIn()
    {
        Assert.Equal(new DateTime(2026, 11, 23), ActivityLog.DayKey(Local(11, 24, 5, 59), 6));
        Assert.Equal(new DateTime(2026, 11, 24), ActivityLog.DayKey(Local(11, 24, 6, 0), 6));
        Assert.Equal(new DateTime(2026, 11, 24), ActivityLog.DayKey(Local(11, 25, 0, 30), 6));
    }

    // ---- The rate ignores idle time --------------------------------------------------

    /// <summary>1 point per hour while working, flat overnight: Mon 9-17, Tue 9-15.</summary>
    private static List<UsagePoint> TwoWorkingDays()
    {
        var points = new List<UsagePoint>();
        void Add(DateTimeOffset at, double used) => points.Add(new UsagePoint(at, used));

        for (var h = 9; h <= 17; h++) Add(Local(11, 23, h), 50 + (h - 9));
        for (var h = 9; h <= 15; h++) Add(Local(11, 24, h), 58 + (h - 9));
        return points;
    }

    private static readonly IReadOnlyList<ActiveDay> TwoDays = new[]
    {
        Day(Local(11, 23, 9), Local(11, 23, 17)),
        Day(Local(11, 24, 9), Local(11, 24, 15)),
    };

    [Fact]
    public void RateIsSpendOverWorkingTimeNotWallClockTime()
    {
        var projection = ActiveRate.Compute(
            TwoDays, TwoWorkingDays(), remainingPct: 36, new Config(), Local(11, 24, 15));

        Assert.NotNull(projection);
        // 14 points over 14 working hours. Over the 30 wall-clock hours it would read 0.47.
        Assert.Equal(1.0, projection!.Value.PercentPerActiveHour, 3);
        Assert.Equal(36, projection.Value.ActiveHoursLeft, 3);
        Assert.Equal(2, projection.Value.DaysUsed);
        Assert.Equal(7, projection.Value.PercentPerActiveDay, 3);
    }

    [Fact]
    public void ProjectedEmptyDayWalksWeekdaysOnlyAndLandsOnTheTypicalStartPlusHoursNeeded()
    {
        // 36 left. Today's typical 7 minus the 6 already spent leaves 1; then Wed, Thu, Fri
        // (7 each), skip the weekend, Mon 7, and the last 7 on Tue Dec 1 from a 09:00 start.
        var projection = ActiveRate.Compute(
            TwoDays, TwoWorkingDays(), 36, new Config(), Local(11, 24, 15))!.Value;

        Assert.Equal(Local(12, 1, 16), projection.EmptyAt);
    }

    [Fact]
    public void WhatFitsInTodayIsProjectedFromNow()
    {
        var projection = ActiveRate.Compute(
            TwoDays, TwoWorkingDays(), remainingPct: 0.5, new Config(), Local(11, 24, 15))!.Value;

        Assert.Equal(Local(11, 24, 15, 30), projection.EmptyAt);
    }

    [Fact]
    public void ActivityAfterTheLastReadingDoesNotDiluteTheRate()
    {
        // The user was working until 17:00 but the meter was last read at 15:00.
        var days = new[] { Day(Local(11, 24, 9), Local(11, 24, 17)) };
        var series = Enumerable.Range(9, 7)
            .Select(h => new UsagePoint(Local(11, 24, h), 10 + (h - 9))).ToList();

        var projection = ActiveRate.Compute(days, series, 30, new Config(), Local(11, 24, 17))!.Value;

        Assert.Equal(1.0, projection.PercentPerActiveHour, 3);
    }

    [Fact]
    public void OnlyTheMostRecentDaysSetTheRate()
    {
        // An expensive day long ago, then a cheap one: with one day in play only the cheap one counts.
        var series = new List<UsagePoint>
        {
            new(Local(11, 20, 9), 0), new(Local(11, 20, 17), 40),
            new(Local(11, 24, 9), 40), new(Local(11, 24, 17), 42),
        };
        var days = new[]
        {
            Day(Local(11, 20, 9), Local(11, 20, 17)),
            Day(Local(11, 24, 9), Local(11, 24, 17)),
        };

        var one = ActiveRate.Compute(days, series, 58, new Config { ActivityRateDays = 1 }, Local(11, 24, 17))!.Value;
        var both = ActiveRate.Compute(days, series, 58, new Config { ActivityRateDays = 3 }, Local(11, 24, 17))!.Value;

        Assert.Equal(0.25, one.PercentPerActiveHour, 3);
        Assert.Equal(42.0 / 16, both.PercentPerActiveHour, 3);
    }

    [Fact]
    public void ADayStraddlingAResetIsIgnored()
    {
        var series = new List<UsagePoint>
        {
            new(Local(11, 23, 9), 70), new(Local(11, 23, 17), 90),
            // reset overnight
            new(Local(11, 24, 9), 2), new(Local(11, 24, 17), 10),
        };
        var days = new[]
        {
            Day(Local(11, 23, 9), Local(11, 23, 17)),
            Day(Local(11, 24, 9), Local(11, 24, 17)),
        };

        var projection = ActiveRate.Compute(days, series, 90, new Config(), Local(11, 24, 17))!.Value;

        Assert.Equal(1, projection.DaysUsed); // only today, since the last reset
        Assert.Equal(1.0, projection.PercentPerActiveHour, 3);
    }

    [Fact]
    public void NoProjectionWithoutActivityReadingsSpendOrAnythingLeft()
    {
        var series = TwoWorkingDays();
        var now = Local(11, 24, 15);

        Assert.Null(ActiveRate.Compute(Array.Empty<ActiveDay>(), series, 36, new Config(), now));
        Assert.Null(ActiveRate.Compute(TwoDays, null, 36, new Config(), now));
        Assert.Null(ActiveRate.Compute(TwoDays, series.Take(1).ToList(), 36, new Config(), now));
        Assert.Null(ActiveRate.Compute(TwoDays, series, 0, new Config(), now));

        var flat = series.Select(p => p with { UsedPct = 50 }).ToList();
        Assert.Null(ActiveRate.Compute(TwoDays, flat, 36, new Config(), now));
    }

    [Fact]
    public void UsedAtInterpolatesBetweenReadings()
    {
        var series = new[] { new UsagePoint(Local(11, 24, 9), 10), new UsagePoint(Local(11, 24, 11), 14) };

        Assert.Equal(12, ActiveRate.UsedAt(series, Local(11, 24, 10)), 6);
        Assert.Equal(10, ActiveRate.UsedAt(series, Local(11, 24, 8)));
        Assert.Equal(14, ActiveRate.UsedAt(series, Local(11, 24, 12)));
    }

    // ---- Through the model, tooltip and trigger ---------------------------------------

    private static UsageModel BuildWith(IReadOnlyList<ActiveDay>? activity)
    {
        var now = DateTimeOffset.Now;
        var history = new UsageHistory();
        for (var minutes = -240; minutes <= 0; minutes += 30)
        {
            var amount = new SpendAmount(285.75 + 4.0 * minutes / 60.0, 350);
            history.Record(Math.Round(amount.UsedPct), "Usage credits", "", now.AddMinutes(minutes), amount);
        }

        return UsageModel.Build(
            UsageSnapshot.Failed("none"),
            new ClaudeCodeUsage
            {
                UsedPct = 82, MeterLabel = "Usage credits",
                MeterDetail = "$285.75 / $350.00 spent", ReadAt = now,
            },
            history, new Config(), activity);
    }

    [Fact]
    public void TheCreditBarCarriesHoursOfUseLeftAndSaysSoInTheTooltip()
    {
        var start = DateTimeOffset.Now.AddHours(-4);
        var model = BuildWith(new[] { Day(start, DateTimeOffset.Now) });
        var bar = Assert.Single(model.Bars);

        Assert.InRange(bar.ActiveHoursLeft!.Value, 15.5, 16.5); // $64.25 left at $4 per hour of use
        Assert.Equal(4, bar.UsdPerHour!.Value, 1);

        var tip = model.ResetTooltip(DateTimeOffset.Now)!;
        Assert.Contains("of use", tip);
        Assert.Contains("h of use left", tip);
        Assert.Contains("/day", tip);
    }

    [Fact]
    public void WithActivityKnownButEmptyThereIsNoDilutedFallback()
    {
        var bar = Assert.Single(BuildWith(Array.Empty<ActiveDay>()).Bars);

        Assert.Null(bar.EmptyAt);
        Assert.Null(bar.ActiveHoursLeft);
        Assert.Equal("no spend rate yet", UsageModel.DepletionText(bar, DateTimeOffset.Now));
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.FromHours(-7));
    private static readonly DateTimeOffset Reset = new(2026, 12, 1, 0, 0, 0, TimeSpan.FromHours(-7));

    private static UsageModel CreditWith(double activeHours, DateTimeOffset emptyAt) => new()
    {
        Bars = new[]
        {
            new UsageBar("C", 10) { EmptyAt = emptyAt, ResetsAt = Reset, ActiveHoursLeft = activeHours },
        },
    };

    [Fact]
    public void TriggerCountsHoursOfUseNotHoursOnTheClock()
    {
        // Two hours of use left, but not empty on the clock until tomorrow: fire.
        Assert.True(LowCreditTrigger.Evaluate(
            CreditWith(1.5, Now.AddDays(1)), new Config(), Now, null).Fire);

        // Empty on the clock within the hour, yet five hours of use remain: wait.
        Assert.False(LowCreditTrigger.Evaluate(
            CreditWith(5, Now.AddHours(1)), new Config(), Now, null).Fire);
    }

    [Fact]
    public void ActivityScanOnlyRunsForLiveSingleMeterCreditOrSpendBars()
    {
        var dollarCredit = new UsageModel
        {
            Bars = new[] { new UsageBar("C", 18) { Spend = new SpendAmount(287, 350) } },
        };
        var percentCredit = new UsageModel { Bars = new[] { new UsageBar("C", 88) } };
        var spendLimit = new UsageModel { Bars = new[] { new UsageBar("spend", 40) } };
        var dollarCustomLabel = new UsageModel
        {
            Bars = new[] { new UsageBar("usage", 50) { Spend = new SpendAmount(100, 200) } },
        };
        var staleCredit = dollarCredit with { Stale = true };
        var subscriptionWindows = new UsageModel
        {
            Bars = new[] { new UsageBar("5h", 72), new UsageBar("7d", 44) },
        };
        var singleSubscriptionWindow = new UsageModel
        {
            Bars = new[] { new UsageBar("7d", 9) },
        };
        var placeholder = new UsageModel
        {
            Bars = new[] { new UsageBar("usage", null) },
            Stale = true,
        };

        Assert.True(OverlayController.NeedsActivityScan(dollarCredit));
        Assert.True(OverlayController.NeedsActivityScan(percentCredit));
        Assert.True(OverlayController.NeedsActivityScan(spendLimit));
        Assert.True(OverlayController.NeedsActivityScan(dollarCustomLabel));

        Assert.False(OverlayController.NeedsActivityScan(staleCredit));
        Assert.False(OverlayController.NeedsActivityScan(subscriptionWindows));
        Assert.False(OverlayController.NeedsActivityScan(singleSubscriptionWindow));
        Assert.False(OverlayController.NeedsActivityScan(placeholder));
    }
}

public class ActivityLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ub-activity-" + Guid.NewGuid().ToString("N"));

    public ActivityLogTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static DateTimeOffset Local(int month, int day, int hour, int minute = 0) =>
        new(new DateTime(2026, month, day, hour, minute, 0, DateTimeKind.Local));

    private static string Line(DateTimeOffset at, string type = "assistant") =>
        $$"""{"type":"{{type}}","timestamp":"{{at.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ}}"}""";

    private string Write(string name, params string[] lines)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    private ActivityLog Open(string? cache = null) => new(_root, 6, cache);

    private static readonly DateTimeOffset Today = Local(11, 24, 12);

    [Fact]
    public void ADayRunsFromTheEarliestToTheLatestMessageAcrossFiles()
    {
        Write("a/one.jsonl", Line(Local(11, 24, 9, 15)), Line(Local(11, 24, 10)));
        Write("b/two.jsonl", Line(Local(11, 24, 8, 40)), Line(Local(11, 24, 16, 5)));
        var log = Open();

        Assert.True(log.Refresh(Today));

        var day = Assert.Single(log.Days);
        Assert.Equal(new DateTime(2026, 11, 24), day.Key);
        Assert.Equal(Local(11, 24, 8, 40), day.Start);
        Assert.Equal(Local(11, 24, 16, 5), day.End);
    }

    [Fact]
    public void WorkPastMidnightBelongsToTheDayItBeganInAndSplitsAtSix()
    {
        Write("s.jsonl",
            Line(Local(11, 23, 22)), Line(Local(11, 24, 1)),
            Line(Local(11, 24, 5, 59)), Line(Local(11, 24, 6, 1)));
        var log = Open();
        log.Refresh(Today);

        Assert.Equal(2, log.Days.Count);
        Assert.Equal(Local(11, 24, 5, 59), log.Days[0].End);
        Assert.Equal(Local(11, 24, 6, 1), log.Days[1].Start);
    }

    [Fact]
    public void OnlyTheLinesOwnTimestampCountsNotOnesNestedInToolResults()
    {
        var real = Local(11, 24, 9);
        Write("n.jsonl",
            $$"""{"type":"user","toolUseResult":{"timestamp":"2020-01-01T00:00:00Z"},"timestamp":"{{real.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}}"}""",
            """{"type":"user","toolUseResult":{"timestamp":"2019-05-05T00:00:00Z"}}""",
            "not json at all \"timestamp\":\"",
            Line(Local(11, 24, 11)));
        var log = Open();
        log.Refresh(Today);

        var day = Assert.Single(log.Days);
        Assert.Equal(real, day.Start);
        Assert.Equal(Local(11, 24, 11), day.End);
    }

    [Fact]
    public void LaterRefreshesReadOnlyWhatWasAppendedAndWaitForACompleteLine()
    {
        var path = Write("grow.jsonl", Line(Local(11, 24, 9)));
        var log = Open();
        log.Refresh(Today);
        Assert.Equal(Local(11, 24, 9), Assert.Single(log.Days).End);

        // A line still being written (no newline yet) is left alone.
        File.AppendAllText(path, Line(Local(11, 24, 13)));
        Assert.False(log.Refresh(Today));
        Assert.Equal(Local(11, 24, 9), log.Days[0].End);

        File.AppendAllText(path, "\n");
        Assert.True(log.Refresh(Today));
        Assert.Equal(Local(11, 24, 13), log.Days[0].End);

        Assert.False(log.Refresh(Today)); // nothing new
    }

    [Fact]
    public void WhatWasLearnedSurvivesARestartThroughTheCache()
    {
        Write("c.jsonl", Line(Local(11, 24, 9)), Line(Local(11, 24, 12)));
        var cache = Path.Combine(_root, "..", "ub-cache-" + Guid.NewGuid().ToString("N") + ".json");
        var first = Open(cache);
        first.Refresh(Today);

        var second = Open(cache);
        Assert.Equal(first.Days, second.Days); // before any refresh

        // And nothing is re-read: no change is reported until the file actually grows.
        Assert.False(second.Refresh(Today));
        File.Delete(cache);
    }

    [Fact]
    public void ACacheMadeForAnotherDayStartIsDiscarded()
    {
        Write("c.jsonl", Line(Local(11, 24, 9)));
        var cache = Path.Combine(_root, "..", "ub-cache-" + Guid.NewGuid().ToString("N") + ".json");
        new ActivityLog(_root, 6, cache).Refresh(Today);

        Assert.Empty(new ActivityLog(_root, 4, cache).Days);
        File.Delete(cache);
    }

    [Fact]
    public void TranscriptsOlderThanTheHorizonAreSkipped()
    {
        var path = Write("old.jsonl", Line(Local(9, 1, 9)));
        File.SetLastWriteTime(path, DateTime.Now.AddDays(-90));
        var log = Open();

        Assert.False(log.Refresh(Today));
        Assert.Empty(log.Days);
    }

    [Fact]
    public void AMissingTranscriptFolderIsNotAnError()
    {
        var log = new ActivityLog(Path.Combine(_root, "nope"), 6);

        Assert.False(log.Refresh(Today));
        Assert.Empty(log.Days);
    }
}
