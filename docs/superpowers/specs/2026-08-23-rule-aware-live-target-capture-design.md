# Rule-Aware Live Target Capture Design

Date: 2026-08-23  
Status: Approved for implementation

## Summary

Expand the tray's window-picker flow into a rule-aware capture workspace. After the user clicks a window, UsageBars will immediately place a temporary pill on that exact window, show which saved rules already apply or overlap, expose a searchable rule sidebar with cleanup actions, and let the user select a stable activity process separately for Claude and Codex. Draft changes update the real on-window preview without entering the saved target store.

The approved UI is a resizable two-pane dialog: rule discovery and management on the left, editing and live-preview controls on the right.

## Goals

- Find saved rules that already apply to the picked window before creating another rule.
- Find potential overlaps and search all targets by name, ID, process, class, or title matcher.
- Default to editing the strongest applicable rule while retaining an explicit path to create a more-specific rule.
- Support immediate Edit, Disable, and confirmed Delete actions from the capture dialog.
- Display a real pill on the selected window as soon as capture succeeds.
- Update that preview in real time as provider, activity process, placement, anchor, offsets, activation, resize requirement, and status-bar settings change.
- Keep unsaved preview state isolated to the clicked HWND and out of `targets.json`.
- Show the selected window's owner and descendant process tree, then allow one stable activity-process name to be selected per enabled provider.
- Correct the clipped/wrapped “Require resizable frame” option and make the expanded dialog DPI-resilient.

## Non-goals

- Do not infer or claim a reliable process-to-tab mapping in Windows Terminal.
- Do not persist process IDs; they are transient display information only.
- Do not insert temporary draft rules into `TargetStore` and roll them back later.
- Do not apply unsaved preview changes to other windows that match the draft.
- Do not replace the existing Manage Targets dialog; the capture workspace supplements it for rule discovery and cleanup at capture time.

## Platform Constraint: Terminal Tabs and Processes

UsageBars can enumerate the owner process for a top-level window and take a Toolhelp process snapshot to find descendant processes and ancestry chains. That is sufficient for an owner-process-scoped inventory, but not exact attribution to one window, tab, or pane.

Windows Terminal does not expose a supported external API for querying open tabs and mapping them to their processes. Its process model also permits shared application infrastructure and tab movement between windows. Therefore, the UI must label the process inventory as “all tabs/panes under this window owner” and must not guess per-tab ownership. When a host process is shared by sibling windows, the UI must additionally warn that the inventory may include those sibling windows.

References:

- [Windows Terminal query-tabs feature request](https://github.com/microsoft/terminal/issues/19818)
- [Windows Terminal session/process model](https://github.com/microsoft/terminal/blob/main/doc/specs/%235000%20Process%20Model%202.0/%234472%20-%20Windows%20Terminal%20Session%20Management.md)

## User Flow

1. The user chooses **Targets → Add by clicking a window...** and clicks a target window.
2. Capture records the stable window identity and current window title, then builds an owner-process-scoped inventory for that window.
3. A `TargetCaptureWorkspace` starts immediately and creates a preview on only the captured HWND.
   - If a provider is inferred, the preview uses the actual provider model.
   - If no provider is inferred, a neutral `Preview` pill appears so placement is visible immediately.
4. The workspace classifies saved rules and opens the two-pane editor.
5. The strongest applicable rule is selected for editing by default. The user may instead choose another rule or explicitly choose **Create more-specific rule**.
6. Changes on the right update the temporary preview immediately.
7. Explicit sidebar Disable/Delete actions persist immediately. Ordinary editor changes remain temporary.
8. **Cancel** disposes the preview and restores normal saved-rule resolution without writing a target.
9. **Save** performs one add or update, disposes the preview override, and lets the normal resolver take over.

## Dialog Layout

### Left: Rules Affecting This Window

The left pane contains a search box and three groups:

1. **Applies to this window**
   - Includes enabled and disabled rules whose selectors match the captured `WindowSnapshot`.
   - Marks exact duplicate identity separately from broader rules.
2. **Overlaps proposed rule**
   - Includes rules that could compete with the current draft for at least one provider, even if a title discriminator prevents them from matching the current title.
3. **Other saved rules**
   - Populated when search text is present.

Search is case-insensitive and covers:

- Rule name
- Full or partial GUID
- Process name
- Window class
- Title matcher kind and value

Each rule row displays name, abbreviated ID, providers, enabled state, and its relationship to the selected window/draft. Long text uses ellipsis and tooltips.

Available actions:

- **Edit selected** loads the rule into the right pane without persisting subsequent changes until Save.
- **Disable/Enable** updates the saved rule immediately and reruns classification.
- **Delete...** requires confirmation, deletes immediately, and reruns classification.
- **Create more-specific rule** creates a new draft with a new ID based on the captured window and sensible defaults from the strongest matching rule.

If the rule currently being edited is deleted, the editor converts its current values into an unsaved new draft so the user's work is not lost.

### Right: Editor and Preview Status

The right pane begins with a preview-status banner containing the selected HWND and one of these states:

- Previewing normally
- Previewing dimmed, with the reason the draft would not apply
- Preview unavailable because the selected window closed
- Preview failed, with a retryable/nonblocking explanation

The editor contains:

- Name
- Read-only captured window owner process and top-level class
- Captured title and persisted title matcher
- Claude enabled plus Claude activity-process selector
- Codex enabled plus Codex activity-process selector
- Expandable owner-process-scoped process tree
- Placement
- Anchor
- X/Y offsets and reset
- Activation policy
- Status-bar height
- Enabled
- Require resizable frame

The form is resizable and uses DPI-aware minimum dimensions. The right pane uses proportional columns. The Options fields are vertically stacked, and “Require resizable frame” must remain on one readable line rather than wrapping inside a narrow flow-layout cell.

## Rule Impact Analysis

Introduce a pure `TargetRuleImpactAnalyzer` that receives:

- The captured `WindowSnapshot`
- The current draft `TargetDefinition`
- All saved `TargetDefinition` values
- Optional search text

It returns immutable rows with a relationship classification and ranking metadata. The classifications are:

- `ExactIdentity`: `HasSameIdentity`-equivalent identity, ignoring name, enabled state, revision, and visual offsets as the current duplicate rule does.
- `AppliesNow`: the saved rule's selectors match the captured window.
- `OverlapsDraft`: `Overlaps` is true for the current draft and saved rule.
- `SearchOnly`: the row matches search but none of the stronger relationships.

A row may satisfy more than one predicate; the UI displays its strongest relationship in the order above and may show secondary details in its tooltip/details text.

Default editing selection ranks rules by:

1. Exact identity
2. Applies now
3. Enabled before disabled
4. Higher specificity
5. Higher revision
6. Stable ID ordering as the final deterministic tie-break

Classification is recomputed whenever:

- The draft identity changes
- Search changes
- A rule is enabled, disabled, deleted, added, or updated

## Process Inventory and Activity Selection

Extend the existing process snapshot boundary to produce a selected-window inventory containing:

- PID
- Stable executable name without `.exe`
- Parent PID
- Depth beneath the selected window owner
- An ancestry path suitable for display
- Whether the entry is a known helper/system process

The inventory refreshes approximately every two seconds while the dialog is open. Repeated executable names are represented as multiple running instances in the tree but as one stable name in each picker. Picker labels may show current PIDs and ancestry for clarity. The section heading reads “all tabs/panes under this window owner”; when another visible top-level window has the same owner PID, an explanatory note says the inventory can include that sibling window as well.

Known helpers such as `OpenConsole` and `conhost` are shown in the expanded tree but excluded from normal dropdown choices. An advanced **Show system helpers** option makes them selectable.

Each enabled provider has an independent activity-process selection:

- Claude defaults to the inferred or existing Claude `CliProcessName`.
- Codex defaults to the inferred or existing Codex `CliProcessName`.
- The selected stable process name is persisted in the existing `ProviderBinding.CliProcessName` field.
- Existing activity-title regexes are preserved unless a future dedicated matcher editor changes them.
- `RequireProviderActivity` evaluates each provider against its selected process independently.
- `AlwaysWhileOpen` does not require the selected activity process but retains the selection if the user switches back.

Because `ProviderBinding` already persists a per-provider process name, this feature does not require a target-document schema version change.

If a selected process exits while the dialog is open, the selection remains and is labeled “not currently running.” The draft then previews as dimmed when its activation policy requires that process.

## Isolated Preview Architecture

Introduce a controller-owned `TargetPreviewSession` for the one active capture workspace. It owns:

- The captured HWND and latest `WindowSnapshot`
- Temporary preview pill window(s)
- The current draft
- Preview applicability and explanation
- Cleanup/restoration behavior

The preview session uses the same overlay view, provider usage models, group layout, DPI scaling, and placement geometry as normal pills. It is not a `TargetStore` entry.

While the session is active:

- Normal resolver output for the captured HWND is suppressed so saved and preview pills cannot overlap.
- All other HWNDs continue resolving solely from saved targets.
- Provider selection produces a neutral pill, one provider pill, or a paired Claude/Codex group.
- Draft changes are coalesced onto the WPF dispatcher and applied using the latest state.
- Matcher and activity evaluation determines normal versus dimmed preview state, but the preview stays visible for diagnosis.

The preview session must be disposed in every exit path: Save, Cancel, form close, picker cancellation after session creation, controller shutdown, and unexpected editor failure.

### Save Handoff

Save validates the draft first, then performs exactly one store mutation:

- Editing an existing rule calls `TargetStore.Update` with the same ID.
- Creating a more-specific rule calls `TargetStore.Add` with a new ID.

After a successful mutation, the preview override is removed and normal tracking runs immediately. The existing visual settings are already on the committed rule, so the transition should not visibly jump.

If persistence fails, the editor and preview remain open and the validation/error banner explains the failure.

### Cancel Handoff

Cancel performs no draft mutation. It removes preview windows and suppression, then immediately runs normal tracking so any previously saved pill is restored.

## Special States

### Selected Window Closes

- Dispose the preview windows.
- Keep the editor open with “Preview unavailable—the selected window closed.”
- Continue allowing Save because the stable captured identity remains valid.

### Draft Does Not Match

- Keep the preview visible and dim it.
- Explain the first actionable reason, such as missing activity process, nonmatching title, disabled rule, or resize requirement.
- Continue showing real placement so the user can correct either identity or appearance settings.

### Rule Mutated From Sidebar

- Immediate Enable/Disable/Delete actions go through `TargetStore`.
- Reload the affected rule by ID before accepting additional edits.
- Recompute impact rows and refresh normal pills outside the preview HWND.

### Process Enumeration Failure

- Retain the owner process captured from the window.
- Preserve prior selections.
- Show a retryable warning in the process section.
- Do not block Save or preview placement.

### Preview Failure

- Show a nonblocking banner.
- Preserve all editor and rule-management functionality.
- Record a state-change diagnostic event without logging every refresh tick.

## Diagnostics

Add bounded, state-change-only events for:

- Capture workspace opened/closed and reason
- Preview created, updated between meaningful states, suppressed normal pills, and disposed
- Rule impact counts by category
- Process inventory refresh failure/recovery
- Save/add/update failure
- Sidebar mutation failure

Do not log full command lines or sensitive process arguments. Process names, PIDs, HWNDs, rule IDs, and already-redacted paths are sufficient.

## Testing Strategy

### Unit Tests

- Exact/applicable/overlap/search-only classification
- Multi-category precedence and deterministic default ranking
- Search by name, partial/full ID, process, class, and title matcher
- Disabled rule inclusion and visual state
- Process ancestry construction and depth
- Repeated process-name deduplication for pickers
- Helper filtering and advanced inclusion
- Independent Claude/Codex process selection and persistence
- Exited selected-process behavior
- Preview applicability reasons
- Preview coordinator start/update/dispose and idempotent cleanup
- Suppression limited to the captured HWND
- Save mutation choice: update same ID versus add new ID
- Cancel performs no draft mutation
- Current edited-rule deletion becomes a new draft

### UI/Layout Tests

- Two-pane minimum widths and proportional resizing
- 100%, 125%, 150%, and 200% DPI layout calculations
- Full readable bounds for “Require resizable frame”
- Sidebar ellipsis/tooltips and right-pane scroll behavior at minimum size
- Preview banner state transitions

### Integration and Smoke Tests

- Pick a window already covered by exact and broad rules; exact rule is selected by default.
- Create a more-specific rule without duplicating the selected existing ID.
- Observe immediate neutral/provider preview before Save.
- Change anchor and X/Y offsets and observe the selected-window pill move without affecting other windows.
- Change per-provider process conditions and observe normal/dimmed preview states.
- Cancel and verify `targets.json` is unchanged and saved pills are restored.
- Save and verify one store mutation followed by seamless normal resolver takeover.
- Disable and confirmed Delete from the sidebar while the editor remains open.
- Close the selected window and verify graceful preview-unavailable behavior.
- Publish the executable, exercise tray capture, then Exit and restart to cover the prior shutdown lifecycle regression.

## Acceptance Criteria

- Picking any eligible window immediately shows a preview pill on that exact window.
- The preview responds to every relevant editor control without persisting the draft or changing other windows.
- Existing applicable and overlapping rules are visible before Save and searchable by all approved fields.
- The strongest existing rule is edited by default; creating a new rule requires the explicit more-specific action.
- Sidebar Edit, Enable/Disable, and confirmed Delete behave as designed.
- Each provider can persist a different selected activity-process name.
- The process tree is accurately labeled as owner-process-scoped, warns about shared sibling windows, and never claims unsupported tab mapping.
- Cancel restores normal saved behavior and leaves `targets.json` unchanged.
- Save performs exactly one add/update and hands off cleanly to normal resolution.
- The resize-frame label is fully readable at supported DPI scales.
- The complete automated suite passes, and the published executable passes the capture/preview/restart smoke test.
