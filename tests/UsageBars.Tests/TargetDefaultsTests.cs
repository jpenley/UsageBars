using System;
using System.Linq;
using Xunit;

namespace UsageBars.Tests;

public class TargetDefaultsTests
{
    [Fact]
    public void TerminalSeedsKeepProviderSelectionsIndependentFromActivityProcesses()
    {
        var targets = TargetDefaults.Create(new Config());
        var claude = Assert.Single(targets, target =>
            target.Name == "Claude terminal (CASCADIA_HOSTING_WINDOW_CLASS)");
        var codex = Assert.Single(targets, target =>
            target.Name == "Codex terminal (CASCADIA_HOSTING_WINDOW_CLASS)");

        Assert.Equal(new[] { ProviderId.Claude }, claude.Providers.Select(binding => binding.Provider));
        Assert.Equal(new[] { "claude" }, claude.ActivityProcessNames);
        Assert.Equal(new[] { ProviderId.Codex }, codex.Providers.Select(binding => binding.Provider));
        Assert.Equal(new[] { "codex" }, codex.ActivityProcessNames);
    }

    [Fact]
    public void AlwaysOpenIdeSeedsHaveNoActivityProcesses()
    {
        var ide = Assert.Single(TargetDefaults.Create(new Config { AlwaysShowOnIdes = true }),
            target => target.Name == "rider64 IDE");

        Assert.Equal(TargetActivationPolicy.AlwaysWhileOpen, ide.Activation);
        Assert.Empty(ide.ActivityProcessNames);
    }

    [Fact]
    public void ProcessActivatedIdeSeedsUseClaudeOrCodexActivity()
    {
        var ide = Assert.Single(TargetDefaults.Create(new Config { AlwaysShowOnIdes = false }),
            target => target.Name == "rider64 IDE");

        Assert.Equal(TargetActivationPolicy.RequireActivityProcess, ide.Activation);
        Assert.Equal(new[] { "claude", "codex" }, ide.ActivityProcessNames);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("ChatGPT")]
    public void ElectronDesktopSeedsRejectEmptyTitlesButMatchNormalTitles(string processName)
    {
        var target = Assert.Single(TargetDefaults.Create(new Config()), target =>
            target.ProcessName == processName && target.WindowClass == "Chrome_WidgetWin_1");

        Assert.False(target.Matches(Window(processName, "")));
        Assert.True(target.Matches(Window(processName, "Usage")));
    }

    [Fact]
    public void ElectronDesktopSeedsMatchNewlineOnlyNonemptyTitles()
    {
        var targets = TargetDefaults.Create(new Config()).Where(target =>
            target.WindowClass == "Chrome_WidgetWin_1"
            && target.ProcessName is "claude" or "ChatGPT");

        Assert.All(targets, target => Assert.True(target.Matches(Window(target.ProcessName!, "\n"))));
    }

    private static WindowSnapshot Window(string processName, string title) =>
        new(IntPtr.Zero, 0, processName, "Chrome_WidgetWin_1", title, true);
}
