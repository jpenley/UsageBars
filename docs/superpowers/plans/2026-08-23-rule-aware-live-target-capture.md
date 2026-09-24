# Rule-Aware Live Target Capture Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the current capture confirmation dialog with a rule-aware workspace that finds applicable and overlapping targets, exposes owner-process activity choices, and previews every draft change on the selected window before saving.

**Architecture:** Keep saved rules authoritative in `TargetStore`, add pure analysis and process-inventory models, and host unsaved state in a capture-workspace model. A controller-owned `TargetPreviewSession` suppresses normal pills only for the selected HWND and renders temporary pills through the existing `ProviderPill` path; the WinForms workspace observes draft changes and never inserts drafts into the store.

**Tech Stack:** C# 12, .NET 8 Windows, WinForms target dialogs, WPF overlay windows, Win32 Toolhelp/window APIs, xUnit.

## Global Constraints

- Temporary preview definitions must never be inserted into `TargetStore` or written to `targets.json`.
- Preview suppression is limited to the captured HWND; all other windows continue using saved rules.
- Windows Terminal process information is owner-process-scoped and must never claim exact tab or pane attribution.
- Process IDs are transient display data; only stable executable names are persisted in `ProviderBinding.CliProcessName`.
- Claude and Codex retain independent activity-process selections.
- Sidebar Enable/Disable/Delete mutations persist immediately; right-pane edits persist only on Save.
- Cancel performs no draft mutation and immediately restores saved-rule resolution.
- The selected window closing disables preview but does not prevent saving its stable rule identity.
- The form is resizable and the text “Require resizable frame” remains readable at supported DPI settings.
- No target-document schema version change is introduced.

---

## File Structure

- Create `src/TargetRuleImpact.cs`: pure rule classification, search, and deterministic ranking.
- Create `src/ProcessInventory.cs`: immutable owner-process inventory and helper filtering.
- Modify `src/HostWindow.cs`: expose safe ancestry enumeration from captured Toolhelp data.
- Modify `src/TargetViewModels.cs`: preserve per-provider process names and add capture-workspace state.
- Create `src/TargetEditorControl.cs`: reusable editor surface shared by capture and management dialogs.
- Modify `src/TargetEditorForm.cs`: thin modal wrapper around the shared editor, fixing the option layout.
- Create `src/TargetCaptureForm.cs`: resizable two-pane capture workspace with rule search/actions and process tree.
- Create `src/TargetPreviewSession.cs`: temporary pills, applicability state, HWND-scoped suppression, and cleanup.
- Modify `src/ProviderPill.cs`: explicit preview dimming independent of source staleness.
- Modify `src/OverlayView.cs`: accept a preview-opacity override while retaining normal stale rendering.
- Modify `src/OverlayController.cs`: capture-workspace lifecycle, preview integration, save handoff, suppression, and diagnostics.
- Create focused xUnit files matching each new production unit.
- Modify `README.md`: describe rule-aware capture, process scope, live preview, and duplicate cleanup.

---

### Task 1: Saved-rule impact analysis

**Files:**
- Create: `src/TargetRuleImpact.cs`
- Create: `tests/UsageBars.Tests/TargetRuleImpactTests.cs`

**Interfaces:**
- Consumes: `WindowSnapshot`, `TargetDefinition.Matches`, `TargetDefinition.HasSameIdentity`, `TargetDefinition.Overlaps`, `TargetDefinition.Specificity`.
- Produces: `TargetRuleRelationship`, `TargetRuleImpact`, and `TargetRuleImpactAnalyzer.Analyze(WindowSnapshot, TargetDefinition, IReadOnlyList<TargetDefinition>, string?)`.

- [ ] **Step 1: Write failing classification and precedence tests**

```csharp
[Fact]
public void ExactIdentityOutranksApplyAndOverlap()
{
    var window = Window("terminal", "frame", "Codex");
    var draft = Target("Draft", "terminal", "frame", ProviderId.Codex);
    var exact = draft with { Id = Guid.NewGuid(), Name = "Exact", Enabled = false };

    var row = Assert.Single(TargetRuleImpactAnalyzer.Analyze(window, draft, new[] { exact }, null));

    Assert.Equal(TargetRuleRelationship.ExactIdentity, row.Relationship);
    Assert.True(row.AppliesNow);
    Assert.True(row.ExactIdentity);
}

[Fact]
public void TitleMismatchCanStillBeReportedAsOverlap()
{
    var window = Window("terminal", "frame", "Codex A");
    var draft = Target("A", "terminal", "frame", ProviderId.Codex) with
    {
        WindowTitle = new(TitleMatchKind.Prefix, "Codex A")
    };
    var other = Target("B", "terminal", "frame", ProviderId.Codex) with
    {
        Id = Guid.NewGuid(),
        WindowTitle = new(TitleMatchKind.Regex, "^Codex B")
    };

    var row = Assert.Single(TargetRuleImpactAnalyzer.Analyze(window, draft, new[] { other }, null));

    Assert.False(row.AppliesNow);
    Assert.Equal(TargetRuleRelationship.OverlapsDraft, row.Relationship);
}
```

- [ ] **Step 2: Run the new test file and verify RED**

Run: `dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter FullyQualifiedName~TargetRuleImpactTests --nologo`

Expected: compilation fails because `TargetRuleImpactAnalyzer` and related types do not exist.

- [ ] **Step 3: Implement the immutable analysis result and strongest-relationship selection**

```csharp
internal enum TargetRuleRelationship { ExactIdentity, AppliesNow, OverlapsDraft, SearchOnly }

internal sealed record TargetRuleImpact(
    TargetDefinition Target,
    TargetRuleRelationship Relationship,
    bool ExactIdentity,
    bool AppliesNow,
    bool OverlapsDraft,
    bool SearchMatch);

internal static class TargetRuleImpactAnalyzer
{
    public static IReadOnlyList<TargetRuleImpact> Analyze(
        WindowSnapshot window,
        TargetDefinition draft,
        IReadOnlyList<TargetDefinition> saved,
        string? search)
    {
        var query = search?.Trim();
        return saved
            .Select(target => Row(window, draft, target, query))
            .Where(row => row is not null)
            .Select(row => row!)
            .OrderBy(row => row.Relationship)
            .ThenByDescending(row => row.Target.Enabled)
            .ThenByDescending(row => row.Target.Specificity)
            .ThenByDescending(row => row.Target.Revision)
            .ThenBy(row => row.Target.Id)
            .ToList();
    }
}
```

`Row` must calculate all four booleans, display only the strongest enum relationship, exclude unrelated rules unless search matches, compare text case-insensitively, and search name, full/partial GUID, process, class, title kind, and title value.

- [ ] **Step 4: Add failing ranking and search tests**

