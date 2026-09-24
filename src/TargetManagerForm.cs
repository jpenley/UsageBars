using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace UsageBars;

internal sealed record TargetManagementSidebarProjection(
    IReadOnlyList<TargetManagementRuleRow> Enabled,
    IReadOnlyList<TargetManagementRuleRow> Disabled,
    IReadOnlyList<TargetManagementRuleRow> Applies,
    IReadOnlyList<TargetManagementRuleRow> Overlaps,
    IReadOnlyList<TargetManagementRuleRow> OtherSearchResults)
{
    public static TargetManagementSidebarProjection Build(
        IReadOnlyList<TargetManagementRuleRow> rows) => new(
        rows.Where(row => row.Group == TargetManagementRuleGroup.Enabled).ToList(),
        rows.Where(row => row.Group == TargetManagementRuleGroup.Disabled).ToList(),
        rows.Where(row => row.Group == TargetManagementRuleGroup.Applies).ToList(),
        rows.Where(row => row.Group == TargetManagementRuleGroup.Overlaps).ToList(),
        rows.Where(row => row.Group == TargetManagementRuleGroup.OtherSearchResults).ToList());
}

internal sealed record TargetManagementRuleItem(
    string PrimaryText,
    string SecondaryText,
    string ToolTip)
{
    public static TargetManagementRuleItem From(
        TargetManagementRuleRow row,
        IReadOnlyList<TargetDefinition> savedTargets)
    {
        var target = row.Target;
        var providers = string.Join(" + ", target.Providers
            .Select(binding => binding.Provider).Distinct().Order());
        var state = target.Enabled ? "enabled" : "disabled";
        var relationship = row.Impact?.Relationship switch
        {
            TargetRuleRelationship.ExactIdentity => "Exact identity",
            TargetRuleRelationship.AppliesNow => "Applies now",
            TargetRuleRelationship.OverlapsDraft => "Overlaps draft",
            TargetRuleRelationship.SearchOnly => "Search result",
            _ => target.Enabled ? "Enabled rule" : "Disabled rule",
        };
        var title = target.WindowTitle is null
            ? "(any)"
            : $"{target.WindowTitle.Kind}:{target.WindowTitle.Value}";
        var overlapWarning = target.Enabled
            && savedTargets.Any(other => other.Enabled && target.Overlaps(other))
                ? " · overlaps another enabled rule"
                : "";
        var tooltip = string.Join(Environment.NewLine, new[]
        {
            $"ID: {target.Id:D}",
            $"Process: {target.ProcessName ?? "(any)"}",
            $"Class: {target.WindowClass ?? "(any)"}",
            $"Title: {title}",
            $"Activity: {TargetRuleText.FormatActivity(target).Replace("|", " OR ")}",
            string.IsNullOrWhiteSpace(target.MigrationWarning)
                ? null
                : $"Migration warning: {target.MigrationWarning}",
            row.Impact?.ImpactText,
        }.Where(line => line is not null)!);
        return new(target.Name,
            $"{target.Id.ToString("N")[..8]} · {providers} · {state} · {relationship}"
            + $" · activity={TargetRuleText.FormatActivity(target)}{overlapWarning}",
            tooltip);
    }
}

