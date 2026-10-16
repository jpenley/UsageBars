using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Media;
using Xunit;

namespace UsageBars.Tests;

public class ResetPaceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 13, 0, 0, TimeSpan.Zero);

    private static UsageBar Timed(double? remaining = 50) => new("5h", remaining)
    {
        WindowDuration = TimeSpan.FromHours(5), ResetsAt = Now.AddHours(2.5),
    };

    [Theory]
    [InlineData(32, 248, 81, 73)]
    [InlineData(42, 239, 146, 72)]
    [InlineData(48, 232, 197, 71)]
    [InlineData(50, 232, 197, 71)]
    [InlineData(52, 232, 197, 71)]
    [InlineData(58, 157, 192, 75)]
    [InlineData(68, 63, 185, 80)]
    public void FillDependsOnDistanceFromResetRatherThanAbsoluteAllowance(double remaining, byte red, byte green, byte blue)
    {
        var pace = ResetPace.For(Timed(remaining), Now)!.Value;
        Assert.Equal(50, pace.TimeRemainingPct);
        Assert.Equal(Color.FromRgb(red, green, blue), pace.FillColor);
    }

    [Fact]
    public void ClockAdvancesThePointerAndColorWithoutANewProviderReading()
    {
        var bar = Timed(50);
        var earlier = ResetPace.For(bar, Now)!.Value;
        var later = ResetPace.For(bar, Now.AddMinutes(60))!.Value;
        Assert.Equal(50, earlier.TimeRemainingPct);
        Assert.Equal(30, later.TimeRemainingPct);
        Assert.Equal(Color.FromRgb(63, 185, 80), later.FillColor);
    }

    [Fact]
    public void CountdownClampsAtBothEndsAndNeverInventsANewReset()
    {
        Assert.Equal(100, ResetPace.For(Timed() with { ResetsAt = Now.AddHours(8) }, Now)!.Value.TimeRemainingPct);
        Assert.Equal(0, ResetPace.For(Timed() with { ResetsAt = Now.AddHours(-1) }, Now)!.Value.TimeRemainingPct);
    }

    [Fact]
    public void UnknownOrInvalidDataCannotProduceAPace()
    {
        foreach (var bar in new[]
        {
            Timed() with { ResetsAt = null }, Timed() with { WindowDuration = null },
            Timed() with { WindowDuration = TimeSpan.Zero }, Timed() with { WindowDuration = TimeSpan.FromHours(-1) },
            Timed(null), Timed(double.NaN), Timed(double.PositiveInfinity),
        }) Assert.Null(ResetPace.For(bar, Now));
    }

    [Fact]
    public void DesktopAndScrapedClaudeCarryTheirOwnWindowLengths()
    {
        var desktop = UsageModel.Build(new UsageSnapshot
        {
            FiveHourUsedPct = 25, SevenDayUsedPct = 50, SampleTime = DateTimeOffset.Now,
            FiveHourResetsAt = Now.AddHours(2.5), SevenDayResetsAt = Now.AddDays(3.5),
        }, null, new UsageHistory(), new Config());
        Assert.Equal(TimeSpan.FromHours(5), desktop.Bars[0].WindowDuration);
        Assert.Equal(TimeSpan.FromDays(7), desktop.Bars[1].WindowDuration);
        Assert.All(desktop.Bars, bar => Assert.Equal(50, ResetPace.For(bar, Now)!.Value.TimeRemainingPct));

        var scraped = UsageModel.Build(UsageSnapshot.Failed("missing"), new ClaudeCodeUsage
        {
            UsedPct = 25, ReadAt = Now,
            Windows = new List<ClaudeUsageWindow>
            {
                new("5h", 25, "Resets 8:30am (America/Phoenix)"),
                new("7d", 50, "Resets Oct 19, 6pm (America/Phoenix)"),
            },
        }, new UsageHistory(), new Config());
        Assert.Equal(TimeSpan.FromHours(5), scraped.Bars[0].WindowDuration);
        Assert.Equal(Now.AddHours(2.5), scraped.Bars[0].ResetsAt);
        Assert.Equal(TimeSpan.FromDays(7), scraped.Bars[1].WindowDuration);
        Assert.Equal(50, ResetPace.For(scraped.Bars[1], Now)!.Value.TimeRemainingPct);
    }

    [Fact]
    public void TrayStatusOmitsResetTimesAndRatesForEverySource()
    {
        var config = new Config();
        var noFile = UsageSnapshot.Failed("missing");
        var models = new[]
        {
            UsageModel.Build(new UsageSnapshot { FiveHourUsedPct = 25, SampleTime = DateTimeOffset.Now, FiveHourResetsAt = Now.AddHours(1) }, null, new UsageHistory(), config),
            UsageModel.Build(noFile, new ClaudeCodeUsage { UsedPct = 25, ReadAt = Now, Windows = new[] { new ClaudeUsageWindow("5h", 25, "Resets 8:30am (America/Phoenix)") } }, new UsageHistory(), config),
            UsageModel.Build(noFile, new ClaudeCodeUsage { UsedPct = 25, ReadAt = Now, MeterLabel = "credit", MeterDetail = "Resets 3pm" }, new UsageHistory(), config),
            UsageModel.BuildCodex(new CodexSnapshot(new CodexWindow(25, 300, Now.AddHours(1)), null, DateTimeOffset.Now), true, null, config),
        };
        foreach (var model in models)
        {
            Assert.Contains("75% left", model.StatusText);
            Assert.DoesNotContain("reset", model.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("rate", model.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("%/h", model.StatusText);
            Assert.Equal(PillAction.None, model.Action);
            Assert.Null(model.ActionUrl);
        }
    }

    [Fact]
    public void WeeklyModelWindowKeepsNominalDurationIndependentlyOfItsLabel()
    {
        var parse = typeof(ClaudeCodeUsageReader).GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Static)!;
        var reading = (ClaudeCodeUsage)parse.Invoke(null, new object[]
        {
            "Current session\n25% used\nResets 8:30am (America/Phoenix)\n" +
            "Current week (Sonnet)\n50% used\nResets Oct 19, 6pm (America/Phoenix)\n",
        })!;
        var model = UsageModel.Build(UsageSnapshot.Failed("missing"), reading with { ReadAt = Now }, new UsageHistory(), new Config());
        var weekly = model.ForPill().Bars.Single(bar => bar.Label == "Sonnet");
        Assert.Equal(TimeSpan.FromDays(7), weekly.WindowDuration);
        Assert.Equal(50, ResetPace.For(weekly, Now)!.Value.TimeRemainingPct);
    }
}

