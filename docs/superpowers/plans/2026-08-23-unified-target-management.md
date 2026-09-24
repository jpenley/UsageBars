# Unified Target Management Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the separate target manager and add-target dialog with one modeless workspace, add multi-window live editing previews, and stop process refreshes from resetting the tree viewport.

**Architecture:** A `TargetManagementWorkspaceModel` owns Browse/EditExisting/AddCaptured state independently of WinForms. `TargetManagerCoordinator` owns the singleton window and hide/pick/restore lifecycle. `TargetPreviewSet` composes the existing per-HWND `TargetPreviewSession` implementation, while `TargetManagerForm` renders the model and reuses `TargetEditorControl`.

**Tech Stack:** C# 12, .NET 8 `net8.0-windows`, WinForms manager/editor controls inside the existing WPF tray application, xUnit 2.9.2.

## Global Constraints

- The tray exposes one target command, **Manage targets…**; adding starts from the manager's **Add** button.
- The manager remains one modeless, resizable two-pane window and repeated tray commands activate the same instance.
- **Add** hides the manager during crosshair capture and restores its prior bounds, search, and selection on cancellation.
- Editing an existing rule previews unsaved changes on every currently matching open window.
- Add-captured mode always keeps an immediate preview on the captured HWND, including a dimmed preview when the draft no longer applies.
- Enable/Disable and confirmed Delete persist immediately; Save/Cancel apply only to the current draft.
- Process refresh must preserve expansion, selection, top visible node, dropdown values, and focus, and must not rebuild unchanged inventory.
- Windows Terminal process scope must remain honest: owner/descendant scope, possible sibling-window warning, and no exact tab attribution.
- Do not add external runtime dependencies.

---

### Task 1: Make Process Inventory Refresh Idempotent and Viewport-Preserving

**Files:**
- Modify: `src/ProcessInventory.cs`
- Modify: `src/TargetEditorControl.cs`
- Modify: `tests/UsageBars.Tests/ProcessInventoryTests.cs`
- Modify: `tests/UsageBars.Tests/TargetEditorLayoutTests.cs`

**Interfaces:**
- Produces: `ProcessInventory.Fingerprint(bool showSystemHelpers) : string`
- Produces: `ProcessTreeViewport(uint? SelectedPid, IReadOnlySet<uint> ExpandedPids, uint? TopPid, bool HadFocus)`
- Produces: `TargetEditorControl.InventoryBuildCount`, `CaptureProcessViewport()`, and state-preserving `SetInventory(ProcessInventory)` behavior.

- [ ] **Step 1: Write failing fingerprint and unchanged-refresh tests**

Add tests proving semantically identical inventories have one fingerprint and helper visibility participates in it:

```csharp
[Fact]
public void InventoryFingerprintIsStableAndIncludesHelperVisibility()
{
    var inventory = Inventory(
        Entry(7, 0, "WindowsTerminal", 0),
        Entry(8, 7, "pwsh", 1),
        Entry(9, 7, "OpenConsole", 1, helper: true));

    Assert.Equal(inventory.Fingerprint(false), inventory.Fingerprint(false));
    Assert.NotEqual(inventory.Fingerprint(false), inventory.Fingerprint(true));
}
```

Add a control test which calls `SetInventory` twice with equivalent values and asserts `InventoryBuildCount` remains `1`.

- [ ] **Step 2: Run the focused tests and verify RED**

```powershell
dotnet test tests\UsageBars.Tests\UsageBars.Tests.csproj --nologo --filter "ProcessInventoryTests|TargetEditorLayoutTests"
```

Expected: compile failures because `Fingerprint` and `InventoryBuildCount` do not exist.

- [ ] **Step 3: Implement deterministic inventory identity and no-op refresh**

Normalize and order fields before serializing them:

```csharp
public string Fingerprint(bool showSystemHelpers) => string.Join("\n",
    Entries
        .Where(entry => showSystemHelpers || !entry.IsSystemHelper)
        .OrderBy(entry => entry.Pid)
        .Select(entry => $"{entry.Pid}|{entry.ParentPid}|{entry.Name}|{entry.Depth}|{entry.IsSystemHelper}"))
    + $"\nwindows={VisibleWindowsWithOwner}|helpers={showSystemHelpers}";
```

In `TargetEditorControl.SetInventory`, return before changing any control if the fingerprint and sibling warning are unchanged. Preserve provider combo text before replacing choices.

- [ ] **Step 4: Write failing viewport-restoration tests**

