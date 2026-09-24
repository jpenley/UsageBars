using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Xunit;

namespace UsageBars.Tests;

public class TargetManagerFormTests
{
    [Fact]
    public void BrowseShowsSearchAddAndGroupedRulesWithoutAnActiveEditor()
    {
        var enabled = Rule("Enabled");
        var disabled = Rule("Disabled") with { Id = Guid.NewGuid(), Enabled = false };
        using var model = new TargetManagementWorkspaceModel(new RecordingTargetRuleStore(enabled, disabled));
        using var form = NewForm(model);

        var ruleList = FindControl<ListView>(form);

        Assert.NotNull(FindSearch(form));
        Assert.NotNull(FindButton(form, "Add"));
        Assert.Contains(ruleList.Groups.Cast<ListViewGroup>(), group => group.Header == "Enabled rules");
        Assert.Contains(ruleList.Groups.Cast<ListViewGroup>(), group => group.Header == "Disabled rules");
        Assert.Equal(new[] { enabled.Id, disabled.Id }.Order(), VisibleRuleIds(form).Order());
        Assert.Empty(FindControls<TargetEditorControl>(form));
    }

    [Fact]
    public void ResizableFrameOptionRemainsReadableAtMinimumSize()
    {
        var target = Rule("Rider");
        using var model = new TargetManagementWorkspaceModel(new RecordingTargetRuleStore(target));
        using var form = NewForm(model);
        form.Show();
        form.RestoreBrowseSelection(target.Id);
        FindButton(form, "Edit").PerformClick();
        form.ClientSize = form.MinimumSize;
        form.PerformLayout();

        var editor = Assert.Single(FindControls<TargetEditorControl>(form));
        editor.PerformLayout();

        var split = FindControl<SplitContainer>(form);
        Assert.True(split.Panel1.ClientSize.Width >= 320);
        Assert.True(split.Panel2.ClientSize.Width >= 620);
        Assert.True(editor.OptionsAreReadable);
    }

    [Fact]
    public void CancelAndSaveStayAdjacentAtMinimumSizeAfterActivityEditorExpansion()
    {
        var target = Rule("Rider");
        using var model = new TargetManagementWorkspaceModel(new RecordingTargetRuleStore(target));
        using var form = NewForm(model);
        form.Size = form.MinimumSize;
        BeginEdit(form, target.Id);
        ShowActivityEditor(form);
        Application.DoEvents();

        var cancel = FindButton(form, "Cancel changes");
        var save = FindButton(form, "Save changes");
        var split = FindControl<SplitContainer>(form);
        var cancelBounds = cancel.RectangleToScreen(cancel.ClientRectangle);
        var saveBounds = save.RectangleToScreen(save.ClientRectangle);
        var editorBounds = split.Panel2.RectangleToScreen(split.Panel2.ClientRectangle);

        Assert.Equal(cancelBounds.Top, saveBounds.Top);
        Assert.True(cancelBounds.Right <= saveBounds.Left);
        Assert.True(editorBounds.Contains(cancelBounds));
        Assert.True(editorBounds.Contains(saveBounds));
    }

    [Fact]
    public void SearchAndSelectionSurviveHideAndBrowseRestore()
    {
        var rider = Rule("Rider");
        var terminal = Rule("Terminal") with { Id = Guid.NewGuid(), ProcessName = "pwsh" };
        using var model = new TargetManagementWorkspaceModel(new RecordingTargetRuleStore(rider, terminal));
        using var form = NewForm(model);
        var search = FindSearch(form);

        search.Text = "pwsh";
        form.RestoreBrowseSelection(terminal.Id);
        form.Hide();
        form.RestoreBrowseSelection(terminal.Id);

        Assert.Equal("pwsh", search.Text);
        Assert.Equal(new[] { terminal.Id }, VisibleRuleIds(form));
        Assert.Equal(terminal.Id, form.SelectedRuleId);
    }

    [Fact]
    public void EditLoadsTheSelectedSavedRuleIntoTheSharedEditor()
    {
        var selected = Rule("Selected");
        using var model = new TargetManagementWorkspaceModel(new RecordingTargetRuleStore(selected));
        using var form = NewForm(model);
        form.Show();
        form.RestoreBrowseSelection(selected.Id);

        FindButton(form, "Edit").PerformClick();

        var editor = Assert.Single(FindControls<TargetEditorControl>(form));
        Assert.Equal(TargetManagementMode.EditExisting, model.Mode);
        Assert.Equal(selected.Id, form.SelectedRuleId);
        Assert.Equal("Selected", editor.Model.Name);
        Assert.NotNull(FindButton(form, "Save changes"));
        Assert.NotNull(FindButton(form, "Cancel changes"));
    }

    [Fact]
    public void EditActionCannotSilentlyReplaceAnActiveDraft()
    {
        var first = Rule("First");
        var second = Rule("Second") with { Id = Guid.NewGuid() };
        using var model = new TargetManagementWorkspaceModel(new RecordingTargetRuleStore(first, second));
        using var form = NewForm(model);
        BeginEdit(form, first.Id);

        form.RestoreBrowseSelection(second.Id);

        Assert.False(FindButton(form, "Edit").Enabled);
        Assert.Equal(first.Id, model.SelectedSavedRuleId);
        Assert.Equal("First", model.CurrentDefinition?.Name);
    }