```csharp
[Theory]
[InlineData("rider")]
[InlineData("sunawtframe")]
[InlineData("prefix")]
[InlineData("workspace")]
public void SearchCoversEveryApprovedRuleField(string query)
{
    var target = Target("Rider project", "rider64", "SunAwtFrame", ProviderId.Claude) with
    {
        WindowTitle = new(TitleMatchKind.Prefix, "Workspace")
    };

    Assert.Single(TargetRuleImpactAnalyzer.Analyze(
        Window("other", "other", "other"),
        Target("Draft", "new", "new", ProviderId.Codex),
        new[] { target }, query));
}

[Fact]
public void DefaultCandidateIsDeterministicAcrossEnabledSpecificityRevisionAndId()
{
    var window = Window("app", "frame", "Project");
    var older = Target("Older", "app", "frame", ProviderId.Claude) with { Revision = 4 };
    var newer = older with { Id = Guid.NewGuid(), Name = "Newer", Revision = 5 };

    var selected = TargetRuleImpactAnalyzer.StrongestApplicable(
        TargetRuleImpactAnalyzer.Analyze(window, older, new[] { older, newer }, null));

    Assert.Equal(newer.Id, selected!.Target.Id);
}
```

Add these literal test fixtures at the bottom of `TargetRuleImpactTests`:

```csharp
private static WindowSnapshot Window(string process, string windowClass, string title) =>
    new(new IntPtr(42), 7, process, windowClass, title, true);

private static TargetDefinition Target(
    string name, string process, string windowClass, ProviderId provider) =>
    TargetDefinition.Captured(name, process, windowClass,
        new[] { provider == ProviderId.Claude ? ProviderBinding.Claude : ProviderBinding.Codex },
        TargetPlacementKind.Terminal, TargetActivationPolicy.AlwaysWhileOpen, true, 0);
```

- [ ] **Step 5: Implement `StrongestApplicable` and complete search matching**

```csharp
public static TargetRuleImpact? StrongestApplicable(IReadOnlyList<TargetRuleImpact> rows) =>
    rows.Where(row => row.ExactIdentity || row.AppliesNow)
        .OrderBy(row => row.ExactIdentity ? 0 : 1)
        .ThenByDescending(row => row.Target.Enabled)
        .ThenByDescending(row => row.Target.Specificity)
        .ThenByDescending(row => row.Target.Revision)
        .ThenBy(row => row.Target.Id)
        .FirstOrDefault();
```

- [ ] **Step 6: Run focused and full tests**

Run: `dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter FullyQualifiedName~TargetRuleImpactTests --nologo`

Expected: all impact tests pass.

Run: `dotnet test UsageBars.sln --nologo`

Expected: all tests pass.

- [ ] **Step 7: Commit the rule-analysis slice**

```powershell
git add src/TargetRuleImpact.cs tests/UsageBars.Tests/TargetRuleImpactTests.cs
git commit -m "feat: analyze saved rules during target capture"
```

---

### Task 2: Owner-process inventory and stable activity choices

**Files:**
- Create: `src/ProcessInventory.cs`
- Modify: `src/HostWindow.cs`
- Create: `tests/UsageBars.Tests/ProcessInventoryTests.cs`

**Interfaces:**
- Consumes: `ProcessTree.FromMap`, `ProcessTree.Capture`, captured `WindowSnapshot.Pid`, and capture-time top-level window snapshots.
- Produces: `ProcessInventoryEntry`, `ProcessInventory`, `ProcessTree.InventoryFor(uint)`, and `ProcessInventoryBuilder.Build(WindowSnapshot, IReadOnlyList<WindowSnapshot>, ProcessTree)`.

- [ ] **Step 1: Write failing ancestry, helper, deduplication, and sibling-warning tests**

```csharp
[Fact]
public void InventoryContainsOwnerAndAllDescendantsWithStablePaths()
{
    var tree = ProcessTree.FromMap(new[]
    {
        (10u, 1u, "WindowsTerminal"),
        (20u, 10u, "OpenConsole"),
        (30u, 20u, "pwsh"),
        (40u, 30u, "codex"),
    });

    var inventory = ProcessInventoryBuilder.Build(
        Window(10u, new IntPtr(100)),
        new[] { Window(10u, new IntPtr(100)) }, tree);

    Assert.Equal(new[] { "WindowsTerminal", "OpenConsole", "pwsh", "codex" },
        inventory.Entries.OrderBy(entry => entry.Depth).Select(entry => entry.Name));
    Assert.Equal("WindowsTerminal > OpenConsole > pwsh > codex",
        inventory.Entries.Single(entry => entry.Pid == 40).AncestryPath);
}

[Fact]
public void NormalChoicesDeduplicateNamesAndExcludeKnownHelpers()
{
    var inventory = new ProcessInventory(new[]
    {
        Entry(10, "OpenConsole", helper: true),
        Entry(11, "conhost", helper: true),
        Entry(12, "pwsh"),
        Entry(13, "codex"),
        Entry(14, "codex"),
    }, 1);

    Assert.Equal(new[] { "codex", "pwsh" }, inventory.SelectableNames(showSystemHelpers: false));
    Assert.Contains("OpenConsole", inventory.SelectableNames(showSystemHelpers: true));
}

[Fact]
public void SharedOwnerPidProducesSiblingWindowWarning()
{
    var selected = Window(10u, new IntPtr(100));
    var sibling = Window(10u, new IntPtr(200));

    var inventory = ProcessInventoryBuilder.Build(selected, new[] { selected, sibling }, TreeFor(10u));

    Assert.True(inventory.MayIncludeSiblingWindows);
    Assert.Equal(2, inventory.VisibleWindowsWithOwner);
}
```

Add these literal test fixtures at the bottom of `ProcessInventoryTests`:

```csharp
private static WindowSnapshot Window(uint pid, IntPtr hwnd) =>
    new(hwnd, pid, "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS", "Terminal", true);

private static ProcessTree TreeFor(uint ownerPid) => ProcessTree.FromMap(new[]
{
    (ownerPid, 1u, "WindowsTerminal")
});

private static ProcessInventoryEntry Entry(uint pid, string name, bool helper = false) =>
    new(pid, 10, name, 1, $"WindowsTerminal > {name}", helper);
```

- [ ] **Step 2: Run the focused test file and verify RED**

Run: `dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter FullyQualifiedName~ProcessInventoryTests --nologo`

Expected: compilation fails because process inventory types and `InventoryFor` are absent.

- [ ] **Step 3: Expose cycle-safe owner/descendant traversal from `ProcessTree`**

```csharp
internal IReadOnlyList<ProcessInventoryEntry> InventoryFor(uint ownerPid)
{
    var rows = new List<ProcessInventoryEntry>();
    foreach (var (pid, entry) in _byPid)
    {
        if (!TryPathFromOwner(ownerPid, pid, out var path)) continue;
        rows.Add(new ProcessInventoryEntry(
            pid, entry.Parent, entry.Name, path.Count - 1,
            string.Join(" > ", path.Select(NameOf)),
            ProcessInventoryHelpers.IsKnownHelper(entry.Name)));
    }
    return rows.OrderBy(row => row.Depth).ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
        .ThenBy(row => row.Pid).ToList();
}
```

