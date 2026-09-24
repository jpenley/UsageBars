using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace UsageBars.Tests;

public class TargetCaptureWorkspaceModelTests
{
    [Fact]
    public void WorkspaceEditsStrongestApplicableRuleByDefault()
    {
        var broad = BroadTarget("Broad", revision: 5);
        var exact = ExactTarget("Exact", revision: 1);
        var store = StoreWith(broad, exact);

        using var model = new TargetCaptureWorkspaceModel(Window(), InferredDraft(), store);

        Assert.Equal(CaptureDraftMode.UpdateExisting, model.Mode);
        Assert.Equal(exact.Id, model.SelectedSavedRuleId);
        Assert.Equal(exact.Id, model.CurrentDefinition.Id);
    }

    [Fact]
    public void CreateMoreSpecificUsesNewIdAndKeepsCurrentVisualDefaults()
    {
        var existing = ExactTarget("Existing", revision: 7) with
        {
            Anchor = PillAnchor.TopRight,
            OffsetX = 12,
            OffsetY = -4,
        };
        var store = StoreWith(existing);
        using var model = new TargetCaptureWorkspaceModel(Window(), InferredDraft(), store);

        model.CreateMoreSpecific();

        Assert.Equal(CaptureDraftMode.AddNew, model.Mode);
        Assert.NotEqual(existing.Id, model.CurrentDefinition.Id);
        Assert.Equal((PillAnchor.TopRight, 12, -4),
            (model.CurrentDefinition.Anchor, model.CurrentDefinition.OffsetX, model.CurrentDefinition.OffsetY));
    }

    [Fact]
    public void SearchAddsOtherwiseUnrelatedRules()
    {
        var unrelated = ExactTarget("Special saved rule", 1) with
        {
            Id = Guid.NewGuid(),
            ProcessName = "other",
            WindowClass = "other-frame",
        };
        using var model = new TargetCaptureWorkspaceModel(
            Window(), InferredDraft(), StoreWith(unrelated));

        Assert.Empty(model.Rows);

        model.SetSearch("special");

        var row = Assert.Single(model.Rows);
        Assert.Equal(TargetRuleRelationship.SearchOnly, row.Relationship);
    }

    [Fact]
    public void DisableAndDeletePersistImmediately()
    {
        var target = ExactTarget("Existing", 1);
        var store = StoreWith(target);
        using var model = new TargetCaptureWorkspaceModel(Window(), InferredDraft(), store);

        Assert.True(model.SetEnabled(target.Id, false).Success);
        Assert.False(store.Targets.Single().Enabled);
        Assert.True(model.Delete(target.Id).Success);
        Assert.Empty(store.Targets);
        Assert.Equal(2, store.MutationCount);
    }

    [Fact]
    public void DeletingEditedRulePreservesValuesAsUnsavedNewDraft()
    {
        var target = ExactTarget("Existing", 1);
        var store = StoreWith(target);
        using var model = new TargetCaptureWorkspaceModel(Window(), InferredDraft(), store);
        model.Editor.Name = "Unsaved name";

        model.Delete(target.Id);

        Assert.Equal(CaptureDraftMode.AddNew, model.Mode);
        Assert.Equal("Unsaved name", model.Editor.Name);
        Assert.NotEqual(target.Id, model.CurrentDefinition.Id);
    }

    [Fact]
    public void SaveUsesUpdateForExistingAndAddForMoreSpecificDraft()
    {
        var target = ExactTarget("Existing", 1);
        var store = StoreWith(target);
        using var model = new TargetCaptureWorkspaceModel(Window(), InferredDraft(), store);

        Assert.True(model.Save().Success);
        Assert.Equal(1, store.UpdateCalls);
        Assert.Equal(0, store.AddCalls);

        model.CreateMoreSpecific();
        model.Editor.Name = "Specific";
        Assert.True(model.Save().Success);
        Assert.Equal(1, store.UpdateCalls);
        Assert.Equal(1, store.AddCalls);
    }

    [Fact]
    public void ApplyingDraftReclassifiesOverlapsImmediately()
    {
        var claude = ExactTarget("Claude", 1);
        var codex = claude with
        {
            Id = Guid.NewGuid(),
            Name = "Codex",
            Providers = new List<ProviderBinding> { ProviderBinding.Codex },
        };
        using var model = new TargetCaptureWorkspaceModel(Window(), InferredDraft(), StoreWith(claude, codex));

        model.ApplyDraft(model.CurrentDefinition with
        {
            Providers = new List<ProviderBinding> { ProviderBinding.Codex },
        });

        Assert.Contains(model.Rows, row => row.Target.Id == codex.Id && row.ExactIdentity);
    }

    private static WindowSnapshot Window() =>
        new(new IntPtr(42), 7, "app", "frame", "Project", true);

    private static TargetDraft InferredDraft() =>
        new(ExactTarget("Inferred", 0) with { Id = Guid.NewGuid() }, "Project", Array.Empty<string>());

    private static TargetDefinition ExactTarget(string name, long revision) =>
        TargetDefinition.Captured(name, "app", "frame", new[] { ProviderBinding.Claude },
            TargetPlacementKind.Desktop, TargetActivationPolicy.AlwaysWhileOpen, true, 0) with
        { Revision = revision };

    private static TargetDefinition BroadTarget(string name, long revision) =>
        TargetDefinition.Seeded(name, "app", null, new[] { ProviderBinding.Claude },
            TargetPlacementKind.Desktop, TargetActivationPolicy.AlwaysWhileOpen, true, 0) with
        { Revision = revision };

    private static RecordingTargetRuleStore StoreWith(params TargetDefinition[] targets) => new(targets);
}

internal sealed class RecordingTargetRuleStore : ITargetRuleStore
{
    private readonly List<TargetDefinition> _targets;

    public RecordingTargetRuleStore(params TargetDefinition[] targets) =>
        _targets = targets.ToList();

    public IReadOnlyList<TargetDefinition> Targets => _targets;
    public int MutationCount { get; private set; }
    public int AddCalls { get; private set; }
    public int UpdateCalls { get; private set; }
    public event Action? Changed;

    public TargetMutationResult Add(TargetDefinition target)
    {
        AddCalls++;
        if (_targets.Any(existing => existing.Id == target.Id)) return new(false, "duplicate id");
        _targets.Add(target);
        Publish();
        return new(true);
    }

    public TargetMutationResult Update(TargetDefinition target)
    {
        UpdateCalls++;
        var index = _targets.FindIndex(existing => existing.Id == target.Id);
        if (index < 0) return new(false, "missing");
        _targets[index] = target;
        Publish();
        return new(true);
    }

    public TargetMutationResult SetEnabled(Guid id, bool enabled)
    {
        var index = _targets.FindIndex(existing => existing.Id == id);
        if (index < 0) return new(false, "missing");
        _targets[index] = _targets[index] with { Enabled = enabled };
        Publish();
        return new(true);
    }

    public TargetMutationResult Delete(Guid id)
    {
        var removed = _targets.RemoveAll(existing => existing.Id == id);
        if (removed == 0) return new(false, "missing");
        Publish();
        return new(true);
    }

    private void Publish()
    {
        MutationCount++;
        Changed?.Invoke();
    }
}