Build a tree with at least twenty descendants, collapse one branch, select a middle PID, set a later node as `TopNode`, focus the tree, then refresh with one added descendant:

```csharp
var before = control.CaptureProcessViewport();
control.SetInventory(changedInventory);
var after = control.CaptureProcessViewport();

Assert.Equal(before.SelectedPid, after.SelectedPid);
Assert.Equal(before.ExpandedPids.Order(), after.ExpandedPids.Order());
Assert.Equal(before.TopPid, after.TopPid);
Assert.True(after.HadFocus);
```

- [ ] **Step 5: Run the viewport test and verify RED**

Run the `TargetEditorLayoutTests` class and confirm the old clear/rebuild/`ExpandAll` path changes the captured viewport.

- [ ] **Step 6: Restore TreeView state only when a rebuild is necessary**

Tag every node with its PID. Before clearing, capture selection, expanded PIDs, `TopNode`, and focus. Rebuild and expand all only when no prior tree exists. On later rebuilds, restore expansion, selection, `TopNode`, and focus under `_loading = true` so no draft-change event fires. Do not call `ExpandAll()` after the first successful load.

- [ ] **Step 7: Verify and commit Task 1**

```powershell
dotnet test tests\UsageBars.Tests\UsageBars.Tests.csproj --nologo --filter "ProcessInventoryTests|TargetEditorLayoutTests"
git add src/ProcessInventory.cs src/TargetEditorControl.cs tests/UsageBars.Tests/ProcessInventoryTests.cs tests/UsageBars.Tests/TargetEditorLayoutTests.cs
git commit -m "fix: preserve process tree viewport during refresh"
```

---

### Task 2: Introduce the Unified Management Workspace Model and Search

**Files:**
- Create: `src/TargetManagementWorkspaceModel.cs`
- Create: `tests/UsageBars.Tests/TargetManagementWorkspaceModelTests.cs`
- Modify: `src/TargetRuleImpact.cs`
- Modify: `tests/UsageBars.Tests/TargetRuleImpactTests.cs`

**Interfaces:**
- Produces: `TargetManagementMode { Browse, EditExisting, AddCaptured }`
- Produces: `TargetManagementWorkspaceModel` with `Mode`, `Editor`, `Rows`, `SelectedSavedRuleId`, `CapturedWindow`, `BeginEdit`, `BeginCaptured`, `CreateMoreSpecific`, `ApplyDraft`, `CancelDraft`, `SaveDraft`, `SetSearch`, `SetEnabled`, and `Delete`.
- Produces: `TargetRuleSearch.Matches(TargetDefinition target, string? query) : bool` covering every documented field.

- [ ] **Step 1: Write failing state-transition tests**

Create tests for Browse/Edit cancellation, strongest applicable selection, new ID creation, successful add/update, persistence failure retaining the draft, toggling the edited rule without losing unrelated edits, and deleting the edited rule returning to Browse:

```csharp
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
```

- [ ] **Step 2: Run the workspace tests and verify RED**

```powershell
dotnet test tests\UsageBars.Tests\UsageBars.Tests.csproj --nologo --filter TargetManagementWorkspaceModelTests
```

Expected: compile failure because the unified model does not exist.

- [ ] **Step 3: Implement the three-state model**

Use this production shape:

```csharp
internal enum TargetManagementMode { Browse, EditExisting, AddCaptured }

internal sealed class TargetManagementWorkspaceModel : IDisposable
{
    public TargetManagementMode Mode { get; private set; }
    public Guid? SelectedSavedRuleId { get; private set; }
    public WindowSnapshot? CapturedWindow { get; private set; }
    public TargetEditorModel? Editor { get; private set; }
    public TargetDefinition? CurrentDefinition => Editor?.ToDefinition();
    public IReadOnlyList<TargetManagementRuleRow> Rows { get; private set; }

    public void BeginEdit(Guid id);
    public void BeginCaptured(WindowSnapshot window, TargetDraft inferredDraft);
    public void CreateMoreSpecific();
    public void ApplyDraft(TargetDefinition definition);
    public void CancelDraft();
    public TargetMutationResult SaveDraft();
    public TargetMutationResult SetEnabled(Guid id, bool enabled);
    public TargetMutationResult Delete(Guid id);
    public void SetSearch(string? query);
}
```

Store notifications must never reload an unrelated draft. Toggling the currently edited rule updates the draft's `Enabled` field. Deleting it clears the draft and capture context.

- [ ] **Step 4: Write failing complete-search tests**

