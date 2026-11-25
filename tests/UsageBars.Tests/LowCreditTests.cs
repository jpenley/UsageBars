using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace UsageBars.Tests;

public class LowCreditTests
{
    private const string Meter = "$285.75 / $350.00 spent · Resets Dec 1 (America/Phoenix)";

    // ---- Dollar parsing and display -------------------------------------------------

    [Fact]
    public void SpendAmountReadsDollarsFromTheMeterLine()
    {
        var spend = SpendAmount.Parse(Meter);

        Assert.NotNull(spend);
        Assert.Equal(285.75, spend!.Value.SpentUsd);
        Assert.Equal(350.00, spend.Value.LimitUsd);
        Assert.Equal(64.25, spend.Value.RemainingUsd, 2);
        Assert.Equal(81.643, spend.Value.UsedPct, 3);
    }

    [Theory]
    [InlineData("One-time credit · Expires October 14")]
    [InlineData("$5.00 / $0.00 spent")]
    [InlineData("")]
    [InlineData(null)]
    public void SpendAmountIsNullWhenTheLineHasNoUsableDollars(string? detail) =>
        Assert.Null(SpendAmount.Parse(detail));

    [Fact]
    public void SpendAmountAcceptsThousandsSeparators()
    {
        var spend = SpendAmount.Parse("$1,250.50 / $2,000 spent");
        Assert.Equal(1250.50, spend!.Value.SpentUsd);
        Assert.Equal(2000, spend.Value.LimitUsd);
    }

    [Fact]
    public void BarUsesExactDollarsInsteadOfTheRoundedPercent()
    {
        var model = UsageModel.Build(
            UsageSnapshot.Failed("none"),
            new ClaudeCodeUsage
            {
                UsedPct = 82, MeterLabel = "Usage credits", MeterDetail = Meter, ReadAt = DateTimeOffset.Now,
            },
            new UsageHistory(), new Config());

        var bar = Assert.Single(model.Bars);
        Assert.Equal("C", bar.Label);
        Assert.Equal(100 - 81.643, bar.RemainingPct!.Value, 2);
        Assert.Equal(64.25, bar.RemainingUsd!.Value, 2);
        Assert.Equal("$64", OverlayView.PercentText(bar));
    }

    [Fact]
    public void ABarWithoutDollarsStillShowsAPercent()
    {
        var model = UsageModel.Build(
            UsageSnapshot.Failed("none"),
            new ClaudeCodeUsage { UsedPct = 47, MeterLabel = "credit", ReadAt = DateTimeOffset.Now },
            new UsageHistory(), new Config());

        Assert.Equal("53%", OverlayView.PercentText(Assert.Single(model.Bars)));
    }

    [Theory]
    [InlineData(64.25, "$64")]
    [InlineData(1234.4, "$1,234")]
    [InlineData(9.5, "$9.50")]
    [InlineData(0, "$0.00")]
    public void MoneyShowsWholeDollarsFromTenUpAndCentsBelow(double usd, string expected) =>
        Assert.Equal(expected, UsageModel.Money(usd));

    // ---- History keeps the dollars --------------------------------------------------

    [Fact]
    public void HistoryKeepsDollarsAndPrefersThemForThePreciseSeries()
    {
        var history = new UsageHistory();
        var at = DateTimeOffset.Now.AddHours(-1);
        Assert.True(history.Record(82, "Usage credits", Meter, at, SpendAmount.Parse(Meter)));

        var point = Assert.Single(history.Points());
        Assert.Equal(81.643, point.UsedPct, 3);

        var reloaded = JsonSerializer.Deserialize<UsageHistory>(JsonSerializer.Serialize(history))!;
        Assert.Equal(285.75, reloaded.Samples[0].SpentUsd);
    }

    [Fact]
    public void SamplesWrittenBeforeDollarsExistStillLoad()
    {
        var old = JsonSerializer.Deserialize<UsageHistory>(
            """{"samples":[{"t":"2026-11-01T10:00:00-07:00","u":40}],"label":"x","detail":"y"}""")!;

        var point = Assert.Single(old.Points());
        Assert.Equal(40, point.UsedPct);
        Assert.Null(old.Samples[0].SpentUsd);
    }

    [Fact]
    public void ACentOfSpendIsAChangeEvenInsideTheSpacingWindow()
    {
        var history = new UsageHistory();
        var at = DateTimeOffset.Now.AddMinutes(-10);
        Assert.True(history.Record(81, "c", "", at, new SpendAmount(285.75, 350)));
        Assert.False(history.Record(81, "c", "", at.AddMinutes(1), new SpendAmount(285.75, 350)));
        Assert.True(history.Record(81, "c", "", at.AddMinutes(2), new SpendAmount(285.76, 350)));
    }

    // ---- Projection runs in dollars --------------------------------------------------

