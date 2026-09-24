using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace UsageBars.Tests;

public class TargetManagementWorkspaceModelTests
{
    [Fact]
    public void WorkspaceMovesBrowseToEditAndCancelWithoutMutatingStore()
    {
        var target = ExactTarget("Saved");
        var store = StoreWith(target);
        using var model = new TargetManagementWorkspaceModel(store);

        model.BeginEdit(target.Id);
        model.ApplyDraft(model.CurrentDefinition! with { Name = "Unsaved" });
        model.CancelDraft();

        Assert.Equal(TargetManagementMode.Browse, model.Mode);
        Assert.Equal("Saved", store.Targets.Single().Name);
        Assert.Null(model.Editor);
    }

    [Fact]
    public void BeginCapturedSelectsStrongestApplicableSavedRule()
    {
        var broad = ExactTarget("Broad") with { WindowClass = null, Revision = 8 };
        var exact = ExactTarget("Exact") with { Id = Guid.NewGuid(), Revision = 1 };
        using var model = new TargetManagementWorkspaceModel(StoreWith(broad, exact));

        model.BeginCaptured(Window(), InferredDraft());

        Assert.Equal(TargetManagementMode.AddCaptured, model.Mode);
        Assert.Equal(exact.Id, model.SelectedSavedRuleId);
        Assert.Equal(exact.Id, model.CurrentDefinition!.Id);
        Assert.Equal(Window().Hwnd, model.CapturedWindow!.Hwnd);
    }

    [Fact]
    public void BeginCapturedSelectsStrongestApplicableRuleDespiteRetainedSearch()
    {
        var target = ExactTarget("Saved");
        using var model = new TargetManagementWorkspaceModel(StoreWith(target));
        model.SetSearch("no match");

        model.BeginCaptured(Window(), InferredDraft());

        Assert.Equal(target.Id, model.SelectedSavedRuleId);
        Assert.Equal(target.Id, model.CurrentDefinition!.Id);
        Assert.Empty(model.Rows);
    }

    [Fact]
    public void CreateMoreSpecificCreatesNewIdFromSelectedCapturedRule()
    {
        var existing = ExactTarget("Existing") with
        {
            Anchor = PillAnchor.TopRight,
            OffsetX = 12,
            OffsetY = -4,
        };
        using var model = new TargetManagementWorkspaceModel(StoreWith(existing));
        model.BeginCaptured(Window(), InferredDraft());

        model.CreateMoreSpecific();

        Assert.Equal(TargetManagementMode.AddCaptured, model.Mode);
        Assert.Null(model.SelectedSavedRuleId);
        Assert.NotEqual(existing.Id, model.CurrentDefinition!.Id);
        Assert.Equal((PillAnchor.TopRight, 12, -4),
            (model.CurrentDefinition.Anchor, model.CurrentDefinition.OffsetX, model.CurrentDefinition.OffsetY));
    }

    [Fact]
    public void SaveDraftUpdatesSelectedRuleAndReturnsToBrowse()
    {
        var target = ExactTarget("Saved");
        var store = StoreWith(target);
        using var model = new TargetManagementWorkspaceModel(store);
        model.BeginEdit(target.Id);
        model.ApplyDraft(model.CurrentDefinition! with { Name = "Updated" });

        var result = model.SaveDraft();

        Assert.True(result.Success);
        Assert.Equal("Updated", store.Targets.Single().Name);
        Assert.Equal(TargetManagementMode.Browse, model.Mode);
        Assert.Equal(target.Id, model.SelectedSavedRuleId);
        Assert.Null(model.Editor);
    }

    [Fact]
    public void SaveDraftAddsMoreSpecificCapturedRuleAndReturnsToBrowse()
    {
        var existing = ExactTarget("Existing");
        var store = StoreWith(existing);
        using var model = new TargetManagementWorkspaceModel(store);
        model.BeginCaptured(Window(), InferredDraft());
        model.CreateMoreSpecific();
        model.ApplyDraft(model.CurrentDefinition! with { Name = "Specific" });

        var result = model.SaveDraft();

        Assert.True(result.Success);
        Assert.Contains(store.Targets, target => target.Name == "Specific");
        Assert.Equal(TargetManagementMode.Browse, model.Mode);
        Assert.Null(model.Editor);
    }

    [Fact]
    public void FailedSaveRetainsDraftAndCaptureContext()
    {
        var store = new FailingWorkspaceStore();
        using var model = new TargetManagementWorkspaceModel(store);
        model.BeginCaptured(Window(), InferredDraft());
        model.ApplyDraft(model.CurrentDefinition! with { Name = "Retry me" });

        var result = model.SaveDraft();

        Assert.False(result.Success);
        Assert.Equal(TargetManagementMode.AddCaptured, model.Mode);
        Assert.Equal("Retry me", model.CurrentDefinition!.Name);
        Assert.Equal(Window().Hwnd, model.CapturedWindow!.Hwnd);
    }

    [Fact]
    public void TogglingEditedRuleUpdatesEnabledDraftWithoutLosingOtherEdits()
    {
        var target = ExactTarget("Saved");
        var unrelated = ExactTarget("Other") with { Id = Guid.NewGuid() };
        var store = StoreWith(target, unrelated);
        using var model = new TargetManagementWorkspaceModel(store);
        model.BeginEdit(target.Id);
        model.ApplyDraft(model.CurrentDefinition! with { Name = "Unsaved name" });

        var result = model.SetEnabled(target.Id, false);

        Assert.True(result.Success);
        Assert.False(model.CurrentDefinition!.Enabled);
        Assert.Equal("Unsaved name", model.CurrentDefinition.Name);
        Assert.Equal("Other", store.Targets.Single(saved => saved.Id == unrelated.Id).Name);
    }