Assert case-insensitive partial matches for name, hyphenated ID, compact ID, process, class, title kind, title value, provider name, and provider activity-process name. Include one negative query.

- [ ] **Step 5: Implement reusable complete search and grouping rows**

Move the current private search predicate into:

```csharp
internal static class TargetRuleSearch
{
    public static bool Matches(TargetDefinition target, string? query) =>
        Contains(target.Name, query)
        || Contains(target.Id.ToString("D"), query)
        || Contains(target.Id.ToString("N"), query)
        || Contains(target.ProcessName, query)
        || Contains(target.WindowClass, query)
        || Contains(target.WindowTitle?.Kind.ToString(), query)
        || Contains(target.WindowTitle?.Value, query)
        || target.Providers.Any(binding =>
            Contains(binding.Provider.ToString(), query)
            || Contains(binding.CliProcessName, query));
}
```

Browse/Edit rows contain every rule when search is empty and group them enabled/disabled. AddCaptured rows use `TargetRuleImpactAnalyzer` so Applies/Overlaps take precedence.

- [ ] **Step 6: Verify and commit Task 2**

```powershell
dotnet test tests\UsageBars.Tests\UsageBars.Tests.csproj --nologo --filter "TargetManagementWorkspaceModelTests|TargetRuleImpactTests"
git add src/TargetManagementWorkspaceModel.cs src/TargetRuleImpact.cs tests/UsageBars.Tests/TargetManagementWorkspaceModelTests.cs tests/UsageBars.Tests/TargetRuleImpactTests.cs
git commit -m "feat: model unified target management"
```

---

### Task 3: Reconcile Multi-Window Preview Sessions and Combined Process Scope

**Files:**
- Create: `src/TargetPreviewSet.cs`
- Create: `tests/UsageBars.Tests/TargetPreviewSetTests.cs`
- Modify: `src/ProcessInventory.cs`
- Modify: `tests/UsageBars.Tests/ProcessInventoryTests.cs`
- Modify: `src/TargetPreviewSession.cs`
- Modify: `tests/UsageBars.Tests/TargetPreviewSessionTests.cs`

**Interfaces:**
- Produces: `ITargetPreviewSet` with `SuppressedHwnds`, `Status`, `Update`, `Refresh`, and `Clear`.
- Produces: `TargetPreviewSetStatus(int WindowCount, TargetPreviewState? WorstState, string Message)`.
- Produces: grouped `ProcessInventoryGroup(IntPtr Hwnd, uint OwnerPid, string Label, IReadOnlyList<ProcessInventoryEntry> Entries)` and `ProcessInventory.Combine(...)`.
- Consumes: `TargetManagementMode` and the existing `ITargetPreviewSession` per-HWND unit.

- [ ] **Step 1: Write failing preview reconciliation tests**

Use recording session factories and mutable window snapshots:

```csharp
[Fact]
public void EditingRuleAddsRetainsAndRemovesSessionsAsMatchingWindowsChange()
{
    var windows = new List<WindowSnapshot> { Window(10, "terminal"), Window(20, "terminal") };
    var sessions = new RecordingPreviewSessionFactory();
    using var previews = NewPreviewSet(() => windows, sessions);

    previews.Update(TerminalDraft(), TargetManagementMode.EditExisting, capturedWindow: null);
    Assert.Equal(new[] { new IntPtr(10), new IntPtr(20) }, previews.SuppressedHwnds.Order());

    windows.RemoveAt(0);
    previews.Refresh();
    Assert.Equal(1, sessions.For(new IntPtr(10)).DisposeCalls);
    Assert.Equal(new[] { new IntPtr(20) }, previews.SuppressedHwnds);
}
```

Also test that AddCaptured retains the captured HWND for a mismatching draft, no edit matches produces a clear no-preview status, `Clear` disposes exactly once, and refresh passes latest titles into retained sessions.

- [ ] **Step 2: Run preview-set tests and verify RED**

Run the new test class and confirm the missing-type failure.

- [ ] **Step 3: Implement the preview set as a composition layer**

```csharp
internal interface ITargetPreviewSet : IDisposable
{
    IReadOnlySet<IntPtr> SuppressedHwnds { get; }
    TargetPreviewSetStatus Status { get; }
    void Update(TargetDefinition draft, TargetManagementMode mode, WindowSnapshot? capturedWindow);
    void Refresh();
    void Clear();
}
```

For EditExisting, desired windows are current top-level snapshots where `draft.Matches(window)`. For AddCaptured, desired windows contain only the latest snapshot for `capturedWindow.Hwnd`, or the captured snapshot when it is still valid but temporarily absent from enumeration. Diff desired HWNDs against the session dictionary and never recreate retained sessions.