    [Fact]
    public void BeginCapturedGroupsAppliesAndOverlapsAndSelectsTheStrongestRule()
    {
        var broad = Rule("Broad") with { WindowClass = null, Revision = 8 };
        var exact = Rule("Exact") with { Id = Guid.NewGuid(), Revision = 1, Enabled = false };
        var overlap = Rule("Title overlap") with
        {
            Id = Guid.NewGuid(),
            WindowTitle = new TitleRule(TitleMatchKind.Literal, "Other"),
        };
        using var model = new TargetManagementWorkspaceModel(
            new RecordingTargetRuleStore(broad, exact, overlap));
        using var form = NewForm(model);

        form.BeginCaptured(new TargetCaptureContext(
            Window(), new TargetDraft(Rule("Inferred") with { Id = Guid.NewGuid() },
                "Project", Array.Empty<string>()), ProcessInventory.Empty));

        var ruleList = FindControl<ListView>(form);
        var groups = ruleList.Groups.Cast<ListViewGroup>().ToArray();
        var exactItem = ruleList.Items.Cast<ListViewItem>()
            .Single(item => ((TargetManagementRuleRow)item.Tag!).Target.Id == exact.Id);
        Assert.Equal(TargetManagementMode.AddCaptured, model.Mode);
        Assert.Contains(groups, group => group.Header == "Applies to this window");
        Assert.Contains(groups, group => group.Header == "Overlaps proposed rule");
        Assert.Equal(exact.Id, form.SelectedRuleId);
        Assert.Equal("Exact", Assert.Single(FindControls<TargetEditorControl>(form)).Model.Name);
        Assert.Contains(exact.Id.ToString("N")[..8], exactItem.SubItems[1].Text);
        Assert.Contains("Claude", exactItem.SubItems[1].Text);
        Assert.Contains("disabled", exactItem.SubItems[1].Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Exact identity", exactItem.SubItems[1].Text);
        Assert.True(FindButton(form, "Create more-specific rule").Enabled);
    }

    [Fact]
    public void SelectedRuleDetailsShowFullIdAndMatchFields()
    {
        var target = Rule("Rider") with
        {
            WindowTitle = new TitleRule(TitleMatchKind.Prefix, "Project"),
        };
        using var model = new TargetManagementWorkspaceModel(new RecordingTargetRuleStore(target));
        using var form = NewForm(model);

        form.RestoreBrowseSelection(target.Id);

        var details = FindControls<TextBox>(form).Single(text => text.Multiline && text.ReadOnly).Text;
        Assert.Contains(target.Id.ToString("D"), details);
        Assert.Contains("process=app", details);
        Assert.Contains("class=frame", details);
        Assert.Contains("Title: Prefix:Project", details);
    }

    [Fact]
    public void ManagerItemShowsActivityAndTransientMigrationWarning()
    {
        var target = Rule("Migrated") with
        {
            ActivityProcessNames = new() { "omp", "codex" },
            Activation = TargetActivationPolicy.RequireActivityProcess,
            MigrationWarning = "Review the migrated activity rule.",
        };

        var item = TargetManagementRuleItem.From(
            new TargetManagementRuleRow(target, TargetManagementRuleGroup.Enabled, null), new[] { target });

        Assert.Contains("Activity: codex OR omp", item.ToolTip);
        Assert.Contains("activity=codex|omp", item.SecondaryText);
        Assert.Contains("Review the migrated activity rule.", item.ToolTip);
        Assert.Contains("Review the migrated activity rule.",
            TargetDetailsFormatter.Format(target, new[] { target }));
    }

    [Fact]
    public void FilteredDetailsStillReportOverlapsWithHiddenSavedRules()
    {
        var selected = Rule("Selected only");
        var hiddenOverlap = Rule("Hidden overlap") with { Id = Guid.NewGuid() };
        using var model = new TargetManagementWorkspaceModel(
            new RecordingTargetRuleStore(selected, hiddenOverlap));
        using var form = NewForm(model);

        FindSearch(form).Text = "Selected only";
        form.RestoreBrowseSelection(selected.Id);

        var details = FindControls<TextBox>(form).Single(text => text.Multiline && text.ReadOnly).Text;
        Assert.Equal(new[] { selected.Id }, VisibleRuleIds(form));
        Assert.Contains("Hidden overlap", details);
    }

    [Fact]
    public void BrowseRowWarnsAboutAnOverlappingRuleHiddenBySearch()
    {
        var selected = Rule("Selected only");
        var hiddenOverlap = Rule("Hidden overlap") with { Id = Guid.NewGuid() };
        using var model = new TargetManagementWorkspaceModel(
            new RecordingTargetRuleStore(selected, hiddenOverlap));
        using var form = NewForm(model);

        FindSearch(form).Text = "Selected only";

        var item = Assert.Single(FindControl<ListView>(form).Items.Cast<ListViewItem>());
        Assert.Contains("overlap", item.SubItems[1].Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BrowseRefreshPreservesTheActualVisibleSelectionInsteadOfDraftHistory()
    {
        var first = Rule("First");
        var second = Rule("Second") with { Id = Guid.NewGuid() };
        using var model = new TargetManagementWorkspaceModel(new RecordingTargetRuleStore(first, second));
        using var form = NewForm(model);
        BeginEdit(form, first.Id);
        FindButton(form, "Cancel changes").PerformClick();
        SelectRule(form, second.Id);

        FindSearch(form).Text = "app";

        Assert.Equal(second.Id, form.SelectedRuleId);
    }

    [Fact]
    public void FilteringOutBrowseSelectionClearsCoordinatorSelectionAndRowActions()
    {
        var first = Rule("First");
        var second = Rule("Second") with { Id = Guid.NewGuid() };
        var store = new RecordingTargetRuleStore(first, second);
        using var model = new TargetManagementWorkspaceModel(store);
        using var form = NewForm(model, confirmDelete: (_, _) => true);
        form.Show();
        SelectRule(form, first.Id);

        FindSearch(form).Text = "Second";

        Assert.Null(form.SelectedRuleId);
        Assert.False(FindButton(form, "Edit").Enabled);
        Assert.False(FindButton(form, "Delete…").Enabled);
        FindButton(form, "Edit").PerformClick();
        FindButton(form, "Delete…").PerformClick();
        Assert.Equal(TargetManagementMode.Browse, model.Mode);
        Assert.Equal(2, store.Targets.Count);
    }

    [Fact]
    public void AddButtonRaisesTheModelessCaptureRequest()
    {
        using var model = new TargetManagementWorkspaceModel(new RecordingTargetRuleStore());
        using var form = NewForm(model);
        form.Show();
        var requests = 0;
        form.AddRequested += (_, _) => requests++;

        FindButton(form, "Add").PerformClick();

        Assert.Equal(1, requests);
        Assert.Equal(TargetManagementMode.Browse, model.Mode);
    }

    [Fact]
    public void EditStartsPreviewAndDraftChangesUpdateItWithoutDuplicateHandleEvents()
    {
        var target = Rule("Rider");
        var previews = new RecordingFormPreviewSet(new IntPtr(42));
        using var model = new TargetManagementWorkspaceModel(new RecordingTargetRuleStore(target));
        using var form = NewForm(model, previews);
        var handleChanges = 0;
        form.PreviewHandlesChanged += (_, _) => handleChanges++;
        BeginEdit(form, target.Id);

        var name = FindEditorName(form, "Rider");
        name.Text = "Unsaved name";

        Assert.Equal(TargetManagementMode.EditExisting, previews.LatestMode);
        Assert.Equal("Unsaved name", previews.LatestDraft?.Name);
        Assert.True(previews.UpdateCalls >= 2);
        Assert.Equal(new[] { new IntPtr(42) }, form.SuppressedHwnds);
        Assert.Equal(1, handleChanges);
        Assert.Contains(FindControls<Label>(form),
            label => label.Text == "Previewing 1 window normally");
    }

    [Fact]
    public void CapturedWindowImmediatelyReceivesPreviewBeforeAutomaticRefresh()
    {
        var previews = new RecordingFormPreviewSet(new IntPtr(42));
        using var model = new TargetManagementWorkspaceModel(new RecordingTargetRuleStore());
        using var form = NewForm(model, previews);

        form.BeginCaptured(new TargetCaptureContext(
            Window(),
            new TargetDraft(Rule("Captured") with { Id = Guid.NewGuid() },
                "Project", Array.Empty<string>()),
            ProcessInventory.Empty));

        Assert.True(previews.UpdateCalls > 0);
        Assert.Equal(0, previews.RefreshCalls);
        Assert.Equal(new[] { new IntPtr(42) }, form.SuppressedHwnds);
    }

    [Theory]
    [InlineData((int)TargetPreviewState.Dimmed, "Previewing 1 window dimmed — process missing")]
    [InlineData((int)TargetPreviewState.WindowClosed, "Preview unavailable — selected window closed")]
    public void PreviewStatusDisplaysDimmedAndClosedExplanations(
        int state,
        string message)
    {
        var target = Rule("Rider");
        var previews = new RecordingFormPreviewSet(new IntPtr(42))
        {
            StatusAfterUpdate = new(1, (TargetPreviewState)state, message),
        };
        using var model = new TargetManagementWorkspaceModel(new RecordingTargetRuleStore(target));
        using var form = NewForm(model, previews);

        BeginEdit(form, target.Id);

        Assert.Contains(FindControls<Label>(form), label => label.Text == message);
    }

    [Fact]
    public void SavePersistsOnceClearsPreviewAndReturnsToBrowse()
    {
        var target = Rule("Rider");
        var store = new RecordingTargetRuleStore(target);
        var previews = new RecordingFormPreviewSet(new IntPtr(42));
        using var model = new TargetManagementWorkspaceModel(store);
        using var form = NewForm(model, previews);
        var previewHandleChanges = 0;
        form.PreviewHandlesChanged += (_, _) => previewHandleChanges++;
        BeginEdit(form, target.Id);
        var changesBeforeSave = previewHandleChanges;
        FindEditorName(form, "Rider").Text = "Saved name";

        FindButton(form, "Save changes").PerformClick();

        Assert.Equal(1, store.UpdateCalls);
        Assert.Equal("Saved name", store.Targets.Single().Name);
        Assert.Equal(TargetManagementMode.Browse, model.Mode);
        Assert.Empty(FindControls<TargetEditorControl>(form));
        Assert.Empty(form.SuppressedHwnds);
        Assert.Equal(changesBeforeSave + 1, previewHandleChanges);
    }

    [Fact]
    public void SaveFailureKeepsDraftPreviewAndShowsInlineError()
    {
        var target = Rule("Rider");
        var previews = new RecordingFormPreviewSet(new IntPtr(42));
        using var model = new TargetManagementWorkspaceModel(new FailingFormStore(target));
        using var form = NewForm(model, previews);
        BeginEdit(form, target.Id);

        FindButton(form, "Save changes").PerformClick();

        Assert.Equal(TargetManagementMode.EditExisting, model.Mode);
        Assert.Single(FindControls<TargetEditorControl>(form));
        Assert.Equal(new[] { new IntPtr(42) }, form.SuppressedHwnds);
        Assert.Contains(FindControls<Label>(form), label => label.Text.Contains("write failed"));
    }

    [Fact]
    public void CancelAndCloseDiscardDraftsAndClearPreviews()
    {
        var target = Rule("Rider");
        var cancelPreviews = new RecordingFormPreviewSet(new IntPtr(42));
        using (var cancelModel = new TargetManagementWorkspaceModel(new RecordingTargetRuleStore(target)))
        using (var cancelForm = NewForm(cancelModel, cancelPreviews))
        {
            var previewHandleChanges = 0;
            cancelForm.PreviewHandlesChanged += (_, _) => previewHandleChanges++;
            BeginEdit(cancelForm, target.Id);
            var changesBeforeCancel = previewHandleChanges;
            FindButton(cancelForm, "Cancel changes").PerformClick();

            Assert.Equal(TargetManagementMode.Browse, cancelModel.Mode);
            Assert.Empty(cancelForm.SuppressedHwnds);
            Assert.Equal(changesBeforeCancel + 1, previewHandleChanges);
        }

        var closePreviews = new RecordingFormPreviewSet(new IntPtr(42));
        using var closeModel = new TargetManagementWorkspaceModel(new RecordingTargetRuleStore(target));
        using var closeForm = NewForm(closeModel, closePreviews);
        BeginEdit(closeForm, target.Id);

        closeForm.Close();

        Assert.Equal(TargetManagementMode.Browse, closeModel.Mode);
        Assert.Empty(closePreviews.SuppressedHwnds);
    }

    [Fact]
    public void EnableDisablePersistsImmediatelyAndUpdatesTheActiveDraft()
    {
        var target = Rule("Rider");
        var store = new RecordingTargetRuleStore(target);
        using var model = new TargetManagementWorkspaceModel(store);
        using var form = NewForm(model);
        BeginEdit(form, target.Id);

        FindButton(form, "Disable").PerformClick();

        Assert.False(store.Targets.Single().Enabled);
        Assert.False(model.CurrentDefinition?.Enabled);
        Assert.False(Assert.Single(FindControls<TargetEditorControl>(form)).Model.Enabled);
        Assert.Equal(TargetManagementMode.EditExisting, model.Mode);
    }

    [Fact]
    public void DeletingTheEditedRulePersistsImmediatelyClearsEditorAndPreview()
    {
        var target = Rule("Rider");
        var store = new RecordingTargetRuleStore(target);
        var previews = new RecordingFormPreviewSet(new IntPtr(42));
        using var model = new TargetManagementWorkspaceModel(store);
        using var form = NewForm(model, previews, confirmDelete: (_, _) => true);
        BeginEdit(form, target.Id);

        FindButton(form, "Delete…").PerformClick();

        Assert.Empty(store.Targets);
        Assert.Equal(TargetManagementMode.Browse, model.Mode);
        Assert.Empty(FindControls<TargetEditorControl>(form));
        Assert.Empty(form.SuppressedHwnds);
    }

    [Fact]
    public void CapturedModeUsesTheCapturedOwnerInventory()
    {
        var capturedInventory = Inventory(42, 7, "Captured owner", "captured-child");
        var other = Window(99, "Other");
        var requestedInventories = new List<IntPtr>();
        using var model = new TargetManagementWorkspaceModel(new RecordingTargetRuleStore());
        using var form = NewForm(model, windows: new[] { other }, inventory: window =>
        {
            requestedInventories.Add(window.Hwnd);
            return Inventory(window.Hwnd.ToInt64(), window.Pid, window.Title, "refreshed-child");
        });
        form.Show();

        form.BeginCaptured(new TargetCaptureContext(
            Window(), new TargetDraft(Rule("Inferred") with { Id = Guid.NewGuid() },
                "Project", Array.Empty<string>()), capturedInventory));
        var tree = FindControl<TreeView>(Assert.Single(FindControls<TargetEditorControl>(form)));
        Assert.Contains(AllNodes(tree.Nodes), node => node.Text.Contains("captured-child"));

        form.RefreshLiveState();

        Assert.Empty(requestedInventories);

        ShowActivityEditor(form);
        FindButton(form, "↻").PerformClick();

        Assert.Equal(new[] { new IntPtr(42) }, requestedInventories);
    }

    [Fact]
    public void ActivityEditsUpdatePreviewWithoutEnumeratingProcesses()
    {
        var target = Rule("Rider");
        var inventoryCalls = 0;
        var previews = new RecordingFormPreviewSet(new IntPtr(42));
        using var model = new TargetManagementWorkspaceModel(new RecordingTargetRuleStore(target));
        using var form = NewForm(model, previews, windows: new[] { Window() }, inventory: window =>
        {
            inventoryCalls++;
            return Inventory(window.Hwnd.ToInt64(), window.Pid, window.Title, "omp");
        });
        BeginEdit(form, target.Id);
        var updatesBeforeEdit = previews.UpdateCalls;

        ShowActivityEditor(form);
        AllNodes(FindControl<TreeView>(form).Nodes)
            .Single(node => node.Text.StartsWith("omp (", StringComparison.Ordinal))
            .Checked = true;

        Assert.Equal(1, inventoryCalls);
        Assert.True(previews.UpdateCalls > updatesBeforeEdit);
        Assert.Equal(new[] { "omp" }, previews.LatestDraft?.ActivityProcessNames);
    }

    [Fact]
    public void TitleEditsUpdatePreviewWithoutEnumeratingProcesses()
    {
        var target = Rule("Rider");
        var inventoryCalls = 0;
        var previews = new RecordingFormPreviewSet(new IntPtr(42));
        using var model = new TargetManagementWorkspaceModel(new RecordingTargetRuleStore(target));
        using var form = NewForm(model, previews, windows: new[] { Window() }, inventory: window =>
        {
            inventoryCalls++;
            return Inventory(window.Hwnd.ToInt64(), window.Pid, window.Title, "omp");
        });
        BeginEdit(form, target.Id);
        var updatesBeforeEdit = previews.UpdateCalls;
        var editor = Assert.Single(FindControls<TargetEditorControl>(form));

        FindControls<CheckBox>(editor).Single(check => check.Text == "Use title matcher").Checked = true;
        FindControls<TextBox>(editor)
            .Single(text => text.AccessibleName == "Title matcher value").Text = "Project";

        Assert.Equal(1, inventoryCalls);
        Assert.True(previews.UpdateCalls > updatesBeforeEdit);
        Assert.Equal("Project", previews.LatestDraft?.WindowTitle?.Value);
    }

    [Fact]
    public void ManualProcessRefreshRetainsAbsentActivityChips()
    {
        var target = Rule("Rider") with
        {
            Activation = TargetActivationPolicy.RequireActivityProcess,
            ActivityProcessNames = new() { "codex" },
        };
        var refresh = false;
        using var model = new TargetManagementWorkspaceModel(new RecordingTargetRuleStore(target));
        using var form = NewForm(model, windows: new[] { Window() }, inventory: window =>
            Inventory(window.Hwnd.ToInt64(), window.Pid, window.Title, refresh ? "omp" : "codex"));
        BeginEdit(form, target.Id);
        refresh = true;

        FindButton(form, "↻").PerformClick();

        Assert.Contains(FindControls<Button>(form),
            button => button.AccessibleName == "Remove activity process codex");
        Assert.Contains(FindControls<Label>(form),
            label => label.Text.Contains("codex — not currently running", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SharedOwnerWarningNamesSiblingWindowScope()
    {
        var target = Rule("Rider") with
        {
            Activation = TargetActivationPolicy.RequireActivityProcess,
            ActivityProcessNames = new() { "omp" },
        };
        var sharedInventory = new ProcessInventory(new[]
        {
            new ProcessInventoryGroup(new IntPtr(42), 7, "Project (PID 7)",
                new[]
                {
                    new ProcessInventoryEntry(7, 0, "owner", 0, "owner", false),
                    new ProcessInventoryEntry(8, 7, "omp", 1, "owner > omp", false),
                }, true),
        });
        using var model = new TargetManagementWorkspaceModel(new RecordingTargetRuleStore(target));
        using var form = NewForm(model, windows: new[] { Window() }, inventory: _ => sharedInventory);

        BeginEdit(form, target.Id);

        Assert.Contains(FindControls<Label>(form), label =>
            label.Text.Contains("Sibling-window scope warning", StringComparison.OrdinalIgnoreCase)
            && label.Text.Contains("Project (PID 7)", StringComparison.Ordinal));
    }

    [Fact]
    public void MigrationWarningAppearsUntilSuccessfulSave()
    {
        var target = Rule("Migrated") with
        {
            MigrationWarning = "Review discarded legacy title evidence.",
        };
        var store = new ClearingMigrationWarningStore(target);
        using var model = new TargetManagementWorkspaceModel(store);
        using var form = NewForm(model);
        BeginEdit(form, target.Id);

        Assert.Contains(FindControls<Label>(form), label =>
            label.Text.Contains("Review discarded legacy title evidence.", StringComparison.Ordinal));

        FindButton(form, "Save changes").PerformClick();

        Assert.Null(store.Targets.Single().MigrationWarning);
        var savedRow = Assert.IsType<TargetManagementRuleRow>(
            Assert.Single(FindControl<ListView>(form).Items.Cast<ListViewItem>()).Tag);
        Assert.Null(savedRow.Target.MigrationWarning);
        Assert.DoesNotContain(FindControls<Label>(form), label =>
            label.Text.Contains("Review discarded legacy title evidence.", StringComparison.Ordinal));
    }

    [Fact]
    public void ActivityProcessSearchFindsAndSelectsSavedRule()
    {
        var target = Rule("Terminal") with
        {
            Activation = TargetActivationPolicy.RequireActivityProcess,
            ActivityProcessNames = new() { "omp" },
        };
        using var model = new TargetManagementWorkspaceModel(new RecordingTargetRuleStore(target));
        using var form = NewForm(model);

        Assert.Contains("activity process", FindSearch(form).PlaceholderText,
            StringComparison.OrdinalIgnoreCase);
        FindSearch(form).Text = "omp.exe";
        SelectRule(form, target.Id);

        Assert.Equal(new[] { target.Id }, VisibleRuleIds(form));
        Assert.Equal(target.Id, form.SelectedRuleId);
    }

    [Fact]
    public void EditInventoryCombinesEveryMatchingLiveWindowAndExcludesNonmatches()
    {
        var target = Rule("Rider");
        var first = Window(10, "First");
        var second = Window(20, "Second");
        var nonmatch = Window(30, "Other") with { ProcessName = "browser" };
        var requested = new List<IntPtr>();
        using var model = new TargetManagementWorkspaceModel(new RecordingTargetRuleStore(target));
        using var form = NewForm(model, windows: new[] { first, second, nonmatch }, inventory: window =>
        {
            requested.Add(window.Hwnd);
            return Inventory(window.Hwnd.ToInt64(), window.Pid, window.Title, $"child-{window.Hwnd}");
        });

        BeginEdit(form, target.Id);

        var tree = FindControl<TreeView>(Assert.Single(FindControls<TargetEditorControl>(form)));
        Assert.Equal(new[] { new IntPtr(10), new IntPtr(20) }, requested.Order());
        Assert.Equal(2, tree.Nodes.Count);
        Assert.Equal("First (PID 7)", tree.Nodes[0].Text);
        Assert.Equal("Second (PID 7)", tree.Nodes[1].Text);
    }

    [Fact]
    public void FirstPopulatedEditorInventoryExpandsProcessDescendants()
    {
        var target = Rule("Rider");
        var window = Window();
        using var model = new TargetManagementWorkspaceModel(new RecordingTargetRuleStore(target));
        using var form = NewForm(model, windows: new[] { window }, inventory: current =>
            Inventory(current.Hwnd.ToInt64(), current.Pid, current.Title, "child"));

        BeginEdit(form, target.Id);

        var tree = FindControl<TreeView>(Assert.Single(FindControls<TargetEditorControl>(form)));
        var owner = Assert.Single(tree.Nodes.Cast<TreeNode>());
        Assert.NotEmpty(owner.Nodes.Cast<TreeNode>());
        Assert.True(owner.IsExpanded);
    }

    [Fact]
    public void RefreshFailurePreservesPriorInventoryAndShowsInlineWarning()
    {
        var target = Rule("Rider");
        var fail = false;
        var previews = new RecordingFormPreviewSet(new IntPtr(42));
        using var model = new TargetManagementWorkspaceModel(new RecordingTargetRuleStore(target));
        using var form = NewForm(model, previews, windows: new[] { Window() }, inventory: window =>
        {
            if (fail) throw new InvalidOperationException("inventory unavailable");
            return Inventory(window.Hwnd.ToInt64(), window.Pid, window.Title, "stable-child");
        });
        BeginEdit(form, target.Id);
        var tree = FindControl<TreeView>(Assert.Single(FindControls<TargetEditorControl>(form)));
        var priorNode = tree.Nodes[0];
        fail = true;

        ShowActivityEditor(form);
        FindButton(form, "↻").PerformClick();
        form.RefreshLiveState();

        Assert.Same(priorNode, tree.Nodes[0]);
        Assert.Equal(1, previews.RefreshCalls);
        Assert.Contains(FindControls<Label>(form),
            label => label.Text.Contains("inventory unavailable"));
    }

    [Theory]
    [InlineData("Cancel changes")]
    [InlineData("Save changes")]
    public void ReturningToBrowseClearsManualProcessRefreshWarning(string browseAction)
    {
        var target = Rule("Rider");
        var fail = false;
        using var model = new TargetManagementWorkspaceModel(new RecordingTargetRuleStore(target));
        using var form = NewForm(model, windows: new[] { Window() }, inventory: window =>
        {
            if (fail) throw new InvalidOperationException("inventory unavailable");
            return Inventory(window.Hwnd.ToInt64(), window.Pid, window.Title, "stable-child");
        });
        BeginEdit(form, target.Id);
        fail = true;

        ShowActivityEditor(form, selectAProcess: browseAction == "Save changes");
        FindButton(form, "↻").PerformClick();

        Assert.Contains(FindControls<Label>(form),
            label => label.Text.Contains("inventory unavailable"));

        FindButton(form, browseAction).PerformClick();

        Assert.Equal(TargetManagementMode.Browse, model.Mode);
        Assert.DoesNotContain(FindControls<Label>(form),
            label => label.Text.Contains("inventory unavailable"));
    }

    [Fact]
    public void PreviewUpdateFailureRemainsVisibleWhenInventoryBindingSucceeds()
    {
        var target = Rule("Rider");
        var previews = new RecordingFormPreviewSet { UpdateError = "preview unavailable" };
        using var model = new TargetManagementWorkspaceModel(new RecordingTargetRuleStore(target));
        using var form = NewForm(model, previews, windows: new[] { Window() },
            inventory: window => Inventory(window.Hwnd.ToInt64(), window.Pid, window.Title, "child"));

        BeginEdit(form, target.Id);

        Assert.Contains(FindControls<Label>(form),
            label => label.Text.Contains("preview unavailable"));
    }

    [Fact]
    public void RetainedPreviewFailurePublishesTheReleasedHandleToTheForm()
    {
        var target = Rule("Rider");
        var window = Window();
        var sessions = new RecordingPreviewSessionFactory();
        using var previews = new TargetPreviewSet(() => new[] { window }, sessions.Create);
        using var model = new TargetManagementWorkspaceModel(new RecordingTargetRuleStore(target));
        using var form = NewForm(model, previews, windows: new[] { window }, inventory: current =>
            Inventory(current.Hwnd.ToInt64(), current.Pid, current.Title, "child"));
        var handleChanges = 0;
        form.PreviewHandlesChanged += (_, _) => handleChanges++;
        BeginEdit(form, target.Id);
        sessions.For(window.Hwnd).FailNextUpdate = true;

        FindEditorName(form, target.Name).Text = "Changed";

        Assert.Empty(form.SuppressedHwnds);
        Assert.Equal(2, handleChanges);
        Assert.Contains(FindControls<Label>(form),
            label => label.Text.Contains("update failed"));
        Assert.Contains(FindControls<Label>(form),
            label => label.Text == "No matching windows are currently available for preview.");
    }

    [Fact]
    public void RetainedPreviewRefreshFailurePublishesZeroActiveStatusToTheForm()
    {
        var target = Rule("Rider");
        var window = Window();
        var sessions = new RecordingPreviewSessionFactory();
        using var previews = new TargetPreviewSet(() => new[] { window }, sessions.Create);
        using var model = new TargetManagementWorkspaceModel(new RecordingTargetRuleStore(target));
        using var form = NewForm(model, previews, windows: new[] { window }, inventory: current =>
            Inventory(current.Hwnd.ToInt64(), current.Pid, current.Title, "child"));
        BeginEdit(form, target.Id);
        sessions.For(window.Hwnd).FailNextRefresh = true;

        form.RefreshLiveState();

        Assert.Empty(form.SuppressedHwnds);
        Assert.Contains(FindControls<Label>(form),
            label => label.Text.Contains("refresh failed"));
        Assert.Contains(FindControls<Label>(form),
            label => label.Text == "No matching windows are currently available for preview.");
    }

    [Fact]
    public void AutomaticRefreshUpdatesPreviewWithoutEnumeratingProcesses()
    {
        var target = Rule("Rider");
        var inventoryCalls = 0;
        var previews = new RecordingFormPreviewSet(new IntPtr(42));
        using var model = new TargetManagementWorkspaceModel(new RecordingTargetRuleStore(target));
        using var form = NewForm(model, previews, windows: new[] { Window() }, inventory: window =>
        {
            inventoryCalls++;
            return Inventory(window.Hwnd.ToInt64(), window.Pid, window.Title, $"child-{inventoryCalls}");
        });
        BeginEdit(form, target.Id);
        Assert.Equal(1, inventoryCalls);

        form.RefreshLiveState();
        form.RefreshLiveState();

        Assert.Equal(1, inventoryCalls);
        Assert.Equal(2, previews.RefreshCalls);
    }

    [Fact]
    public void EditingDraftFieldsDoesNotReenumerateProcesses()
    {
        var target = Rule("Rider");
        var calls = 0;
        using var model = new TargetManagementWorkspaceModel(new RecordingTargetRuleStore(target));
        using var form = NewForm(model, windows: new[] { Window() }, inventory: window =>
        {
            calls++;
            return Inventory(window.Hwnd.ToInt64(), window.Pid, window.Title, "stable-child");
        });
        BeginEdit(form, target.Id);
        Assert.Equal(1, calls);

        FindEditorName(form, target.Name).Text = "Changed";
        Application.DoEvents();

        Assert.Equal(1, calls);
    }

    [Fact]
    public void WorkerThreadStoreChangeIsMarshaledBeforeRebuildingWinFormsControls()
    {
        var target = Rule("Rider");
        var store = new RecordingTargetRuleStore(target);
        using var model = new TargetManagementWorkspaceModel(store);
        using var form = NewForm(model);
        form.Show();
        Exception? backgroundError = null;
        using var completed = new ManualResetEventSlim();
        var priorCheck = Control.CheckForIllegalCrossThreadCalls;
        Control.CheckForIllegalCrossThreadCalls = true;
        try
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { store.SetEnabled(target.Id, false); }
                catch (Exception ex) { backgroundError = ex; }
                finally { completed.Set(); }
            });

            while (!completed.Wait(10)) Application.DoEvents();
            Application.DoEvents();

            Assert.Null(backgroundError);
            var item = FindControl<ListView>(form).Items.Cast<ListViewItem>()
                .Single(row => ((TargetManagementRuleRow)row.Tag!).Target.Id == target.Id);
            Assert.Equal("Disabled rules", item.Group?.Header);
        }
        finally
        {
            Control.CheckForIllegalCrossThreadCalls = priorCheck;
        }
    }

    private static TargetManagerForm NewForm(
        TargetManagementWorkspaceModel model,
        ITargetPreviewSet? previews = null,
        IReadOnlyList<WindowSnapshot>? windows = null,
        Func<WindowSnapshot, ProcessInventory>? inventory = null,
        Func<TargetDefinition, IWin32Window, bool>? confirmDelete = null) =>
        new(model, previews ?? new RecordingFormPreviewSet(),
            () => windows ?? Array.Empty<WindowSnapshot>(),
            inventory ?? (_ => ProcessInventory.Empty), confirmDelete);

    private static void BeginEdit(TargetManagerForm form, Guid id)
    {
        form.Show();
        form.RestoreBrowseSelection(id);
        FindButton(form, "Edit").PerformClick();
    }

    private static TextBox FindEditorName(TargetManagerForm form, string currentName) =>
        FindControls<TextBox>(Assert.Single(FindControls<TargetEditorControl>(form)))
            .Single(text => !text.ReadOnly && text.Text == currentName);

    private static void ShowActivityEditor(TargetManagerForm form, bool selectAProcess = false)
    {
        var editor = Assert.Single(FindControls<TargetEditorControl>(form));
        FindControls<RadioButton>(editor)
            .Single(button => button.Text == "When any selected process is running")
            .Checked = true;
        if (!selectAProcess) return;
        FindControl<TreeView>(editor).Nodes[0].Checked = true;
    }

    private static void SelectRule(TargetManagerForm form, Guid id)
    {
        var rules = FindControl<ListView>(form);
        foreach (var item in rules.Items.Cast<ListViewItem>())
            item.Selected = ((TargetManagementRuleRow)item.Tag!).Target.Id == id;
        Application.DoEvents();
    }

    private static IReadOnlyList<Guid> VisibleRuleIds(TargetManagerForm form) =>
        FindControl<ListView>(form).Items.Cast<ListViewItem>()
            .Select(item => ((TargetManagementRuleRow)item.Tag!).Target.Id)
            .ToList();

    private static ProcessInventory Inventory(long hwnd, uint ownerPid, string title, string child) =>
        new(new[]
        {
            new ProcessInventoryGroup(new IntPtr(hwnd), ownerPid, $"{title} (PID {ownerPid})",
                new[]
                {
                    new ProcessInventoryEntry(ownerPid, 0, "owner", 0, "owner", false),
                    new ProcessInventoryEntry(ownerPid + 1, ownerPid, child, 1, child, false),
                }, false),
        });

    private static WindowSnapshot Window(long hwnd = 42, string title = "Project") =>
        new(new IntPtr(hwnd), 7, "app", "frame", title, true);

    private static TargetDefinition Rule(string name) =>
        TargetDefinition.Captured(name, "app", "frame", new[] { ProviderBinding.Claude },
            TargetPlacementKind.Desktop, TargetActivationPolicy.AlwaysWhileOpen, true, 0);

    private static TextBox FindSearch(Control root) =>
        FindControls<TextBox>(root).Single(text => !string.IsNullOrWhiteSpace(text.PlaceholderText));

    private static Button FindButton(Control root, string text) =>
        FindControls<Button>(root).Single(button => button.Text == text);

    private static T FindControl<T>(Control root) where T : Control =>
        FindControls<T>(root).Single();

    private static IEnumerable<T> FindControls<T>(Control root) where T : Control =>
        root.Controls.Cast<Control>()
            .SelectMany(control => Descendants(control).Append(control))
            .OfType<T>();

    private static IEnumerable<Control> Descendants(Control control) =>
        control.Controls.Cast<Control>()
            .SelectMany(child => Descendants(child).Append(child));

    private static IEnumerable<TreeNode> AllNodes(TreeNodeCollection nodes) =>
        nodes.Cast<TreeNode>().SelectMany(node => AllNodes(node.Nodes).Append(node));
}

internal sealed class RecordingFormPreviewSet : ITargetPreviewSet
{
    private readonly HashSet<IntPtr> _suppressed = new();

    public RecordingFormPreviewSet(params IntPtr[] suppressedOnUpdate) =>
        SuppressedOnUpdate = suppressedOnUpdate;

    public IReadOnlySet<IntPtr> SuppressedHwnds => _suppressed;
    public TargetPreviewSetStatus Status { get; set; } = new(0, null, "No preview is active.");
    public TargetDefinition? LatestDraft { get; private set; }
    public TargetManagementMode? LatestMode { get; private set; }
    public WindowSnapshot? LatestCapturedWindow { get; private set; }
    public IReadOnlyList<IntPtr> SuppressedOnUpdate { get; set; }
    public int UpdateCalls { get; private set; }
    public int RefreshCalls { get; private set; }
    public int ClearCalls { get; private set; }
    public string? UpdateError { get; set; }
    public string? RefreshError { get; set; }
    public TargetPreviewSetStatus? StatusAfterUpdate { get; set; }

    public void Update(TargetDefinition draft, TargetManagementMode mode, WindowSnapshot? capturedWindow)
    {
        if (UpdateError is not null) throw new InvalidOperationException(UpdateError);
        UpdateCalls++;
        LatestDraft = draft;
        LatestMode = mode;
        LatestCapturedWindow = capturedWindow;
        _suppressed.Clear();
        _suppressed.UnionWith(SuppressedOnUpdate);
        Status = StatusAfterUpdate ?? (_suppressed.Count == 0
            ? new(0, null, "No preview is active.")
            : new(_suppressed.Count, TargetPreviewState.Normal,
                $"Previewing {_suppressed.Count} window normally"));
    }

    public void Refresh()
    {
        RefreshCalls++;
        if (RefreshError is not null) throw new InvalidOperationException(RefreshError);
    }

    public void Clear()
    {
        ClearCalls++;
        _suppressed.Clear();
        Status = new(0, null, "No preview is active.");
    }

    public void Dispose() => Clear();
}

internal sealed class FailingFormStore : ITargetRuleStore
{
    public FailingFormStore(params TargetDefinition[] targets) => Targets = targets;

    public IReadOnlyList<TargetDefinition> Targets { get; }
    public event Action? Changed { add { } remove { } }
    public TargetMutationResult Add(TargetDefinition target) => new(false, "write failed");
    public TargetMutationResult Update(TargetDefinition target) => new(false, "write failed");
    public TargetMutationResult SetEnabled(Guid id, bool enabled) => new(false, "write failed");
    public TargetMutationResult Delete(Guid id) => new(false, "write failed");
}

internal sealed class ClearingMigrationWarningStore : ITargetRuleStore
{
    private TargetDefinition _target;

    public ClearingMigrationWarningStore(TargetDefinition target) => _target = target;

    public IReadOnlyList<TargetDefinition> Targets => new[] { _target };
    public event Action? Changed;
    public TargetMutationResult Add(TargetDefinition target) => new(false, "not supported");

    public TargetMutationResult Update(TargetDefinition target)
    {
        _target = target with { MigrationWarning = null };
        Changed?.Invoke();
        return new(true);
    }

    public TargetMutationResult SetEnabled(Guid id, bool enabled) => new(false, "not supported");
    public TargetMutationResult Delete(Guid id) => new(false, "not supported");
}
