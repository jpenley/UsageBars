using System;
using System.Collections.Generic;
using Xunit;

namespace UsageBars.Tests;

public class ResetEstimatorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 30, 18, 0, 0, TimeSpan.FromHours(-7));

    private static List<UsagePoint> Series(params (double HoursAgo, double UsedPct)[] points)
    {
        var list = new List<UsagePoint>();
        foreach (var (hoursAgo, used) in points)
            list.Add(new UsagePoint(Now.AddHours(-hoursAgo), used));
        list.Sort((a, b) => a.At.CompareTo(b.At));
        return list;
    }

    /// <summary>
    /// The shape observed in a real plan-usage-history.json: usage climbs, drops to zero at the
    /// reset, then climbs again. The next deadline is one window after usage <em>resumed</em>,
    /// which is not the same as one window after the reset — the gap between them is idle time.
    /// </summary>
    [Fact]
    public void NextResetIsOneWindowAfterUsageResumes()
    {
        // Reset 4h ago, idle for an hour, usage resumed 3h ago.
        var series = Series(
            (6, 20), (5, 27),
            (4, 0),
            (3.5, 0),
            (3, 4), (2, 8), (1, 11), (0, 13));

        var reset = ResetEstimator.NextReset(series, ResetEstimator.FiveHourWindow, Now);

        Assert.NotNull(reset);
        Assert.Equal(Now.AddHours(-3).AddHours(5), reset!.Value);
    }

    [Fact]
    public void WindowStartIsTheFirstNonIdleReadingAfterTheReset()
    {
        var series = Series((6, 27), (4, 0), (3.5, 0), (3, 4), (0, 13));

        var start = ResetEstimator.CurrentWindowStart(series, Now);

        Assert.Equal(Now.AddHours(-3), start!.Value);
    }

    /// <summary>
    /// If the window began before the retained samples, its deadline is genuinely unknowable.
    /// Guessing would put a confident wrong time on the tooltip.
    /// </summary>
    [Fact]
    public void NoResetInHistoryAndAlreadyBusyIsUnknown()
    {
        var series = Series((3, 40), (2, 50), (1, 60), (0, 70));

        Assert.Null(ResetEstimator.CurrentWindowStart(series, Now));
        Assert.Null(ResetEstimator.NextReset(series, ResetEstimator.FiveHourWindow, Now));
    }

    [Fact]
    public void HistoryThatStartsIdleDatesTheWindowWithoutSeeingAReset()
    {
        // Begins at zero, so the window demonstrably started inside the retained samples.
        var series = Series((5, 0), (4, 0), (3, 5), (0, 20));

        var start = ResetEstimator.CurrentWindowStart(series, Now);

        Assert.Equal(Now.AddHours(-3), start!.Value);
    }

    [Fact]
    public void ResetWithNoUsageSinceMeansNothingIsPending()
    {
        var series = Series((5, 40), (2, 0), (1, 0), (0, 0));

        Assert.Null(ResetEstimator.NextReset(series, ResetEstimator.FiveHourWindow, Now));
    }

    [Fact]
    public void ADeadlineAlreadyPassedIsReportedAsUnknown()
    {
        // Usage resumed nine hours ago on a five-hour window, and the drop was never recorded —
        // which is what happens when Claude Desktop is closed over the boundary.
        var series = Series((12, 30), (10, 0), (9, 5), (8, 10));

        Assert.Null(ResetEstimator.NextReset(series, ResetEstimator.FiveHourWindow, Now));
    }

    [Fact]
    public void EmptySeriesIsUnknown()
    {
        Assert.Null(ResetEstimator.NextReset(new List<UsagePoint>(), ResetEstimator.FiveHourWindow, Now));
        Assert.Null(ResetEstimator.CurrentWindowStart(new List<UsagePoint>(), Now));
    }

    [Fact]
    public void SevenDayWindowUsesItsOwnLength()
    {
        var series = Series((49, 30), (48, 0), (47, 3), (0, 12));

        var reset = ResetEstimator.NextReset(series, ResetEstimator.SevenDayWindow, Now);

        Assert.Equal(Now.AddHours(-47).AddDays(7), reset!.Value);
    }

    // ---- meter detail parsing -------------------------------------------------

    [Fact]
    public void ParsesAnExpiryDate()
    {
        var reset = ResetEstimator.ParseMeterDetail("One-time credit · Expires October 14", Now);

        Assert.NotNull(reset);
        Assert.Equal(10, reset!.Value.Month);
        Assert.Equal(14, reset.Value.Day);
        Assert.Equal(2026, reset.Value.Year);
    }

    [Fact]
    public void ADateAlreadyPastRollsToNextYear()
    {
        // "January 4" read in July means next January.
        var reset = ResetEstimator.ParseMeterDetail("Expires January 4", Now);

        Assert.Equal(2027, reset!.Value.Year);
        Assert.Equal(1, reset.Value.Month);
    }

    [Theory]
    [InlineData("Resets 3pm", 15, 0)]
    [InlineData("Resets at 15:30", 15, 30)]
    [InlineData("resets 9am", 9, 0)]
    [InlineData("Resets 12am", 0, 0)]
    [InlineData("Resets 12pm", 12, 0)]
    public void ParsesAResetTime(string detail, int expectedHour, int expectedMinute)
    {
        var reset = ResetEstimator.ParseMeterDetail(detail, Now);

        Assert.NotNull(reset);
        Assert.Equal(expectedHour, reset!.Value.Hour);
        Assert.Equal(expectedMinute, reset.Value.Minute);
        Assert.True(reset.Value > Now, "a reset time must always be in the future");
    }

    [Fact]
    public void ParsesADayFirstDate()
    {
        var reset = ResetEstimator.ParseMeterDetail("Resets on 14 October", Now);

        Assert.Equal(10, reset!.Value.Month);
        Assert.Equal(14, reset.Value.Day);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Claude Code and Cowork credit")]
    [InlineData("Expires Smarch 40")]
    [InlineData("Resets 99pm")]
    public void UnparseableDetailIsNullRatherThanAGuess(string? detail)
    {
        Assert.Null(ResetEstimator.ParseMeterDetail(detail, Now));
    }

    [Fact]
    public void DescribesAResetRelativeToNow()
    {
        Assert.Equal("unknown", ResetEstimator.Describe(null, Now));
        Assert.Equal("due", ResetEstimator.Describe(Now.AddMinutes(-1), Now));
        Assert.Contains("in 3", ResetEstimator.Describe(Now.AddHours(3), Now));
    }
}
