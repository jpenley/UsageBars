using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace UsageBars.Tests;

/// <summary>The hover text on a pill, which says when each of its windows resets.</summary>
public class ResetTooltipTests
{
    [Fact]
    public void ScrapedClaudeWindowsKeepTheirReportedResetTimesInTooltip()
    {
        var model = UsageModel.Build(UsageSnapshot.Failed("no file"), new ClaudeCodeUsage
        {
            UsedPct = 30,
            ReadAt = Now,
            Windows = new List<ClaudeUsageWindow>
            {
                new("5h", 30, "Resets 4:50pm (America/Phoenix)"),
                new("7d", 47, "Resets Oct 6, 11pm (America/Phoenix)"),
                new("Fable", 0, "Resets Oct 6, 11pm (America/Phoenix)"),
            },
        }, new UsageHistory(), new Config());

        Assert.Equal("5h Resets 4:50pm (America/Phoenix)" + Environment.NewLine +
            "7d Resets Oct 6, 11pm (America/Phoenix)" + Environment.NewLine +
            "Reading age: 3m 20s", model.ResetTooltip(Now.AddMinutes(3).AddSeconds(20)));
        Assert.Equal(new[] { "5h", "7d" }, model.ForPill().Bars.Select(bar => bar.Label));
        Assert.Contains("Fable 100% left", model.StatusText);
        Assert.DoesNotContain("Resets", model.StatusText);
        Assert.Equal(3, model.Bars.Count);
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DesktopReadingAgeUsesSampleTimeRatherThanTimeFileWasRead()
    {
        var model = UsageModel.Build(new UsageSnapshot
        {
            FiveHourUsedPct = 30,
            SampleTime = Now.AddHours(-2).AddMinutes(-5),
        }, null, new UsageHistory(), new Config());
        Assert.Equal("Reading age: 2h 5m", model.ResetTooltip(Now));
        Assert.Equal("Reading age: 2h 6m", model.ResetTooltip(Now.AddMinutes(1)));
    }

    [Fact]
    public void CodexTooltipIncludesAgeEvenWithoutAKnownReset()
    {
        var model = UsageModel.BuildCodex(
            new CodexSnapshot(new CodexWindow(20, 300, null), null, Now),
            true, null, new Config());
        Assert.Equal("Reading age: 42s", model.ResetTooltip(Now.AddSeconds(42)));
    }

    [Fact]
    public void FailedScrapeDoesNotMakeDisplayedSampleLookNew()
    {
        var model = UsageModel.Build(new UsageSnapshot
        {
            FiveHourUsedPct = 30, SampleTime = Now.AddDays(-1).AddHours(-2),
        }, new ClaudeCodeUsage { Error = "failed", ReadAt = Now }, new UsageHistory(), new Config());
        Assert.Equal("Reading age: 1d 2h", model.ResetTooltip(Now));
    }

    [Fact]
    public void ListsEachBarWithAKnownReset()
    {
        var model = new UsageModel
        {
            Bars = new List<UsageBar>
            {
                new("5h", 60) { ResetsAt = Now.AddHours(2) },
                new("7d", 80) { ResetsAt = Now.AddDays(3) },
            },
        };

        Assert.Equal(
            "5h resets 12:00 (in 2 h)" + Environment.NewLine + "7d resets Sun 27 Sep 10:00 (in 72 h)",
            model.ResetTooltip(Now));
    }

    [Fact]
    public void SkipsBarsWhoseResetIsUnknown()
    {
        var model = new UsageModel
        {
            Bars = new List<UsageBar>
            {
                new("5h", 60),
                new("7d", 80) { ResetsAt = Now.AddMinutes(45) },
            },
        };

        Assert.Equal("7d resets 10:45 (in 45 min)", model.ResetTooltip(Now));
    }

    [Fact]
    public void IsNullWhenNoResetIsKnown()
    {
        var model = new UsageModel { Bars = new List<UsageBar> { new("usage", null) } };

        Assert.Null(model.ResetTooltip(Now));
    }
}
