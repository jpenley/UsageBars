# Target-Level Process Activity and Editable Title Matching Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let each saved target use an editable optional title condition and one target-level OR-list of descendant process names that activates all checked provider pills.

**Architecture:** Version 3 of `targets.json` separates provider selection from process activity and migrates version-2 provider-specific evidence through an explicit compatibility reader. The resolver evaluates window identity, then one normalized activity-process set, then emits each globally enabled checked provider. Focused WinForms controls own title editing and process checkbox/chip synchronization, while the existing manager continues to own process enumeration, manual refresh, draft persistence, and pinned live preview sessions.

**Tech Stack:** .NET 8, C#, Windows Forms, WPF-hosted tray application, System.Text.Json, Toolhelp process snapshots, xUnit, `dotnet test`, `dotnet publish`

## Global Constraints

- Window process, class, resizable-frame, and optional title conditions use AND semantics.
- Activity process names use OR semantics and are independent of provider selection.
- An empty activity list means `AlwaysWhileOpen`; a non-empty list means `RequireActivityProcess`.
- Activity process names are case-insensitive, omit a trailing `.exe`, reject paths/invalid executable base names, and persist in deterministic order.
- Every checked provider is emitted when any activity process qualifies, subject to existing global provider enablement.
- Processes in any descendant tab or pane count; UsageBars must not claim reliable process-to-tab attribution.
- When several top-level windows share one owner PID, every otherwise-matching window may activate and the editor must show a scope warning.
- The process hierarchy is captured only on draft entry/window capture and on the explicit `Refresh processes` action; timer ticks and ordinary field edits must not enumerate processes.
- Refresh preserves saved chips, expansion, selection, top node, focus, and scroll position where the corresponding nodes still exist.
- A captured window receives an unsaved preview immediately; inactive draft conditions dim the pinned preview and explain that the saved rule would currently be inactive.
- Save and Cancel remain horizontally adjacent and visible at the manager's minimum size; `Require resizable frame` remains fully readable.
- `targets.json` version 2 migrates in memory and writes version 3 atomically on the next successful mutation.
- Legacy provider activity-title patterns are not converted into window title conditions; affected migrated targets display a review warning until successfully saved.
- Preserve IDs, revisions, placement, anchor, offsets, enablement, source, status-bar height, corruption recovery, and partial-load behavior.
- Add no runtime dependencies.

---

## File Structure

- Create `src/TargetActivity.cs`: normalize and validate executable base names and sets.
- Create `src/TargetDocumentV2.cs`: version-1/version-2 compatibility DTOs and migration into the version-3 target model.
- Modify `src/WindowTargets.cs`: version-3 provider selection, target-level activity fields, activation invariant, duplicate identity, and transient migration warning.
- Modify `src/TargetStore.cs`: accept versions 1-3, invoke compatibility migration, validate version-3 required fields, and write version 3.
- Modify `src/TargetDefaults.cs`: seed process activity on terminal/editor targets without provider coupling.
- Modify `src/TargetCapture.cs`: infer providers and target-level activity names separately.
- Modify `src/HostWindow.cs`: evaluate a set of activity names beneath a window owner.
- Modify `src/TargetResolver.cs`: gate all checked providers through the same target/window activity result.
- Modify `src/TargetViewModels.cs`: expose editable title and activity state and format version-3 rule details.
- Modify `src/TargetRuleImpact.cs`: search and duplicate/overlap reporting for activity names.
- Create `src/ActivityProcessEditorControl.cs`: checkbox tree, persistent chips, manual entry, scope text, and refresh event.
- Create `src/TitleRuleEditorControl.cs`: optional Literal/Prefix/Regex editor with current-title capture and validation.
- Modify `src/TargetEditorControl.cs`: compose the focused controls, remove provider-process fields, and preserve readable layout.
- Modify `src/TargetPreviewSession.cs`: evaluate target-level activity once and report pinned inactive previews precisely.
- Modify `src/TargetPreviewSet.cs`: preserve captured-HWND pinning and status wording.
- Modify `src/TargetManagerForm.cs`: include activity in rule UI, retain manual inventory ownership, surface migration/scope warnings, and keep footer geometry stable.
- Modify `README.md` and `UsageBars.config.default.json`: document schema 3 and the new target editor semantics.
- Create `tests/UsageBars.Tests/TargetActivityTests.cs`: process normalization and validation.
- Create `tests/UsageBars.Tests/ActivityProcessEditorControlTests.cs`: checkbox/chip/manual-entry behavior and refresh stability.
- Create `tests/UsageBars.Tests/TitleRuleEditorControlTests.cs`: title editor load/edit/validation behavior.
- Modify `tests/UsageBars.Tests/TargetStoreTests.cs`: version-2 migration, version-3 round trip, warnings, and invalid data.
- Modify `tests/UsageBars.Tests/WindowTargetTests.cs`: activation invariants, identity, and overlap.
- Modify `tests/UsageBars.Tests/TargetDefaultsTests.cs`: target-level seed activity.
- Modify `tests/UsageBars.Tests/TargetCaptureTests.cs`: independent provider/activity inference.
- Modify `tests/UsageBars.Tests/ProcessInventoryTests.cs`: normalized multi-name descendant matching.
- Modify `tests/UsageBars.Tests/TargetResolverTests.cs`: shared OR gate and shared-owner behavior.
- Modify `tests/UsageBars.Tests/TargetViewModelTests.cs`: title/activity editing and details.
- Modify `tests/UsageBars.Tests/TargetRuleImpactTests.cs`: activity search and identity.
- Modify `tests/UsageBars.Tests/TargetEditorLayoutTests.cs`: integrated control layout and readable options.
- Modify `tests/UsageBars.Tests/TargetPreviewSessionTests.cs`: active/inactive all-provider previews.
- Modify `tests/UsageBars.Tests/TargetPreviewSetTests.cs`: pinned captured preview behavior.
- Modify `tests/UsageBars.Tests/TargetManagerFormTests.cs`: immediate preview, manual inventory refresh, warnings, search, and footer geometry.
- Update the constructor fixtures in `TargetCaptureTests.cs`, `TargetEditorLayoutTests.cs`,
  `TargetPreviewSessionTests.cs`, `TargetResolverTests.cs`, `TargetStoreTests.cs`,
  `TargetViewModelTests.cs`, `UsageModelTests.cs`, and `WindowTargetTests.cs` in the task that owns
  each behavior.

