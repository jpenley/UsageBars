using System;
using System.Linq;
using Xunit;

namespace UsageBars.Tests;

public class TargetResolverTests
{
    private const uint OwnPid = 999;

    private static WindowSnapshot Window(
        string process = "rider64", string windowClass = "SunAwtFrame", string title = "",
        bool isResizable = true) =>
        new(new IntPtr(42), 7, process, windowClass, title, isResizable);

    private static ProcessTree EmptyTree() =>
        ProcessTree.FromMap(Array.Empty<(uint Pid, uint Parent, string Name)>());

    private static TargetDefinition AlwaysTarget(ProviderId provider) =>
        TargetDefinition.Captured(
            "Test target", "app", "frame", new[] { new ProviderBinding(provider) },
            TargetPlacementKind.Desktop, TargetActivationPolicy.AlwaysWhileOpen, false, 0);

    private static TargetDefinition TerminalTarget(ProviderId provider) =>
        TargetDefinition.Captured(
            "Terminal", "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS",
            new[] { new ProviderBinding(provider) },
            TargetPlacementKind.Terminal, TargetActivationPolicy.RequireActivityProcess, false, 0,
            new[] { provider == ProviderId.Claude ? "claude" : "codex" });

    [Fact]
    public void EitherActivityProcessEmitsEveryCheckedProvider()
    {
        var target = TargetDefinition.Captured(
            "Terminal", "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS",
            new[] { ProviderBinding.Claude, ProviderBinding.Codex },
            TargetPlacementKind.Desktop, TargetActivationPolicy.RequireActivityProcess, true, 0,
            new[] { "omp", "codex" });
        var window = new WindowSnapshot(
            new IntPtr(42), 10, "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS", "", true);
        var tree = ProcessTree.FromMap(new[]
        {
            (10u, 1u, "WindowsTerminal"),
            (11u, 10u, "pwsh"),
            (12u, 11u, "omp.exe"),
        });

        var hosts = TargetResolver.Resolve(new[] { window }, new[] { target }, tree,
            new Config { EnableClaude = true, EnableCodex = true }, 999u);

        Assert.Equal(new[] { ProviderId.Claude, ProviderId.Codex },
            hosts.Select(host => host.Provider).Order().ToArray());
    }

    [Fact]
    public void UnrelatedOwnerDoesNotSatisfyActivity()
    {
        var tree = ProcessTree.FromMap(new[]
        {
            (10u, 1u, "WindowsTerminal"),
            (20u, 1u, "other-terminal"),
            (21u, 20u, "codex"),
        });

        var hosts = TargetResolver.Resolve(
            new[] { Window("WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS") },
            new[] { TerminalTarget(ProviderId.Codex) }, tree, new Config(), OwnPid);

        Assert.Empty(hosts);
    }

    [Fact]
    public void DeepDescendantActivityIsNotLimitedToLegacyHostDepth()
    {
        var tree = ProcessTree.FromMap(new[]
        {
            (7u, 1u, "WindowsTerminal"),
            (10u, 7u, "pwsh"),
            (11u, 10u, "node"),
            (12u, 11u, "node"),
            (13u, 12u, "codex"),
        });

        var host = Assert.Single(TargetResolver.Resolve(
            new[] { Window("WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS") },
            new[] { TerminalTarget(ProviderId.Codex) }, tree, new Config(), OwnPid));

        Assert.Equal(HostEvidence.Ancestry, host.Evidence);
    }

    [Fact]
    public void EmptyAlwaysOpenTargetNeedsNoProcess()
    {
        var target = TargetDefinition.Captured(
            "IDE", "rider64", "SunAwtFrame", new[] { ProviderBinding.Claude },
            TargetPlacementKind.Ide, TargetActivationPolicy.AlwaysWhileOpen, false, 26,
            Array.Empty<string>());

        var host = Assert.Single(TargetResolver.Resolve(
            new[] { Window() }, new[] { target }, EmptyTree(), new Config(), OwnPid));

        Assert.Equal(HostEvidence.Open, host.Evidence);
    }

    [Fact]
    public void SharedOwnerWindowsBothReceiveAllCheckedProviders()
    {
        var target = TargetDefinition.Captured(
            "Terminal", "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS",
            new[] { ProviderBinding.Claude, ProviderBinding.Codex },
            TargetPlacementKind.Terminal, TargetActivationPolicy.RequireActivityProcess, false, 0,
            new[] { "codex" });
        var windows = new[]
        {
            new WindowSnapshot(new IntPtr(42), 7, "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS", "one", true),
            new WindowSnapshot(new IntPtr(43), 7, "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS", "two", true),
        };
        var tree = ProcessTree.FromMap(new[] { (10u, 7u, "codex"), (7u, 1u, "WindowsTerminal") });

        var hosts = TargetResolver.Resolve(windows, new[] { target }, tree, new Config(), OwnPid);

        Assert.Equal(4, hosts.Count);
        Assert.All(windows, window => Assert.Equal(new[] { ProviderId.Claude, ProviderId.Codex },
            hosts.Where(host => host.Hwnd == window.Hwnd).Select(host => host.Provider).Order()));
    }

