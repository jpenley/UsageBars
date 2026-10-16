using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace UsageBars.Tests;

/// <summary>
/// The shared "when do I hit 0 %" projection, which every bar goes through regardless of which
/// source it came from.
/// </summary>
public class ProjectionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Now;

    private static Config Defaults() => new();

    private static IReadOnlyList<UsagePoint> Series(
        params (double HoursAgo, double UsedPct)[] points) =>
        points.Select(p => new UsagePoint(Now.AddHours(-p.HoursAgo), p.UsedPct))
              .OrderBy(p => p.At)
              .ToList();

    [Fact]
    public void ProjectsFromTheMeasuredRate()
    {
        // 30 points over 3 hours = 10 %/h; 30 % left divides into 3 hours.
        var projection = UsageVelocity.Project(
            Series((3, 40), (2, 50), (1, 60), (0, 70)), remainingPct: 30, Defaults(), Now);

        Assert.NotNull(projection);
        Assert.Equal(10, projection!.Value.PercentPerHour, precision: 3);
        Assert.Equal(3, projection.Value.HoursToEmpty, precision: 3);
        Assert.Equal(Now.AddHours(3), projection.Value.EmptyAt, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// The padding factor exists to make the *warning* fire early. A time printed in the menu is a
    /// statement about the world, so it must not carry that safety margin — otherwise the menu and
    /// the clock disagree by 25 % and neither is wrong on its own terms.
    /// </summary>
    [Fact]
    public void ProjectionIsUnpaddedEvenThoughTheWarningIsNot()
    {
        var config = Defaults();
        config.VelocityPaddingFactor = 1.25;

        var samples = Series((3, 40), (2, 50), (1, 60), (0, 70));
        var projection = UsageVelocity.Project(samples, 25, config, Now);

        var history = new UsageHistory();
        foreach (var point in samples)
            history.Samples.Add(new UsageSample { At = point.At, UsedPct = point.UsedPct });
        var warned = UsageVelocity.Estimate(history, 25, config);

        // 25 / 10 = 2.5 h measured, against 25 / (10 * 1.25) = 2 h once padded.
        Assert.Equal(2.5, projection!.Value.HoursToEmpty, precision: 3);
        Assert.Equal(2.0, warned.HoursToEmpty!.Value, precision: 3);
    }

    [Fact]
    public void FlatUsageProjectsNothing()
    {
        Assert.Null(UsageVelocity.Project(
            Series((3, 40), (2, 40), (1, 40), (0, 40)), 60, Defaults(), Now));
    }

    [Fact]
    public void DecreasingUsageProjectsNothing()
    {
        Assert.Null(UsageVelocity.Project(
            Series((3, 70), (2, 60), (1, 50), (0, 40)), 60, Defaults(), Now));
    }

    [Fact]
    public void TooFewSamplesProjectNothing()
    {
        var config = Defaults();
        config.MinVelocitySamples = 3;

        Assert.Null(UsageVelocity.Project(Series((1, 10), (0, 90)), 10, config, Now));
    }

    [Fact]
    public void TooShortASpanProjectsNothing()
    {
        var config = Defaults();
        config.MinVelocitySpanMinutes = 20;

        // Three samples, but spanning only two minutes.
        Assert.Null(UsageVelocity.Project(
            Series((2 / 60.0, 10), (1 / 60.0, 30), (0, 60)), 40, config, Now));
    }

    [Fact]
    public void AnEmptyOrSingletonSeriesProjectsNothing()
    {
        Assert.Null(UsageVelocity.Project(null, 50, Defaults(), Now));
        Assert.Null(UsageVelocity.Project(Array.Empty<UsagePoint>(), 50, Defaults(), Now));
        Assert.Null(UsageVelocity.Project(Series((0, 10)), 50, Defaults(), Now));
    }

    /// <summary>
    /// A meter barely moving divides into years. "0 % in 2031" is noise wearing the costume of a
    /// measurement, so beyond the horizon the answer is nothing at all.
    /// </summary>
    [Fact]
    public void AGlacialRateIsNotProjectedAtAll()
    {
        // 0.03 points over 3 hours = 0.01 %/h; 100 % left is over a year away.
        Assert.Null(UsageVelocity.Project(
            Series((3, 0), (2, 0.01), (1, 0.02), (0, 0.03)), 100, Defaults(), Now));
    }

    /// <summary>
    /// Consumption from before a reset must not carry into the new window, or every projection
    /// taken shortly after one reads far too soon.
    /// </summary>
    [Fact]
    public void UsageBeforeAResetIsIgnored()
    {
        // Heavy use, a reset, then a gentle 5 %/h. Only the second half may count.
        var projection = UsageVelocity.Project(
            Series((5, 80), (4, 90), (3, 5), (2, 10), (1, 15), (0, 20)),
            remainingPct: 80, Defaults(), Now);

        Assert.NotNull(projection);
        Assert.Equal(5, projection!.Value.PercentPerHour, precision: 3);
        Assert.Equal(16, projection.Value.HoursToEmpty, precision: 3);
    }
}

/// <summary>
/// Projections retained for diagnostics, per source. What matters here is that a bar without
/// enough history stays silent rather than showing a confident-looking guess.
/// </summary>
public class ModelProjectionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Now;

    private static List<UsagePoint> Rising(double perHour, double endingAt, int hours = 4)
    {
        var points = new List<UsagePoint>();
        for (var h = hours; h >= 0; h--)
            points.Add(new UsagePoint(Now.AddHours(-h), endingAt - perHour * h));
        return points;
    }

    [Fact]
    public void TheFileSourceProjectsFromItsOwnSampleSeries()
    {
        var file = new UsageSnapshot
        {
            FiveHourUsedPct = 60,
            FiveHourSeries = Rising(perHour: 10, endingAt: 60),
            SampleTime = Now,
        };

        var model = UsageModel.Build(file, null, new UsageHistory(), new Config());

        var bar = Assert.Single(model.Bars);
        Assert.Equal("5h", bar.Label);
        Assert.NotNull(bar.EmptyAt);
        // 40 % left at 10 %/h = 4 hours.
        Assert.Equal(Now.AddHours(4), bar.EmptyAt!.Value, TimeSpan.FromMinutes(1));
        Assert.Contains("5h 0% at", model.ProjectionText);
    }

    [Fact]
    public void ABarWithNoSeriesIsSilentRatherThanGuessing()
    {
        var file = new UsageSnapshot { FiveHourUsedPct = 60, SampleTime = Now };

        var model = UsageModel.Build(file, null, new UsageHistory(), new Config());

        Assert.Null(Assert.Single(model.Bars).EmptyAt);
        Assert.Equal("", model.ProjectionText);
    }

    [Fact]
    public void CodexProjectsFromTheSeriesUsageBarsAccumulated()
    {
        var history = new BarHistory
        {
            Series =
            {
                [UsageModel.CodexHistoryKey("5h")] = Rising(perHour: 5, endingAt: 40)
                    .Select(p => new UsageSample { At = p.At, UsedPct = p.UsedPct })
                    .ToList(),
            },
        };

        // 300-minute window, 40 % used -> labelled "5h" with 60 % left.
        var snapshot = new CodexSnapshot(
            new CodexWindow(40, 300, Now.AddHours(2)), null, Now);

        var model = UsageModel.BuildCodex(snapshot, true, null, new Config(), history);

        var bar = Assert.Single(model.Bars);
        Assert.NotNull(bar.EmptyAt);
        // 60 % left at 5 %/h = 12 hours.
        Assert.Equal(12, (bar.EmptyAt!.Value - Now).TotalHours, precision: 1);
    }

    /// <summary>
    /// Codex without accumulated history is the state every fresh install starts in, and it must
    /// not break the model — the bar simply carries no projection.
    /// </summary>
    [Fact]
    public void CodexWithoutHistoryStillBuilds()
    {
        var snapshot = new CodexSnapshot(
            new CodexWindow(40, 300, Now.AddHours(2)), null, Now);

        var model = UsageModel.BuildCodex(snapshot, true, null, new Config());

        Assert.Null(Assert.Single(model.Bars).EmptyAt);
        Assert.Equal("", model.ProjectionText);
    }
}