    private static UsageModel ModelSpendingAt(double usdPerHour, double spentNow, double limit = 350)
    {
        var now = DateTimeOffset.Now;
        var history = new UsageHistory();
        for (var minutes = -240; minutes <= 0; minutes += 30)
        {
            var spent = spentNow + usdPerHour * minutes / 60.0;
            var amount = new SpendAmount(spent, limit);
            history.Record(Math.Round(amount.UsedPct), "Usage credits", "", now.AddMinutes(minutes), amount);
        }

        var detail = $"${spentNow:0.00} / ${limit:0.00} spent";
        return UsageModel.Build(
            UsageSnapshot.Failed("none"),
            new ClaudeCodeUsage
            {
                UsedPct = Math.Round(new SpendAmount(spentNow, limit).UsedPct),
                MeterLabel = "Usage credits", MeterDetail = detail, ReadAt = now,
            },
            history, new Config());
    }

    [Fact]
    public void ProjectionUsesTheMeasuredDollarRate()
    {
        var model = ModelSpendingAt(usdPerHour: 4, spentNow: 285.75);
        var bar = Assert.Single(model.Bars);

        Assert.Equal(4, bar.UsdPerHour!.Value, 1);
        var hours = (bar.EmptyAt!.Value - DateTimeOffset.Now).TotalHours;
        Assert.InRange(hours, 15.5, 16.5); // $64.25 left at $4/h
    }

    [Fact]
    public void TooltipStatesDollarsLeftAndWhereDepletionFallsAgainstTheReset()
    {
        var model = ModelSpendingAt(usdPerHour: 4, spentNow: 285.75);

        var tip = model.ResetTooltip(DateTimeOffset.Now)!;

        Assert.Contains("C $64 of $350 left", tip);
        Assert.Contains("at $4.00/h lasts until", tip);
    }

    [Fact]
    public void TooltipSaysSoWhenThereIsNoRateYet()
    {
        var bar = new UsageBar("C", 18) { Spend = new SpendAmount(285.75, 350) };
        Assert.Equal("no spend rate yet", UsageModel.DepletionText(bar, DateTimeOffset.Now));
    }

    // ---- When the insights run triggers ---------------------------------------------

