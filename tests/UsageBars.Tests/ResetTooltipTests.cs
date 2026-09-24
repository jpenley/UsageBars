using System;
using System.Collections.Generic;
using Xunit;

namespace UsageBars.Tests;

/// <summary>The hover text on a pill, which says when each of its windows resets.</summary>
public class ResetTooltipTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);

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