`TryPathFromOwner` must cap traversal at 64, reject cycles, include the owner itself, and return false for unrelated processes.

- [ ] **Step 4: Implement immutable inventory models and helper filtering**

```csharp
internal sealed record ProcessInventoryEntry(
    uint Pid, uint ParentPid, string Name, int Depth, string AncestryPath, bool IsSystemHelper);

internal sealed record ProcessInventory(
    IReadOnlyList<ProcessInventoryEntry> Entries,
    int VisibleWindowsWithOwner)
{
    public static ProcessInventory Empty { get; } = new(Array.Empty<ProcessInventoryEntry>(), 0);
    public bool MayIncludeSiblingWindows => VisibleWindowsWithOwner > 1;

    public IReadOnlyList<string> SelectableNames(bool showSystemHelpers) => Entries
        .Where(entry => showSystemHelpers || !entry.IsSystemHelper)
        .Select(entry => entry.Name)
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToList();
}
```

The known-helper set must include at least `OpenConsole` and `conhost`; helpers remain in `Entries` even when filtered from picker choices.

- [ ] **Step 5: Run focused and full tests**

Run: `dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter FullyQualifiedName~ProcessInventoryTests --nologo`

Expected: all process inventory tests pass.

Run: `dotnet test UsageBars.sln --nologo`

Expected: all tests pass.

- [ ] **Step 6: Commit the process-inventory slice**

```powershell
git add src/HostWindow.cs src/ProcessInventory.cs tests/UsageBars.Tests/ProcessInventoryTests.cs
git commit -m "feat: expose owner process activity inventory"
```

---

### Task 3: Per-provider editor state and readable shared editor control

**Files:**
- Modify: `src/TargetViewModels.cs`
- Create: `src/TargetEditorControl.cs`
- Modify: `src/TargetEditorForm.cs`
- Modify: `tests/UsageBars.Tests/TargetViewModelTests.cs`
- Create: `tests/UsageBars.Tests/TargetEditorLayoutTests.cs`

**Interfaces:**
- Consumes: `TargetEditorModel.From`, `TargetEditorModel.FromExisting`, `ProviderBinding`, `ProcessInventory`.
- Produces: editable `ClaudeProcessName` and `CodexProcessName`, `TargetEditorControl.DraftChanged`, `CurrentDefinition`, `SetInventory`, and a fixed/readable modal management editor.

- [ ] **Step 1: Write failing independent process-selection round-trip tests**

```csharp
[Fact]
public void EditorPersistsIndependentActivityProcessForEachProvider()
{
    var target = TargetDefinition.Captured(
        "Terminal", "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS",
        new[] { ProviderBinding.Claude, ProviderBinding.Codex },
        TargetPlacementKind.Terminal, TargetActivationPolicy.RequireProviderActivity, true, 0);
    var model = TargetEditorModel.FromExisting(target);

    model.ClaudeProcessName = "claude-worker";
    model.CodexProcessName = "codex";
    var edited = model.ToDefinition();

    Assert.Equal("claude-worker", edited.Providers.Single(x => x.Provider == ProviderId.Claude).CliProcessName);
    Assert.Equal("codex", edited.Providers.Single(x => x.Provider == ProviderId.Codex).CliProcessName);
}

[Fact]
public void DisabledProviderRetainsItsProcessSelectionWhenReenabled()
{
    var model = TargetEditorModel.FromExisting(TwoProviderTarget());
    model.ClaudeProcessName = "claude-child";
    model.Claude = false;
    model.Claude = true;

    Assert.Equal("claude-child", model.ToDefinition().Providers
        .Single(x => x.Provider == ProviderId.Claude).CliProcessName);
}
```

Add this fixture to both editor test classes:

```csharp
private static TargetDefinition TwoProviderTarget() => TargetDefinition.Captured(
    "Terminal", "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS",
    new[] { ProviderBinding.Claude, ProviderBinding.Codex },
    TargetPlacementKind.Terminal, TargetActivationPolicy.RequireProviderActivity, true, 0);
```

- [ ] **Step 2: Run model tests and verify RED**

Run: `dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter FullyQualifiedName~TargetViewModelTests --nologo`

Expected: compilation fails because the per-provider process-name properties do not exist.

- [ ] **Step 3: Extend `TargetEditorModel` without changing persisted schema**

```csharp
public string ClaudeProcessName { get; set; }
public string CodexProcessName { get; set; }

private ProviderBinding BindingFor(ProviderId provider, string fallback)
{
    var process = provider == ProviderId.Claude ? ClaudeProcessName : CodexProcessName;
    if (string.IsNullOrWhiteSpace(process)) process = fallback;
    return new ProviderBinding(provider, process.Trim(),
        _inferredActivityPatterns.TryGetValue(provider, out var pattern) ? pattern : null);
}
```

Initialize each property from its existing `ProviderBinding` and preserve it when its provider checkbox is toggled off.

- [ ] **Step 4: Write failing editor-layout behavior tests**

```csharp
[Fact]
public void SharedEditorUsesResizableStackedOptionsLayout()
{
    using var control = new TargetEditorControl(
        TargetEditorModel.FromExisting(TwoProviderTarget()), ProcessInventory.Empty);
    control.Size = control.MinimumSize;
    control.PerformLayout();

    Assert.Equal(Orientation.Vertical, control.OptionsOrientation);
    Assert.True(control.OptionsContentWidth >= control.ResizableOptionPreferredWidth);
}
```

These properties describe production layout state used by the control itself: `OptionsOrientation` selects the layout container and the width properties drive its minimum size; they are not test-only cleanup hooks.

- [ ] **Step 5: Run the layout test and verify RED**

Run: `dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter FullyQualifiedName~TargetEditorLayoutTests --nologo`

Expected: compilation fails because `TargetEditorControl` does not exist.

- [ ] **Step 6: Build the reusable editor and convert `TargetEditorForm` to a wrapper**

```csharp
internal sealed class TargetEditorControl : UserControl
{
    public event Action<TargetDefinition>? DraftChanged;
    public Orientation OptionsOrientation => Orientation.Vertical;
    public int OptionsContentWidth => _options.ClientSize.Width;
    public int ResizableOptionPreferredWidth => _resizable.PreferredSize.Width;
    public TargetDefinition CurrentDefinition => PullModelAndBuild();
    public void SetInventory(ProcessInventory inventory)
    {
        _inventory = inventory;
        ReplaceProcessChoices(_claudeProcess, _model.ClaudeProcessName);
        ReplaceProcessChoices(_codexProcess, _model.CodexProcessName);
        RebuildProcessTree();
    }
}
```