### Task 1: Introduce the version-3 target domain and compatibility migration

**Files:**
- Create: `src/TargetActivity.cs`
- Create: `src/TargetDocumentV2.cs`
- Modify: `src/WindowTargets.cs`
- Modify: `src/TargetStore.cs`
- Test: `tests/UsageBars.Tests/TargetActivityTests.cs`
- Test: `tests/UsageBars.Tests/TargetStoreTests.cs`
- Test: `tests/UsageBars.Tests/WindowTargetTests.cs`

**Interfaces:**
- Consumes: existing `TitleRule`, `TargetPlacementKind`, `TargetSource`, and `PillAnchor` persistence values.
- Produces: `ActivityProcessName.TryNormalize(string?, out string)`, `ActivityProcessName.NormalizeMany(IEnumerable<string>)`, `TargetActivationPolicy.RequireActivityProcess`, `ProviderBinding(ProviderId Provider)`, `TargetDefinition.ActivityProcessNames`, and `TargetDefinition.MigrationWarning`.
- Produces: `TargetDocumentV2Migration.Read(JsonElement element, int documentVersion, JsonSerializerOptions options) : MigratedTarget`, where `MigratedTarget` contains a version-3 `TargetDefinition` and optional warning.

- [ ] **Step 1: Write failing normalization and activation-invariant tests**

Create `TargetActivityTests.cs` with these cases:

```csharp
[Theory]
[InlineData(" codex.exe ", "codex")]
[InlineData("CODEX", "CODEX")]
[InlineData("code - insiders.exe", "code - insiders")]
public void NormalizeRemovesOnlyWhitespaceAndExeSuffix(string input, string expected)
{
    Assert.True(ActivityProcessName.TryNormalize(input, out var actual));
    Assert.Equal(expected, actual);
}

[Theory]
[InlineData("")]
[InlineData("C:\\tools\\codex.exe")]
[InlineData("../codex")]
[InlineData("bad:name")]
public void NormalizeRejectsBlankPathsAndInvalidBaseNames(string input) =>
    Assert.False(ActivityProcessName.TryNormalize(input, out _));

[Fact]
public void NormalizeManyDeduplicatesCaseInsensitivelyAndSorts()
{
    Assert.Equal(new[] { "codex", "omp" },
        ActivityProcessName.NormalizeMany(new[] { "omp.exe", "CODEX", "codex.exe", " omp " }));
}
```

Add `WindowTargetTests` cases proving `AlwaysWhileOpen` rejects a non-empty activity list,
`RequireActivityProcess` rejects an empty list, and identical activity sets compare equal regardless
of order, casing, or `.exe` spelling.

- [ ] **Step 2: Run the focused tests and verify the version-3 API is absent**

```powershell
dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter "FullyQualifiedName~TargetActivityTests|FullyQualifiedName~WindowTargetTests"
```

Expected: FAIL with missing `ActivityProcessName`, `RequireActivityProcess`, and
`ActivityProcessNames` members.

- [ ] **Step 3: Add the process-name value rules**

Create `src/TargetActivity.cs` with the concrete normalization boundary:

```csharp
internal static class ActivityProcessName
{
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = (value ?? "").Trim();
        if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^4].TrimEnd();
        if (normalized.Length == 0
            || normalized.IndexOfAny(new[] { '\\', '/', ':' }) >= 0
            || normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || normalized is "." or "..")
        {
            normalized = "";
            return false;
        }
        return true;
    }

    public static List<string> NormalizeMany(IEnumerable<string> values) => values
        .Select(value => TryNormalize(value, out var normalized) ? normalized : null)
        .Where(value => value is not null)
        .Select(value => value!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
        .ToList();
}
```

Use explicit `System`, `System.Collections.Generic`, `System.IO`, and `System.Linq` imports.

- [ ] **Step 4: Convert the target model to provider selections plus target activity**

In `WindowTargets.cs`, replace the provider-specific activation name and binding payload:

```csharp
internal enum TargetActivationPolicy { AlwaysWhileOpen, RequireActivityProcess }

internal sealed record ProviderBinding(ProviderId Provider)
{
    public static ProviderBinding Claude => new(ProviderId.Claude);
    public static ProviderBinding Codex => new(ProviderId.Codex);
}
```

Add these properties to `TargetDefinition`:

```csharp
public List<string> ActivityProcessNames { get; init; } = new();

[JsonIgnore]
public string? MigrationWarning { get; init; }
```

Add an optional final `IEnumerable<string>? activityProcessNames = null` argument to `Captured` and
`Seeded`, normalize it in the factory, and enforce these errors in `Validate`:

```csharp
if (Activation == TargetActivationPolicy.AlwaysWhileOpen && ActivityProcessNames.Count != 0)
    errors.Add("always-open targets cannot have activity processes");
if (Activation == TargetActivationPolicy.RequireActivityProcess && ActivityProcessNames.Count == 0)
    errors.Add("process-activated targets require at least one activity process");
if (ActivityProcessName.NormalizeMany(ActivityProcessNames).Count != ActivityProcessNames.Count)
    errors.Add("activity processes must be valid and unique");
```

Normalize into a case-insensitive sorted sequence before `HasSameIdentity`; compare the sequence
case-insensitively. Keep `Overlaps` conservative whenever window spaces and providers overlap.

- [ ] **Step 5: Write failing version-2 migration and version-3 round-trip tests**

Add `TargetStoreTests` named:

- `VersionTwoProviderActivityMigratesToOneTargetLevelOrList`
- `VersionTwoAlwaysOpenMigratesWithNoActivityProcesses`
- `VersionTwoActivityTitlePatternProducesReviewWarningWithoutBecomingWindowTitle`
- `FirstMutationWritesMigratedTargetsAsVersionThreeWithoutLegacyProviderFields`
- `VersionThreeRoundTripPreservesTitleAndActivityProcesses`
- `VersionThreeRejectsMissingActivityProcessCollection`

