using System;
using System.Linq;
using Xunit;

namespace UsageBars.Tests;

public class TargetViewModelTests
{
    [Fact]
    public void ExistingTitleRuleCanBeEditedAndCleared()
    {
        var target = TwoProviderTarget() with
        {
            WindowTitle = new TitleRule(TitleMatchKind.Literal, "Old title"),
        };
        var model = TargetEditorModel.FromExisting(target);

        Assert.True(model.TitleMatcherEnabled);
        Assert.Equal(TitleMatchKind.Literal, model.TitleKind);
        Assert.Equal("Old title", model.TitleValue);

        model.TitleKind = TitleMatchKind.Prefix;
        model.TitleValue = "WorldGen";
        Assert.Equal(new TitleRule(TitleMatchKind.Prefix, "WorldGen"), model.ToDefinition().WindowTitle);

        model.TitleMatcherEnabled = false;
        Assert.Null(model.ToDefinition().WindowTitle);
    }

    [Fact]
    public void ActivityProcessesRoundTripIndependentlyOfProviders()
    {
        var target = TwoProviderTarget() with
        {
            ActivityProcessNames = new() { "claude" },
        };
        var model = TargetEditorModel.FromExisting(target);

        model.TitleMatcherEnabled = true;
        model.TitleKind = TitleMatchKind.Prefix;
        model.TitleValue = "WorldGen";
        model.SetActivityProcesses(new[] { "omp.exe", "CODEX" });

        var edited = model.ToDefinition();
        Assert.Equal(new TitleRule(TitleMatchKind.Prefix, "WorldGen"), edited.WindowTitle);
        Assert.Equal(TargetActivationPolicy.RequireActivityProcess, edited.Activation);
        Assert.Equal(new[] { "codex", "omp" }, edited.ActivityProcessNames);
        Assert.Equal(2, edited.Providers.Count);
    }