- [ ] **Step 4: Write failing combined-inventory tests**

Build two matching owner trees and assert `ProcessInventory.Combine` creates two title/PID-labeled groups, keeps entries separated, deduplicates dropdown names case-insensitively, and preserves each sibling-window warning.

- [ ] **Step 5: Implement grouped inventory and single-window compatibility**

```csharp
internal sealed record ProcessInventoryGroup(
    IntPtr Hwnd, uint OwnerPid, string Label,
    IReadOnlyList<ProcessInventoryEntry> Entries,
    bool MayIncludeSiblingWindows);

internal sealed record ProcessInventory(IReadOnlyList<ProcessInventoryGroup> Groups)
{
    public IReadOnlyList<ProcessInventoryEntry> Entries =>
        Groups.SelectMany(group => group.Entries).ToList();
}
```

Keep `ProcessInventoryBuilder.Build` as the one-window entry point and add `Combine` for EditExisting. Update `TargetEditorControl.RebuildProcessTree` to render a root node per group only when more than one group exists.

Update `ProcessInventory.Fingerprint` from Task 1 to include each group's HWND, owner PID, label, and sibling-warning flag in deterministic order. This ensures a group label or scope warning change triggers one rebuild while identical grouped inventories remain no-ops.

- [ ] **Step 6: Verify and commit Task 3**

```powershell
dotnet test tests\UsageBars.Tests\UsageBars.Tests.csproj --nologo --filter "TargetPreviewSetTests|TargetPreviewSessionTests|ProcessInventoryTests"
git add src/TargetPreviewSet.cs src/TargetPreviewSession.cs src/ProcessInventory.cs tests/UsageBars.Tests/TargetPreviewSetTests.cs tests/UsageBars.Tests/TargetPreviewSessionTests.cs tests/UsageBars.Tests/ProcessInventoryTests.cs
git commit -m "feat: preview target edits across matching windows"
```

---

### Task 4: Extend the Singleton Manager Coordinator with Hide/Pick/Restore

**Files:**
- Modify: `src/TargetManagerCoordinator.cs`
- Modify: `tests/UsageBars.Tests/TargetManagerCoordinatorTests.cs`

**Interfaces:**
- Produces: `TargetCaptureContext(WindowSnapshot Window, TargetDraft Draft, ProcessInventory Inventory)`.
- Extends: `IModelessTargetManager` with `AddRequested`, `SelectedRuleId`, `SuppressedHwnds`, `Hide`, `RestoreBrowseSelection`, `BeginCaptured`, and `RefreshLiveState`.
- Consumes: `Func<Task<WindowCaptureResult>>` and `Func<WindowCaptureResult, TargetCaptureContext>`.

- [ ] **Step 1: Write failing picker lifecycle tests**

Extend the fake manager and add tests for successful hide/restore on the same instance, Escape/right-click cancellation restoring prior selection, capture failure restoration and warning/report, repeated Add rejection while picker is active, manager close while await is pending, and coordinator disposal:

```csharp
[Fact]
public async Task AddHidesManagerAndSuccessfulPickRestoresSameInstanceInCapturedMode()
{
    var pending = new TaskCompletionSource<WindowCaptureResult>();
    var manager = new FakeManager { SelectedRuleId = Guid.NewGuid() };
    using var coordinator = Coordinator(manager, () => pending.Task);
    coordinator.Open();

    manager.RaiseAddRequested();
    Assert.False(manager.Visible);

    pending.SetResult(CaptureSuccess());
    await manager.CapturedStarted;

    Assert.Equal(1, manager.HideCount);
    Assert.Equal(2, manager.ShowCount);
}
```

- [ ] **Step 2: Run coordinator tests and verify RED**

Run `TargetManagerCoordinatorTests` and confirm compile failures for the new lifecycle surface.

- [ ] **Step 3: Implement async Add orchestration without replacing the manager**

Wire `AddRequested` when a manager is created and unsubscribe on close/dispose. In the async handler: capture the selected ID and manager reference, hide it, await one pick, ignore results after close/dispose, re-show the same manager, call `BeginCaptured` on success, or call `RestoreBrowseSelection` on cancellation/failure. Report non-cancellation failures. Guard with `_pickActive`, reset in `finally`.

- [ ] **Step 4: Verify and commit Task 4**