internal sealed class TargetManagerForm : Form, IModelessTargetManager
{
    private readonly TargetManagementWorkspaceModel _model;
    private readonly ITargetPreviewSet _previews;
    private readonly Func<IReadOnlyList<WindowSnapshot>> _liveWindows;
    private readonly Func<
        IReadOnlyList<WindowSnapshot>,
        IReadOnlyList<WindowSnapshot>,
        ProcessInventory> _inventoryForWindows;
    private readonly Func<TargetDefinition, IWin32Window, bool> _confirmDelete;
    private readonly SplitContainer _split = new()
    {
        Dock = DockStyle.Fill,
        FixedPanel = FixedPanel.Panel1,
    };
    private readonly TextBox _search = new()
    {
        PlaceholderText = "Find by name, ID, process, activity process, class, title, or provider…",
    };
    private readonly ListView _rules = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        ShowItemToolTips = true,
        HideSelection = false,
    };
    private readonly TextBox _details = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        BorderStyle = BorderStyle.FixedSingle,
    };
    private readonly Button _add = new() { Text = "Add", AutoSize = true };
    private readonly Button _edit = new() { Text = "Edit", AutoSize = true };
    private readonly Button _createMoreSpecific = new()
    {
        Text = "Create more-specific rule",
        AutoSize = true,
    };
    private readonly Button _toggle = new() { Text = "Enable/Disable", AutoSize = true };
    private readonly Button _delete = new() { Text = "Delete…", AutoSize = true };
    private readonly Button _save = new() { Text = "Save changes", AutoSize = true };
    private readonly Button _cancel = new() { Text = "Cancel changes", AutoSize = true };
    private readonly Label _previewStatus = new()
    {
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(8),
        BackColor = Color.FromArgb(235, 243, 252),
        Text = "No preview is active.",
    };
    private readonly Label _error = new() { AutoSize = true, ForeColor = Color.Firebrick };
    private readonly Label _refreshWarning = new()
    {
        AutoSize = true,
        ForeColor = Color.DarkGoldenrod,
    };
    private readonly Panel _editorHost = new() { Dock = DockStyle.Fill };
    private readonly Label _browseMessage = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = SystemColors.GrayText,
        Text = "Select a saved rule and choose Edit, or choose Add to capture a window.",
    };
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 2000 };
    private readonly int _uiThreadId = Environment.CurrentManagedThreadId;
    private TargetEditorControl? _editor;
    private ProcessInventory _capturedInventory = ProcessInventory.Empty;
    private ProcessInventory _activeInventory = ProcessInventory.Empty;
    private TargetEditorModel? _inventoryEditorModel;
    private string? _processRefreshWarning;
    private HashSet<IntPtr> _lastSuppressedHwnds;
    private int _pendingModelRebuild;
    private bool _applyingEditorDraft;
    private bool _workspaceCleaned;
    private bool _disposed;

    public TargetManagerForm(
        TargetManagementWorkspaceModel model,
        ITargetPreviewSet previews,
        Func<IReadOnlyList<WindowSnapshot>> liveWindows,
        Func<WindowSnapshot, ProcessInventory> inventoryForWindow,
        Func<TargetDefinition, IWin32Window, bool>? confirmDelete = null)
        : this(
            model,
            previews,
            liveWindows,
            (selectedWindows, _) => ProcessInventory.Combine(
                selectedWindows.Select(inventoryForWindow)),
            confirmDelete)
    {
    }

    public TargetManagerForm(
        TargetManagementWorkspaceModel model,
        ITargetPreviewSet previews,
        Func<IReadOnlyList<WindowSnapshot>> liveWindows,
        Func<IReadOnlyList<WindowSnapshot>, IReadOnlyList<WindowSnapshot>, ProcessInventory>
            inventoryForWindows,
        Func<TargetDefinition, IWin32Window, bool>? confirmDelete = null)
    {
        _model = model;
        _previews = previews;
        _liveWindows = liveWindows;
        _inventoryForWindows = inventoryForWindows;
        _confirmDelete = confirmDelete ?? ConfirmDelete;
        _lastSuppressedHwnds = previews.SuppressedHwnds.ToHashSet();

        Text = "Manage targets";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        ClientSize = new Size(1120, 760);
        MinimumSize = new Size(1040, 680);

        BuildRulePane();
        BuildEditorPane();
        Controls.Add(_split);
        _split.Size = ClientSize;
        _split.Panel1MinSize = 320;
        _split.Panel2MinSize = 620;
        _split.SplitterDistance = 360;

        _search.TextChanged += OnSearchChanged;
        _rules.SelectedIndexChanged += OnRuleSelectionChanged;
        _rules.DoubleClick += (_, _) => BeginEditSelected();
        _add.Click += (_, _) => AddRequested?.Invoke(this, EventArgs.Empty);
        _edit.Click += (_, _) => BeginEditSelected();
        _createMoreSpecific.Click += (_, _) => CreateMoreSpecific();
        _toggle.Click += (_, _) => ToggleSelected();
        _delete.Click += (_, _) => DeleteSelected();
        _save.Click += (_, _) => SaveDraft();
        _cancel.Click += (_, _) => CancelDraft();
        _model.Changed += OnModelChanged;
        _refreshTimer.Tick += (_, _) => RefreshLiveState();
        Shown += (_, _) => _refreshTimer.Start();

        BuildFromModel();
    }

    public event EventHandler? AddRequested;
    public event EventHandler? PreviewHandlesChanged;

    public Guid? SelectedRuleId => SelectedRow()?.Target.Id;
    public IReadOnlySet<IntPtr> SuppressedHwnds => _previews.SuppressedHwnds;

    public void RestoreBrowseSelection(Guid? selectedRuleId)
    {
        SelectVisibleRule(selectedRuleId);
        UpdateSelectionDetailsAndButtons();
    }

    public void BeginCaptured(TargetCaptureContext context)
    {
        _capturedInventory = context.Inventory;
        _model.BeginCaptured(context.Window, context.Draft);
        SelectVisibleRule(_model.SelectedSavedRuleId);
        UpdateSelectionDetailsAndButtons();
    }

    public void RefreshLiveState()
    {
        if (_disposed || _workspaceCleaned) return;
        UpdateRefreshWarning();
        try
        {
            _previews.Refresh();
        }
        catch (Exception ex)
        {
            AddRefreshWarning($"Preview could not be refreshed: {ex.Message}");
        }
        finally
        {
            SetPreviewStatus(_previews.Status);
            RaisePreviewHandlesChangedIfNeeded();
        }
    }

    private void BuildRulePane()
    {
        _rules.Columns.Add("Rule", 160);
        _rules.Columns.Add("Match", 330);
        _search.Dock = DockStyle.Fill;

        var searchRow = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Margin = Padding.Empty,
        };
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        searchRow.Controls.Add(_search, 0, 0);
        searchRow.Controls.Add(_add, 1, 0);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            WrapContents = true,
            Padding = new Padding(0, 4, 0, 0),
        };
        actions.Controls.AddRange(new Control[]
        {
            _edit, _createMoreSpecific, _toggle, _delete,
        });

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 4,
            Padding = new Padding(8),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 65));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(searchRow, 0, 0);
        layout.Controls.Add(_rules, 0, 1);
        layout.Controls.Add(_details, 0, 2);
        layout.Controls.Add(actions, 0, 3);
        _split.Panel1.Controls.Add(layout);
    }

    private void BuildEditorPane()
    {
        var actions = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(8),
            Margin = Padding.Empty,
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        actions.Controls.Add(_cancel, 0, 0);
        actions.Controls.Add(_save, 1, 0);

        var messages = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
        };
        messages.Controls.Add(_error);
        messages.Controls.Add(_refreshWarning);

        var footer = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 2,
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(messages, 0, 0);
        footer.Controls.Add(actions, 1, 0);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_previewStatus, 0, 0);
        layout.Controls.Add(_editorHost, 0, 1);
        layout.Controls.Add(footer, 0, 2);
        _split.Panel2.Controls.Add(layout);

        AcceptButton = _save;
        CancelButton = _cancel;
    }

    private void OnSearchChanged(object? sender, EventArgs e) => _model.SetSearch(_search.Text);

    private void OnRuleSelectionChanged(object? sender, EventArgs e)
    {
        UpdateSelectionDetailsAndButtons();
    }

    private void OnModelChanged()
    {
        if (_disposed || IsDisposed || Disposing)
            return;

        if (Environment.CurrentManagedThreadId == _uiThreadId)
        {
            BuildFromModel();
            return;
        }

        if (Interlocked.Exchange(ref _pendingModelRebuild, 1) == 1)
            return;

        TargetManagerReloadScheduler.TryQueue(
            IsDisposed,
            Disposing,
            IsHandleCreated,
            action => BeginInvoke(action),
            RunPendingModelRebuild);
    }

    private void RunPendingModelRebuild()
    {
        if (Interlocked.Exchange(ref _pendingModelRebuild, 0) == 0
            || _disposed || IsDisposed || Disposing)
            return;
        BuildFromModel();
    }

    private void BuildFromModel()
    {
        UpdateRefreshWarning();
        BindEditor();
        ReloadRules();
        UpdateSelectionDetailsAndButtons();
        RefreshEditorInventoryForModelChange();
        ReconcilePreview();
    }

    private void BindEditor()
    {
        if (_model.Editor is null)
        {
            if (_editor is not null)
            {
                _editor.DraftChanged -= OnEditorDraftChanged;
                _editor.InventoryRefreshRequested -= OnInventoryRefreshRequested;
                _editor.Dispose();
                _editor = null;
            }
            _editorHost.Controls.Clear();
            _editorHost.Controls.Add(_browseMessage);
            _inventoryEditorModel = null;
            _activeInventory = ProcessInventory.Empty;
            _processRefreshWarning = null;
            UpdateRefreshWarning();
            return;
        }

        if (_editor is null)
        {
            _editorHost.Controls.Clear();
            _editor = new TargetEditorControl(_model.Editor, ProcessInventory.Empty)
            {
                Dock = DockStyle.Fill,
            };
            _editor.DraftChanged += OnEditorDraftChanged;
            _editor.InventoryRefreshRequested += OnInventoryRefreshRequested;
            _editorHost.Controls.Add(_editor);
        }
        else if (!ReferenceEquals(_editor.Model, _model.Editor))
        {
            _editor.LoadModel(_model.Editor);
        }
        else if (!_applyingEditorDraft)
        {
            _editor.LoadModel(_model.Editor);
        }
    }

    private void OnEditorDraftChanged(TargetDefinition draft)
    {
        _applyingEditorDraft = true;
        try { _model.ApplyDraft(draft); }
        finally { _applyingEditorDraft = false; }
    }

    private void ReloadRules()
    {
        var selectedId = SelectedRuleId;
        var projection = TargetManagementSidebarProjection.Build(_model.Rows);
        _rules.BeginUpdate();
        try
        {
            _rules.Items.Clear();
            _rules.Groups.Clear();
            AddGroup("enabled", "Enabled rules", projection.Enabled, selectedId);
            AddGroup("disabled", "Disabled rules", projection.Disabled, selectedId);
            AddGroup("applies", "Applies to this window", projection.Applies, selectedId);
            AddGroup("overlaps", "Overlaps proposed rule", projection.Overlaps, selectedId);
            AddGroup("other", "Other search results", projection.OtherSearchResults, selectedId);
        }
        finally { _rules.EndUpdate(); }
    }

    private void AddGroup(
        string name,
        string header,
        IReadOnlyList<TargetManagementRuleRow> rows,
        Guid? selectedId)
    {
        if (rows.Count == 0) return;
        var group = new ListViewGroup(name, header);
        _rules.Groups.Add(group);
        foreach (var row in rows)
        {
            var display = TargetManagementRuleItem.From(row, _model.SavedTargets);
            var item = new ListViewItem(display.PrimaryText, group)
            {
                Tag = row,
                ToolTipText = display.ToolTip,
            };
            item.SubItems.Add(display.SecondaryText);
            _rules.Items.Add(item);
            if (row.Target.Id == selectedId) item.Selected = true;
        }
    }

    private void SelectVisibleRule(Guid? id)
    {
        foreach (var item in _rules.Items.Cast<ListViewItem>())
            item.Selected = id is not null
                && ((TargetManagementRuleRow)item.Tag!).Target.Id == id;
    }

    private TargetManagementRuleRow? SelectedRow()
    {
        return _rules.Items.Cast<ListViewItem>()
            .Where(item => item.Selected)
            .Select(item => item.Tag as TargetManagementRuleRow)
            .FirstOrDefault(row => row is not null);
    }

    private void UpdateSelectionDetailsAndButtons()
    {
        var row = SelectedRow();
        _edit.Enabled = row is not null && _model.Mode == TargetManagementMode.Browse;
        _toggle.Enabled = row is not null;
        _delete.Enabled = row is not null;
        _createMoreSpecific.Enabled = _model.Mode == TargetManagementMode.AddCaptured
            && _model.Editor is not null;
        _add.Enabled = _model.Mode == TargetManagementMode.Browse;
        _save.Enabled = _model.Editor is not null;
        _cancel.Enabled = _model.Editor is not null;
        _toggle.Text = row?.Target.Enabled == true ? "Disable" : "Enable";

        _details.Text = row is null
            ? "Select a target to see its full ID and matching details."
            : $"ID: {row.Target.Id:D}{Environment.NewLine}"
                + TargetDetailsFormatter.Format(row.Target, _model.SavedTargets);
    }

    private void BeginEditSelected()
    {
        if (_model.Mode != TargetManagementMode.Browse || SelectedRow() is not { } row) return;
        _model.BeginEdit(row.Target.Id);
    }

    private void CreateMoreSpecific()
    {
        if (_model.Mode != TargetManagementMode.AddCaptured || _model.Editor is null) return;
        _model.CreateMoreSpecific();
    }

    private void ToggleSelected()
    {
        if (SelectedRow() is not { } row) return;
        _error.Text = "";
        var result = _model.SetEnabled(row.Target.Id, !row.Target.Enabled);
        if (!result.Success)
        {
            _error.Text = result.Error ?? "Could not update the target.";
            return;
        }

        UpdateSelectionDetailsAndButtons();
    }

    private void DeleteSelected()
    {
        if (SelectedRow() is not { } row || !_confirmDelete(row.Target, this)) return;
        _error.Text = "";
        var deletedId = row.Target.Id;
        var result = _model.Delete(deletedId);
        if (!result.Success)
        {
            _error.Text = result.Error ?? "Could not delete the target.";
            return;
        }

        UpdateSelectionDetailsAndButtons();
    }

    private void SaveDraft()
    {
        if (_editor is null) return;
        var errors = _editor.ValidateDefinition();
        if (errors.Count > 0)
        {
            _editor.ShowValidation(string.Join(" ", errors));
            return;
        }

        _error.Text = "";
        _applyingEditorDraft = true;
        try { _model.ApplyDraft(_editor.CurrentDefinition); }
        finally { _applyingEditorDraft = false; }

        var result = _model.SaveDraft();
        if (!result.Success)
            _error.Text = result.Error ?? "Could not save the target.";
        else
        {
            SelectVisibleRule(_model.SelectedSavedRuleId);
            UpdateSelectionDetailsAndButtons();
        }
    }

    private void CancelDraft()
    {
        _error.Text = "";
        _model.CancelDraft();
    }

    private void ReconcilePreview()
    {
        try
        {
            if (_model.CurrentDefinition is { } draft)
            {
                _previews.Update(draft, _model.Mode, _model.CapturedWindow);
            }
            else
            {
                _previews.Clear();
            }
        }
        catch (Exception ex)
        {
            AddRefreshWarning($"Preview could not be refreshed: {ex.Message}");
        }
        finally
        {
            SetPreviewStatus(_previews.Status);
            RaisePreviewHandlesChangedIfNeeded();
        }
    }

    private void RefreshEditorInventoryForModelChange()
    {
        if (_editor is null || _model.Editor is null) return;
        if (ReferenceEquals(_inventoryEditorModel, _model.Editor))
        {
            _editor.SetInventory(_activeInventory);
            return;
        }

        _inventoryEditorModel = _model.Editor;
        _activeInventory = ProcessInventory.Empty;
        _processRefreshWarning = null;
        _editor.SetInventory(_activeInventory);
        try
        {
            _activeInventory = _model.Mode == TargetManagementMode.AddCaptured
                ? _capturedInventory
                : BuildInventoryForCurrentMode();
            _editor.SetInventory(_activeInventory);
        }
        catch (Exception ex)
        {
            _processRefreshWarning = $"Process list could not be refreshed: {ex.Message}";
        }
        UpdateRefreshWarning();
    }

    private void OnInventoryRefreshRequested(object? sender, EventArgs e)
    {
        if (_editor is null || _model.Editor is null) return;
        try
        {
            var inventory = BuildInventoryForCurrentMode();
            _activeInventory = inventory;
            _processRefreshWarning = null;
            _editor.SetInventory(inventory);
        }
        catch (Exception ex)
        {
            _processRefreshWarning = $"Process list could not be refreshed: {ex.Message}";
        }
        UpdateRefreshWarning();
    }

    private ProcessInventory BuildInventoryForCurrentMode()
    {
        if (_model.CurrentDefinition is not { } draft)
            return ProcessInventory.Empty;

        var liveWindows = _liveWindows()
            .Where(window => window.Hwnd != IntPtr.Zero)
            .GroupBy(window => window.Hwnd)
            .Select(group => group.Last())
            .ToList();

        if (_model.Mode == TargetManagementMode.EditExisting)
        {
            var matchingWindows = liveWindows.Where(draft.Matches).ToList();
            return matchingWindows.Count == 0
                ? ProcessInventory.Empty
                : _inventoryForWindows(matchingWindows, liveWindows);
        }

        if (_model.Mode == TargetManagementMode.AddCaptured
            && _model.CapturedWindow is { } captured)
        {
            var latest = liveWindows.LastOrDefault(window => window.Hwnd == captured.Hwnd)
                ?? captured;
            return _inventoryForWindows(new[] { latest }, liveWindows);
        }

        return ProcessInventory.Empty;
    }

    private void SetPreviewStatus(TargetPreviewSetStatus status) =>
        _previewStatus.Text = status.Message;

    private void UpdateRefreshWarning()
    {
        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(_processRefreshWarning))
            warnings.Add(_processRefreshWarning);
        if (_model.CurrentDefinition?.MigrationWarning is { } migrationWarning
            && !string.IsNullOrWhiteSpace(migrationWarning))
        {
            warnings.Add($"Migration warning: {migrationWarning}");
        }

        var sharedScopes = (_model.CurrentDefinition?.Activation
                == TargetActivationPolicy.RequireActivityProcess
                ? _activeInventory.Groups
                : Array.Empty<ProcessInventoryGroup>())
            .Where(group => group.MayIncludeSiblingWindows)
            .Select(group => group.Label)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(label => label, StringComparer.Ordinal)
            .ToList();
        if (sharedScopes.Count > 0)
        {
            warnings.Add(
                $"Sibling-window scope warning: {string.Join(", ", sharedScopes)}. "
                + "Selected activity processes may activate every otherwise-matching window under this shared owner.");
        }

        _refreshWarning.Text = string.Join(Environment.NewLine, warnings);
    }

    private void AddRefreshWarning(string message) =>
        _refreshWarning.Text = string.IsNullOrWhiteSpace(_refreshWarning.Text)
            ? message
            : $"{_refreshWarning.Text}{Environment.NewLine}{message}";

    private void RaisePreviewHandlesChangedIfNeeded()
    {
        var current = _previews.SuppressedHwnds.ToHashSet();
        if (_lastSuppressedHwnds.SetEquals(current)) return;
        _lastSuppressedHwnds = current;
        PreviewHandlesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CleanupWorkspace()
    {
        if (_workspaceCleaned) return;
        _workspaceCleaned = true;
        _refreshTimer.Stop();
        if (_model.Editor is not null) _model.CancelDraft();
        _previews.Clear();
        SetPreviewStatus(_previews.Status);
        RaisePreviewHandlesChangedIfNeeded();
    }

    private static bool ConfirmDelete(TargetDefinition target, IWin32Window owner) =>
        MessageBox.Show(owner, $"Delete target “{target.Name}”?", "UsageBars",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        CleanupWorkspace();
        base.OnFormClosed(e);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RunPendingModelRebuild();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            CleanupWorkspace();
            _disposed = true;
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
            _model.Changed -= OnModelChanged;
            if (_editor is not null) _editor.DraftChanged -= OnEditorDraftChanged;
            _previews.Dispose();
            _model.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal static class TargetManagerReloadScheduler
{
    public static bool TryQueue(
        bool isDisposed,
        bool isDisposing,
        bool isHandleCreated,
        Action<Action> post,
        Action reload)
    {
        if (isDisposed || isDisposing || !isHandleCreated) return false;

        try
        {
            post(reload);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