    [Fact]
    public void ProcessActivationRequiresAValidActivityName()
    {
        var model = TargetEditorModel.FromExisting(TwoProviderTarget());

        model.SetActivityProcesses(new[] { "bad:name" });

        Assert.Contains(model.Validate(), error =>
            error.Contains("activity process", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(TargetActivationPolicy.AlwaysWhileOpen, model.ToDefinition().Activation);
    }

    [Fact]
    public void ApplyDefinitionRefreshesTitleAndActivityFields()
    {
        var model = TargetEditorModel.FromExisting(TwoProviderTarget());
        var refreshed = TwoProviderTarget() with
        {
            WindowTitle = new TitleRule(TitleMatchKind.Regex, "^WorldGen"),
            ActivityProcessNames = new() { "OMP.exe", "codex" },
            Activation = TargetActivationPolicy.RequireActivityProcess,
        };

        model.ApplyDefinition(refreshed);

        Assert.True(model.TitleMatcherEnabled);
        Assert.Equal(TitleMatchKind.Regex, model.TitleKind);
        Assert.Equal("^WorldGen", model.TitleValue);
        Assert.Equal(new[] { "codex", "omp" }, model.ActivityProcessNames);
    }

    [Fact]
    public void TogglingProvidersNeverChangesActivityProcessNames()
    {
        var target = TwoProviderTarget() with
        {
            ActivityProcessNames = new() { "omp" },
            Activation = TargetActivationPolicy.RequireActivityProcess,
        };
        var model = TargetEditorModel.FromExisting(target);

        model.Claude = false;
        model.Codex = false;
        model.Claude = true;

        Assert.Equal(new[] { "omp" }, model.ActivityProcessNames);
        Assert.Equal(TargetActivationPolicy.RequireActivityProcess, model.ToDefinition().Activation);
    }

    [Fact]
    public void EmptyActivityListDerivesAlwaysOpenRegardlessOfDefinitionActivation()
    {
        var model = TargetEditorModel.FromExisting(TwoProviderTarget() with
        {
            Activation = TargetActivationPolicy.RequireActivityProcess,
            ActivityProcessNames = new(),
        });

        Assert.Empty(model.ActivityProcessNames);
        Assert.Equal(TargetActivationPolicy.AlwaysWhileOpen, model.Activation);
        Assert.Equal(TargetActivationPolicy.AlwaysWhileOpen, model.ToDefinition().Activation);
    }

    [Fact]
    public void OnlySetActivityProcessesChangesCanonicalActivityNames()
    {
        var model = TargetEditorModel.FromExisting(TwoProviderTarget() with
        {
            ActivityProcessNames = new() { "omp" },
            Activation = TargetActivationPolicy.RequireActivityProcess,
        });

        model.Claude = false;
        model.Codex = false;

        Assert.Equal(new[] { "omp" }, model.ActivityProcessNames);

        model.SetActivityProcesses(new[] { "CODEX.exe" });

        Assert.Equal(new[] { "codex" }, model.ActivityProcessNames);
    }

    [Fact]
    public void TrayCommandsKeepTargetsAndDoNotOfferGlobalPositioning()
    {
        Assert.Contains("Targets", Tray.CommandLabels);
        Assert.DoesNotContain("Position", Tray.CommandLabels);
    }

    [Fact]
    public void TrayOffersOnlyManagementForTargetCommands()
    {
        Assert.Contains("Manage targets...", Tray.TargetCommandLabels);
        Assert.DoesNotContain(Tray.TargetCommandLabels,
            label => label.Contains("clicking a window", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ManualAssignmentRequiresAtLeastOneProvider()
    {
        var definition = TargetDefinition.Captured(
            "New IDE", "newide", "MainFrame", Array.Empty<ProviderBinding>(),
            TargetPlacementKind.Desktop, TargetActivationPolicy.AlwaysWhileOpen, true, 0);
        var model = TargetEditorModel.From(
            new TargetDraft(definition, "Project - New IDE", new[] { "No provider detected." }));

        Assert.Contains(model.Validate(), x => x.Contains("provider", StringComparison.OrdinalIgnoreCase));

        model.Claude = true;
        Assert.DoesNotContain(model.Validate(), x => x.Contains("provider", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RowShowsBothProvidersAndMatcher()
    {
        var target = TargetDefinition.Captured(
            "Rider", "rider64", "SunAwtFrame",
            new[] { ProviderBinding.Claude, ProviderBinding.Codex },
            TargetPlacementKind.Ide, TargetActivationPolicy.AlwaysWhileOpen, false, 26);

        var row = TargetRow.From(target, new[] { target });

        Assert.Equal("Claude + Codex", row.Providers);
        Assert.Contains("rider64", row.Match);
        Assert.Contains("SunAwtFrame", row.Match);
        Assert.Contains("activity=always", row.Match);
    }

    [Fact]
    public void ReloadSchedulerQueuesWorkInsteadOfRunningItSynchronously()
    {
        Action? queued = null;
        var reloaded = false;

        var posted = TargetManagerReloadScheduler.TryQueue(
            isDisposed: false,
            isDisposing: false,
            isHandleCreated: true,
            post: action => queued = action,
            reload: () => reloaded = true);

        Assert.True(posted);
        Assert.False(reloaded);
        Assert.NotNull(queued);
        queued();
        Assert.True(reloaded);
    }

    [Fact]
    public void ReloadSchedulerIgnoresClosingAndPostRaces()
    {
        var reloaded = false;
        var postedWhileClosing = false;

        Assert.False(TargetManagerReloadScheduler.TryQueue(
            isDisposed: false,
            isDisposing: true,
            isHandleCreated: true,
            post: _ => postedWhileClosing = true,
            reload: () => reloaded = true));
        Assert.False(postedWhileClosing);
        Assert.False(TargetManagerReloadScheduler.TryQueue(
            isDisposed: false,
            isDisposing: false,
            isHandleCreated: true,
            post: _ => throw new InvalidOperationException(),
            reload: () => reloaded = true));
        Assert.False(TargetManagerReloadScheduler.TryQueue(
            isDisposed: false,
            isDisposing: false,
            isHandleCreated: true,
            post: _ => throw new ObjectDisposedException("form"),
            reload: () => reloaded = true));

        Assert.False(reloaded);
    }

    [Fact]
    public void DetailsIncludeCompleteRulePolicySourceAndOverlapResolution()
    {
        var selected = TargetDefinition.Captured(
            "Rider", "rider64", "SunAwtFrame",
            new[] { ProviderBinding.Claude, ProviderBinding.Codex },
            TargetPlacementKind.Ide, TargetActivationPolicy.RequireActivityProcess, false, 26,
            new[] { "OMP", "codex.exe" })
            with { WindowTitle = new TitleRule(TitleMatchKind.Prefix, "Project") };
        var overlap = TargetDefinition.Captured(
            "Rider fallback", "rider64", "SunAwtFrame",
            new[] { ProviderBinding.Claude },
            TargetPlacementKind.Desktop, TargetActivationPolicy.AlwaysWhileOpen, true, 0);

        var text = TargetDetailsFormatter.Format(selected, new[] { selected, overlap });

        Assert.Contains("process=rider64", text);
        Assert.Contains("class=SunAwtFrame", text);
        Assert.Contains("Title: Prefix:Project", text);
        Assert.Contains("Activity: codex OR omp", text);
        Assert.Contains("RequireActivityProcess", text);
        Assert.Contains("Captured", text);
        Assert.Contains("Rider fallback", text);
        Assert.Contains("more specific", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DisabledTargetDoesNotReportAnActiveOverlap()
    {
        var disabled = TargetDefinition.Captured(
            "Disabled", "app", "frame", new[] { ProviderBinding.Claude },
            TargetPlacementKind.Desktop, TargetActivationPolicy.AlwaysWhileOpen, true, 0)
            with { Enabled = false };
        var enabled = disabled with { Id = Guid.NewGuid(), Name = "Enabled", Enabled = true };

        Assert.False(TargetRow.From(disabled, new[] { disabled, enabled }).HasOverlap);
        Assert.Contains("No overlapping enabled rules",
            TargetDetailsFormatter.Format(disabled, new[] { disabled, enabled }));
    }

    [Fact]
    public void EditorRoundTripsAnchorAndOffsetsWithoutChangingThemWithPlacement()
    {
        var target = TargetDefinition.Captured(
            "Rider", "rider64", "SunAwtFrame", new[] { ProviderBinding.Claude },
            TargetPlacementKind.Ide, TargetActivationPolicy.AlwaysWhileOpen, false, 26) with
        {
            Anchor = PillAnchor.TopRight,
            OffsetX = 8,
            OffsetY = -12,
        };
        var model = TargetEditorModel.FromExisting(target);

        model.Placement = TargetPlacementKind.Desktop;
        var edited = model.ToDefinition();

        Assert.Equal(PillAnchor.TopRight, edited.Anchor);
        Assert.Equal(8, edited.OffsetX);
        Assert.Equal(-12, edited.OffsetY);
    }

    [Fact]
    public void ManagerRowAndDetailsShowFriendlyPerTargetPosition()
    {
        var target = TargetDefinition.Captured(
            "Terminal", "pwsh", "ConsoleWindowClass", new[] { ProviderBinding.Codex },
            TargetPlacementKind.Terminal, TargetActivationPolicy.AlwaysWhileOpen, true, 0) with
        {
            Anchor = PillAnchor.TopRight,
            OffsetX = 8,
            OffsetY = -4,
        };

        var row = TargetRow.From(target, new[] { target });
        var details = TargetDetailsFormatter.Format(target, new[] { target });

        Assert.Equal("Top right (+8, -4)", row.Position);
        Assert.Contains("Position: Top right (+8, -4)", details);
    }

    [Fact]
    public void EditorRejectsInvalidAnchorAndOffsets()
    {
        var target = TargetDefinition.Captured(
            "Terminal", "pwsh", "ConsoleWindowClass", new[] { ProviderBinding.Codex },
            TargetPlacementKind.Terminal, TargetActivationPolicy.AlwaysWhileOpen, true, 0);
        var model = TargetEditorModel.FromExisting(target);
        model.Anchor = (PillAnchor)99;
        model.OffsetX = TargetPositionRules.MaximumOffset + 1;
        model.OffsetY = TargetPositionRules.MinimumOffset - 1;

        var errors = model.Validate();

        Assert.Contains("anchor is invalid", errors);
        Assert.Contains("horizontal offset is out of range", errors);
        Assert.Contains("vertical offset is out of range", errors);
    }

    private static TargetDefinition TwoProviderTarget() => TargetDefinition.Captured(
        "Terminal", "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS",
        new[] { ProviderBinding.Claude, ProviderBinding.Codex },
        TargetPlacementKind.Terminal, TargetActivationPolicy.RequireActivityProcess, true, 0);
}
