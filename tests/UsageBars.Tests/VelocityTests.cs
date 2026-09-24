using System;
using System.Collections.Generic;
using Xunit;

namespace UsageBars.Tests;

public class VelocityTests
{
    private static Config Defaults() => new();

    private static UsageHistory HistoryOf(params (double HoursAgo, double UsedPct)[] samples)
    {
        var now = DateTimeOffset.Now;
        var history = new UsageHistory();
        foreach (var (hoursAgo, used) in samples)
            history.Samples.Add(new UsageSample { At = now.AddHours(-hoursAgo), UsedPct = used });
        history.Samples.Sort((a, b) => a.At.CompareTo(b.At));
        return history;
    }

    /// <summary>
    /// The README's central claim about the reset day: it is display-only and must never move the
    /// bar, the rate, or the warning. Asserted across every day/hour combination, because the
    /// failure mode this guards against — a reset date quietly feeding the projection — would only
    /// show up on particular days and would otherwise be found in production, not in review.
    /// </summary>
    [Fact]
    public void ConfiguredResetDayNeverAffectsRateOrWarning()
    {
        var history = HistoryOf((3, 40), (2, 50), (1, 60), (0, 70));

        double? baselineRate = null;
        double? baselineHours = null;
        bool? baselineWarn = null;

        foreach (DayOfWeek day in Enum.GetValues<DayOfWeek>())
        {
            for (var hour = 0; hour < 24; hour++)
            {
                var config = Defaults();
                config.ResetDayOfWeek = day;
                config.ResetHour = hour;

                var estimate = UsageVelocity.Estimate(history, remainingPct: 30, config);
                var warn = UsageVelocity.ShouldWarn(estimate, 30, config, out _);

                if (baselineRate is null)
                {
                    baselineRate = estimate.PercentPerHour;
                    baselineHours = estimate.HoursToEmpty;
                    baselineWarn = warn;
                    continue;
                }

                Assert.Equal(baselineRate, estimate.PercentPerHour);
                Assert.Equal(baselineHours, estimate.HoursToEmpty);
                Assert.Equal(baselineWarn, warn);
            }
        }

        Assert.NotNull(baselineRate);
    }

    [Fact]
    public void RateIsMeasuredFromConsumptionOverElapsedTime()
    {
        // 30 points over 3 hours = 10 %/h.
        var history = HistoryOf((3, 40), (2, 50), (1, 60), (0, 70));

        var estimate = UsageVelocity.Estimate(history, remainingPct: 30, Defaults());

        Assert.NotNull(estimate.PercentPerHour);
        Assert.Equal(10, estimate.PercentPerHour!.Value, precision: 3);
    }

    [Fact]
    public void ProjectionAppliesThePaddingFactor()
    {
        var history = HistoryOf((3, 40), (2, 50), (1, 60), (0, 70));
        var config = Defaults();
        config.VelocityPaddingFactor = 1.25;

        var estimate = UsageVelocity.Estimate(history, remainingPct: 25, config);

        // 25 remaining / (10 %/h * 1.25) = 2 hours.
        Assert.Equal(2, estimate.HoursToEmpty!.Value, precision: 3);
    }

    [Fact]
    public void FlatUsageNeverProjectsExhaustion()
    {
        var history = HistoryOf((3, 40), (2, 40), (1, 40), (0, 40));

        var estimate = UsageVelocity.Estimate(history, remainingPct: 60, Defaults());

        Assert.Null(estimate.HoursToEmpty);
        Assert.False(UsageVelocity.ShouldWarn(estimate, 60, Defaults(), out _));
    }

    [Fact]
    public void DecreasingUsageNeverProjectsExhaustion()
    {
        var history = HistoryOf((3, 70), (2, 60), (1, 50), (0, 40));

        var estimate = UsageVelocity.Estimate(history, remainingPct: 60, Defaults());

        Assert.Null(estimate.HoursToEmpty);
    }

