using System;
using System.Reflection;
using System.Linq;
using Xunit;

namespace UsageBars.Tests;

public class ClaudeCodeUsageTests
{
    [Theory]
    [InlineData("")]
    [InlineData("Esc to cancel\n\n")]
    public void UnlabelledWeeklyRedrawCannotReplaceSessionReset(string separator)
    {
        // A terminal refresh may repaint a percentage and reset without its heading.
        // Keep the last attributable readings instead of guessing which meter changed.
        var screen = "Current session\n11% used\nResets 12:50pm (America/Phoenix)\n" +
            "Current week (all models)\n77% used\nResets Oct 6, 11pm (America/Phoenix)\n" +
            separator + "78% used\nResets Oct 6, 11pm (America/Phoenix)\n";
        var parse = typeof(ClaudeCodeUsageReader).GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (ClaudeCodeUsage)parse.Invoke(null, new object[] { screen })!;
        var model = UsageModel.Build(UsageSnapshot.Failed("no file"), result,
            new UsageHistory(), new Config());

        Assert.Contains("5h Resets 12:50pm (America/Phoenix)", model.ResetTooltip(result.ReadAt!.Value));
        Assert.Equal(89, model.Bars.Single(b => b.Label == "5h").RemainingPct);
        Assert.Equal(23, model.Bars.Single(b => b.Label == "7d").RemainingPct);
        Assert.Contains("7d Resets Oct 6, 11pm (America/Phoenix)", model.ResetTooltip(result.ReadAt!.Value));
    }

    [Fact]
    public void SessionPercentageDoesNotComeFromLastModelWindow()
    {
        var screen = "Current session\n28% 28% used\nResets 4:50pm (America/Phoenix)\n" +
            "Current week (all models)\n46% 46% used\nResets Oct 6, 11pm (America/Phoenix)\n" +
            "Esc to cancel\n\nCurrent session\n29% 29% used\nResets 4:49pm (America/Phoenix)\n" +
            "Current week (all models)\n47% 47% used\nResets Oct 6, 10:59pm (America/Phoenix)\n" +
            "Current week (Fable)\n0% 0% used\nResets Oct 6, 11pm (America/Phoenix)\n";
        var parse = typeof(ClaudeCodeUsageReader).GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (ClaudeCodeUsage)parse.Invoke(null, new object[] { screen })!;
        Assert.Equal(29, result.UsedPct);
        Assert.Equal("Current session", result.MeterLabel);
        var model = UsageModel.Build(new UsageSnapshot
        {
            FiveHourUsedPct = 29, SevenDayUsedPct = 10,
            SampleTime = DateTimeOffset.Now.AddHours(-15),
        }, result, new UsageHistory(), new Config());
        Assert.Equal(71, model.Bars.Single(b => b.Label == "5h").RemainingPct);
        Assert.Equal(53, model.Bars.Single(b => b.Label == "7d").RemainingPct);
        Assert.Equal(100, model.Bars.Single(b => b.Label == "Fable").RemainingPct);
        Assert.Contains("4:49pm", model.ResetTooltip(result.ReadAt!.Value));
        Assert.Contains("10:59pm", model.ResetTooltip(result.ReadAt!.Value));
    }

    [Fact]
    public void CreditMeterStillUsesLatestReading()
    {
        var parse = typeof(ClaudeCodeUsageReader).GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (ClaudeCodeUsage)parse.Invoke(null, new object[] {
            "Claude Code and Cowork credit\n5% used\nOne-time credit · Expires October 14\n\n6% used\n"
        })!;
        Assert.Equal(6, result.UsedPct);
        Assert.Equal("credit", result.ShortLabel);
        Assert.Empty(result.Windows);
    }
}
