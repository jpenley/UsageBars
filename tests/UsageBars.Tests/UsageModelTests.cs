using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace UsageBars.Tests;

public class UsageModelTests
{
    [Fact]
    public void FreshScrapeReplacesStaleDesktopHistory()
    {
        var scraped = new ClaudeCodeUsage { UsedPct = 47, MeterLabel = "credit", ReadAt = DateTimeOffset.Now };
        var model = UsageModel.Build(FileWith(29, 10, DateTimeOffset.Now.AddHours(-15)),
            scraped, new UsageHistory(), new Config());
        Assert.Equal(53, Assert.Single(model.Bars).RemainingPct);
        Assert.False(model.Stale);
        Assert.True(UsageModel.NeedsClaudeFallback(FileWith(29, 10, DateTimeOffset.Now.AddHours(-15)), new Config()));
        Assert.False(UsageModel.NeedsClaudeFallback(FileWith(29, 10), new Config()));
    }

    [Fact]
    public void AncientCachedCreditDoesNotReplaceMoreRecentDesktopHistory()
    {
        var model = UsageModel.Build(FileWith(29, 10, DateTimeOffset.Now.AddHours(-15)),
            new ClaudeCodeUsage { UsedPct = 12, ReadAt = DateTimeOffset.Now.AddDays(-60) },
            new UsageHistory(), new Config());
        Assert.Equal(90, model.Bars.Single(b => b.Label == "7d").RemainingPct);
        Assert.True(model.Stale);
    }

    private static UsageSnapshot FileWith(double? fh, double? sd, DateTimeOffset? sampled = null) =>
        new()
        {
            FiveHourUsedPct = fh,
            SevenDayUsedPct = sd,
            SampleTime = sampled ?? DateTimeOffset.Now,
            SourceLabel = "claude-msix-history",
        };

    [Fact]
    public void FileSourceWinsWhenItHasValues()
    {
        var scraped = new ClaudeCodeUsage { UsedPct = 90, MeterLabel = "credit", ReadAt = DateTimeOffset.Now };

        var model = UsageModel.Build(FileWith(10, 20), scraped, new UsageHistory(), new Config());

        Assert.Equal(2, model.Bars.Count);
        Assert.Equal(90, model.Bars[0].RemainingPct);
        Assert.Contains("history", model.SourceText);
    }

    [Fact]
    public void BarsConvertUtilizationToRemaining()
    {
        var model = UsageModel.Build(FileWith(30, 55), null, new UsageHistory(), new Config());

        Assert.Equal(70, model.Bars.Single(b => b.Label == "5h").RemainingPct);
        Assert.Equal(45, model.Bars.Single(b => b.Label == "7d").RemainingPct);
    }

    [Fact]
    public void MissingWindowProducesNoBarRatherThanAZeroBar()
    {
        var model = UsageModel.Build(FileWith(30, null), null, new UsageHistory(), new Config());

        Assert.Single(model.Bars);
        Assert.Equal("5h", model.Bars[0].Label);
    }

    [Fact]
    public void LowUsageHasNoCreditAction()
    {
        var config = new Config
        {
            LowThresholdPct = 10,
        };

        var model = UsageModel.Build(FileWith(95, 20), null, new UsageHistory(), config);

        Assert.Equal(PillAction.None, model.Action);
        Assert.Null(model.ActionUrl);
    }

    [Fact]
    public void TheStaticFloorRemainsInDiagnostics()
    {
        var config = new Config
        {
            LowThresholdPct = 10,
        };

        var model = UsageModel.Build(FileWith(95, 20), null, new UsageHistory(), config);

        Assert.Equal(PillAction.None, model.Action);
        Assert.True(model.Warned);
    }

