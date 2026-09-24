# Manual Process Refresh and Stable Editor Footer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep live pill previews automatic while loading process inventory only once per target draft and on an explicit refresh click, and keep Cancel/Save side by side at the manager's minimum size.

**Architecture:** `TargetEditorControl` remains a passive inventory renderer and exposes a user-request event from a compact refresh button. `TargetManagerForm` owns inventory enumeration, caches the inventory against the active `TargetEditorModel`, and separates persistent manual-process warnings from transient preview warnings. The footer switches from a wrapping flow container to a fixed two-column table.

**Tech Stack:** .NET 8, C#, Windows Forms, xUnit, `dotnet test`, `dotnet publish`

## Global Constraints

- Automatic live refresh continues to reconcile preview sessions, update preview status/warnings, and publish preview-handle changes.
- Automatic live refresh must not enumerate processes, replace provider-process choices, or rebuild the process tree.
- Add mode initially uses the inventory captured with the selected window; Edit mode initially combines inventory for all matching eligible windows.
- A compact `↻` button must expose the accessible name and tooltip `Refresh processes`.
- Manual refresh retains the previous inventory and shows `Process list could not be refreshed: ...` when enumeration fails.
- Manual refresh must continue using `TargetEditorControl.SetInventory(ProcessInventory)` so viewport selection, expansion, top node, and focus are preserved.
- Cancel changes must remain left of Save changes, on the same row, and inside the editor pane at `TargetManagerForm.MinimumSize`.
- Do not change the two-second preview interval, process ownership/tab attribution, inference, persistence, or placement behavior.

---

## File Structure

- Modify `src/TargetEditorControl.cs`: add the refresh button, tooltip, and `InventoryRefreshRequested` event; keep all enumeration outside this control.
- Modify `src/TargetManagerForm.cs`: cache inventory per active editor model, handle explicit refresh, keep automatic refresh preview-only, retain process errors, and replace the footer button container.
- Modify `tests/UsageBars.Tests/TargetEditorLayoutTests.cs`: verify the button's semantics and event without reaching into private methods.
- Modify `tests/UsageBars.Tests/TargetManagerFormTests.cs`: verify initial/manual enumeration behavior, failure retention, preview independence, and footer geometry.

### Task 1: Add the explicit process-refresh control contract

**Files:**
- Modify: `src/TargetEditorControl.cs:67-272`
- Test: `tests/UsageBars.Tests/TargetEditorLayoutTests.cs:9-100`

**Interfaces:**
- Consumes: existing `TargetEditorControl.SetInventory(ProcessInventory inventory)` for rendering refreshed data.
- Produces: `public event EventHandler? InventoryRefreshRequested`, raised once for each click of the button whose `Text` is `↻` and `AccessibleName`/tooltip are `Refresh processes`.

- [ ] **Step 1: Write the failing refresh-button contract test**

Add this test to `TargetEditorLayoutTests`:

```csharp
[Fact]
public void RefreshProcessesButtonIsAccessibleAndRaisesOneRequestPerClick()
{
    using var control = new TargetEditorControl(
        TargetEditorModel.FromExisting(TwoProviderTarget()), ProcessInventory.Empty);
    var requests = 0;
    control.InventoryRefreshRequested += (_, _) => requests++;

    var refresh = FindControls<Button>(control)
        .Single(button => button.AccessibleName == "Refresh processes");
    refresh.PerformClick();

    Assert.Equal("↻", refresh.Text);
    Assert.Equal(1, requests);
}
```

Reuse the existing recursive `FindControls<T>` helper in the same test file.

- [ ] **Step 2: Run the focused test and verify the contract is missing**

Run:

```powershell
dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter "FullyQualifiedName~TargetEditorLayoutTests.RefreshProcessesButtonIsAccessibleAndRaisesOneRequestPerClick"
```

Expected: compilation fails because `TargetEditorControl.InventoryRefreshRequested` does not exist.

