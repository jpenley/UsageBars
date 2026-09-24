# Dynamic Window Targets Design

## Summary

UsageBars will replace its hard-coded host-window catalog with a persisted, user-manageable list of target definitions. Existing Claude Desktop, Codex/ChatGPT, terminal, Rider, and VS Code behavior will be seeded into that list on first run. Users can then add a target by choosing a tray command and clicking a window, confirm or correct the inferred Claude/Codex association, and view, enable, disable, or delete targets in a compact manager.

The existing usage providers, pills, placement engine, window ownership, and refresh behavior remain unchanged. This feature changes how a qualifying host window is identified and managed.

## Goals

- Make host-window targeting data-driven rather than compiled into static lists.
- Preserve current behavior on the first run after upgrading.
- Let a user capture a stable rule by clicking a running window.
- Infer Claude and Codex associations where possible and allow manual correction.
- Persist targets across application restarts.
- Provide clear viewing, enabling, disabling, and deletion of all targets.
- Prevent duplicate pills and resolve overlapping rules deterministically.

## Non-goals

- Adding providers other than Claude and Codex.
- Per-target colors, pill contents, or styling.
- Turning the target manager into a general-purpose regular-expression editor.
- Changing usage acquisition, refresh timing, overlay ownership, placement, or occlusion behavior.
- Guaranteeing that Windows Terminal process ancestry identifies a particular tab; existing title and live-process corroboration limitations remain.

## Persisted model

Targets are stored at `UsageBars.data/targets.json`, using the existing portable-first data-root behavior and `%LOCALAPPDATA%\UsageBars` fallback.

Each `TargetDefinition` contains:

- A stable ID.
- An editable display name.
- An enabled flag.
- One or both provider associations: Claude and Codex.
- A process executable name when the rule is process-specific.
- An optional top-level window class. Captured targets always record one; seeded compatibility
  rules may omit it when the existing behavior recognizes any resizable main frame owned by a
  known editor process.
- An optional `RequireResizableFrame` constraint used by those seeded editor rules to reject
  menus, tooltips, and other process-owned popups.
- An optional title matcher used only when process and class are not sufficiently specific.
- A placement kind: desktop title bar, terminal title bar, or IDE status bar.
- An activation policy:
  - `AlwaysWhileOpen`, used for provider desktop applications and explicitly persistent IDE/application targets.
  - `RequireProviderActivity`, used for terminals and other applications that qualify only while provider-specific title or process evidence is present.
- Source metadata indicating whether the rule was seeded or captured by a user. The source is informational and does not make seeded rules immutable.
- A last-edited value used only to break ties between equally specific overlapping rules.

Matchers compare process names and window classes case-insensitively. A title matcher may be a captured literal/prefix or a seeded regular expression. Newly captured rules always persist both process and class and use the least title-specific stable matcher that distinguishes the selected top-level window; users are not exposed to arbitrary regex authoring in this feature. Process-only rules are reserved for seeded backward compatibility and must require a resizable frame.

## First-run seeding and compatibility

When `targets.json` does not exist, `TargetStore` creates definitions equivalent to the current behavior:

- Claude Desktop.
- Codex/ChatGPT Desktop.
- Supported terminal window classes with provider-specific title and live-process evidence.
- Rider main frames.
- VS Code main frames.
- Existing supported editor hosts that are currently identified by process and resizable-frame behavior.

Seeded definitions immediately become ordinary target records. Users may view, disable, edit through the supported manager controls, or delete them. Deleted seeded records do not reappear unless the user explicitly resets all targets to defaults in a future feature; no reset command is part of this scope.

`EnableClaude` and `EnableCodex` remain global master switches. Existing appearance, positioning, usage-source, refresh, startup, and diagnostics settings remain authoritative.

Legacy targeting settings influence only initial seeding:

- `AttachToClaudeCodeTerminals` and `AttachToCodexTerminals` determine whether corresponding terminal targets start enabled.
- `TerminalTitlePattern` and `CodexTitlePattern` populate the seeded terminal title matchers.
- `AlwaysShowOnIdes` determines the seeded IDE activation policy.

After the first successful target-store creation, `targets.json` is the targeting source of truth. Legacy targeting settings remain readable for compatibility and are documented as seed-time settings so they cannot silently recreate or re-enable targets.

## Components and boundaries

### TargetDefinition and target matching