The primary migration assertion should use a version-2 target with Claude `claude.exe`, Codex
`codex`, `Activation:"RequireProviderActivity"`, and assert:

```csharp
var target = Assert.Single(store.Targets);
Assert.Equal(TargetActivationPolicy.RequireActivityProcess, target.Activation);
Assert.Equal(new[] { "claude", "codex" }, target.ActivityProcessNames);
Assert.Equal(new[] { ProviderId.Claude, ProviderId.Codex },
    target.Providers.Select(provider => provider.Provider).Order().ToArray());
Assert.Null(target.WindowTitle);
```

After `SetEnabled`, assert serialized JSON contains `"Version": 3` and
`"ActivityProcessNames"`, and does not contain `CliProcessName`, `ActivityTitlePattern`, or
`RequireProviderActivity`.

- [ ] **Step 6: Run the migration tests and verify version 2 is still the writer**

```powershell
dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter "FullyQualifiedName~TargetStoreTests"
```

Expected: FAIL because the store still deserializes directly into the old model and writes version 2.

- [ ] **Step 7: Implement the isolated version-1/version-2 compatibility reader**

Create `TargetDocumentV2.cs` with DTOs that exactly cover the old JSON fields:

```csharp
internal sealed record ProviderBindingV2(
    ProviderId Provider, string CliProcessName, string? ActivityTitlePattern);

internal sealed record TargetDefinitionV2
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public List<ProviderBindingV2> Providers { get; init; } = new();
    public string? ProcessName { get; init; }
    public string? WindowClass { get; init; }
    public bool RequireResizableFrame { get; init; }
    public TitleRule? WindowTitle { get; init; }
    public TargetPlacementKind Placement { get; init; }
    public string Activation { get; init; } = "AlwaysWhileOpen";
    public TargetSource Source { get; init; }
    public double StatusBarHeight { get; init; }
    public long Revision { get; init; }
    public PillAnchor Anchor { get; init; }
    public int OffsetX { get; init; }
    public int OffsetY { get; init; }
}

internal sealed record MigratedTarget(TargetDefinition Definition, string? Warning);
```

`TargetDocumentV2Migration.Read` must deserialize this DTO, apply placement-default anchors only for
document version 1, map providers to distinct `ProviderBinding` values, map activation exactly, and
set the activity union only for `RequireProviderActivity`. If any legacy activity title pattern is
nonblank, set both `MigratedTarget.Warning` and `Definition.MigrationWarning` to:

```text
Migrated provider-specific title activity to process-only activation; review this target.
```

Reject unknown legacy activation strings rather than treating them as always-open.

- [ ] **Step 8: Route store loading and writing through schema version 3**

In `TargetStore` set `CurrentVersion = 3`, keep `OldestSupportedVersion = 1`, and branch per element:

```csharp
var migrated = documentVersion < CurrentVersion
    ? TargetDocumentV2Migration.Read(element, documentVersion, ReadOptions)
    : new MigratedTarget(
        element.Deserialize<TargetDefinition>(ReadOptions)
            ?? throw new JsonException("target is null"),
        null);
var target = migrated.Definition;
if (migrated.Warning is not null)
    issues.Add($"target {target.Id}: {migrated.Warning}");
```

For version 3, require `Anchor`, `ActivityProcessNames`, and the provider entries' `Provider`
properties, and reject a JSON null activity collection before calling model validation. Track
nonfatal migration warnings separately from invalid-entry failures: expose them through `Issues`, but
do not set `_preserveBeforeWrite = "invalid"` merely because a migration warning exists. Clear
`MigrationWarning` in `Add` and after a successful `Update`, but preserve it when a write fails.
Continue clamping offsets and preserving genuinely invalid/corrupt sources exactly as today.

- [ ] **Step 9: Update domain and store fixtures, then run their tests**

Replace test-only `new ProviderBinding(provider, process, pattern)` calls with
`new ProviderBinding(provider)` and pass process activity through the new final factory argument.
Replace `RequireProviderActivity` with `RequireActivityProcess` only when the fixture supplies at
least one activity name.

```powershell
dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter "FullyQualifiedName~TargetActivityTests|FullyQualifiedName~TargetStoreTests|FullyQualifiedName~WindowTargetTests"
```

Expected: PASS.

- [ ] **Step 10: Commit the version-3 domain and migration**

```powershell
git add src/TargetActivity.cs src/TargetDocumentV2.cs src/WindowTargets.cs src/TargetStore.cs tests/UsageBars.Tests/TargetActivityTests.cs tests/UsageBars.Tests/TargetStoreTests.cs tests/UsageBars.Tests/WindowTargetTests.cs
git commit -m "feat: migrate targets to activity schema v3"
```

### Task 2: Resolve and infer target-level activity independently of providers

**Files:**
- Modify: `src/HostWindow.cs`
- Modify: `src/TargetResolver.cs`
- Modify: `src/TargetDefaults.cs`
- Modify: `src/TargetCapture.cs`
- Test: `tests/UsageBars.Tests/ProcessInventoryTests.cs`
- Test: `tests/UsageBars.Tests/TargetResolverTests.cs`
- Test: `tests/UsageBars.Tests/TargetDefaultsTests.cs`
- Test: `tests/UsageBars.Tests/TargetCaptureTests.cs`
- Test: `tests/UsageBars.Tests/UsageModelTests.cs`

**Interfaces:**
- Consumes: `TargetDefinition.ActivityProcessNames` and `TargetActivationPolicy.RequireActivityProcess` from Task 1.
- Produces: `ProcessTree.HasAnyProcessUnder(uint hostPid, IReadOnlyCollection<string> processNames, uint excludePid) : bool`.
- Produces: target inference where provider selections and activity names are separate outputs.

- [ ] **Step 1: Write failing resolver tests for OR activation and all-provider output**

Add these focused `TargetResolverTests`:

