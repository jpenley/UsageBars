using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace UsageBars.Tests;

public class TargetCaptureTests
{
    private const uint OwnPid = 999;

    private static WindowSnapshot Window(
        string process = "rider64", string windowClass = "SunAwtFrame", string title = "Project",
        uint pid = 7, bool resizable = true) =>
        new(new IntPtr(42), pid, process, windowClass, title, resizable);

    private static ProcessTree EmptyTree() =>
        ProcessTree.FromMap(Array.Empty<(uint Pid, uint Parent, string Name)>());

    [Fact]
    public void ClickedChildIsCapturedAsItsTopLevelWindow()
    {
        var native = new FakeIntrospection
        {
            PointWindow = new IntPtr(100),
            RootWindow = new IntPtr(200),
            Snapshot = Window(),
        };

        var result = new WindowCaptureService(native, OwnPid).CaptureAt(new Native.POINT());

        Assert.True(result.Success);
        Assert.Equal(new IntPtr(200), result.Window!.Hwnd);
    }

    [Fact]
    public void CaptureIncludesTopLevelSiblingsForIdentityDiscrimination()
    {
        var selected = Window(title: "Workspace Alpha - 18472");
        var sibling = Window(title: "Workspace Beta - 75931") with { Hwnd = new IntPtr(43) };
        var native = new FakeIntrospection
        {
            Snapshot = selected,
            TopLevelWindows = new[] { selected, sibling },
        };

        var result = new WindowCaptureService(native, OwnPid).CaptureAt(new Native.POINT());

        var topLevelWindows = result.TopLevelWindows!;
        Assert.Equal(2, topLevelWindows.Count);
        Assert.Contains(topLevelWindows, window => window.Hwnd == sibling.Hwnd);
    }

    [Fact]
    public void CaptureExcludesInvisibleTopLevelSiblings()
    {
        var selected = Window(title: "Workspace Alpha");
        var hiddenSibling = Window(title: "Internal helper") with { Hwnd = new IntPtr(43) };
        var native = new FakeIntrospection
        {
            Snapshot = selected,
            TopLevelWindows = new[] { selected, hiddenSibling },
            VisibleTopLevelWindows = new[] { selected.Hwnd },
        };

        var result = new WindowCaptureService(native, OwnPid).CaptureAt(new Native.POINT());

        var topLevelWindows = result.TopLevelWindows!;
        Assert.Single(topLevelWindows);
        Assert.Equal(selected.Hwnd, topLevelWindows[0].Hwnd);
    }

    [Fact]
    public void OwnWindowsAndTaskbarAreRejected()
    {
        var own = FakeIntrospection.For(Window("UsageBars", "HwndWrapper", pid: OwnPid));
        var taskbar = FakeIntrospection.For(Window("explorer", "Shell_TrayWnd", pid: 20));

        Assert.False(new WindowCaptureService(own, OwnPid).CaptureAt(new Native.POINT()).Success);
        Assert.False(new WindowCaptureService(taskbar, OwnPid).CaptureAt(new Native.POINT()).Success);
    }