Use a scrollable `TableLayoutPanel`, separate Claude/Codex rows with editable dropdowns, an expandable process `TreeView`, and a vertically stacked options panel. Every editable control calls one coalesced `RaiseDraftChanged`; read-only application/class fields do not. When a persisted selection is absent from the latest inventory, keep it as the selected dropdown item and suffix its display text with “(not currently running)” without changing the stable value saved to `CliProcessName`. The advanced Show system helpers checkbox repopulates both dropdowns without losing either selection.

`TargetEditorForm` must set `FormBorderStyle = FormBorderStyle.Sizable`, `MinimumSize = new Size(680, 620)`, host the shared control docked to Fill, validate `CurrentDefinition` on Save, and keep the existing `EditedTarget` contract used by Manage Targets.

- [ ] **Step 7: Run focused and full tests**

Run: `dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter "FullyQualifiedName~TargetViewModelTests|FullyQualifiedName~TargetEditorLayoutTests" --nologo`

Expected: all editor model and layout tests pass.

Run: `dotnet test UsageBars.sln --nologo`

Expected: all tests pass.

- [ ] **Step 8: Commit the shared-editor slice**

```powershell
git add src/TargetViewModels.cs src/TargetEditorControl.cs src/TargetEditorForm.cs tests/UsageBars.Tests/TargetViewModelTests.cs tests/UsageBars.Tests/TargetEditorLayoutTests.cs
git commit -m "feat: add per-provider process target editor"
```

---

### Task 4: Capture-workspace state and immediate sidebar mutations

**Files:**
- Modify: `src/TargetStore.cs`
- Modify: `src/TargetViewModels.cs`
- Create: `tests/UsageBars.Tests/TargetCaptureWorkspaceModelTests.cs`

**Interfaces:**
- Consumes: `ITargetRuleStore`, `TargetRuleImpactAnalyzer`, captured `WindowSnapshot`, inferred `TargetDraft`.
- Produces: `CaptureDraftMode`, `TargetCaptureWorkspaceModel`, selected saved-rule ID, current editor model, grouped impact rows, search, immediate SetEnabled/Delete, and final add/update intent.

- [ ] **Step 1: Write failing default-selection and explicit-new-draft tests**

```csharp
[Fact]
public void WorkspaceEditsStrongestApplicableRuleByDefault()
{
    var broad = BroadTarget("Broad", revision: 5);
    var exact = ExactTarget("Exact", revision: 1);
    var store = StoreWith(broad, exact);

    var model = new TargetCaptureWorkspaceModel(Window(), InferredDraft(), store);

    Assert.Equal(CaptureDraftMode.UpdateExisting, model.Mode);
    Assert.Equal(exact.Id, model.SelectedSavedRuleId);
    Assert.Equal(exact.Id, model.CurrentDefinition.Id);
}

[Fact]
public void CreateMoreSpecificUsesNewIdAndKeepsStrongestVisualDefaults()
{
    var existing = ExactTarget("Existing", revision: 7) with
    {
        Anchor = PillAnchor.TopRight, OffsetX = 12, OffsetY = -4
    };
    var store = StoreWith(existing);
    var model = new TargetCaptureWorkspaceModel(Window(), InferredDraft(), store);

    model.CreateMoreSpecific();

    Assert.Equal(CaptureDraftMode.AddNew, model.Mode);
    Assert.NotEqual(existing.Id, model.CurrentDefinition.Id);
    Assert.Equal((PillAnchor.TopRight, 12, -4),
        (model.CurrentDefinition.Anchor, model.CurrentDefinition.OffsetX, model.CurrentDefinition.OffsetY));
}
```

- [ ] **Step 2: Run workspace tests and verify RED**

Run: `dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter FullyQualifiedName~TargetCaptureWorkspaceModelTests --nologo`

Expected: compilation fails because the workspace model does not exist.

- [ ] **Step 3: Implement selection, search, and draft-mode transitions**

```csharp
internal enum CaptureDraftMode { AddNew, UpdateExisting }

internal sealed class TargetCaptureWorkspaceModel
{
    public CaptureDraftMode Mode { get; private set; }
    public Guid? SelectedSavedRuleId { get; private set; }
    public TargetDefinition CurrentDefinition => Editor.ToDefinition();
    public TargetEditorModel Editor { get; private set; }
    public IReadOnlyList<TargetRuleImpact> Rows { get; private set; }
    public event Action? Changed;

    public void SelectForEdit(Guid id)
    {
        var target = _store.Targets.Single(target => target.Id == id);
        Mode = CaptureDraftMode.UpdateExisting;
        SelectedSavedRuleId = id;
        Editor = TargetEditorModel.From(new TargetDraft(target, _window.Title, Array.Empty<string>()));
        Reanalyze();
    }

    public void CreateMoreSpecific()
    {
        var current = Editor.ToDefinition();
        Mode = CaptureDraftMode.AddNew;
        SelectedSavedRuleId = null;
        Editor = TargetEditorModel.From(new TargetDraft(
            current with { Id = Guid.NewGuid(), Source = TargetSource.Captured, Revision = 0 },
            _window.Title, Array.Empty<string>()));
        Reanalyze();
    }

    public void SetSearch(string? query)
    {
        _search = query;
        Reanalyze();
    }
}
```

Subscribe to `TargetStore.Changed`, re-resolve selected rules by ID, and convert an edited deleted rule into `AddNew` while preserving editor values.

- [ ] **Step 4: Add failing immediate mutation and deleted-current-rule tests**

```csharp
[Fact]
public void DisableAndDeletePersistImmediately()
{
    var target = ExactTarget("Existing", 1);
    var store = new RecordingTargetRuleStore(target);
    var model = new TargetCaptureWorkspaceModel(Window(), InferredDraft(), store);

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
    var model = new TargetCaptureWorkspaceModel(Window(), InferredDraft(), store);
    model.Editor.Name = "Unsaved name";

    model.Delete(target.Id);

    Assert.Equal(CaptureDraftMode.AddNew, model.Mode);
    Assert.Equal("Unsaved name", model.Editor.Name);
    Assert.NotEqual(target.Id, model.CurrentDefinition.Id);
}
```

Add these fixtures to `TargetCaptureWorkspaceModelTests`:

```csharp
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
```

- [ ] **Step 5: Add the narrow store boundary used by the workspace and flow tests**

```csharp
internal interface ITargetRuleStore
{
    IReadOnlyList<TargetDefinition> Targets { get; }
    event Action? Changed;
    TargetMutationResult Add(TargetDefinition target);
    TargetMutationResult Update(TargetDefinition target);
    TargetMutationResult SetEnabled(Guid id, bool enabled);
    TargetMutationResult Delete(Guid id);
}
```