```csharp
[Fact]
public void EitherActivityProcessEmitsEveryCheckedProvider()
{
    var target = TargetDefinition.Captured(
        "Terminal", "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS",
        new[] { ProviderBinding.Claude, ProviderBinding.Codex },
        TargetPlacementKind.Desktop, TargetActivationPolicy.RequireActivityProcess, true, 0,
        new[] { "omp", "codex" });
    var window = new WindowSnapshot(
        new IntPtr(42), 10, "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS", "", true);
    var tree = ProcessTree.FromMap(new[]
    {
        (10u, 1u, "WindowsTerminal"),
        (11u, 10u, "pwsh"),
        (12u, 11u, "omp.exe"),
    });

    var hosts = TargetResolver.Resolve(new[] { window }, new[] { target }, tree,
        new Config { EnableClaude = true, EnableCodex = true }, 999u);

    Assert.Equal(new[] { ProviderId.Claude, ProviderId.Codex },
        hosts.Select(host => host.Provider).Order().ToArray());
}
```

Also add tests named `UnrelatedOwnerDoesNotSatisfyActivity`,
`DeepDescendantActivityIsNotLimitedToLegacyHostDepth`, `EmptyAlwaysOpenTargetNeedsNoProcess`, and
`SharedOwnerWindowsBothReceiveAllCheckedProviders`.

- [ ] **Step 2: Run resolver/process tests and verify provider-specific logic fails**

```powershell
dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter "FullyQualifiedName~TargetResolverTests|FullyQualifiedName~ProcessInventoryTests"
```

Expected: FAIL because `EvidenceFor` still reads `binding.CliProcessName` and the process tree lacks a
set-based deep-descendant query.

- [ ] **Step 3: Add one set-based descendant query**

In `ProcessTree`, add:

```csharp
public bool HasAnyProcessUnder(
    uint hostPid,
    IReadOnlyCollection<string> processNames,
    uint excludePid)
{
    var normalized = ActivityProcessName.NormalizeMany(processNames)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    return normalized.Count != 0
        && normalized.Any(name => HasProcessUnder(hostPid, name, excludePid, maxDepth: 64));
}
```

This reuses the existing `NotAHost`, cycle, and own-process safeguards while lifting only the old
three-level host-crediting limit for explicit user-selected activity filters.

- [ ] **Step 4: Gate providers through the same target/window decision**

Replace provider-specific evidence in `TargetResolver` with:

```csharp
private static HostEvidence? EvidenceFor(
    TargetDefinition target, WindowSnapshot window, ProcessTree tree, uint ownPid)
{
    if (target.Activation == TargetActivationPolicy.AlwaysWhileOpen)
        return target.Placement == TargetPlacementKind.Desktop
            ? HostEvidence.Desktop
            : HostEvidence.Open;

    return tree.HasAnyProcessUnder(window.Pid, target.ActivityProcessNames, ownPid)
        ? HostEvidence.Ancestry
        : null;
}
```

Compute it once for each matching target/window pair, then emit a host for every globally enabled
`target.Providers` entry. Remove `SafeTitleMatch` from the runtime resolver; optional title matching
already belongs to `TargetDefinition.Matches`.

- [ ] **Step 5: Write failing default and capture-inference tests**

Update/add tests proving:

- seeded Claude terminal targets select Claude and have activity `claude`;
- seeded Codex terminal targets select Codex and have activity `codex`;
- an IDE configured always-open has no activity processes;
- a process-activated two-provider IDE has `claude OR codex`;
- detecting only `omp` is not inferred as a provider, while detected Claude/Codex processes become
  both provider selections and target-level activity names;
- an existing always-open matching rule preserves its empty activity list;
- provider title evidence may help initial detection but is not persisted as an activity-title rule.

- [ ] **Step 6: Update defaults and capture inference**

Seed terminal/editor activity through the factory's final argument:

```csharp
TargetDefinition.Seeded(
    $"Claude terminal ({className})", null, className,
    new[] { ProviderBinding.Claude }, TargetPlacementKind.Terminal,
    TargetActivationPolicy.RequireActivityProcess, false, 0,
    new[] { "claude" })
```

In `ProviderInference`, use a private detection value instead of provider bindings as evidence:

```csharp
private sealed record ProviderDetection(
    ProviderId Provider, string ProcessName, string? ActivityTitlePattern);
```

Collect detected providers with `new ProviderBinding(detection.Provider)` and collect activity names
with `ActivityProcessName.NormalizeMany(detections.Select(x => x.ProcessName))`. Pass both lists to
`Draft`. Existing template matches contribute their already-saved provider and activity selections.

- [ ] **Step 7: Run engine, defaults, and inference tests**

```powershell
dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter "FullyQualifiedName~ProcessInventoryTests|FullyQualifiedName~TargetResolverTests|FullyQualifiedName~TargetDefaultsTests|FullyQualifiedName~TargetCaptureTests|FullyQualifiedName~UsageModelTests"
```

Expected: PASS.

- [ ] **Step 8: Commit the target-level runtime**

```powershell
git add src/HostWindow.cs src/TargetResolver.cs src/TargetDefaults.cs src/TargetCapture.cs tests/UsageBars.Tests/ProcessInventoryTests.cs tests/UsageBars.Tests/TargetResolverTests.cs tests/UsageBars.Tests/TargetDefaultsTests.cs tests/UsageBars.Tests/TargetCaptureTests.cs tests/UsageBars.Tests/UsageModelTests.cs
git commit -m "feat: resolve target activity independently"
```

### Task 3: Expose editable title and activity state throughout models and rule management

**Files:**
- Modify: `src/TargetViewModels.cs`
- Modify: `src/TargetRuleImpact.cs`
- Modify: `src/TargetManagerForm.cs`
- Modify: `src/TargetStore.cs`
- Test: `tests/UsageBars.Tests/TargetViewModelTests.cs`
- Test: `tests/UsageBars.Tests/TargetRuleImpactTests.cs`
- Test: `tests/UsageBars.Tests/TargetManagerFormTests.cs`
- Test: `tests/UsageBars.Tests/DiagnosticsAndStartupTests.cs`

**Interfaces:**
- Consumes: version-3 `TargetDefinition` from Task 1.
- Produces: mutable `TargetEditorModel.TitleMatcherEnabled`, `TitleKind`, `TitleValue`, and `ActivityProcessNames`.
- Produces: `TargetEditorModel.SetActivityProcesses(IEnumerable<string>)` and normalized version-3 `ToDefinition()` output.
- Produces: rule text/search/diagnostics containing `activity=always` or `activity=name1|name2`.