public class WindowResetParsingTests
{
    [Theory]
    [InlineData("2026-10-16T12:59:59Z")]
    [InlineData("2026-10-16T13:00:00Z")]
    [InlineData("2026-10-16T13:00:05Z")]
    public void RecentlyExpiredResetKeepsItsDeadlineAcrossTheDisplayedMinute(string observed)
    {
        foreach (var text in new[] { "Resets 6am (America/Phoenix)", "Resets Oct 16, 6am (America/Phoenix)" })
            Assert.Equal(DateTimeOffset.Parse("2026-10-16T13:00:00Z"), ResetEstimator.ParseWindowReset(text, DateTimeOffset.Parse(observed), TimeSpan.FromHours(5)));
    }

    [Theory]
    [InlineData("Resets 8am (America/Phoenix)", "2026-10-16T13:00:00Z", "2026-10-16T15:00:00Z", 5)]
    [InlineData("Resets 1am (America/Phoenix)", "2026-10-16T06:00:00Z", "2026-10-16T08:00:00Z", 5)]
    [InlineData("Resets Oct 18, 11pm (America/Phoenix)", "2026-10-16T13:00:00Z", "2026-10-19T06:00:00Z", 168)]
    [InlineData("Resets Jan 2, 1pm (UTC)", "2026-12-31T23:00:00Z", "2027-01-02T13:00:00Z", 168)]
    public void ResolvesTheClockDateAndReportedTimeZone(string text, string observed, string expected, int hours)
    {
        Assert.Equal(DateTimeOffset.Parse(expected), ResetEstimator.ParseWindowReset(text, DateTimeOffset.Parse(observed), TimeSpan.FromHours(hours)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Resets in 5 hours")]
    [InlineData("Resets 13pm (UTC)")]
    [InlineData("Resets 8:99am (UTC)")]
    [InlineData("Resets Feb 30, 8am (UTC)")]
    [InlineData("Resets 8am (Unknown/Zone)")]
    [InlineData("Resets ٨am (UTC)")]
    public void UnsupportedResetTextStaysUnknown(string? text)
    {
        Assert.Null(ResetEstimator.ParseWindowReset(text, DateTimeOffset.Parse("2026-10-16T13:00:00Z"), TimeSpan.FromHours(5)));
    }

    [Fact]
    public void AnAmbiguousDstClockTimeStaysUnknown()
    {
        Assert.Null(ResetEstimator.ParseWindowReset("Resets 2:30am (America/New_York)", DateTimeOffset.Parse("2026-03-08T05:00:00Z"), TimeSpan.FromHours(5)));
        Assert.Null(ResetEstimator.ParseWindowReset("Resets 1:30am (America/New_York)", DateTimeOffset.Parse("2026-11-01T04:00:00Z"), TimeSpan.FromHours(5)));
    }
}