    private static UsageModel Credit(
        double remainingPct, DateTimeOffset? emptyAt, DateTimeOffset? resetsAt, bool stale = false,
        string label = "C") =>
        new()
        {
            Stale = stale,
            Bars = new[] { new UsageBar(label, remainingPct) { EmptyAt = emptyAt, ResetsAt = resetsAt } },
        };

    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.FromHours(-7));
    private static readonly DateTimeOffset Reset = new(2026, 12, 1, 0, 0, 0, TimeSpan.FromHours(-7));

    [Fact]
    public void FiresWhenTwoHoursOrLessRemainAndItEmptiesBeforeTheReset()
    {
        var decision = LowCreditTrigger.Evaluate(
            Credit(5, Now.AddHours(1.5), Reset), new Config(), Now, firedCycle: null);

        Assert.True(decision.Fire);
        Assert.Equal("2026-12-01", decision.Cycle);
    }

    [Fact]
    public void FiresAtExactlyTheThreshold() =>
        Assert.True(LowCreditTrigger.Evaluate(
            Credit(5, Now.AddHours(2), Reset), new Config(), Now, null).Fire);

    [Fact]
    public void WaitsWhileMoreThanTheThresholdRemains() =>
        Assert.False(LowCreditTrigger.Evaluate(
            Credit(20, Now.AddHours(3), Reset), new Config(), Now, null).Fire);

    [Fact]
    public void DoesNotFireWhenTheWindowResetsBeforeItEmpties() =>
        Assert.False(LowCreditTrigger.Evaluate(
            Credit(5, Reset.AddHours(1), Reset), new Config(), Now.AddDays(6.99), null).Fire);

    [Fact]
    public void FiresOnceThenRearmsWhenTheCycleChanges()
    {
        var first = LowCreditTrigger.Evaluate(
            Credit(5, Now.AddHours(1), Reset), new Config(), Now, null);
        Assert.True(first.Fire);

        Assert.False(LowCreditTrigger.Evaluate(
            Credit(4, Now.AddHours(1), Reset), new Config(), Now, first.Cycle).Fire);

        var nextReset = Reset.AddMonths(1);
        Assert.True(LowCreditTrigger.Evaluate(
            Credit(4, Now.AddHours(1), nextReset), new Config(), Now, first.Cycle).Fire);
    }

    [Fact]
    public void FiresWhenTheCreditIsAlreadyUsedUpEvenWithNoRate() =>
        Assert.True(LowCreditTrigger.Evaluate(
            Credit(0, null, Reset), new Config(), Now, null).Fire);

    [Fact]
    public void DoesNotFireWithNoRateOrOnAStaleReading()
    {
        Assert.False(LowCreditTrigger.Evaluate(
            Credit(5, null, Reset), new Config(), Now, null).Fire);
        Assert.False(LowCreditTrigger.Evaluate(
            Credit(5, Now.AddHours(1), Reset, stale: true), new Config(), Now, null).Fire);
    }

    [Fact]
    public void IgnoresRollingWindowsAndCanBeSwitchedOff()
    {
        Assert.False(LowCreditTrigger.Evaluate(
            Credit(5, Now.AddHours(1), Reset, label: "5h"), new Config(), Now, null).Fire);

        var model = Credit(5, Now.AddHours(1), Reset);
        Assert.False(LowCreditTrigger.Evaluate(
            model, new Config { InsightsOnLowCredit = false }, Now, null).Fire);
        Assert.False(LowCreditTrigger.Evaluate(
            model, new Config { InsightsTriggerHours = 0 }, Now, null).Fire);
    }

    // ---- What the new terminal runs --------------------------------------------------

    [Fact]
    public void ScriptStartsAFreshClaudeAndClearsTheProbeMarker()
    {
        var script = InsightsLauncher.BuildScript(@"C:\Users\o'neil\claude.exe", withRequest: false);

        Assert.Contains("Remove-Item Env:CLAUDE_CODE_CHILD_SESSION", script);
        Assert.Contains(@"& 'C:\Users\o''neil\claude.exe' '/insights'", script);
        Assert.DoesNotContain(InsightsLauncher.FillInstructionsFile, script);
    }

    [Fact]
    public void ScriptAddsASecondFreshInstanceForTheFormOnlyWhenConfigured()
    {
        var script = InsightsLauncher.BuildScript(@"C:\claude.exe", withRequest: true);

        var claudeRuns = script.Split('\n').Count(line => line.StartsWith("& 'C:"));
        Assert.Equal(2, claudeRuns);
        Assert.Contains(InsightsLauncher.FillInstructionsFile, script);
    }

    private static readonly RequestUsageSettings Request = new()
    {
        Url = "https://example.test/run",
        ModelChoice = "Because reasons",
        TokenUse = { "3a", "3B", "3c" },
        ExtraNotes = "",
    };

    [Fact]
    public void InstructionsCarryTheAnswersAndForbidSubmitting()
    {
        var text = InsightsLauncher.BuildFillInstructions(Request, @"C:\r\report.html");

        Assert.Contains("https://example.test/run", text);
        Assert.Contains("Because reasons", text);
        Assert.Contains("Tick only: 3a, 3b, 3c", text);
        Assert.Contains("Select **Confirm**", text);
        Assert.Contains(@"C:\r\report.html", text);
        Assert.Contains("Never click Run", text);
        Assert.Contains("sign in in the Playwright browser window themselves", text);
        Assert.Contains("Leave empty", text);
        Assert.DoesNotContain("Field 4 answer", text);
    }

    [Fact]
    public void ExtraNotesAppearOnlyWhenSet()
    {
        var withNotes = new RequestUsageSettings
        {
            Url = Request.Url, ModelChoice = "m", ExtraNotes = "thanks",
        };
        var text = InsightsLauncher.BuildFillInstructions(withNotes, "r.html");

        Assert.Contains("## Field 4 answer", text);
        Assert.Contains("thanks", text);
        Assert.Contains("(none)", text);
    }

    // ---- Config: values stay local ---------------------------------------------------

    [Fact]
    public void OnlyHttpsAddressesSurviveConfigNormalisation()
    {
        var config = new Config { RequestUsage = { Url = "file:///c:/windows/system32/calc.exe" } };
        config.Normalize();

        Assert.Equal("", config.RequestUsage.Url);
        Assert.Contains(config.Adjustments, note => note.Contains("https"));

        var good = new Config { RequestUsage = { Url = "https://example.test/x" } };
        good.Normalize();
        Assert.Equal("https://example.test/x", good.RequestUsage.Url);
    }

    [Fact]
    public void AMissingRequestBlockInConfigIsHarmless()
    {
        var config = JsonSerializer.Deserialize<Config>("""{"RequestUsage": null}""")!;
        config.Normalize();

        Assert.NotNull(config.RequestUsage);
        Assert.False(config.RequestUsage.HasUsableUrl);
    }

    [Fact]
    public void TheShippedDefaultConfigCarriesNoRequestValues()
    {
        var path = FindUp("UsageBars.config.default.json");
        var raw = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<Config>(raw, new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        })!;

        Assert.Equal("", config.RequestUsage.Url);
        Assert.Equal("", config.RequestUsage.ModelChoice);
        Assert.Empty(config.RequestUsage.TokenUse);
        Assert.Equal("", config.RequestUsage.ExtraNotes);
        Assert.DoesNotContain("octopus", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("paylocity", raw, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindUp(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, name);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(name);
    }
}