- [ ] **Step 1: Write failing editor-model tests**

Add tests named `ExistingTitleRuleCanBeEditedAndCleared`,
`ActivityProcessesRoundTripIndependentlyOfProviders`,
`ProcessActivationRequiresAValidActivityName`, and
`ApplyDefinitionRefreshesTitleAndActivityFields`.

The central edit assertion is:

```csharp
var model = TargetEditorModel.FromExisting(target);
model.TitleMatcherEnabled = true;
model.TitleKind = TitleMatchKind.Prefix;
model.TitleValue = "WorldGen";
model.SetActivityProcesses(new[] { "omp.exe", "CODEX" });

var edited = model.ToDefinition();
Assert.Equal(new TitleRule(TitleMatchKind.Prefix, "WorldGen"), edited.WindowTitle);
Assert.Equal(TargetActivationPolicy.RequireActivityProcess, edited.Activation);
Assert.Equal(new[] { "CODEX", "omp" }, edited.ActivityProcessNames);
Assert.Equal(2, edited.Providers.Count);
```

- [ ] **Step 2: Run the model tests and verify the read-only title/process fields fail**

```powershell
dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter "FullyQualifiedName~TargetViewModelTests"
```

Expected: FAIL because title state is derived from `_original` and provider process names still own
the editor's process values.

- [ ] **Step 3: Replace provider process editor state with title/activity state**

In `TargetEditorModel`, remove `ClaudeProcessName`, `CodexProcessName`,
`TitleAndMatcherPreview`, and inferred per-provider pattern storage. Add:

```csharp
public bool TitleMatcherEnabled { get; set; }
public TitleMatchKind TitleKind { get; set; }
public string TitleValue { get; set; } = "";
public IReadOnlyList<string> ActivityProcessNames => _activityProcessNames;

public void SetActivityProcesses(IEnumerable<string> values) =>
    _activityProcessNames = ActivityProcessName.NormalizeMany(values);
```

Initialize and refresh these fields in the constructor and `ApplyDefinition`. In `ToDefinition`, set
the title to null when disabled and derive activation strictly from whether the normalized activity
list is empty:

```csharp
WindowTitle = TitleMatcherEnabled
    ? new TitleRule(TitleKind, TitleValue.Trim())
    : null,
ActivityProcessNames = _activityProcessNames.ToList(),
Activation = _activityProcessNames.Count == 0
    ? TargetActivationPolicy.AlwaysWhileOpen
    : TargetActivationPolicy.RequireActivityProcess,
```

Validation must reject a blank enabled title rule, invalid regex, and any manually supplied invalid
process name with user-facing sentences.

- [ ] **Step 4: Write failing search, details, and diagnostics tests**

Add assertions that searching `omp`, `codex.exe`, and a migrated target's full/short ID finds the
rule; searching a removed legacy provider process does not. Assert details contain separate lines:

```text
Title: Prefix:WorldGen
Activity: omp OR codex
```

and diagnostics contain `activity=omp|codex`. Add a manager-row assertion that a transient migration
warning is included in the tooltip/details.

- [ ] **Step 5: Update search, identity text, manager rows, and diagnostics**

In `TargetRuleSearch.Matches`, search `target.ActivityProcessNames` and provider enum names; remove
the legacy provider process search. Extend `TargetRow.From`, `TargetDetailsFormatter.Format`,
`TargetManagementRuleItem.From`, and `TargetDiagnostics.Append` with a shared formatter:

```csharp
internal static string FormatActivity(TargetDefinition target) =>
    target.ActivityProcessNames.Count == 0
        ? "always"
        : string.Join("|", target.ActivityProcessNames);
```

Include `MigrationWarning` in details and tooltips when nonblank. Keep overlap conservative and add
`activity filters differ` to impact text when overlapping normalized sets are unequal.

- [ ] **Step 6: Run model and management-projection tests**

```powershell
dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter "FullyQualifiedName~TargetViewModelTests|FullyQualifiedName~TargetRuleImpactTests|FullyQualifiedName~TargetManagerFormTests|FullyQualifiedName~DiagnosticsAndStartupTests"
```

Expected: PASS.

- [ ] **Step 7: Commit editable model and management semantics**

```powershell
git add src/TargetViewModels.cs src/TargetRuleImpact.cs src/TargetManagerForm.cs src/TargetStore.cs tests/UsageBars.Tests/TargetViewModelTests.cs tests/UsageBars.Tests/TargetRuleImpactTests.cs tests/UsageBars.Tests/TargetManagerFormTests.cs tests/UsageBars.Tests/DiagnosticsAndStartupTests.cs
git commit -m "feat: expose title and activity rule state"
```

### Task 4: Build the checkbox tree, persistent chips, and manual activity entry

**Files:**
- Create: `src/ActivityProcessEditorControl.cs`
- Test: `tests/UsageBars.Tests/ActivityProcessEditorControlTests.cs`
- Modify: `tests/UsageBars.Tests/ProcessInventoryTests.cs`

**Interfaces:**
- Consumes: `ProcessInventory`, `ProcessInventoryEntry`, and `ActivityProcessName`.
- Produces: `ActivityProcessEditorControl.ActivityProcessNames`, `SetActivityProcesses`, `SetInventory`, `ValueChanged`, and `InventoryRefreshRequested`.
- Produces: public/internal test-observable state for selected filters, scope warning, and manual-entry validation without exposing Win32 enumeration.

- [ ] **Step 1: Write failing checkbox/chip interaction tests**

Create tests named:

- `CheckingAnyPidAddsOneNormalizedProcessChip`
- `UncheckingLastVisiblePidRemovesTheFilter`
- `RemovingAChipUnchecksEveryPidWithThatName`
- `SavedAbsentProcessRemainsMarkedNotCurrentlyRunning`
- `ManualEntryAddsAProcessThatIsNotInInventory`
- `InvalidManualEntryShowsValidationAndDoesNotChangeDraft`
- `RefreshButtonRaisesOneRequestAndNeverEnumeratesItself`
- `ReplacingInventoryPreservesViewportAndCheckedFilters`
- `SharedOwnerInventoryShowsScopeWarning`

