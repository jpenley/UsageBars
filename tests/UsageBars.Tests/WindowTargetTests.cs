using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace UsageBars.Tests;

public class WindowTargetTests
{
    [Fact]
    public void ProviderBindingsContainOnlyDisplaySelections()
    {
        var properties = typeof(ProviderBinding).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .OrderBy(name => name)
            .ToArray();

        Assert.Equal(new[] { nameof(ProviderBinding.Provider) }, properties);
    }

    private static WindowSnapshot Window(
        string process = "rider64", string @class = "SunAwtFrame",
        string title = "UsageBars", bool resizable = true) =>
        new(new IntPtr(42), 7, process, @class, title, resizable);

    [Fact]
    public void CapturedTargetsRequireProcessClassAndProvider()
    {
        var target = TargetDefinition.Captured(
            "Rider", "", "SunAwtFrame", new[] { ProviderBinding.Claude },
            TargetPlacementKind.Ide, TargetActivationPolicy.AlwaysWhileOpen, true, 26);

        Assert.Contains(target.Validate(captured: true), x => x.Contains("process", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProcessAndClassMatchingIsCaseInsensitive()
    {
        var target = TargetDefinition.Captured(
            "Rider", "RIDER64", "sunawtframe", new[] { ProviderBinding.Claude },
            TargetPlacementKind.Ide, TargetActivationPolicy.AlwaysWhileOpen, true, 26);

        Assert.True(target.Matches(Window()));
    }

    [Fact]
    public void ATitleRuleOutranksProcessAndClass()
    {
        var general = TargetDefinition.Captured(
            "Terminal", "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS",
            new[] { ProviderBinding.Codex }, TargetPlacementKind.Terminal,
            TargetActivationPolicy.RequireActivityProcess, true, 0);
        var specific = general with
        {
            Id = Guid.NewGuid(),
            Name = "Codex tab",
            WindowTitle = new TitleRule(TitleMatchKind.Prefix, "codex"),
        };

        Assert.True(specific.Specificity > general.Specificity);
        Assert.True(specific.Matches(Window(
            "windowsterminal", "CASCADIA_HOSTING_WINDOW_CLASS", "Codex session")));
    }

    [Fact]
    public void SeededProcessOnlyRulesMustRequireAResizableFrame()
    {
        var target = TargetDefinition.Seeded(
            "Cursor", "cursor", null, new[] { ProviderBinding.Claude },
            TargetPlacementKind.Terminal, TargetActivationPolicy.RequireActivityProcess,
            requireResizableFrame: true, statusBarHeight: 0);

        Assert.False(target.Matches(Window("cursor", "#32768", resizable: false)));
        Assert.True(target.Matches(Window("cursor", "Chrome_WidgetWin_1", resizable: true)));
    }

    [Fact]
    public void AlwaysWhileOpenRejectsActivityProcesses()
    {
        var target = TargetDefinition.Captured(
            "Terminal", "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS",
            new[] { ProviderBinding.Claude }, TargetPlacementKind.Terminal,
            TargetActivationPolicy.AlwaysWhileOpen, true, 0, new[] { "codex" });

        Assert.Contains(target.Validate(captured: true), error =>
            error.Contains("always-open", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RequireActivityProcessRejectsEmptyActivityProcesses()
    {
        var target = TargetDefinition.Captured(
            "Terminal", "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS",
            new[] { ProviderBinding.Claude }, TargetPlacementKind.Terminal,
            TargetActivationPolicy.RequireActivityProcess, true, 0, Array.Empty<string>());

        Assert.Contains(target.Validate(captured: true), error =>
            error.Contains("activity process", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FactoriesDoNotDeriveActivityProcessesFromProviderSelections()
    {
        var providers = new[] { new ProviderBinding(ProviderId.Claude) };
        var captured = TargetDefinition.Captured(
            "Captured", "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS", providers,
            TargetPlacementKind.Terminal, TargetActivationPolicy.RequireActivityProcess, true, 0);
        var seeded = TargetDefinition.Seeded(
            "Seeded", null, "CASCADIA_HOSTING_WINDOW_CLASS", providers,
            TargetPlacementKind.Terminal, TargetActivationPolicy.RequireActivityProcess, false, 0);

        Assert.Empty(captured.ActivityProcessNames);
        Assert.Empty(seeded.ActivityProcessNames);
        Assert.Contains(captured.Validate(captured: true), error =>
            error.Contains("activity process", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(seeded.Validate(captured: false), error =>
            error.Contains("activity process", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IdenticalActivitySetsCompareEqualRegardlessOfOrderCasingOrExeSuffix()
    {
        var first = TargetDefinition.Captured(
            "Terminal", "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS",
            new[] { ProviderBinding.Claude }, TargetPlacementKind.Terminal,
            TargetActivationPolicy.RequireActivityProcess, true, 0,
            new[] { "codex.exe", "claude" });
        var second = first with
        {
            Id = Guid.NewGuid(),
            ActivityProcessNames = new() { "CLAUDE.exe", "CODEX" },
        };

        Assert.True(first.HasSameIdentity(second));
    }

    [Fact]
    public void IdenticalIdentityIgnoresNameEnabledAndRevision()
    {
        var first = TargetDefinition.Captured(
            "A", "code", "Chrome_WidgetWin_1", new[] { ProviderBinding.Claude },
            TargetPlacementKind.Ide, TargetActivationPolicy.AlwaysWhileOpen, true, 22);
        var second = first with { Id = Guid.NewGuid(), Name = "B", Enabled = false, Revision = 99 };

        Assert.True(first.HasSameIdentity(second));
    }

    [Fact]
    public void IdenticalIdentityIgnoresVisualPosition()
    {
        var first = TargetDefinition.Captured(
            "A", "code", "Chrome_WidgetWin_1", new[] { ProviderBinding.Claude },
            TargetPlacementKind.Ide, TargetActivationPolicy.AlwaysWhileOpen, true, 22);
        var second = first with
        {
            Id = Guid.NewGuid(),
            Anchor = PillAnchor.TopRight,
            OffsetX = 12,
            OffsetY = -8,
        };

        Assert.True(first.HasSameIdentity(second));
    }

    [Fact]
    public void InvalidVisualPositionIsAValidationError()
    {
        var target = TargetDefinition.Captured(
            "A", "code", "Chrome_WidgetWin_1", new[] { ProviderBinding.Claude },
            TargetPlacementKind.Ide, TargetActivationPolicy.AlwaysWhileOpen, true, 22) with
        {
            Anchor = (PillAnchor)99,
            OffsetX = 2001,
            OffsetY = -2001,
        };

        var errors = target.Validate(captured: true);

        Assert.Contains(errors, error => error.Contains("anchor", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("offset", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AClassOnlyProviderRuleOverlapsAMoreSpecificRuleForTheSameProvider()
    {
        var broad = TargetDefinition.Seeded(
            "All Cascadia", null, "CASCADIA_HOSTING_WINDOW_CLASS",
            new[] { ProviderBinding.Claude }, TargetPlacementKind.Terminal,
            TargetActivationPolicy.RequireActivityProcess, false, 0);
        var narrow = TargetDefinition.Captured(
            "Windows Terminal", "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS",
            new[] { ProviderBinding.Claude }, TargetPlacementKind.Terminal,
            TargetActivationPolicy.RequireActivityProcess, true, 0);

        Assert.True(broad.Overlaps(narrow));
    }

    [Fact]
    public void CapturedSourceRequiresSelectorsEvenWhenCallerPassesFalse()
    {
        var target = TargetDefinition.Captured(
            "Captured", "", "", new[] { ProviderBinding.Claude },
            TargetPlacementKind.Ide, TargetActivationPolicy.AlwaysWhileOpen, true, 0);

        var errors = target.Validate(captured: false);

        Assert.Contains(errors, x => x.Contains("process", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, x => x.Contains("class", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnsupportedProviderIdsAreValidationErrors()
    {
        var target = TargetDefinition.Seeded(
            "Unknown", "process", "class",
            new[] { new ProviderBinding((ProviderId)999) },
            TargetPlacementKind.Terminal, TargetActivationPolicy.AlwaysWhileOpen, true, 0);

        Assert.Contains(target.Validate(captured: false), x => x.Contains("provider", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RuntimeEquivalentLiteralTitleRulesHaveTheSameIdentity()
    {
        var first = TargetDefinition.Captured(
            "First", "app", "frame", new[] { ProviderBinding.Claude },
            TargetPlacementKind.Desktop, TargetActivationPolicy.AlwaysWhileOpen, true, 0)
            with { WindowTitle = new TitleRule(TitleMatchKind.Literal, "Project") };
        var second = first with
        {
            Id = Guid.NewGuid(),
            WindowTitle = new TitleRule(TitleMatchKind.Literal, "project"),
        };

        Assert.True(first.HasSameIdentity(second));
    }

    [Fact]
    public void DisjointLiteralAndPrefixTitleSpacesDoNotWarnAsOverlapping()
    {
        var first = TargetDefinition.Captured(
            "First", "app", "frame", new[] { ProviderBinding.Claude },
            TargetPlacementKind.Desktop, TargetActivationPolicy.AlwaysWhileOpen, true, 0)
            with { WindowTitle = new TitleRule(TitleMatchKind.Prefix, "Alpha") };
        var second = first with
        {
            Id = Guid.NewGuid(),
            WindowTitle = new TitleRule(TitleMatchKind.Literal, "Beta project"),
        };

        Assert.False(first.Overlaps(second));
        Assert.True(first.Overlaps(second with
        {
            Id = Guid.NewGuid(),
            WindowTitle = new TitleRule(TitleMatchKind.Literal, "Alpha project"),
        }));
    }
}