`TargetDefinition` is a serialization-friendly record containing only stable targeting data. A separate matcher evaluates a definition against a captured top-level window snapshot. This keeps persistence independent of Win32 handles and makes matching directly unit-testable.

A `WindowSnapshot` contains the transient facts used during discovery or capture: top-level handle, process ID and name, class, title, placement-relevant frame characteristics, process tree, and provider evidence. Handles and process IDs are never persisted.

### TargetStore

`TargetStore` owns loading, validation, first-run seeding, mutation, and saving. It exposes an immutable current snapshot and a change notification. Add, update, enable/disable, and delete operations save first and publish the new snapshot only after persistence succeeds, preventing the UI and resolver from claiming a change that was not stored.

Saving writes a complete document to a temporary sibling file, flushes it, and replaces the old file. A failed save leaves the last good store in effect.

### Provider inference

`ProviderInference` examines the selected window and current process tree for:

- Exact provider desktop application identity.
- Provider-specific terminal titles corroborated by a live CLI process.
- Provider CLI ancestry under the selected application.
- Existing matching target definitions.

Inference can produce Claude, Codex, both, or no provider. It supplies defaults to the confirmation dialog but never prevents manual provider selection.

### HostResolver

`HostResolver` receives the current target definitions rather than using static desktop-process, terminal-class, editor-process, and IDE-frame catalogs as the primary source of truth. It enumerates eligible top-level windows once per tracking pass, evaluates enabled definitions, applies activation policies, and emits at most one `HostWindowInfo` for each provider/window pair.

The existing overlay controller continues to key pills by provider and window handle. Target-store changes trigger an immediate tracking pass so additions, enable/disable changes, and deletions take effect without restarting.

### Window picker

`WindowPicker` owns the short-lived mouse/keyboard capture session and contains no target persistence logic. It reports either a captured top-level window or cancellation. Native calls are wrapped behind a narrow interface so root-window resolution and self-window filtering can be tested without installing global hooks.

### Tray and target manager

The existing tray surface adds entry points and delegates mutations to the controller/store. The manager consumes target view models rather than performing Win32 discovery itself.

## Tray and capture interaction

The tray menu gains a `Targets` submenu containing:

- `Add by clicking a window...`
- `Manage targets...`
- A disabled summary line such as `7 enabled - 2 disabled`.

Choosing Add closes the tray menu and starts picker mode. UsageBars changes the cursor to a crosshair and shows a small, non-activating instruction banner: `Click a window to target - Esc to cancel`.

The next left-click is intercepted so it does not activate a control in the chosen application. `WindowFromPoint` identifies the clicked child, and `GetAncestor(..., GA_ROOT)` resolves it to the top-level application window. UsageBars rejects its own pills, tray-owned windows, picker banner, confirmation dialog, and manager.

Escape or right-click cancels picker mode. Every exit path removes hooks, restores the cursor, and closes the banner. Starting a second picker while one is active is rejected.

After capture, a confirmation dialog displays:

- An editable target name.
- Captured executable name, top-level class, and a concise title/matcher preview.
- Claude and Codex checkboxes preselected from inference.
- Placement choice inferred as desktop title bar, terminal title bar, or IDE status bar.
- Activation choice between `Always while this application window is open` and `Only while provider activity is detected`.

When no provider is detected, the dialog explicitly says so and allows Claude, Codex, or both to be assigned manually. At least one provider and a stable process/class matcher are required to save.

If the selected process exits before confirmation, the captured stable fingerprint remains valid. If Windows denies some details, the dialog shows the available information and prevents saving unless both the executable name and top-level window class needed by a captured rule were obtained.

## Target management interaction

`Manage targets...` opens a compact table with these columns:

- Enabled.
- Name.
- Provider association.
- Application.
- Match summary.
- Placement.

A details pane shows the complete process/class/title rule, activation policy, and seeded/captured source. The manager supports enable/disable and deletion. Deletion requires confirmation and is persisted before the row disappears.

The initial scope does not expose arbitrary matcher editing. A user can rename a target, change provider associations, placement, activation policy, and enabled state. To change the captured identity itself, the user deletes the target and captures a new window.

Closing the manager does not exit UsageBars. Only one manager and one confirmation dialog may exist at a time.

## Matching and conflict resolution

Each tracking pass performs the following data flow:

1. Enumerate eligible top-level windows once.
2. Create a `WindowSnapshot` for each candidate.
3. Match enabled definitions against process, class, and optional title rules.
4. Evaluate each matching definition's activation policy for each associated provider.
5. Rank successful matches and select one result per provider/window pair.
6. Feed selected `HostWindowInfo` values into the existing pill synchronization and positioning path.

Specificity ranks are:

1. Process, class, and title.
2. Process and class.
3. Process plus resizable-frame constraint, for seeded editor compatibility.
4. Class-only seeded terminal compatibility rules.

When specificity is equal, the most recently edited target wins. The selected definition supplies placement and host-description metadata. This ordering is deterministic across tracking passes.

Before adding, the store checks for an identical matcher and provider association. The confirmation dialog reports `Already targeted` and offers to edit the existing record instead of creating a duplicate. The manager flags non-identical overlapping rules with a warning, but overlaps remain legal because a more specific rule may intentionally override a general one.

The overlay controller's existing provider/window key guarantees that overlapping definitions never create duplicate pills.

## Error handling and diagnostics

- A malformed store is not overwritten in place. UsageBars records the parse error, warns through the tray, and uses seeded defaults in memory. On the first later mutation, it renames the malformed document to a timestamped `targets.corrupt.*.json` backup before atomically writing a fresh validated store.
- Invalid individual entries are skipped while valid entries remain active. Diagnostics identify skipped entry IDs and validation failures. Before the first mutation, the original document is likewise preserved as a timestamped `targets.invalid.*.json` backup so skipped records are recoverable even though the newly saved store contains only validated entries.
- A failed save leaves the previous in-memory and on-disk definitions active and reports the failure in the manager or a tray warning.
- Hook installation failure cancels picker mode and shows a warning.
- Capture cleanup runs in a `finally` path so hooks, cursor state, and the banner cannot remain active after errors.
- Provider inference failure is non-fatal; the confirmation dialog falls back to manual provider selection.
- Process exit during confirmation is non-fatal because handles and process IDs are not persisted.
- Matching and activation rejection reasons are added to copied diagnostics alongside the existing pill/resolver information.

## Testing strategy

Automated tests cover:

- First-run seeding and one-time migration from targeting config values.
- Seeded entries matching the current Claude Desktop, Codex/ChatGPT, terminal, Rider, VS Code, and supported-editor behavior.
- Atomic save/load behavior, malformed documents, invalid entries, and failed writes.
- Case-insensitive process and class matching.
- Optional title matching and invalid seeded regex handling.
- Provider inference from desktop identity, provider title, and process ancestry.
- Manual provider assignment when inference returns none.
- Activation policies for always-open and provider-activity targets.
- Specificity ordering, equal-specificity tie breaking, identical duplicate detection, and overlap warnings.
- Immediate add, update, enable/disable, and deletion notifications.
- Child-control-to-root-window resolution.
- Picker cancellation and filtering of all UsageBars-owned windows.
- Tray and manager view-model state without live desktop automation.

A Windows smoke-test checklist verifies:

1. Start capture from the tray and observe the crosshair/instruction banner.
2. Cancel with Escape and right-click, confirming the cursor and hooks are restored.
3. Capture Claude Desktop, Codex/ChatGPT, a terminal, Rider, and VS Code.
4. Confirm the selection click does not activate the underlying control.
5. Confirm inferred providers and manually associate an unrelated or undetected IDE.
6. Restart UsageBars and verify all saved targets remain.
7. Disable and delete targets and verify their pills disappear immediately and do not return after restart.
8. Reopen a targeted application and verify the stable process/class rule matches its new window handle.
9. Exercise overlapping rules and verify only one pill per provider/window pair.
10. Corrupt a copy of `targets.json` under an isolated test data root and verify warning, diagnostics, preservation, and in-memory fallback behavior.

## Acceptance criteria

- Existing users retain equivalent targeting behavior after the first-run seed.
- The tray can initiate a click-to-target workflow without restarting UsageBars.
- A clicked child control produces a stable top-level process/class-oriented rule rather than a transient window handle.
- Provider inference preselects known Claude/Codex relationships, while manual assignment remains available.
- Added, enabled, disabled, renamed, and deleted targets take effect immediately.
- The target list survives normal restart and cannot be partially written.
- Seeded targets can be deleted and do not silently reappear.
- Duplicate or overlapping definitions never produce duplicate pills.
- Picker hooks and visual state are removed on success, cancellation, and failure.
- All automated tests pass, and the Windows smoke-test checklist succeeds.