Use a recursive `FindControls<T>` helper and accessible names rather than private method reflection.
For example:

```csharp
var tree = FindControls<TreeView>(control).Single();
var omp = AllNodes(tree.Nodes).Single(node => node.Text.StartsWith("omp ("));
omp.Checked = true;

Assert.Equal(new[] { "omp" }, control.ActivityProcessNames);
Assert.Contains(FindControls<Button>(control),
    button => button.AccessibleName == "Remove activity process omp");
```

- [ ] **Step 2: Run the new control tests and verify the control is missing**

```powershell
dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter "FullyQualifiedName~ActivityProcessEditorControlTests"
```

Expected: FAIL with missing `ActivityProcessEditorControl`.

- [ ] **Step 3: Move stable tree rendering into the focused control**

Create `ActivityProcessEditorControl` as a WinForms `UserControl` containing:

- `TreeView { CheckBoxes = true, ShowNodeToolTips = true }`;
- `Show system helpers` checkbox;
- accessible `↻` refresh button and tooltip `Refresh processes`;
- `FlowLayoutPanel` of removable chip buttons;
- manual-entry `TextBox` and `Add process` button;
- scope and validation labels.

Use a node tag that includes both stable viewport identity and normalized process name:

```csharp
private sealed record NodeTag(
    IntPtr Hwnd,
    uint? Pid,
    string? ProcessName,
    bool Selectable);
```

Copy the existing `ProcessTreeViewport` capture/restore behavior into this control and extend it to
restore check state from `_activityProcessNames`, never from transient PID identity.

- [ ] **Step 4: Implement guarded checkbox-to-chip synchronization**

Use `_loading` to prevent recursive `AfterCheck` events. The event contract is:

```csharp
private void OnAfterCheck(object? sender, TreeViewEventArgs e)
{
    if (_loading || e.Node.Tag is not NodeTag { Selectable: true, ProcessName: { } name }) return;
    var next = _activityProcessNames.ToList();
    if (e.Node.Checked) next.Add(name);
    else if (!AnyCheckedNodeNamed(name))
        next.RemoveAll(value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
    ApplyActivityProcesses(next, raiseChanged: true);
}
```

`ApplyActivityProcesses` normalizes/sorts the set, synchronizes every visible same-name node,
rebuilds chips, marks missing values `not currently running`, and raises `ValueChanged` exactly once
only when the normalized set changed.

- [ ] **Step 5: Implement manual entry, chip removal, scope warning, and refresh event**

Manual Add must call `ActivityProcessName.TryNormalize`; on success it adds the value without
requiring an inventory match, clears the text box/error, and raises one change. Each chip button uses
text `<name> ×`, accessible name `Remove activity process <name>`, and removes by normalized name.

Reuse the existing sibling-owner warning copy and raise `InventoryRefreshRequested` only from the
refresh button. `SetInventory` must retain the prior tree and filters when rendering throws; its
caller still owns enumeration failures.

- [ ] **Step 6: Run the focused UI tests**

```powershell
dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter "FullyQualifiedName~ActivityProcessEditorControlTests|FullyQualifiedName~ProcessInventoryTests"
```

Expected: PASS.

- [ ] **Step 7: Commit the process activity editor**

```powershell
git add src/ActivityProcessEditorControl.cs tests/UsageBars.Tests/ActivityProcessEditorControlTests.cs tests/UsageBars.Tests/ProcessInventoryTests.cs
git commit -m "feat: add process activity checkbox editor"
```

### Task 5: Add the title-rule editor and integrate the target editor layout

**Files:**
- Create: `src/TitleRuleEditorControl.cs`
- Modify: `src/TargetEditorControl.cs`
- Test: `tests/UsageBars.Tests/TitleRuleEditorControlTests.cs`
- Test: `tests/UsageBars.Tests/TargetEditorLayoutTests.cs`

**Interfaces:**
- Consumes: `TargetEditorModel` from Task 3 and `ActivityProcessEditorControl` from Task 4.
- Produces: `TitleRuleEditorControl.Rule`, `CapturedTitle`, `ValueChanged`, and `ValidateRule`.
- Preserves: `TargetEditorControl.DraftChanged`, `InventoryRefreshRequested`, `SetInventory`, `CurrentDefinition`, and `ValidateDefinition` for `TargetManagerForm`.

- [ ] **Step 1: Write failing title-editor tests**

Create tests named:

- `DisabledEditorReturnsNoRule`
- `ExistingLiteralPrefixAndRegexRulesRoundTrip`
- `UseCurrentTitleEnablesLiteralRule`
- `BlankEnabledMatcherIsInvalid`
- `InvalidRegexIsReportedWithoutThrowing`
- `ChangingKindOrValueRaisesOneValueChangedEvent`

The current-title action test should assert:

```csharp
control.CapturedTitle = "WorldGen — PowerShell";
FindControls<Button>(control).Single(x => x.Text == "Use current title").PerformClick();
Assert.Equal(new TitleRule(TitleMatchKind.Literal, "WorldGen — PowerShell"), control.Rule);
```

- [ ] **Step 2: Run title tests and verify the control is missing**

```powershell
dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter "FullyQualifiedName~TitleRuleEditorControlTests"
```

Expected: FAIL with missing `TitleRuleEditorControl`.

- [ ] **Step 3: Implement the optional title-rule editor**

Create a two-column WinForms control with:

- `Use title matcher` checkbox;
- `DropDownList` bound to `TitleMatchKind` values;
- editable matcher text;
- read-only current-title text;
- `Use current title` button; and
- inline validation label.

`Rule` must return null when disabled and a trimmed `TitleRule` when enabled. `ValidateRule` returns
`Title matcher value is required.` for blank enabled values and `Title matcher regex is invalid.`
when bounded `Regex` construction fails. Do not wait until `TargetDefinition.Validate` to show these.

- [ ] **Step 4: Write failing integrated layout and draft tests**

Update `TargetEditorLayoutTests` to assert:

- there are provider checkboxes but no provider-specific process combo boxes;
- title controls load the saved matcher and update `CurrentDefinition.WindowTitle`;
- activity checkbox/chip changes update `CurrentDefinition.ActivityProcessNames` and activation;
- the existing refresh button contract is forwarded exactly once;
- `Require resizable frame` has at least its preferred width;
- Save/Cancel-facing minimum editor dimensions remain compatible with a 620-pixel editor pane.

- [ ] **Step 5: Compose the focused controls in `TargetEditorControl`**

Remove `_titlePreview`, `_claudeProcess`, `_codexProcess`, the embedded process-tree implementation,
and the raw activation enum combo. Add:

```csharp
private readonly TitleRuleEditorControl _title = new();
private readonly ActivityProcessEditorControl _activity = new();
```

Use provider checkboxes only. Present activation as two radio buttons or one explicit mode selector
with labels `Always while the window is open` and `When any selected process is running`; selecting
process mode reveals `_activity` and requires at least one chip before Save. Switching back to always
clears the draft's activity list so `CurrentDefinition` and the live preview cannot disagree about
the saved activation mode.

Forward `_activity.InventoryRefreshRequested`, pass inventories only to `_activity.SetInventory`,
and pull/push title/activity values through `TargetEditorModel`.

- [ ] **Step 6: Fix the content layout without changing the manager footer contract**

Keep a scrollable editor body, use a 150-pixel label column, give `Require resizable frame` a
dedicated full-width options row, and set controls to `DockStyle.Top` or `Fill` without wrapping the
option text. Preserve `MinimumSize = new Size(620, 540)` unless the tests prove a smaller compatible
minimum.

- [ ] **Step 7: Run title and integrated editor tests**

```powershell
dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter "FullyQualifiedName~TitleRuleEditorControlTests|FullyQualifiedName~TargetEditorLayoutTests"
```

Expected: PASS.

- [ ] **Step 8: Commit title editing and target-editor integration**

```powershell
git add src/TitleRuleEditorControl.cs src/TargetEditorControl.cs tests/UsageBars.Tests/TitleRuleEditorControlTests.cs tests/UsageBars.Tests/TargetEditorLayoutTests.cs
git commit -m "feat: edit title and activity target rules"
```

### Task 6: Complete live preview, manager warnings, and add/edit integration

**Files:**
- Modify: `src/TargetPreviewSession.cs`
- Modify: `src/TargetPreviewSet.cs`
- Modify: `src/TargetManagerForm.cs`
- Modify: `src/TargetManagementWorkspaceModel.cs`
- Test: `tests/UsageBars.Tests/TargetPreviewSessionTests.cs`
- Test: `tests/UsageBars.Tests/TargetPreviewSetTests.cs`
- Test: `tests/UsageBars.Tests/TargetManagerFormTests.cs`
- Test: `tests/UsageBars.Tests/TargetManagementWorkspaceModelTests.cs`
- Test: `tests/UsageBars.Tests/TargetManagerCompositionTests.cs`

**Interfaces:**
- Consumes: integrated `TargetEditorControl`, target-level resolver, and existing `ITargetPreviewSet` lifecycle.
- Produces: pinned preview status text `Preview shown; saved rule would currently be inactive — <reason>` for captured inactive drafts.
- Preserves: manager-owned initial/manual process enumeration, two-second preview refresh, preview suppression, Cancel rollback, and atomic Save behavior.

- [ ] **Step 1: Write failing preview tests for all-provider and inactive drafts**

Add tests proving:

- a captured target creates a preview session before the first timer tick;
- `omp OR codex` with only `omp` running produces normal Claude and Codex preview pills;
- neither process running keeps both preview pills visible and dimmed;
- the reason names the target-level OR-list rather than a provider binding;
- a mismatching title keeps the captured HWND preview visible and dimmed;
- editing an existing target previews only windows satisfying window identity/title conditions;
- a closed captured window removes the preview without saving or cancelling the draft.

The inactive assertion should expect:

```text
Preview shown; saved rule would currently be inactive — None of the selected activity processes (omp OR codex) is running under this window owner.
```

- [ ] **Step 2: Run preview tests and verify provider-specific reason text fails**

```powershell
dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter "FullyQualifiedName~TargetPreviewSessionTests|FullyQualifiedName~TargetPreviewSetTests"
```

Expected: FAIL because `TargetPreviewEvaluator` still searches for a missing provider binding's CLI
process and status uses generic dimmed wording.

- [ ] **Step 3: Evaluate activity once while retaining pinned pills**

In `TargetPreviewEvaluator`, derive preview providers from checked providers filtered by global
enablement. Evaluate the draft's window conditions and then:

```csharp
if (draft.Activation == TargetActivationPolicy.RequireActivityProcess
    && !tree.HasAnyProcessUnder(window.Pid, draft.ActivityProcessNames, ownPid))
{
    var names = string.Join(" OR ", draft.ActivityProcessNames);
    return new(TargetPreviewState.Dimmed,
        $"None of the selected activity processes ({names}) is running under this window owner.",
        providers);
}
```

Never remove the captured session merely because its draft conditions fail. Keep the existing exact
HWND pin in `TargetPreviewSet.DesiredWindows`.

- [ ] **Step 4: Make preview status distinguish shown preview from saved behavior**

Pass mode into status formatting or store it on the set. For add-captured dimmed state, format:

```csharp
$"Preview shown; saved rule would currently be inactive — {worst.Reason}"
```

Keep normal multi-window edit status and window-closed behavior unchanged.

- [ ] **Step 5: Write failing manager integration tests**

Add/update tests named:

- `CapturedWindowImmediatelyReceivesPreviewBeforeAutomaticRefresh`
- `ActivityEditsUpdatePreviewWithoutEnumeratingProcesses`
- `TitleEditsUpdatePreviewWithoutEnumeratingProcesses`
- `ManualProcessRefreshRetainsAbsentActivityChips`
- `SharedOwnerWarningNamesSiblingWindowScope`
- `MigrationWarningAppearsUntilSuccessfulSave`
- `ActivityProcessSearchFindsAndSelectsSavedRule`
- `CancelAndSaveStayAdjacentAtMinimumSizeAfterActivityEditorExpansion`
- `ResizableFrameOptionRemainsReadableAtMinimumSize`