    [Fact]
    public void NoClaudeCodeOffersTheInstallLink()
    {
        var model = UsageModel.Build(
            UsageSnapshot.Failed("not found"),
            ClaudeCodeUsage.NotInstalled(),
            new UsageHistory(),
            new Config());

        Assert.Equal(PillAction.InstallClaudeCode, model.Action);
        Assert.Equal(ClaudeCli.InstallUrl, model.ActionUrl);
        Assert.False(model.HasValues);
    }

    [Fact]
    public void FileResetsFlowOntoTheBars()
    {
        var reset = DateTimeOffset.Now.AddHours(3);
        var snapshot = FileWith(30, 40) with { FiveHourResetsAt = reset };

        var model = UsageModel.Build(snapshot, null, new UsageHistory(), new Config());

        Assert.Equal(reset, model.Bars.Single(b => b.Label == "5h").ResetsAt);
        Assert.Contains("measured", model.ResetBasis);
    }

    [Fact]
    public void WithoutADerivableResetTheBasisSaysSoRatherThanGuessing()
    {
        var model = UsageModel.Build(FileWith(30, 40), null, new UsageHistory(), new Config());

        Assert.Null(model.NextReset);
        Assert.DoesNotContain("measured", model.ResetBasis);
    }
}

public class CodexModelTests
{
    private static CodexSnapshot Snapshot(
        CodexWindow? primary, CodexWindow? secondary = null, CodexSpendLimit? spend = null) =>
        new(primary, secondary, DateTimeOffset.Now) { SpendLimit = spend };

    [Fact]
    public void BuildsBarsFromWindows()
    {
        var model = UsageModel.BuildCodex(
            Snapshot(new CodexWindow(9, 10080, DateTimeOffset.Now.AddDays(2))),
            codexInstalled: true, error: null, new Config());

        var bar = Assert.Single(model.Bars);
        Assert.Equal("7d", bar.Label);
        Assert.Equal(91, bar.RemainingPct);
        Assert.NotNull(bar.ResetsAt);
        Assert.Contains("app-server", model.ResetBasis);
    }

    [Fact]
    public void ASpendLimitBecomesItsOwnBar()
    {
        var model = UsageModel.BuildCodex(
            Snapshot(null, null, new CodexSpendLimit(35, DateTimeOffset.Now.AddDays(1), "$65", "$100")),
            codexInstalled: true, error: null, new Config());

        var bar = Assert.Single(model.Bars);
        Assert.Equal("spend", bar.Label);
        Assert.Equal(35, bar.RemainingPct);
    }

    [Fact]
    public void NoCodexOffersTheInstallLink()
    {
        var model = UsageModel.BuildCodex(null, codexInstalled: false, error: null, new Config());

        Assert.Equal(PillAction.InstallCodex, model.Action);
        Assert.Equal(CodexCli.InstallUrl, model.ActionUrl);
    }

    [Fact]
    public void CodexUsageNeverOffersCreditAction()
    {
        var config = new Config { LowThresholdPct = 50 };

        var model = UsageModel.BuildCodex(
            Snapshot(new CodexWindow(95, 300, null)), true, null, config);

        Assert.Equal(PillAction.None, model.Action);
        Assert.Null(model.ActionUrl);
    }

    [Fact]
    public void ResetPointerUsesTheActualDurationRatherThanTheRoundedLabel()
    {
        var now = DateTimeOffset.Now;
        var model = UsageModel.BuildCodex(
            Snapshot(new CodexWindow(50, 280, now.AddMinutes(140))), true, null, new Config());

        var bar = Assert.Single(model.Bars);
        Assert.Equal("5h", bar.Label);
        Assert.Equal(TimeSpan.FromMinutes(280), bar.WindowDuration);
        Assert.Equal(50, ResetPace.For(bar, now)!.Value.TimeRemainingPct, precision: 6);
    }

    [Fact]
    public void AvailableResetCreditsStayOutOfTrayStatus()
    {
        var snapshot = Snapshot(new CodexWindow(50, 10080, null)) with { ResetCreditsAvailable = 1 };

        var model = UsageModel.BuildCodex(snapshot, true, null, new Config());

        Assert.DoesNotContain("reset credit", model.StatusText);
    }

