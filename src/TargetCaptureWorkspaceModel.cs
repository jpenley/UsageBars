using System;
using System.Collections.Generic;
using System.Linq;

namespace UsageBars;

internal enum CaptureDraftMode
{
    AddNew,
    UpdateExisting,
}

internal sealed class TargetCaptureWorkspaceModel : IDisposable
{
    private readonly WindowSnapshot _window;
    private readonly TargetDraft _inferredDraft;
    private readonly ITargetRuleStore _store;
    private string? _search;
    private bool _disposed;

    public TargetCaptureWorkspaceModel(
        WindowSnapshot window,
        TargetDraft inferredDraft,
        ITargetRuleStore store)
    {
        _window = window;
        _inferredDraft = inferredDraft;
        _store = store;
        Editor = TargetEditorModel.From(inferredDraft);
        Rows = TargetRuleImpactAnalyzer.Analyze(window, inferredDraft.Definition, store.Targets, null);
        _store.Changed += OnStoreChanged;

        var strongest = TargetRuleImpactAnalyzer.StrongestApplicable(Rows);
        if (strongest is not null) SelectForEdit(strongest.Target.Id);
        else Mode = CaptureDraftMode.AddNew;
    }

    public CaptureDraftMode Mode { get; private set; }
    public Guid? SelectedSavedRuleId { get; private set; }
    public TargetEditorModel Editor { get; private set; }
    public TargetDefinition CurrentDefinition => Editor.ToDefinition();
    public IReadOnlyList<TargetRuleImpact> Rows { get; private set; }
    public WindowSnapshot Window => _window;

    public event Action? Changed;

    public void SelectForEdit(Guid id)
    {
        var target = _store.Targets.Single(target => target.Id == id);
        Mode = CaptureDraftMode.UpdateExisting;
        SelectedSavedRuleId = id;
        Editor = TargetEditorModel.From(new TargetDraft(
            target, _window.Title, _inferredDraft.InferenceNotes));
        Reanalyze();
    }

    public void CreateMoreSpecific()
    {
        var current = Editor.ToDefinition();
        Mode = CaptureDraftMode.AddNew;
        SelectedSavedRuleId = null;
        Editor = TargetEditorModel.From(new TargetDraft(
            current with { Id = Guid.NewGuid(), Source = TargetSource.Captured, Revision = 0 },
            _window.Title,
            _inferredDraft.InferenceNotes));
        Reanalyze();
    }

    public void ApplyDraft(TargetDefinition definition)
    {
        Editor.ApplyDefinition(definition);
        Reanalyze();
    }

    public void SetSearch(string? query)
    {
        _search = query;
        Reanalyze();
    }

    public TargetMutationResult SetEnabled(Guid id, bool enabled) =>
        _store.SetEnabled(id, enabled);

    public TargetMutationResult Delete(Guid id) => _store.Delete(id);

    public TargetMutationResult Save()
    {
        var definition = CurrentDefinition;
        return Mode == CaptureDraftMode.UpdateExisting
            ? _store.Update(definition)
            : _store.Add(definition);
    }

    private void OnStoreChanged()
    {
        if (Mode == CaptureDraftMode.UpdateExisting && SelectedSavedRuleId is { } selectedId)
        {
            var saved = _store.Targets.FirstOrDefault(target => target.Id == selectedId);
            if (saved is null)
            {
                var current = Editor.ToDefinition();
                Mode = CaptureDraftMode.AddNew;
                SelectedSavedRuleId = null;
                Editor = TargetEditorModel.From(new TargetDraft(
                    current with { Id = Guid.NewGuid(), Revision = 0, Source = TargetSource.Captured },
                    _window.Title,
                    _inferredDraft.InferenceNotes));
            }
            else
            {
                Editor = TargetEditorModel.From(new TargetDraft(
                    saved, _window.Title, _inferredDraft.InferenceNotes));
            }
        }
        Reanalyze();
    }

    private void Reanalyze()
    {
        Rows = TargetRuleImpactAnalyzer.Analyze(
            _window, Editor.ToDefinition(), _store.Targets, _search);
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _store.Changed -= OnStoreChanged;
    }
}