Reuse the existing recording preview set, inventory-call counter, and recursive control helpers.

- [ ] **Step 6: Integrate manager inventory, warning, and search copy**

Keep `RefreshEditorInventoryForModelChange` and `OnInventoryRefreshRequested` as the only manager
paths that call `_inventoryForWindow`. Do not add process enumeration to `RefreshLiveState` or
`OnEditorDraftChanged`. Update search placeholder to include `activity process`, add migration and
scope warnings to details/preview status without overwriting manual-refresh failures, and keep the
existing fixed two-column footer with Cancel left of Save.

When a successful save clears `MigrationWarning`, rebuild the selected row from the store. A failed
save leaves the warning, editor, and preview intact.

- [ ] **Step 7: Run all preview and manager integration tests**

```powershell
dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter "FullyQualifiedName~TargetPreviewSessionTests|FullyQualifiedName~TargetPreviewSetTests|FullyQualifiedName~TargetManagerFormTests|FullyQualifiedName~TargetManagementWorkspaceModelTests|FullyQualifiedName~TargetManagerCompositionTests"
```

Expected: PASS.

- [ ] **Step 8: Commit preview and manager integration**

```powershell
git add src/TargetPreviewSession.cs src/TargetPreviewSet.cs src/TargetManagerForm.cs src/TargetManagementWorkspaceModel.cs tests/UsageBars.Tests/TargetPreviewSessionTests.cs tests/UsageBars.Tests/TargetPreviewSetTests.cs tests/UsageBars.Tests/TargetManagerFormTests.cs tests/UsageBars.Tests/TargetManagementWorkspaceModelTests.cs tests/UsageBars.Tests/TargetManagerCompositionTests.cs
git commit -m "feat: preview target activity filters live"
```

### Task 7: Update documentation, run the complete regression suite, publish, and smoke-test

**Files:**
- Modify: `README.md`
- Modify: `UsageBars.config.default.json`
- Verify: `dist/UsageBars.exe`

**Interfaces:**
- Consumes: all implementation tasks and existing publish configuration.
- Produces: complete user documentation, a passing repository, and a refreshed distribution executable.

- [ ] **Step 1: Scan for obsolete schema and UI terms**

```powershell
rg -n "RequireProviderActivity|CliProcessName|ActivityTitlePattern|Claude activity|Codex activity|targets.json.*version 2|Version\": 2" src tests README.md UsageBars.config.default.json
```

Expected: matches exist only in `TargetDocumentV2.cs`, explicit migration tests/fixtures, and text that
describes legacy migration. Any runtime/editor match is a defect to remove before continuing.

- [ ] **Step 2: Update user documentation and default-config comments**

Document:

- `targets.json` version 3;
- editable Off/Literal/Prefix/Regex title matching;
- one process OR-list per target;
- checked providers as display outputs;
- checkbox tree, persistent chips, manual Add, and explicit refresh;
- all-tab/pane descendant scope and shared-owner ambiguity;
- immediate pinned preview and inactive-state wording; and
- migration of version-2 provider process names plus review warnings for discarded legacy title
  activity evidence.

Keep configuration comments clear that legacy terminal title patterns are seed-time detection inputs,
not live version-3 provider activation rules.

- [ ] **Step 3: Run the complete test suite**

```powershell
dotnet test UsageBars.sln
```

Expected: the full suite passes with zero failed tests.

- [ ] **Step 4: Commit documentation and final fixture cleanup**

```powershell
git add README.md UsageBars.config.default.json tests/UsageBars.Tests
git commit -m "docs: explain target activity filters"
```

- [ ] **Step 5: Verify a clean tree and publish the distribution executable**

```powershell
git status --short
dotnet publish src/UsageBars.csproj -c Release -o dist
```

Expected: source/test/docs are clean before publish; publish succeeds and refreshes
`dist/UsageBars.exe`.

- [ ] **Step 6: Restart only the distribution executable**

Resolve and print the exact executable-scoped process selection before stopping it:

```powershell
$usageBarsExe = (Resolve-Path -LiteralPath 'C:\path\to\UsageBars\dist\UsageBars.exe').Path
$usageBarsProcesses = @(Get-Process UsageBars -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $usageBarsExe })
$usageBarsProcesses | Select-Object Id, Path
```

After confirming every printed path equals `$usageBarsExe`:

```powershell
if ($usageBarsProcesses.Count -gt 0) {
    $usageBarsProcesses | Stop-Process
}
Start-Process -FilePath $usageBarsExe -WorkingDirectory (Split-Path $usageBarsExe) -WindowStyle Hidden
```

Expected: exactly the intended distribution instance is replaced.

- [ ] **Step 7: Perform the Windows Terminal acceptance smoke test**

Open **Manage targets**, edit `WindowsTerminal`, and verify:

1. the title matcher is editable and can be disabled;
2. checking `omp` creates one persistent chip;
3. `codex` can be checked when present or manually added when absent;
4. the tree remains still across at least two preview timer intervals;
5. refresh occurs only after clicking `↻` and does not force the tree away from its restored top node;
6. both Claude and Codex preview pills appear immediately and move live with anchor/X/Y edits;
7. if neither selected process runs, the pinned preview dims with the saved-rule-inactive message;
8. shared-owner scope text explains that sibling Terminal windows may also receive pills;
9. `Require resizable frame` is readable; and
10. Cancel changes and Save changes are side by side and fully visible at minimum size.

Save the target, close/reopen UsageBars, and verify the `omp OR codex` chips, title rule, providers,
placement, and offsets survive and runtime pills appear only while either process is beneath the
Terminal owner.

- [ ] **Step 8: Record final evidence**

```powershell
git log -8 --oneline
Get-Item -LiteralPath 'dist\UsageBars.exe' | Select-Object FullName, Length, LastWriteTime
Get-CimInstance Win32_Process -Filter "Name = 'UsageBars.exe'" |
    Select-Object ProcessId, ExecutablePath
```

Expected: the task commits are present, the distribution executable is freshly published, and the
running process path is the repository's `dist\UsageBars.exe`.