Declare `TargetStore : ITargetRuleStore` without changing any method behavior. In the test project, add an `internal sealed class RecordingTargetRuleStore : ITargetRuleStore` that stores a mutable list, increments `MutationCount` for each successful method, replaces records by ID for Update/SetEnabled, removes by ID for Delete, appends for Add, and raises `Changed` after each successful mutation. Its four methods return `new TargetMutationResult(false, "missing")` when an ID cannot be found.

- [ ] **Step 6: Implement immediate mutation methods and save intent**

```csharp
public TargetMutationResult SetEnabled(Guid id, bool enabled) => _store.SetEnabled(id, enabled);
public TargetMutationResult Delete(Guid id) => _store.Delete(id);

public TargetMutationResult Save()
{
    var definition = CurrentDefinition;
    return Mode == CaptureDraftMode.UpdateExisting
        ? _store.Update(definition)
        : _store.Add(definition);
}
```

`Save` remains unused by the form until Task 7; keeping it in the model makes the one-mutation contract directly testable.

- [ ] **Step 7: Run focused and full tests**

Run: `dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter FullyQualifiedName~TargetCaptureWorkspaceModelTests --nologo`

Expected: all workspace model tests pass.

Run: `dotnet test UsageBars.sln --nologo`

Expected: all tests pass.

- [ ] **Step 8: Commit the workspace-model slice**

```powershell
git add src/TargetStore.cs src/TargetViewModels.cs tests/UsageBars.Tests/TargetCaptureWorkspaceModelTests.cs
git commit -m "feat: model rule-aware target capture workspace"
```

---

### Task 5: Preview applicability and isolated pill lifecycle

**Files:**
- Create: `src/TargetPreviewSession.cs`
- Modify: `src/ProviderPill.cs`
- Modify: `src/OverlayView.cs`
- Create: `tests/UsageBars.Tests/TargetPreviewSessionTests.cs`
- Modify: `tests/UsageBars.Tests/TargetResolverTests.cs`

**Interfaces:**
- Consumes: captured `WindowSnapshot`, draft `TargetDefinition`, `TargetResolver.Resolve`, `ProviderPill`, `UsageModel`, `Config`.
- Produces: `TargetPreviewState`, `TargetPreviewEvaluation`, `TargetPreviewEvaluator.Evaluate`, `ITargetPreviewPill`, `ITargetPreviewPillFactory`, and `TargetPreviewSession`.

- [ ] **Step 1: Write failing applicability-reason tests**

```csharp
[Theory]
[InlineData(false, TargetPreviewState.Dimmed, "disabled")]
[InlineData(true, TargetPreviewState.Dimmed, "title")]
public void NonapplicableDraftStaysVisibleWithActionableReason(
    bool enabled, TargetPreviewState expected, string reasonFragment)
{
    var evaluation = TargetPreviewEvaluator.Evaluate(
        MismatchingDraft(enabled), Window(), EmptyTree(), EnabledConfig(), OwnPid);

    Assert.Equal(expected, evaluation.State);
    Assert.Contains(reasonFragment, evaluation.Reason, StringComparison.OrdinalIgnoreCase);
    Assert.NotEmpty(evaluation.PreviewProviders);
}

[Fact]
public void DraftWithoutProvidersUsesNeutralPreview()
{
    var evaluation = TargetPreviewEvaluator.Evaluate(
        NoProviderDraft(), Window(), EmptyTree(), EnabledConfig(), OwnPid);

    Assert.Equal(TargetPreviewState.Neutral, evaluation.State);
    Assert.Empty(evaluation.PreviewProviders);
}
```

- [ ] **Step 2: Run preview tests and verify RED**

Run: `dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter FullyQualifiedName~TargetPreviewSessionTests --nologo`

Expected: compilation fails because preview evaluation and session types do not exist.

- [ ] **Step 3: Implement pure evaluation using production matching/activity rules**

```csharp
internal enum TargetPreviewState { Normal, Dimmed, Neutral, WindowClosed, Failed }

internal sealed record TargetPreviewEvaluation(
    TargetPreviewState State,
    string? Reason,
    IReadOnlyList<ProviderId> PreviewProviders);

internal static class TargetPreviewEvaluator
{
    public static TargetPreviewEvaluation Evaluate(
        TargetDefinition draft, WindowSnapshot window, ProcessTree tree, Config config, uint ownPid)
    {
        var providers = draft.Providers.Select(binding => binding.Provider).Distinct().ToList();
        if (providers.Count == 0) return new(TargetPreviewState.Neutral, null, providers);
        if (!draft.Enabled) return new(TargetPreviewState.Dimmed, "The draft target is disabled.", providers);
        if (!draft.Matches(window)) return new(TargetPreviewState.Dimmed, FirstMatcherFailure(draft, window), providers);
        var resolved = TargetResolver.Resolve(new[] { window }, new[] { draft }, tree, config, ownPid);
        return resolved.Count == providers.Count
            ? new(TargetPreviewState.Normal, null, providers)
            : new(TargetPreviewState.Dimmed, MissingActivityReason(draft, resolved), providers);
    }
}
```

The failure order is disabled, process, class, title, resize requirement, global provider disabled, then missing selected activity process.

- [ ] **Step 4: Add failing lifecycle, latest-update, and idempotent-dispose tests**

```csharp
[Fact]
public void SessionReplacesPreviewPillsAndDisposesExactlyOnce()
{
    var factory = new RecordingPreviewPillFactory();
    using var session = NewSession(factory);

    session.Update(ClaudeDraft());
    var first = Assert.Single(factory.Live);
    session.Update(CodexDraft());

    Assert.Equal(1, first.DisposeCount);
    Assert.Equal(ProviderId.Codex, Assert.Single(factory.Live.Where(x => !x.IsDisposed)).Provider);
    session.Dispose();
    session.Dispose();
    Assert.All(factory.Created, pill => Assert.Equal(1, pill.DisposeCount));
}

[Fact]
public void SessionNeverChangesSuppressedHandle()
{
    using var session = NewSession(new RecordingPreviewPillFactory(), hwnd: new IntPtr(77));
    session.Update(ClaudeDraft());

    Assert.True(session.Suppresses(new IntPtr(77)));
    Assert.False(session.Suppresses(new IntPtr(78)));
}
```

Add these deterministic fixtures to `TargetPreviewSessionTests`:

```csharp
private const uint OwnPid = 999;

private static WindowSnapshot Window() =>
    new(new IntPtr(42), 7, "terminal", "frame", "Codex", true);

private static ProcessTree EmptyTree() =>
    ProcessTree.FromMap(new[] { (7u, 1u, "terminal") });

private static Config EnabledConfig() => new() { EnableClaude = true, EnableCodex = true };

private static TargetDefinition BaseDraft(params ProviderBinding[] providers) =>
    TargetDefinition.Captured("Draft", "terminal", "frame", providers,
        TargetPlacementKind.Terminal, TargetActivationPolicy.AlwaysWhileOpen, true, 0);

private static TargetDefinition ClaudeDraft() => BaseDraft(ProviderBinding.Claude);
private static TargetDefinition CodexDraft() => BaseDraft(ProviderBinding.Codex);
private static TargetDefinition NoProviderDraft() => BaseDraft();

private static TargetDefinition MismatchingDraft(bool enabled) =>
    BaseDraft(ProviderBinding.Codex) with
    {
        Enabled = enabled,
        WindowTitle = new(TitleMatchKind.Literal, "Different title")
    };

private static TargetPreviewSession NewSession(
    RecordingPreviewPillFactory factory, IntPtr? hwnd = null) =>
    new(Window() with { Hwnd = hwnd ?? Window().Hwnd }, factory,
        _ => new UsageModel { Bars = new[] { new UsageBar("test", 50) } },
        EnabledConfig(), EmptyTree, () => new List<IntPtr> { hwnd ?? Window().Hwnd });
```

Add `RecordingPreviewPillFactory : ITargetPreviewPillFactory` and `RecordingPreviewPill : ITargetPreviewPill` in the same test file. The factory appends every created pill to `Created`, exposes `Live` as `Created.Where(pill => !pill.IsDisposed)`, and records a nullable `ProviderId`; each pill increments `DisposeCount` only on its first disposal and records the latest host/model/dimmed values passed to `Update`.

- [ ] **Step 5: Implement preview pill abstraction and session**

```csharp
internal interface ITargetPreviewPill : IDisposable
{
    ProviderId? Provider { get; }
    void Update(HostWindowInfo host, UsageModel model, bool dimmed, List<IntPtr> zOrder, Config config);
}

internal interface ITargetPreviewPillFactory
{
    ITargetPreviewPill Create(ProviderId? provider, HostWindowInfo host);
}

internal interface ITargetPreviewSession : IDisposable
{
    IntPtr Hwnd { get; }
    TargetPreviewEvaluation Evaluation { get; }
    bool Suppresses(IntPtr hwnd);
    void Update(TargetDefinition draft);
    void WindowClosed();
}
```

Implement `internal sealed class TargetPreviewSession : ITargetPreviewSession` with constructor `TargetPreviewSession(WindowSnapshot window, ITargetPreviewPillFactory factory, Func<ProviderId, UsageModel> modelFor, Config config, Func<ProcessTree> processTree, Func<List<IntPtr>> zOrder)`, readonly constructor fields, and a `Dictionary<ProviderId?, ITargetPreviewPill>`. Its methods use these exact transitions: `Update` stores the latest draft, evaluates it, disposes pills whose nullable provider key is no longer requested, creates missing provider or neutral pills through the factory, and updates every remaining pill with the latest host geometry/model/dim state; `WindowClosed` disposes all pills once and sets `Evaluation` to `WindowClosed`; `Dispose` sets `_disposed`, disposes every live pill once, clears the dictionary, and ignores later calls. The production factory wraps `ProviderPill`; a provider preview uses the real `ModelFor(provider)`. A neutral preview uses `new UsageModel { Bars = new[] { new UsageBar("Preview", null) }, Stale = true }`. Add `ProviderPill.SetPreviewDimmed(bool?)` and `OverlayView.Update(..., bool? dimmedOverride = null)` so mismatch dimming is explicit and normal pills retain stale/empty opacity behavior.

- [ ] **Step 6: Run focused and full tests**

Run: `dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter "FullyQualifiedName~TargetPreviewSessionTests|FullyQualifiedName~TargetResolverTests" --nologo`

Expected: all preview and resolver tests pass.

Run: `dotnet test UsageBars.sln --nologo`

Expected: all tests pass.

- [ ] **Step 7: Commit the preview slice**

```powershell
git add src/TargetPreviewSession.cs src/ProviderPill.cs src/OverlayView.cs tests/UsageBars.Tests/TargetPreviewSessionTests.cs tests/UsageBars.Tests/TargetResolverTests.cs
git commit -m "feat: preview unsaved targets on selected windows"
```

---

### Task 6: Two-pane rule-aware capture form

**Files:**
- Create: `src/TargetCaptureForm.cs`
- Create: `tests/UsageBars.Tests/TargetCaptureFormModelTests.cs`
- Modify: `src/TargetEditorControl.cs`

**Interfaces:**
- Consumes: `TargetCaptureWorkspaceModel`, `TargetEditorControl`, `ProcessInventory`, `TargetRuleImpact`.
- Produces: `TargetCaptureForm.EditedTarget`, `DraftChanged`, `PreviewStatus`, and a modal OK/Cancel result without directly committing editor drafts.

- [ ] **Step 1: Write failing grouping and display-projection tests**

```csharp
[Fact]
public void SidebarGroupsAppliesOverlapsAndSearchOnlyWithoutDroppingDisabledRules()
{
    var projection = TargetCaptureSidebarProjection.Build(ImpactRows());

    Assert.Contains(projection.Applies, row => row.Target.Name == "Disabled exact" && !row.Target.Enabled);
    Assert.Contains(projection.Overlaps, row => row.Target.Name == "Title overlap");
    Assert.Contains(projection.OtherSearchResults, row => row.Target.Name == "Search result");
}

[Fact]
public void RuleDisplayIncludesNameShortIdProvidersStateAndRelationship()
{
    var item = TargetCaptureRuleItem.From(ImpactRow());

    Assert.Contains("Rider", item.PrimaryText);
    Assert.Contains(ImpactRow().Target.Id.ToString("N")[..8], item.SecondaryText);
    Assert.Contains("Claude", item.SecondaryText);
    Assert.Contains("disabled", item.SecondaryText, StringComparison.OrdinalIgnoreCase);
}
```

Add these literal fixtures to `TargetCaptureFormModelTests`:

```csharp
private static TargetDefinition Rule(string name, bool enabled = true) =>
    TargetDefinition.Captured(name, "rider64", "SunAwtFrame", new[] { ProviderBinding.Claude },
        TargetPlacementKind.Ide, TargetActivationPolicy.AlwaysWhileOpen, false, 26) with
    { Enabled = enabled };

private static TargetRuleImpact ImpactRow() => new(
    Rule("Rider", enabled: false), TargetRuleRelationship.ExactIdentity,
    ExactIdentity: true, AppliesNow: true, OverlapsDraft: false, SearchMatch: true);

private static IReadOnlyList<TargetRuleImpact> ImpactRows() => new[]
{
    ImpactRow() with { Target = Rule("Disabled exact", enabled: false) },
    new TargetRuleImpact(Rule("Title overlap"), TargetRuleRelationship.OverlapsDraft,
        false, false, true, false),
    new TargetRuleImpact(Rule("Search result"), TargetRuleRelationship.SearchOnly,
        false, false, false, true),
};
```