    [Fact]
    public void UnrelatedStoreNotificationPreservesActiveDraft()
    {
        var target = ExactTarget("Saved");
        var unrelated = ExactTarget("Other") with { Id = Guid.NewGuid() };
        var store = StoreWith(target, unrelated);
        using var model = new TargetManagementWorkspaceModel(store);
        model.BeginEdit(target.Id);
        model.ApplyDraft(model.CurrentDefinition! with { Name = "Unsaved name" });

        store.SetEnabled(unrelated.Id, false);

        Assert.Equal(TargetManagementMode.EditExisting, model.Mode);
        Assert.Equal(target.Id, model.SelectedSavedRuleId);
        Assert.Equal("Unsaved name", model.CurrentDefinition!.Name);
    }

    [Fact]
    public void DeletingEditedRuleReturnsToBrowseAndClearsDraftAndCapture()
    {
        var target = ExactTarget("Saved");
        var store = StoreWith(target);
        using var model = new TargetManagementWorkspaceModel(store);
        model.BeginCaptured(Window(), InferredDraft());

        var result = model.Delete(target.Id);

        Assert.True(result.Success);
        Assert.Equal(TargetManagementMode.Browse, model.Mode);
        Assert.Null(model.Editor);
        Assert.Null(model.CapturedWindow);
        Assert.Null(model.SelectedSavedRuleId);
    }

    [Fact]
    public void BrowseRowsIncludeAllRulesAndGroupEnabledAndDisabled()
    {
        var enabled = ExactTarget("Enabled");
        var disabled = ExactTarget("Disabled") with { Id = Guid.NewGuid(), Enabled = false };
        using var model = new TargetManagementWorkspaceModel(StoreWith(enabled, disabled));

        var rows = model.Rows;

        Assert.Equal(2, rows.Count);
        Assert.Equal(TargetManagementRuleGroup.Enabled, rows.Single(row => row.Target.Id == enabled.Id).Group);
        Assert.Equal(TargetManagementRuleGroup.Disabled, rows.Single(row => row.Target.Id == disabled.Id).Group);
    }

    [Fact]
    public void SearchFiltersWithinCapturedGroupsAndKeepsSearchOnlyResults()
    {
        var applies = ExactTarget("Applies");
        var overlaps = ExactTarget("Overlap") with
        {
            Id = Guid.NewGuid(),
            WindowTitle = new(TitleMatchKind.Prefix, "Other"),
        };
        var searched = ExactTarget("Discoverable") with
        {
            Id = Guid.NewGuid(),
            ProcessName = "other",
            WindowClass = "other-frame",
        };
        using var model = new TargetManagementWorkspaceModel(StoreWith(applies, overlaps, searched));
        model.BeginCaptured(Window(), InferredDraft());

        model.SetSearch("discover");

        var row = Assert.Single(model.Rows);
        Assert.Equal(searched.Id, row.Target.Id);
        Assert.Equal(TargetManagementRuleGroup.OtherSearchResults, row.Group);
    }

    [Fact]
    public void SearchFiltersRowsInBrowseAndEditModes()
    {
        var selected = ExactTarget("Visible rule");
        var hidden = ExactTarget("Hidden rule") with { Id = Guid.NewGuid() };
        using var model = new TargetManagementWorkspaceModel(StoreWith(selected, hidden));

        model.SetSearch("visible");
        Assert.Equal(new[] { selected.Id }, model.Rows.Select(row => row.Target.Id));

        model.BeginEdit(selected.Id);
        Assert.Equal(TargetManagementMode.EditExisting, model.Mode);
        Assert.Equal(new[] { selected.Id }, model.Rows.Select(row => row.Target.Id));
    }

    [Fact]
    public void ActivityProcessSearchFindsNormalizedExecutableName()
    {
        var target = ExactTarget("Terminal") with
        {
            Activation = TargetActivationPolicy.RequireActivityProcess,
            ActivityProcessNames = new() { "omp" },
        };
        using var model = new TargetManagementWorkspaceModel(StoreWith(target));

        model.SetSearch("omp.exe");

        Assert.Equal(new[] { target.Id }, model.Rows.Select(row => row.Target.Id));
    }

    private static WindowSnapshot Window() =>
        new(new IntPtr(42), 7, "app", "frame", "Project", true);

    private static TargetDraft InferredDraft() =>
        new(ExactTarget("Inferred") with { Id = Guid.NewGuid() }, "Project", Array.Empty<string>());

    private static TargetDefinition ExactTarget(string name) =>
        TargetDefinition.Captured(name, "app", "frame", new[] { ProviderBinding.Claude },
            TargetPlacementKind.Desktop, TargetActivationPolicy.AlwaysWhileOpen, true, 0);

    private static RecordingTargetRuleStore StoreWith(params TargetDefinition[] targets) => new(targets);

    private sealed class FailingWorkspaceStore : ITargetRuleStore
    {
        public IReadOnlyList<TargetDefinition> Targets => Array.Empty<TargetDefinition>();
        public event Action? Changed { add { } remove { } }
        public TargetMutationResult Add(TargetDefinition target) => new(false, "write failed");
        public TargetMutationResult Update(TargetDefinition target) => new(false, "write failed");
        public TargetMutationResult SetEnabled(Guid id, bool enabled) => new(false, "write failed");
        public TargetMutationResult Delete(Guid id) => new(false, "write failed");
    }
}