public class BarHistoryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Now;

    [Fact]
    public void DenseReadingsAreThinnedOut()
    {
        var history = new BarHistory();

        Assert.True(history.Record("codex:5h", 10, Now.AddMinutes(-30)));
        Assert.False(history.Record("codex:5h", 11, Now.AddMinutes(-29)));
        Assert.True(history.Record("codex:5h", 12, Now.AddMinutes(-20)));

        Assert.Equal(2, history.Points("codex:5h").Count);
    }

    /// <summary>
    /// A reset is the one reading that must never be thinned away: every consumer keys off the
    /// drop, and losing it makes the old window's consumption contaminate the new one.
    /// </summary>
    [Fact]
    public void AResetIsKeptHoweverCloseItFalls()
    {
        var history = new BarHistory();

        Assert.True(history.Record("codex:5h", 80, Now.AddMinutes(-10)));
        Assert.True(history.Record("codex:5h", 2, Now.AddMinutes(-9)));

        Assert.Equal(2, history.Points("codex:5h").Count);
    }

    [Fact]
    public void OutOfOrderReadingsAreIgnored()
    {
        var history = new BarHistory();

        history.Record("codex:5h", 10, Now.AddHours(-1));
        Assert.False(history.Record("codex:5h", 20, Now.AddHours(-2)));

        Assert.Single(history.Points("codex:5h"));
    }

    [Fact]
    public void SeriesAreKeptApartByKey()
    {
        var history = new BarHistory();

        history.Record("codex:5h", 10, Now.AddHours(-1));
        history.Record("codex:7d", 90, Now.AddHours(-1));

        Assert.Single(history.Points("codex:5h"));
        Assert.Single(history.Points("codex:7d"));
        Assert.Equal(2, history.TotalPoints);
        Assert.Empty(history.Points("codex:never-seen"));
    }

    [Fact]
    public void NonFiniteReadingsAreRejected()
    {
        var history = new BarHistory();

        Assert.False(history.Record("codex:5h", double.NaN, Now));
        Assert.False(history.Record("codex:5h", double.PositiveInfinity, Now));
        Assert.Empty(history.Points("codex:5h"));
    }
}