- [ ] **Step 2: Run form-model tests and verify RED**

Run: `dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter FullyQualifiedName~TargetCaptureFormModelTests --nologo`

Expected: compilation fails because sidebar projection types do not exist.

- [ ] **Step 3: Implement testable sidebar and preview-banner projections**

```csharp
internal sealed record TargetCaptureSidebarProjection(
    IReadOnlyList<TargetRuleImpact> Applies,
    IReadOnlyList<TargetRuleImpact> Overlaps,
    IReadOnlyList<TargetRuleImpact> OtherSearchResults)
{
    public static TargetCaptureSidebarProjection Build(IReadOnlyList<TargetRuleImpact> rows) => new(
        rows.Where(row => row.ExactIdentity || row.AppliesNow).ToList(),
        rows.Where(row => !row.AppliesNow && row.OverlapsDraft).ToList(),
        rows.Where(row => row.Relationship == TargetRuleRelationship.SearchOnly).ToList());
}
```

Add `TargetCaptureRuleItem` and `TargetPreviewStatusText.For(TargetPreviewEvaluation, IntPtr)` with concise, actionable text for all preview states.

- [ ] **Step 4: Implement the resizable two-pane WinForms workspace**

```csharp
internal sealed class TargetCaptureForm : Form
{
    public event Action<TargetDefinition>? DraftChanged;
    public TargetDefinition? EditedTarget { get; private set; }
    public void SetPreviewStatus(TargetPreviewEvaluation evaluation) =>
        _previewStatus.Text = TargetPreviewStatusText.For(evaluation, _window.Hwnd);
    public void SetInventory(ProcessInventory inventory) { _editor.SetInventory(inventory); }
}
```

Use a `SplitContainer` with minimum client size 1040×680. The left pane contains search, grouped `ListView`/`TreeView` rows, Edit, Enable/Disable, confirmed Delete, and Create more-specific rule. The right pane contains the preview banner and shared editor. Tooltips expose untruncated rule identity details. A two-second WinForms timer invokes an injected inventory refresh callback; failures retain old data and show a nonblocking warning. Save validates and sets `EditedTarget` but does not call `TargetStore`; Cancel closes without setting it.

- [ ] **Step 5: Add a layout construction smoke test and run it**

```csharp
[Fact]
public void CaptureWorkspaceHonorsMinimumPaneAndEditorWidths()
{
    using var form = CreateCaptureForm();
    form.ClientSize = form.MinimumSize;
    form.PerformLayout();

    Assert.True(form.RulePaneWidth >= 320);
    Assert.True(form.EditorPaneWidth >= 620);
    Assert.True(form.EditorOptionsAreReadable);
}
```

`CreateCaptureForm` constructs a `RecordingTargetRuleStore` containing `Rule("Rider")`, a captured `WindowSnapshot(new IntPtr(42), 7, "rider64", "SunAwtFrame", "Project", true)`, a `TargetDraft` for that rule, a `TargetCaptureWorkspaceModel`, and finally `new TargetCaptureForm(model, () => ProcessInventory.Empty)`. Expose `RulePaneWidth`, `EditorPaneWidth`, and `EditorOptionsAreReadable` as the same production layout measurements used when enforcing the split-panel minimum widths.

Run: `dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter FullyQualifiedName~TargetCaptureFormModelTests --nologo`

Expected: all capture form/model tests pass.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test UsageBars.sln --nologo`

Expected: all tests pass.

- [ ] **Step 7: Commit the capture-form slice**

```powershell
git add src/TargetCaptureForm.cs src/TargetEditorControl.cs tests/UsageBars.Tests/TargetCaptureFormModelTests.cs
git commit -m "feat: add rule-aware target capture workspace"
```

---

### Task 7: Overlay-controller integration, save/cancel handoff, and diagnostics

**Files:**
- Modify: `src/OverlayController.cs`
- Create: `tests/UsageBars.Tests/TargetCaptureFlowTests.cs`
- Modify: `tests/UsageBars.Tests/TargetViewModelTests.cs`

**Interfaces:**
- Consumes: `TargetCaptureForm`, `TargetCaptureWorkspaceModel`, `TargetPreviewSession`, existing `_pills`, `Track`, `TargetStore`.
- Produces: a single active capture session, HWND-scoped normal-pill suppression, immediate preview after capture, live updates, and exact add/update/cancel handoff.

- [ ] **Step 1: Write failing pure flow-coordinator tests**

```csharp
[Fact]
public void SavePerformsExactlyOneMutationThenDisposesPreviewAndTracks()
{
    var harness = CaptureFlowHarness.ForExistingRule();

    harness.Flow.Save(harness.EditedDefinition, CaptureDraftMode.UpdateExisting);

    Assert.Equal(0, harness.Store.AddCalls);
    Assert.Equal(1, harness.Store.UpdateCalls);
    Assert.Equal(1, harness.Preview.DisposeCalls);
    Assert.Equal(1, harness.TrackCalls);
}

[Fact]
public void CancelDoesNotMutateStoreAndRestoresNormalTracking()
{
    var harness = CaptureFlowHarness.ForNewRule();

    harness.Flow.Cancel();

    Assert.Equal(0, harness.Store.AddCalls + harness.Store.UpdateCalls);
    Assert.Equal(1, harness.Preview.DisposeCalls);
    Assert.Equal(1, harness.TrackCalls);
}

[Fact]
public void WantedNormalPillsAreSuppressedOnlyForPreviewHandle()
{
    var wanted = Wanted(new IntPtr(10), new IntPtr(20));

    TargetPreviewSuppression.RemoveCapturedWindow(wanted, new IntPtr(10));

    Assert.DoesNotContain(wanted.Keys, key => key.Hwnd == new IntPtr(10));
    Assert.Contains(wanted.Keys, key => key.Hwnd == new IntPtr(20));
}
```

Use this exact flow constructor so tests need only fakes at the external boundaries:

```csharp
internal TargetCaptureFlow(
    ITargetRuleStore store,
    ITargetPreviewSession preview,
    Action track,
    Action<string, Exception?> report)