- [ ] **Step 3: Add the button, tooltip, layout, event, and disposal**

In `TargetEditorControl`, add fields and the public event:

```csharp
private readonly Button _refreshProcesses = new()
{
    Text = "↻",
    AutoSize = true,
    AccessibleName = "Refresh processes",
    Margin = Padding.Empty,
};
private readonly ToolTip _toolTip = new();

public event EventHandler? InventoryRefreshRequested;
```

Replace the first row of `BuildProcessPanel()` with a two-column header. The scope label consumes remaining width and the refresh button keeps its preferred width:

```csharp
var header = new TableLayoutPanel
{
    AutoSize = true,
    Dock = DockStyle.Fill,
    ColumnCount = 2,
    Margin = Padding.Empty,
};
header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
header.Controls.Add(_processScope, 0, 0);
header.Controls.Add(_refreshProcesses, 1, 0);

var panel = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 1 };
panel.Controls.Add(header, 0, 0);
panel.Controls.Add(_processWarning, 0, 1);
panel.Controls.Add(_showHelpers, 0, 2);
panel.Controls.Add(_processTree, 0, 3);
```

In the constructor, set the tooltip after controls are built:

```csharp
_toolTip.SetToolTip(_refreshProcesses, "Refresh processes");
```

In `WireChanges()`, forward clicks without enumerating:

```csharp
_refreshProcesses.Click += (_, _) => InventoryRefreshRequested?.Invoke(this, EventArgs.Empty);
```

Dispose the non-control tooltip deterministically:

```csharp
protected override void Dispose(bool disposing)
{
    if (disposing) _toolTip.Dispose();
    base.Dispose(disposing);
}
```

- [ ] **Step 4: Run the editor layout suite**

Run:

```powershell
dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter "FullyQualifiedName~TargetEditorLayoutTests"
```

Expected: all `TargetEditorLayoutTests` pass, including the new event test and existing viewport-preservation tests.

- [ ] **Step 5: Commit the editor contract**

```powershell
git add src/TargetEditorControl.cs tests/UsageBars.Tests/TargetEditorLayoutTests.cs
git commit -m "feat: add manual process refresh control"
```

### Task 2: Make process inventory one-shot and user-refreshed

**Files:**
- Modify: `src/TargetManagerForm.cs:134-238,370-420,593-644`
- Test: `tests/UsageBars.Tests/TargetManagerFormTests.cs:392-563`

**Interfaces:**
- Consumes: `TargetEditorControl.InventoryRefreshRequested` and `TargetEditorControl.SetInventory(ProcessInventory inventory)` from Task 1.
- Produces: cached `ProcessInventory _activeInventory`, active-draft identity `TargetEditorModel? _inventoryEditorModel`, `OnInventoryRefreshRequested(object? sender, EventArgs e)`, and a preview-only `RefreshLiveState()`.

- [ ] **Step 1: Replace the periodic-refresh expectations with failing one-shot/manual-refresh tests**

Update `CapturedModeUsesTheCapturedOwnerInventory` so automatic refresh makes no inventory request, then click the explicit button and verify one current-window request:

```csharp
form.RefreshLiveState();
Assert.Empty(requestedInventories);

FindButton(form, "↻").PerformClick();
Assert.Equal(new[] { new IntPtr(42) }, requestedInventories);
```

Replace `PreviewRefreshFailureDoesNotBlockAHealthyInventoryRefresh` with:

```csharp
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
```

Change `RefreshFailurePreservesPriorInventoryAndShowsInlineWarning` to click the refresh button instead of calling `RefreshLiveState()`, then call `RefreshLiveState()` once afterward and assert the inventory warning is still visible:

```csharp
FindButton(form, "↻").PerformClick();
form.RefreshLiveState();

Assert.Same(priorNode, tree.Nodes[0]);
Assert.Equal(1, previews.RefreshCalls);
Assert.Contains(FindControls<Label>(form),
    label => label.Text.Contains("inventory unavailable"));
```