    [Fact]
    public void ThinDataDoesNotProduceARate()
    {
        var config = Defaults();
        config.MinVelocitySamples = 3;

        var estimate = UsageVelocity.Estimate(HistoryOf((1, 10), (0, 90)), 10, config);

        Assert.Null(estimate.PercentPerHour);
        Assert.Equal("not enough samples yet", estimate.Reason);
    }

    [Fact]
    public void ShortSpanDoesNotProduceARate()
    {
        var config = Defaults();
        config.MinVelocitySpanMinutes = 20;

        // Three samples, but spanning only two minutes.
        var estimate = UsageVelocity.Estimate(
            HistoryOf((2 / 60.0, 10), (1 / 60.0, 30), (0, 60)), 40, config);

        Assert.Null(estimate.PercentPerHour);
        Assert.Equal("sample span too short", estimate.Reason);
    }

    [Fact]
    public void LowThresholdWarnsEvenWhenUsageHasGoneQuiet()
    {
        var config = Defaults();
        config.LowThresholdPct = 10;

        var estimate = UsageVelocity.Estimate(HistoryOf((3, 40), (2, 40), (1, 40), (0, 40)), 5, config);

        Assert.True(UsageVelocity.ShouldWarn(estimate, 5, config, out var why));
        Assert.Contains("floor", why);
    }

    /// <summary>
    /// The README's worked example: 40 % remaining burning 20 %/h warns, 25 % remaining burning
    /// 1 %/h does not. That contrast is the entire argument for using velocity over a flat line.
    /// </summary>
    [Fact]
    public void VelocityBeatsAFlatLine()
    {
        var config = Defaults();
        config.WarnHoursToEmpty = 2;
        config.VelocityPaddingFactor = 1.25;
        config.LowThresholdPct = 10;

        // 20 %/h over three hours, 40 % left -> 40 / 25 = 1.6 h.
        var fast = UsageVelocity.Estimate(HistoryOf((3, 0), (2, 20), (1, 40), (0, 60)), 40, config);
        Assert.True(UsageVelocity.ShouldWarn(fast, 40, config, out _));

        // 1 %/h over three hours, 25 % left -> 20 h.
        var slow = UsageVelocity.Estimate(HistoryOf((3, 0), (2, 1), (1, 2), (0, 3)), 25, config);
        Assert.False(UsageVelocity.ShouldWarn(slow, 25, config, out _));
    }

    /// <summary>
    /// A drop in usage means the window reset or was topped up. Rate must be measured from after
    /// that point, or the old window's consumption contaminates the new window's projection.
    /// </summary>
    [Fact]
    public void HistoryBeforeAResetIsIgnored()
    {
        var history = HistoryOf((5, 80), (4, 90), (3, 5), (2, 10), (1, 15), (0, 20));

        var window = history.CurrentWindow();

        Assert.Equal(4, window.Count);
        Assert.Equal(5, window[0].UsedPct);
    }

    [Fact]
    public void MeasuredResetOverridesTheConfiguredDay()
    {
        var config = Defaults();
        config.UseConfiguredResetDay = true;
        var measured = DateTimeOffset.Now.AddHours(3);

        var estimate = UsageVelocity.Estimate(
            HistoryOf((3, 40), (2, 50), (1, 60), (0, 70)), 30, config, measured);

        Assert.Equal(measured, estimate.NextReset);
        Assert.Contains("measured", estimate.ResetBasis);
    }

    [Fact]
    public void WithoutAMeasuredResetAndWithoutTheFallbackTheResetIsUnknown()
    {
        var config = Defaults();
        Assert.False(config.UseConfiguredResetDay);

        var estimate = UsageVelocity.Estimate(
            HistoryOf((3, 40), (2, 50), (1, 60), (0, 70)), 30, config);

        Assert.Null(estimate.NextReset);
        Assert.Equal("unknown", estimate.ResetBasis);
    }
}