```

`CaptureFlowHarness` owns a `RecordingTargetRuleStore`, a `RecordingPreviewSession`, integer `TrackCalls`, and a valid captured `EditedDefinition`. `ForExistingRule` seeds the store with the same ID; `ForNewRule` uses an empty store. The recording store exposes `AddCalls` and `UpdateCalls`; the preview exposes `DisposeCalls` and implements idempotent disposal. Each test passes the current `CaptureDraftMode` to `Save`, so switching to Create more-specific after the form opens cannot use a stale mode captured by the flow constructor.

Define `Wanted(params IntPtr[] handles)` as a dictionary containing one Claude `HostWindowInfo` per handle, with `HostKind.Terminal`, `HostEvidence.Open`, `Guid.NewGuid()`, `PillAnchor.TopRight`, and zero status height/offsets. `TargetPreviewSuppression.RemoveCapturedWindow` removes every `(ProviderId, Hwnd)` key whose HWND equals the captured handle and leaves all other entries untouched.

- [ ] **Step 2: Run flow tests and verify RED**

Run: `dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter FullyQualifiedName~TargetCaptureFlowTests --nologo`

Expected: compilation fails because the capture flow coordinator and suppression helper do not exist.

- [ ] **Step 3: Implement a testable flow boundary**

```csharp
internal sealed class TargetCaptureFlow : IDisposable
{
    public TargetMutationResult Save(TargetDefinition edited, CaptureDraftMode mode)
    {
        var result = mode == CaptureDraftMode.UpdateExisting
            ? _store.Update(edited)
            : _store.Add(edited);
        if (result.Success) Finish("saved");
        return result;
    }

    public void Cancel() => Finish("cancelled");

    private void Finish(string reason)
    {
        if (_finished) return;
        _finished = true;
        _preview.Dispose();
        _track();
        _report(reason, null);
    }
}
```

The real flow owns the model/form subscription lifetime and also finishes on form close, controller shutdown, or unexpected editor failure.

- [ ] **Step 4: Replace `BeginAddTarget`’s old editor/add/duplicate loop**

After successful capture:

```csharp
var inferred = ProviderInference.CreateDraft(
    capture.Window!, _targets.Targets, ProcessTreeCache.Current(), _config, _ownProcessId,
    capture.TopLevelWindows);
var workspace = new TargetCaptureWorkspaceModel(capture.Window!, inferred, _targets);
using var preview = CreatePreviewSession(capture.Window!);
preview.Update(workspace.CurrentDefinition);
using var form = new TargetCaptureForm(workspace, RefreshInventory);
form.DraftChanged += preview.Update;
var dialogResult = ShowTargetCapture(form);
```

If OK, call `flow.Save(form.EditedTarget!, workspace.Mode)` exactly once. If persistence fails, keep the form open and show the error banner rather than discarding the draft. If Cancel/close, call Cancel. Remove the post-Save exact-duplicate prompt because applicable/exact rules are handled before editing.

- [ ] **Step 5: Apply HWND-scoped suppression in normal tracking**

```csharp
var wanted = _targetResolver.FindAll(candidates, _targets.Targets, _config)
    .ToDictionary(host => (host.Provider, host.Hwnd));
if (_targetPreview is { } preview)
    TargetPreviewSuppression.RemoveCapturedWindow(wanted, preview.Hwnd);
```

Track/reposition the preview after normal pills so it follows host movement. If the captured HWND disappears from top-level candidates, call `WindowClosed` once and update the form banner without closing the editor.

- [ ] **Step 6: Add state-change-only diagnostics and complete cleanup paths**

Record capture open/close reason, preview state transitions, impact counts, inventory failure/recovery, sidebar mutation failure, and save failure. Do not log command lines. In `OverlayController.Dispose`, dispose/close the capture flow before stopping normal tracking and before disposing the shared provider models.

- [ ] **Step 7: Run focused, shutdown-regression, and full tests**

Run: `dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter "FullyQualifiedName~TargetCaptureFlowTests|FullyQualifiedName~Shutdown|FullyQualifiedName~TargetViewModelTests" --nologo`

Expected: capture handoff and existing shutdown regression tests pass.

Run: `dotnet test UsageBars.sln --nologo`

Expected: all tests pass.

- [ ] **Step 8: Commit controller integration**

```powershell
git add src/OverlayController.cs tests/UsageBars.Tests/TargetCaptureFlowTests.cs tests/UsageBars.Tests/TargetViewModelTests.cs
git commit -m "feat: integrate live rule-aware target capture"
```

---

### Task 8: Documentation, publish, and Windows UI smoke verification

**Files:**
- Modify: `README.md`
- Modify: `docs/superpowers/specs/2026-08-23-rule-aware-live-target-capture-design.md` only if implementation reveals a factual correction

**Interfaces:**
- Consumes: completed feature and existing `build.ps1`.
- Produces: documented user workflow and a verified self-contained executable.

- [ ] **Step 1: Update user documentation with exact behavior**

Document that clicking a window opens a searchable Applies/Overlaps/Other workspace; exact or strongest applicable rules are edited by default; Create more-specific makes a new rule; Disable/Delete are immediate; Save/Cancel govern only the right-hand draft; process choices are owner-scoped and may include sibling Terminal windows; and preview changes affect only the clicked HWND.

- [ ] **Step 2: Run the complete automated suite from a clean build**

Run: `dotnet clean UsageBars.sln --nologo`

Run: `dotnet test UsageBars.sln --nologo`

Expected: all tests pass with zero failures.

- [ ] **Step 3: Publish without relaunching**

Run: `pwsh -File build.ps1 -NoRelaunch`

Expected: `dist\UsageBars.exe` is produced and the script reports a successful self-contained win-x64 publish.

- [ ] **Step 4: Exercise the complete capture workflow manually**

Start the published executable, then verify:

1. Right-click tray → Targets → Add by clicking a window.
2. Clicking an already covered window immediately displays a pill and opens the two-pane workspace.
3. The strongest matching rule is selected; search finds a rule by full/partial ID and by name.
4. Applies, Overlaps, and Other search groups are correct, including disabled rules.
5. Anchor, X, Y, placement, provider, activation, and selected activity process move/dim/update the pill without changing another matching window.
6. The process heading says “all tabs/panes under this window owner”; sibling-owner warning appears when applicable.
7. `OpenConsole` and `conhost` appear in the tree but not normal pickers until Show system helpers is enabled.
8. “Require resizable frame” is fully readable at 100%, 125%, 150%, and 200% display scaling.
9. Cancel restores the saved pill and leaves `targets.json` byte-for-byte unchanged.
10. Save performs one update or add and transitions without a visible jump.
11. Disable and confirmed Delete persist immediately.
12. Closing the selected window removes preview but leaves Save available.

- [ ] **Step 5: Restart regression check**

Exit from the tray, confirm the process terminates, launch `dist\UsageBars.exe` again, and verify saved pills return. This explicitly covers the prior shutdown/rebuild regression.

- [ ] **Step 6: Inspect repository state and commit documentation**

Run: `git status --short`

Expected: only intentional documentation or factual-spec corrections are uncommitted; `dist`, `bin`, `obj`, runtime data, and diagnostics remain ignored.

```powershell
git add README.md docs/superpowers/specs/2026-08-23-rule-aware-live-target-capture-design.md
git commit -m "docs: explain rule-aware target capture"
```

- [ ] **Step 7: Final verification evidence**

Run: `dotnet test UsageBars.sln --nologo`

Run: `git status --short`

Expected: all tests pass and the feature branch is clean.