    [Fact]
    public void UnstableWindowIdentityIsRejected()
    {
        var native = FakeIntrospection.For(Window(process: "", windowClass: ""));

        var result = new WindowCaptureService(native, OwnPid).CaptureAt(new Native.POINT());

        Assert.False(result.Success);
        Assert.Contains("required", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DesktopIdentityPreselectsProviderAndAlwaysOpen()
    {
        var draft = ProviderInference.CreateDraft(
            Window("ChatGPT", "Chrome_WidgetWin_1"), TargetDefaults.Create(new Config()),
            EmptyTree(), new Config(), OwnPid);

        Assert.Contains(draft.Definition.Providers, p => p.Provider == ProviderId.Codex);
        Assert.Equal(TargetActivationPolicy.AlwaysWhileOpen, draft.Definition.Activation);
        Assert.Equal(TargetPlacementKind.Desktop, draft.Definition.Placement);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("ChatGPT")]
    public void MatchingDesktopSeedRetainsItsTitleMatcherForIdentity(string processName)
    {
        var defaults = TargetDefaults.Create(new Config());
        var existing = Assert.Single(defaults, target =>
            target.ProcessName == processName && target.WindowClass == "Chrome_WidgetWin_1");

        var draft = ProviderInference.CreateDraft(
            Window(processName, "Chrome_WidgetWin_1", "Usage"), defaults,
            EmptyTree(), new Config(), OwnPid);

        Assert.Equal(existing.WindowTitle, draft.Definition.WindowTitle);
        Assert.True(draft.Definition.HasSameIdentity(existing));
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("ChatGPT")]
    public void DisabledDesktopSeedStillRetainsItsTitleMatcherForIdentity(string processName)
    {
        var existing = Assert.Single(TargetDefaults.Create(new Config()), target =>
            target.ProcessName == processName && target.WindowClass == "Chrome_WidgetWin_1")
            with { Enabled = false };

        var draft = ProviderInference.CreateDraft(
            Window(processName, "Chrome_WidgetWin_1", "Usage"), new[] { existing },
            EmptyTree(), new Config(), OwnPid);

        Assert.Equal(existing.WindowTitle, draft.Definition.WindowTitle);
        Assert.True(draft.Definition.HasSameIdentity(existing));
    }

    [Fact]
    public void HighestSpecificityAlwaysOpenMatchesUnionTheirProviders()
    {
        var first = TargetDefinition.Captured(
            "IDE Claude", "rider64", "SunAwtFrame", new[] { ProviderBinding.Claude },
            TargetPlacementKind.Ide, TargetActivationPolicy.AlwaysWhileOpen, false, 26);
        var second = TargetDefinition.Captured(
            "IDE Codex", "rider64", "SunAwtFrame", new[] { ProviderBinding.Codex },
            TargetPlacementKind.Ide, TargetActivationPolicy.AlwaysWhileOpen, false, 26);

        var draft = ProviderInference.CreateDraft(
            Window(), new[] { first, second }, EmptyTree(), new Config(), OwnPid);

        Assert.Equal(new[] { ProviderId.Claude, ProviderId.Codex },
            draft.Definition.Providers.Select(p => p.Provider).OrderBy(p => p));
        Assert.Equal(TargetPlacementKind.Ide, draft.Definition.Placement);
        Assert.Equal(TargetActivationPolicy.AlwaysWhileOpen, draft.Definition.Activation);
        Assert.Equal(26, draft.Definition.StatusBarHeight);
        Assert.Empty(draft.Definition.ActivityProcessNames);
    }

    [Fact]
    public void TerminalClassAloneDoesNotPreselectAProvider()
    {
        var defaults = TargetDefaults.Create(new Config());

        var draft = ProviderInference.CreateDraft(
            Window("WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS", "pwsh"),
            defaults, EmptyTree(), new Config(), OwnPid);

        Assert.Empty(draft.Definition.Providers);
    }

    [Fact]
    public void TerminalTitleWithLiveCliPreselectsOnlyItsProvider()
    {
        var tree = ProcessTree.FromMap(new[]
        {
            (10u, 1u, "claude"),
            (11u, 1u, "codex"),
        });
        var config = new Config
        {
            TerminalTitlePattern = @"(?i)^claude session$",
            CodexTitlePattern = @"(?i)^codex session$",
        };

        var draft = ProviderInference.CreateDraft(
            Window("WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS", "claude session"),
            TargetDefaults.Create(config), tree, config, OwnPid);

        var provider = Assert.Single(draft.Definition.Providers);
        Assert.Equal(ProviderId.Claude, provider.Provider);
        Assert.Equal(TargetActivationPolicy.RequireActivityProcess, draft.Definition.Activation);
        Assert.Equal(TargetPlacementKind.Terminal, draft.Definition.Placement);
        Assert.Equal(new[] { "claude" }, draft.Definition.ActivityProcessNames);
    }

    [Fact]
    public void CliAncestryPreselectsProviderWithoutATitle()
    {
        var tree = ProcessTree.FromMap(new[]
        {
            (100u, 7u, "claude"),
            (7u, 1u, "WindowsTerminal"),
        });

        var draft = ProviderInference.CreateDraft(
            Window("WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS", "pwsh"),
            TargetDefaults.Create(new Config()), tree, new Config(), OwnPid);

        var provider = Assert.Single(draft.Definition.Providers);
        Assert.Equal(ProviderId.Claude, provider.Provider);
    }

    [Fact]
    public void CanonicalCliEvidenceIsInferredForAnUnknownHostWithoutAMatchingTarget()
    {
        var tree = ProcessTree.FromMap(new[]
        {
            (100u, 7u, "claude"),
            (101u, 7u, "codex"),
            (7u, 1u, "unknown-host"),
        });

        var draft = ProviderInference.CreateDraft(
            Window("unknown-host", "UnknownFrame", "Untitled"), Array.Empty<TargetDefinition>(),
            tree, new Config(), OwnPid);

        Assert.Equal(new[] { ProviderId.Claude, ProviderId.Codex },
            draft.Definition.Providers.Select(binding => binding.Provider).OrderBy(provider => provider));
        Assert.Equal(TargetActivationPolicy.RequireActivityProcess, draft.Definition.Activation);
        Assert.Equal(new[] { "claude", "codex" }, draft.Definition.ActivityProcessNames);
    }

    [Fact]
    public void NonProviderActivityDoesNotInferAProvider()
    {
        var tree = ProcessTree.FromMap(new[]
        {
            (100u, 7u, "omp"),
            (7u, 1u, "WindowsTerminal"),
        });

        var draft = ProviderInference.CreateDraft(
            Window("WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS", "pwsh"),
            TargetDefaults.Create(new Config()), tree, new Config(), OwnPid);

        Assert.Empty(draft.Definition.Providers);
        Assert.Empty(draft.Definition.ActivityProcessNames);
    }

    [Fact]
    public void ActivityTargetTemplatePreservesIdePlacementAndStatusHeight()
    {
        var template = TargetDefinition.Captured(
            "IDE activity", "rider64", "SunAwtFrame", new[] { ProviderBinding.Claude },
            TargetPlacementKind.Ide, TargetActivationPolicy.RequireActivityProcess, false, 26,
            new[] { "claude" });
        var tree = ProcessTree.FromMap(new[]
        {
            (100u, 7u, "claude"),
            (7u, 1u, "rider64"),
        });

        var draft = ProviderInference.CreateDraft(
            Window(), new[] { template }, tree, new Config(), OwnPid);

        Assert.Equal(TargetPlacementKind.Ide, draft.Definition.Placement);
        Assert.Equal(26, draft.Definition.StatusBarHeight);
        Assert.Equal(TargetActivationPolicy.RequireActivityProcess, draft.Definition.Activation);
    }

    [Fact]
    public void ActiveCustomTemplatePreservesProviderAndActivityFilter()
    {
        var template = TargetDefinition.Captured(
            "IDE custom activity", "rider64", "SunAwtFrame", new[] { ProviderBinding.Codex },
            TargetPlacementKind.Ide, TargetActivationPolicy.RequireActivityProcess, false, 26,
            new[] { "omp" });
        var tree = ProcessTree.FromMap(new[]
        {
            (100u, 7u, "omp"),
            (7u, 1u, "rider64"),
        });

        var draft = ProviderInference.CreateDraft(
            Window(), new[] { template }, tree, new Config(), OwnPid);

        var provider = Assert.Single(draft.Definition.Providers);
        Assert.Equal(ProviderId.Codex, provider.Provider);
        Assert.Equal(new[] { "omp" }, draft.Definition.ActivityProcessNames);
        Assert.Equal(TargetActivationPolicy.RequireActivityProcess, draft.Definition.Activation);
    }

    [Fact]
    public void SameProcessAndClassSiblingsProduceAPrefixWithoutVolatileSuffix()
    {
        var selected = Window("newide", "MainFrame", "Workspace Alpha - 18472");
        var sibling = Window("newide", "MainFrame", "Workspace Beta - 75931")
            with { Hwnd = new IntPtr(43), Pid = 8 };

        var draft = ProviderInference.CreateDraft(
            selected, Array.Empty<TargetDefinition>(), EmptyTree(), new Config(), OwnPid,
            topLevelWindows: new[] { selected, sibling });

        var title = Assert.IsType<TitleRule>(draft.Definition.WindowTitle);
        Assert.Equal(TitleMatchKind.Prefix, title.Kind);
        Assert.Equal("Workspace Alpha", title.Value);
        Assert.True(title.Matches(selected.Title));
        Assert.False(title.Matches(sibling.Title));
        Assert.Equal(selected.Title, draft.CapturedTitle);
    }

    [Fact]
    public void SiblingDiscriminatorWinsOverAMatchingTargetTitleMatcher()
    {
        var existing = TargetDefinition.Captured(
            "App", "newide", "MainFrame", new[] { ProviderBinding.Claude },
            TargetPlacementKind.Desktop, TargetActivationPolicy.AlwaysWhileOpen, true, 0) with
        {
            WindowTitle = new(TitleMatchKind.Regex, @"(?s).+"),
        };
        var selected = Window("newide", "MainFrame", "Workspace Alpha - 18472");
        var sibling = Window("newide", "MainFrame", "Workspace Beta - 75931")
            with { Hwnd = new IntPtr(43), Pid = 8 };

        var draft = ProviderInference.CreateDraft(
            selected, new[] { existing }, EmptyTree(), new Config(), OwnPid,
            topLevelWindows: new[] { selected, sibling });

        Assert.Equal(new TitleRule(TitleMatchKind.Prefix, "Workspace Alpha"),
            draft.Definition.WindowTitle);
    }

    [Fact]
    public void VolatileOnlySiblingTitlesDoNotPersistATitleRule()
    {
        var selected = Window("newide", "MainFrame", "Project - 18472");
        var sibling = Window("newide", "MainFrame", "Project - 75931")
            with { Hwnd = new IntPtr(43), Pid = 8 };

        var draft = ProviderInference.CreateDraft(
            selected, Array.Empty<TargetDefinition>(), EmptyTree(), new Config(), OwnPid,
            topLevelWindows: new[] { selected, sibling });

        Assert.Null(draft.Definition.WindowTitle);
        Assert.Equal(selected.Title, draft.CapturedTitle);
    }

    [Fact]
    public void UniqueProcessAndClassDoesNotPersistItsCapturedTitle()
    {
        var selected = Window("newide", "MainFrame", "Workspace Alpha - 18472");

        var draft = ProviderInference.CreateDraft(
            selected, Array.Empty<TargetDefinition>(), EmptyTree(), new Config(), OwnPid,
            topLevelWindows: new[] { selected });

        Assert.Null(draft.Definition.WindowTitle);
        Assert.Equal(selected.Title, draft.CapturedTitle);
    }

    [Fact]
    public void NoEvidenceProducesManuallyAssignableDraftWithTransientTitle()
    {
        var draft = ProviderInference.CreateDraft(
            Window("newide", "MainFrame", "My project"), Array.Empty<TargetDefinition>(),
            EmptyTree(), new Config(), OwnPid);

        Assert.Empty(draft.Definition.Providers);
        Assert.Contains(draft.InferenceNotes, note => note.Contains("No provider detected"));
        Assert.Equal("newide", draft.Definition.ProcessName);
        Assert.Equal("MainFrame", draft.Definition.WindowClass);
        Assert.Equal("My project", draft.CapturedTitle);
        Assert.Null(draft.Definition.WindowTitle);
    }

    private sealed class FakeIntrospection : IWindowIntrospection
    {
        public IntPtr PointWindow { get; init; } = new(100);
        public IntPtr RootWindow { get; init; } = new(200);
        public WindowSnapshot? Snapshot { get; init; }
        public IReadOnlyList<WindowSnapshot> TopLevelWindows { get; init; } = Array.Empty<WindowSnapshot>();
        public IReadOnlyList<IntPtr> VisibleTopLevelWindows { get; init; } = Array.Empty<IntPtr>();

        public static FakeIntrospection For(WindowSnapshot snapshot) => new() { Snapshot = snapshot };

        public IntPtr WindowFromPoint(Native.POINT point) => PointWindow;
        public IntPtr RootOf(IntPtr hwnd) => RootWindow;
        public WindowSnapshot? SnapshotOf(IntPtr hwnd) => Snapshot is null ? null : Snapshot with { Hwnd = hwnd };
        public IReadOnlyList<WindowSnapshot> GetTopLevelWindows() => TopLevelWindows;
        public bool IsVisibleTopLevel(IntPtr hwnd) =>
            VisibleTopLevelWindows.Count == 0 || VisibleTopLevelWindows.Contains(hwnd);
    }
}