    [Fact]
    public void AnErrorWithNoSnapshotIsReportedRatherThanShownAsZero()
    {
        var model = UsageModel.BuildCodex(null, true, "connection refused", new Config());

        Assert.False(model.HasValues);
        Assert.Contains("connection refused", model.StatusText);
    }
}

public class ProviderTargetTests
{
    private static IReadOnlyList<TargetDefinition> Defaults(Config? config = null) =>
        TargetDefaults.Create(config ?? new Config());

    private static TargetDefinition DesktopTarget(ProviderId provider) =>
        Assert.Single(Defaults(), target =>
            target.Placement == TargetPlacementKind.Desktop
            && target.Providers.Any(binding => binding.Provider == provider));

    private static TargetDefinition TerminalTarget(ProviderId provider, Config? config = null) =>
        Assert.Single(Defaults(config), target =>
            target.WindowClass == "CASCADIA_HOSTING_WINDOW_CLASS"
            && target.Providers.Any(binding => binding.Provider == provider))
            ;

    private static WindowSnapshot Window(string processName) =>
        new(IntPtr.Zero, 0, processName, "Chrome_WidgetWin_1", "Application", true);

    [Fact]
    public void ClaudeAndCodexTargetDifferentDesktopProcesses()
    {
        Assert.Equal("claude", DesktopTarget(ProviderId.Claude).ProcessName);

        // The Codex desktop app's process is ChatGPT.exe while its CLI is codex.exe — unlike
        // Claude, where both are "claude". Conflating them would put the Codex pill on nothing.
        Assert.Equal("ChatGPT", DesktopTarget(ProviderId.Codex).ProcessName);
        Assert.Equal(new[] { "codex" }, TerminalTarget(ProviderId.Codex).ActivityProcessNames);
    }

    [Fact]
    public void EachProviderReadsItsOwnTerminalConfig()
    {
        var config = new Config
        {
            AttachToClaudeCodeTerminals = false,
            AttachToCodexTerminals = true,
        };

        var claudeTerminal = Assert.Single(Defaults(config), target =>
            target.WindowClass == "CASCADIA_HOSTING_WINDOW_CLASS"
            && target.Providers.Any(binding => binding.Provider == ProviderId.Claude));
        var codexTerminal = Assert.Single(Defaults(config), target =>
            target.WindowClass == "CASCADIA_HOSTING_WINDOW_CLASS"
            && target.Providers.Any(binding => binding.Provider == ProviderId.Codex));

        Assert.False(claudeTerminal.Enabled);
        Assert.True(codexTerminal.Enabled);
        Assert.Equal(new[] { "claude" }, claudeTerminal.ActivityProcessNames);
        Assert.Equal(new[] { "codex" }, codexTerminal.ActivityProcessNames);
    }

    /// <summary>
    /// The regression this guards: Chrome, VS Code and every other Electron/CEF app share
    /// Chrome_WidgetWin_1 with both desktop apps, so the class alone once matched Chrome. The
    /// owning process name is the discriminator, and it must never be a substring match.
    /// </summary>
    [Theory]
    [InlineData("chrome")]
    [InlineData("msedge")]
    [InlineData("Code")]
    [InlineData("explorer")]
    [InlineData("Teams")]
    [InlineData("claudette")]
    public void OtherElectronAppsAreNotClaudeDesktop(string processName)
    {
        Assert.False(DesktopTarget(ProviderId.Claude).Matches(Window(processName)));
    }

    [Theory]
    [InlineData("chrome")]
    [InlineData("Code")]
    [InlineData("ChatGPTHelper")]
    public void OtherElectronAppsAreNotCodexDesktop(string processName)
    {
        Assert.False(DesktopTarget(ProviderId.Codex).Matches(Window(processName)));
    }

}