Add a draft-edit regression proving rerenders reuse the cached inventory:

```csharp
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
```

- [ ] **Step 2: Run the focused form tests and verify old automatic enumeration fails them**

Run:

```powershell
dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter "FullyQualifiedName~TargetManagerFormTests"
```

Expected: the new automatic-refresh tests fail because `RefreshLiveState()` still calls `BuildInventoryForCurrentMode()`, and the manual-refresh tests fail because the form does not subscribe to the new event.

- [ ] **Step 3: Cache inventory by active editor model and subscribe safely**

Add state beside `_capturedInventory`:

```csharp
private ProcessInventory _activeInventory = ProcessInventory.Empty;
private TargetEditorModel? _inventoryEditorModel;
private string? _processRefreshWarning;
```

In `BindEditor()`, unsubscribe before disposing, subscribe after constructing, and reset cache identity when returning to Browse mode:

```csharp
_editor.InventoryRefreshRequested -= OnInventoryRefreshRequested;
```

and:

```csharp
_editor.InventoryRefreshRequested += OnInventoryRefreshRequested;
```

When `_model.Editor` is null, also set `_inventoryEditorModel = null`, `_activeInventory = ProcessInventory.Empty`, and `_processRefreshWarning = null`. Do not reset these values when `LoadModel` receives the same `TargetEditorModel` reference.

- [ ] **Step 4: Replace model-change enumeration with one initial load**

Rewrite `RefreshEditorInventoryForModelChange()` around editor-model identity:

```csharp
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
```

Add `UpdateRefreshWarning()` and use it at the start of preview refreshes so a manual-process error survives the timer:

```csharp
private void UpdateRefreshWarning() =>
    _refreshWarning.Text = _processRefreshWarning ?? "";
```

Replace direct `_refreshWarning.Text = ""` statements at the beginning of `BuildFromModel()` and `RefreshLiveState()` with `UpdateRefreshWarning()`. Existing preview errors continue to call `AddRefreshWarning`, so they append after any retained process warning.

In `ReconcilePreview()`, replace its direct catch assignment with the same append helper so an editor-triggered preview error cannot overwrite a retained process error:

```csharp
catch (Exception ex)
{
    AddRefreshWarning($"Preview could not be refreshed: {ex.Message}");
}
```

- [ ] **Step 5: Add explicit mode-aware refresh and make automatic refresh preview-only**

Delete the inventory `try/catch` block from `RefreshLiveState()` so it ends after preview status/handle publication. Add this handler:

```csharp
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
```

Leave `BuildInventoryForCurrentMode()` unchanged: its Edit path combines matching windows, and its Add path resolves the latest snapshot for the captured handle. This guarantees one enumeration per click and retains `_activeInventory` if the call throws.

- [ ] **Step 6: Run focused tests and the complete suite**

Run:

```powershell
dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter "FullyQualifiedName~TargetManagerFormTests|FullyQualifiedName~TargetEditorLayoutTests"
dotnet test UsageBars.sln
```

Expected: all focused tests pass; the full suite passes with no regression to preview refresh, process viewport restoration, or editor switching.

- [ ] **Step 7: Commit the inventory lifecycle change**

```powershell
git add src/TargetManagerForm.cs tests/UsageBars.Tests/TargetManagerFormTests.cs
git commit -m "fix: make process refresh user controlled"
```

### Task 3: Keep Cancel and Save visible side by side

**Files:**
- Modify: `src/TargetManagerForm.cs:287-331`
- Test: `tests/UsageBars.Tests/TargetManagerFormTests.cs:1-390`

**Interfaces:**
- Consumes: existing `_cancel`, `_save`, `_split.Panel2`, and `TargetManagerForm.MinimumSize`.
- Produces: a right-aligned, nonwrapping two-column action table with Cancel in column 0 and Save in column 1.