```powershell
dotnet test tests\UsageBars.Tests\UsageBars.Tests.csproj --nologo --filter TargetManagerCoordinatorTests
git add src/TargetManagerCoordinator.cs tests/UsageBars.Tests/TargetManagerCoordinatorTests.cs
git commit -m "feat: add targets from the modeless manager"
```

---

### Task 5: Rebuild TargetManagerForm as the Unified Two-Pane Workspace

**Files:**
- Rewrite: `src/TargetManagerForm.cs`
- Create: `tests/UsageBars.Tests/TargetManagerFormTests.cs`
- Modify: `src/TargetEditorControl.cs`
- Delete after migration: `src/TargetCaptureForm.cs`
- Delete after migration: `src/TargetEditorForm.cs`
- Delete after migration: `tests/UsageBars.Tests/TargetCaptureFormModelTests.cs`

**Interfaces:**
- Consumes: `TargetManagementWorkspaceModel`, `ITargetPreviewSet`, and grouped `ProcessInventory`.
- Implements: the extended `IModelessTargetManager` contract from Task 4.
- Produces events: `AddRequested` and `PreviewHandlesChanged`.

- [ ] **Step 1: Write failing form layout and mode tests**

Create tests asserting minimum pane widths, search and Add in Browse, disabled/blank editor in Browse, Edit loading a selected rule, BeginCaptured showing Applies/Overlaps and selecting the strongest rule, readable resize option, and search/selection survival across hide/restore. Expose semantic internal properties: `VisibleRuleIds`, `EditorEnabled`, `RulePaneWidth`, `EditorPaneWidth`, and `SelectedRuleId`.

- [ ] **Step 2: Run form tests and verify RED**

Run `TargetManagerFormTests` and verify the old grid manager lacks the unified surface.

- [ ] **Step 3: Build the left pane and state-driven editor pane**

Port the searchable grouped `ListView` from `TargetCaptureForm`. Add full-ID/match details and buttons labeled **Add**, **Edit**, **Create more-specific rule**, **Enable/Disable**, **Delete…**, **Save changes**, and **Cancel changes**. Bind `TargetEditorControl` only when `model.Editor` exists; never open another modal form.

- [ ] **Step 4: Write failing preview and immediate-mutation form tests**

With a recording `ITargetPreviewSet`, prove initial edit preview, draft-change preview, Save success cleanup/Browse, Save failure retention/error, Cancel/close cleanup, immediate Enable/Disable/Delete, and deleting the edited rule clearing the editor.

- [ ] **Step 5: Implement previews, inventories, timers, and cleanup**

The two-second timer performs:

```csharp
_previews.Refresh();
SetPreviewStatus(_previews.Status);
_editor.SetInventory(BuildInventoryForCurrentMode());
```

AddCaptured inventory uses the captured owner. EditExisting combines all live snapshots matching the draft. Refresh exceptions preserve the prior tree and show an inline warning. Raise `PreviewHandlesChanged` only when the suppression set changes.

- [ ] **Step 6: Remove obsolete modal forms after migrated tests are green**

Delete `TargetCaptureForm.cs`, `TargetEditorForm.cs`, and the superseded form tests. Move relevant sidebar projection/status assertions into `TargetManagerFormTests` first.

- [ ] **Step 7: Verify and commit Task 5**

```powershell
dotnet test tests\UsageBars.Tests\UsageBars.Tests.csproj --nologo --filter "TargetManagerFormTests|TargetEditorLayoutTests|TargetManagementWorkspaceModelTests|TargetPreviewSetTests"
git add src/TargetManagerForm.cs src/TargetEditorControl.cs tests/UsageBars.Tests/TargetManagerFormTests.cs
git rm src/TargetCaptureForm.cs src/TargetEditorForm.cs tests/UsageBars.Tests/TargetCaptureFormModelTests.cs
git commit -m "feat: unify target management and capture workspace"
```

---

### Task 6: Integrate the Unified Manager into Tray and Overlay Tracking

**Files:**
- Modify: `src/Tray.cs`
- Modify: `src/OverlayController.cs`
- Modify or delete: `src/TargetCaptureFlow.cs`
- Modify or delete: `tests/UsageBars.Tests/TargetCaptureFlowTests.cs`
- Modify: `tests/UsageBars.Tests/TargetViewModelTests.cs`
- Modify: `README.md`

**Interfaces:**
- Consumes: `TargetManagerCoordinator.SuppressedHwnds` and `RefreshLiveState()`.
- Produces: `TargetPreviewSuppression.RemovePreviewWindows(..., IReadOnlySet<IntPtr>)`.
- Removes: `Tray.AddTargetRequested` and the old `OverlayController.BeginAddTarget` modal flow.

