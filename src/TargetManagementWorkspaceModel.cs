using System;
using System.Collections.Generic;
using System.Linq;

namespace UsageBars;

internal enum TargetManagementMode { Browse, EditExisting, AddCaptured }

internal enum TargetManagementRuleGroup
{
    Enabled,
    Disabled,
    Applies,
    Overlaps,
    OtherSearchResults,
}

internal sealed record TargetManagementRuleRow(
    TargetDefinition Target,
    TargetManagementRuleGroup Group,
    TargetRuleImpact? Impact);

internal sealed class TargetManagementWorkspaceModel : IDisposable
{
    private readonly ITargetRuleStore _store;
    private string? _search;
    private bool _disposed;

    public TargetManagementWorkspaceModel(ITargetRuleStore store)
    {
        _store = store;
        Mode = TargetManagementMode.Browse;
        Rows = Array.Empty<TargetManagementRuleRow>();
        _store.Changed += OnStoreChanged;
        RebuildRows();
    }

    public TargetManagementMode Mode { get; private set; }
    public Guid? SelectedSavedRuleId { get; private set; }
    public WindowSnapshot? CapturedWindow { get; private set; }
    public TargetEditorModel? Editor { get; private set; }
    public TargetDefinition? CurrentDefinition => Editor?.ToDefinition();
    public IReadOnlyList<TargetDefinition> SavedTargets => _store.Targets;
    public IReadOnlyList<TargetManagementRuleRow> Rows { get; private set; }
    public event Action? Changed;

    public void BeginEdit(Guid id)
    {
        var target = _store.Targets.Single(target => target.Id == id);
        Mode = TargetManagementMode.EditExisting;
        SelectedSavedRuleId = id;
        CapturedWindow = null;
        Editor = TargetEditorModel.FromExisting(target);
        RebuildRows();
    }

    public void BeginCaptured(WindowSnapshot window, TargetDraft inferredDraft)
    {
        Mode = TargetManagementMode.AddCaptured;
        SelectedSavedRuleId = null;
        CapturedWindow = window;
        Editor = TargetEditorModel.From(inferredDraft);
        RebuildRows();

        var strongest = TargetRuleImpactAnalyzer.StrongestApplicable(
            TargetRuleImpactAnalyzer.Analyze(window, inferredDraft.Definition, _store.Targets, null));
        if (strongest is null) return;

        SelectedSavedRuleId = strongest.Target.Id;
        Editor = TargetEditorModel.From(new TargetDraft(
            strongest.Target, inferredDraft.CapturedTitle, inferredDraft.InferenceNotes));
        RebuildRows();
    }

    public void CreateMoreSpecific()
    {
        if (Editor is null || CapturedWindow is null)
            throw new InvalidOperationException("A captured draft is required to create a more-specific rule.");

        var current = Editor.ToDefinition();
        SelectedSavedRuleId = null;
        Mode = TargetManagementMode.AddCaptured;
        Editor = TargetEditorModel.From(new TargetDraft(
            current with { Id = Guid.NewGuid(), Source = TargetSource.Captured, Revision = 0 },
            CapturedWindow.Title,
            Editor.InferenceNotes));
        RebuildRows();
    }

    public void ApplyDraft(TargetDefinition definition)
    {
        if (Editor is null)
            throw new InvalidOperationException("There is no active target draft.");

        Editor.ApplyDefinition(definition);
        RebuildRows();
    }

    public void CancelDraft()
    {
        if (Editor is null) return;

        Editor = null;
        CapturedWindow = null;
        Mode = TargetManagementMode.Browse;
        RebuildRows();
    }

    public TargetMutationResult SaveDraft()
    {
        var definition = CurrentDefinition;
        if (definition is null) return new(false, "There is no active target draft.");

        var result = SelectedSavedRuleId is not null
            ? _store.Update(definition)
            : _store.Add(definition);
        if (!result.Success) return result;

        SelectedSavedRuleId = definition.Id;
        Editor = null;
        CapturedWindow = null;
        Mode = TargetManagementMode.Browse;
        RebuildRows();
        return result;
    }

    public TargetMutationResult SetEnabled(Guid id, bool enabled)
    {
        var result = _store.SetEnabled(id, enabled);
        if (!result.Success) return result;

        if (Editor is not null && SelectedSavedRuleId == id)
            Editor.ApplyDefinition(Editor.ToDefinition() with { Enabled = enabled });
        RebuildRows();
        return result;
    }

    public TargetMutationResult Delete(Guid id)
    {
        var result = _store.Delete(id);
        if (!result.Success) return result;

        if (SelectedSavedRuleId == id)
        {
            SelectedSavedRuleId = null;
            Editor = null;
            CapturedWindow = null;
            Mode = TargetManagementMode.Browse;
        }
        RebuildRows();
        return result;
    }

    public void SetSearch(string? query)
    {
        _search = query;
        RebuildRows();
    }

    private void OnStoreChanged()
    {
        if (SelectedSavedRuleId is { } selectedId)
        {
            var saved = _store.Targets.FirstOrDefault(target => target.Id == selectedId);
            if (saved is null)
            {
                SelectedSavedRuleId = null;
                Editor = null;
                CapturedWindow = null;
                Mode = TargetManagementMode.Browse;
            }
            else if (Editor is not null)
            {
                Editor.ApplyDefinition(Editor.ToDefinition() with { Enabled = saved.Enabled });
            }
        }
        RebuildRows();
    }

    private void RebuildRows()
    {
        if (Mode == TargetManagementMode.AddCaptured
            && CapturedWindow is { } window
            && CurrentDefinition is { } draft)
        {
            Rows = TargetRuleImpactAnalyzer.Analyze(window, draft, _store.Targets, _search)
                .Select(impact => new TargetManagementRuleRow(
                    impact.Target,
                    impact.ExactIdentity || impact.AppliesNow
                        ? TargetManagementRuleGroup.Applies
                        : impact.OverlapsDraft
                            ? TargetManagementRuleGroup.Overlaps
                            : TargetManagementRuleGroup.OtherSearchResults,
                    impact))
                .ToList();
        }
        else
        {
            Rows = _store.Targets
                .Where(target => string.IsNullOrWhiteSpace(_search) || TargetRuleSearch.Matches(target, _search))
                .OrderByDescending(target => target.Enabled)
                .ThenBy(target => target.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(target => target.Id)
                .Select(target => new TargetManagementRuleRow(
                    target,
                    target.Enabled ? TargetManagementRuleGroup.Enabled : TargetManagementRuleGroup.Disabled,
                    null))
                .ToList();
        }

        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _store.Changed -= OnStoreChanged;
    }
}