    [Fact]
    public void AlwaysOpenTargetNeedsNoCliEvidence()
    {
        var target = TargetDefinition.Captured(
            "Rider", "rider64", "SunAwtFrame",
            new[] { ProviderBinding.Claude, ProviderBinding.Codex },
            TargetPlacementKind.Ide, TargetActivationPolicy.AlwaysWhileOpen, false, 26);

        var hosts = TargetResolver.Resolve(
            new[] { Window("rider64", "SunAwtFrame") }, new[] { target },
            EmptyTree(), new Config(), OwnPid);

        Assert.Equal(2, hosts.Count);
        Assert.All(hosts, host =>
        {
            Assert.Equal(HostKind.Ide, host.Kind);
            Assert.Equal(26, host.StatusBarHeight);
            Assert.Equal(target.Id, host.TargetId);
        });
    }

    [Fact]
    public void WinningTargetCarriesItsAnchorAndOffsetsIntoTheHost()
    {
        var target = TargetDefinition.Captured(
            "Terminal", "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS",
            new[] { ProviderBinding.Codex }, TargetPlacementKind.Terminal,
            TargetActivationPolicy.AlwaysWhileOpen, true, 0) with
        {
            Anchor = PillAnchor.BottomCenter,
            OffsetX = 12,
            OffsetY = -16,
        };
        var window = new WindowSnapshot(
            new IntPtr(42), 7, "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS", "Codex", true);

        var host = Assert.Single(TargetResolver.Resolve(
            new[] { window }, new[] { target }, ProcessTree.FromMap(Array.Empty<(uint, uint, string)>()),
            new Config(), ownPid: 999));

        Assert.Equal(target.Id, host.TargetId);
        Assert.Equal(PillAnchor.BottomCenter, host.Anchor);
        Assert.Equal(12, host.OffsetX);
        Assert.Equal(-16, host.OffsetY);
    }

    [Fact]
    public void ActivityTargetRequiresConfiguredProcessUnderItsWindow()
    {
        var target = TerminalTarget(ProviderId.Claude);

        var hosts = TargetResolver.Resolve(
            new[] { Window("WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS", "pwsh") },
            new[] { target }, EmptyTree(), new Config(), OwnPid);

        Assert.Empty(hosts);
    }

    [Fact]
    public void SpecificThenNewestRuleWinsWithoutDuplicateHosts()
    {
        var general = TargetDefinition.Seeded(
            "General", null, "CASCADIA_HOSTING_WINDOW_CLASS",
            new[] { ProviderBinding.Claude }, TargetPlacementKind.Terminal,
            TargetActivationPolicy.AlwaysWhileOpen, false, 0) with { Revision = 10 };
        var specific = TargetDefinition.Captured(
            "Specific", "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS",
            new[] { ProviderBinding.Claude }, TargetPlacementKind.Desktop,
            TargetActivationPolicy.AlwaysWhileOpen, true, 0) with { Revision = 1 };

        var host = Assert.Single(TargetResolver.Resolve(
            new[] { Window("WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS") },
            new[] { general, specific }, EmptyTree(), new Config(), OwnPid));

        Assert.Equal(HostKind.Desktop, host.Kind);
        Assert.Equal(specific.Id, host.TargetId);
    }

    [Fact]
    public void NewestRuleBreaksASpecificityTie()
    {
        var older = AlwaysTarget(ProviderId.Claude) with { Revision = 1 };
        var newer = older with { Id = Guid.NewGuid(), Name = "Newer", Revision = 2 };

        var host = Assert.Single(TargetResolver.Resolve(
            new[] { Window("app", "frame") }, new[] { older, newer }, EmptyTree(), new Config(), OwnPid));

        Assert.Equal(newer.Id, host.TargetId);
    }

    [Fact]
    public void GlobalProviderSwitchStillWins()
    {
        var config = new Config { EnableClaude = false };

        Assert.Empty(TargetResolver.Resolve(
            new[] { Window("app", "frame") }, new[] { AlwaysTarget(ProviderId.Claude) },
            EmptyTree(), config, OwnPid));
    }

    [Fact]
    public void PartialMatchRetainsRejectionForTheOtherRelevantProvider()
    {
        var claude = AlwaysTarget(ProviderId.Claude);
        var codex = AlwaysTarget(ProviderId.Codex) with { ProcessName = "other-app" };

        var result = TargetResolver.ResolveWithRejections(
            new[] { Window("app", "frame") }, new[] { claude, codex },
            EmptyTree(), new Config(), OwnPid);

        var host = Assert.Single(result.Hosts);
        Assert.Equal(ProviderId.Claude, host.Provider);
        Assert.DoesNotContain(ProviderId.Claude, result.Rejections.Keys);
        Assert.Contains(ProviderId.Codex, result.Rejections.Keys);
    }
}