- [ ] **Step 1: Write failing tray and suppression tests**

```csharp
[Fact]
public void TrayOffersOnlyManagementForTargetCommands()
{
    Assert.Contains("Manage targets...", Tray.TargetCommandLabels);
    Assert.DoesNotContain(Tray.TargetCommandLabels,
        label => label.Contains("clicking a window", StringComparison.OrdinalIgnoreCase));
}

[Fact]
public void SuppressionRemovesEveryPreviewedWindow()
{
    var wanted = Wanted(new IntPtr(10), new IntPtr(20), new IntPtr(30));
    TargetPreviewSuppression.RemovePreviewWindows(
        wanted, new HashSet<IntPtr> { new(10), new(30) });
    Assert.Equal(new[] { new IntPtr(20) }, wanted.Keys.Select(key => key.Hwnd).Distinct());
}
```

- [ ] **Step 2: Run tray/suppression tests and verify RED**

Run `TargetViewModelTests` and `TargetCaptureFlowTests` and confirm failure against two-command/single-HWND behavior.

- [ ] **Step 3: Remove the separate tray Add command**

Keep **Manage targets…**, a separator, and the enabled/disabled summary. Delete `AddTargetRequested` and its subscription.

- [ ] **Step 4: Replace modal capture state in OverlayController**

Construct `TargetManagerCoordinator` with `_windowPicker.PickAsync`, a context builder using `ProviderInference.CreateDraft`, a manager factory using the unified model/preview/form, and diagnostics/warning callbacks. Delete `_targetEditor`, `_targetCaptureForm`, `_targetPreview`, `_targetCaptureFlow`, `_addTargetFlowActive`, `BeginAddTarget`, `ShowTargetEditor`, and `EditTargetFromManager` once unreferenced.

In `Track()`:

```csharp
_targetManagerCoordinator.RefreshLiveState();
TargetPreviewSuppression.RemovePreviewWindows(
    wanted, _targetManagerCoordinator.SuppressedHwnds);
```

Dispose the manager coordinator before normal pills and picker so previews cannot outlive their hosts.

- [ ] **Step 5: Update documentation and remove obsolete flow code**

Rewrite README around the one modeless manager/Add button, multi-window edit preview, and stable process refresh. If `TargetCaptureFlow` has no responsibility left, migrate its cleanup tests and delete the source/test pair.

- [ ] **Step 6: Verify and commit Task 6**

```powershell
dotnet test UsageBars.sln --nologo --verbosity minimal
git add src tests README.md
git commit -m "feat: manage all target workflows from one window"
```

---

### Task 7: Release Verification and Manual Smoke Test

**Files:**
- Modify only if verification exposes a defect: the owning source/test files from Tasks 1-6.

**Interfaces:**
- Verifies the committed branch as a complete product; produces no new architecture.

- [ ] **Step 1: Run clean full verification**

```powershell
dotnet clean UsageBars.sln --nologo
dotnet test UsageBars.sln --nologo --verbosity minimal
git diff --check
git status --short
```

Expected: all tests pass, `git diff --check` emits nothing, and status is clean.

- [ ] **Step 2: Publish the self-contained executable**

```powershell
pwsh -File build.ps1 -NoRelaunch
```

Expected: `dist\UsageBars.exe` is produced for `win-x64` without warnings or publish errors.

- [ ] **Step 3: Exercise the live workflow**

After explicit approval to launch/restart the app if needed:

1. Open tray → Targets → Manage targets… and verify only one target command exists.
2. Reopen the command and verify the same manager activates.
3. Scroll the process tree away from the top, wait at least six seconds, and verify the viewport remains fixed.
4. Click Add, confirm the manager hides, cancel with right-click, and verify bounds/search/selection restore.
5. Click Add again, pick a window, verify immediate preview, adjust anchor/offset/provider process, and verify live response.
6. Edit a rule matching multiple windows and verify all matching windows preview.
7. Cancel and verify normal pills restore; repeat and Save to verify persistence.
8. Verify Enable/Disable and confirmed Delete behave immediately.

- [ ] **Step 4: Fix each observed defect with a new failing regression test first**

For each defect, add one focused test, run it to observe the expected failure, make the smallest production change, rerun the focused test, then rerun the full suite.

- [ ] **Step 5: Commit verification fixes, if any**

Use a defect-specific commit message. If no files changed, do not create an empty commit.