- [ ] **Step 1: Write the failing minimum-size footer geometry test**

Add this test to `TargetManagerFormTests`:

```csharp
[Fact]
public void MinimumSizeKeepsCancelAndSaveOnOneVisibleRow()
{
    var target = Rule("Rider");
    using var model = new TargetManagementWorkspaceModel(new RecordingTargetRuleStore(target));
    using var form = NewForm(model);
    form.Size = form.MinimumSize;
    BeginEdit(form, target.Id);
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
```

- [ ] **Step 2: Run the focused test and confirm the wrapping layout fails**

Run:

```powershell
dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter "FullyQualifiedName~TargetManagerFormTests.MinimumSizeKeepsCancelAndSaveOnOneVisibleRow"
```

Expected: the current right-to-left wrapping `FlowLayoutPanel` places the buttons on different vertical positions or outside the editor panel.

- [ ] **Step 3: Replace the wrapping flow panel with a fixed two-column table**

In `BuildEditorPane()`, replace `actions` with:

```csharp
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
```

Keep the outer footer's first column at `SizeType.Percent, 100` and second column at `SizeType.AutoSize`; this leaves all variable-width warning text in the first column and reserves the buttons' complete preferred widths in the second.

- [ ] **Step 4: Run form/layout tests and the full suite**

Run:

```powershell
dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter "FullyQualifiedName~TargetManagerFormTests|FullyQualifiedName~TargetEditorLayoutTests"
dotnet test UsageBars.sln
```

Expected: the new geometry test passes, all existing form/editor tests pass, and the complete solution is green.

- [ ] **Step 5: Commit the footer fix**

```powershell
git add src/TargetManagerForm.cs tests/UsageBars.Tests/TargetManagerFormTests.cs
git commit -m "fix: keep target editor actions visible"
```

### Task 4: Publish and smoke-test the Windows executable

**Files:**
- Verify: `dist/UsageBars.exe`

**Interfaces:**
- Consumes: the passing solution and the existing project publish configuration.
- Produces: a refreshed `dist/UsageBars.exe` and evidence that the manager opens with stable process content and visible footer actions.

- [ ] **Step 1: Verify the working tree and run the final suite**

```powershell
git status --short
dotnet test UsageBars.sln
```

Expected: no uncommitted source/test changes and the complete suite passes.

- [ ] **Step 2: Publish to the existing distribution directory**

```powershell
dotnet publish src/UsageBars.csproj -c Release -o dist
```

Expected: publish succeeds and `dist/UsageBars.exe` has a fresh modification time.

- [ ] **Step 3: Restart only the running UsageBars distribution process**

Resolve the distribution executable, select only processes whose reported path is exactly that file, and print the selected PID/path before stopping it:

```powershell
$usageBarsExe = (Resolve-Path -LiteralPath 'C:\path\to\UsageBars\dist\UsageBars.exe').Path
$usageBarsProcesses = @(Get-Process UsageBars -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $usageBarsExe })
$usageBarsProcesses | Select-Object Id, Path
```

After the printed paths confirm the selection is scoped to the distribution executable, stop only those selected processes, then start the published executable hidden:

```powershell
if ($usageBarsProcesses.Count -gt 0) {
    $usageBarsProcesses | Stop-Process
}
Start-Process -FilePath 'C:\path\to\UsageBars\dist\UsageBars.exe' -WorkingDirectory 'C:\path\to\UsageBars\dist' -WindowStyle Hidden
```

Expected: one UsageBars process runs from the distribution path.

- [ ] **Step 4: Perform the live UI smoke check**

Open **Manage targets**, edit a target, and verify:

1. the process tree remains still for at least two preview timer intervals;
2. changing name or placement does not rebuild or scroll the tree;
3. clicking `↻` refreshes the process list once;
4. Cancel changes and Save changes are side by side and fully visible at minimum window size.

Record any discrepancy before claiming completion.
