# Unified Target Management Design

## Goal

Replace the separate target manager and add-target dialog with one modeless management workspace that supports searching, adding, editing, previewing, enabling, disabling, duplicating, and deleting saved target rules. Fix the process inventory refresh so it never repeatedly steals the process tree's scroll position.

## Tray entry point

The **Targets** tray submenu contains one command, **Manage targets…**, followed by the noninteractive enabled/disabled summary. The separate **Add by clicking a window…** command is removed.

Repeated **Manage targets…** commands activate and bring forward the existing manager instance. They never create duplicate manager windows.

## Unified workspace

The manager is a modeless, resizable two-pane window.

The left pane contains:

- Search across rule name, full or partial rule ID, window process, window class, title matcher kind and value, provider name, and provider activity-process name.
- A grouped saved-rule list.
- **Add**, **Edit**, **Create more-specific rule**, **Enable/Disable**, and **Delete…** actions.
- Readable full rule ID and match details for the selected rule.

The right pane contains the existing reusable target editor, preview status, inline validation or persistence errors, **Save changes**, and **Cancel changes**.

## Workspace states

The workspace has three explicit states.

### Browse

All saved rules are searchable. There is no unsaved draft and no temporary preview. Selecting a rule and choosing **Edit**, or double-clicking it, enters Edit existing state.

### Edit existing

The selected saved rule is copied into a draft. Unsaved changes do not mutate the saved rule. Every currently open window matching the draft receives temporary preview pills, and normal pills on those windows are suppressed.

As draft fields change, the preview set is reconciled:

- Newly matching windows receive temporary preview pills.
- Windows that stop matching lose their temporary pills and immediately resume saved-rule behavior.
- Claude and Codex bindings remain independently configurable and previewed.
- If no live window matches, the status banner states that the draft is valid but has no current preview window.
- Closing one previewed window removes only that preview and leaves the editor open.

**Save changes** validates and persists the draft once, clears all temporary previews, and returns to Browse state with the saved rule selected. **Cancel changes** discards the draft, clears all temporary previews, restores normal pills, and returns to Browse state.

### Add captured

Choosing **Add** temporarily hides the manager and starts the existing crosshair window picker. The hidden manager cannot be selected accidentally.

After a successful pick, the same manager is restored with its previous bounds and search text. It loads the inferred draft, shows a temporary pill on the captured HWND immediately, and classifies saved rules into:

- Applies to this window
- Overlaps proposed rule
- Other search results

The strongest existing applicable rule replaces the previous selection and is selected for editing by default. **Create more-specific rule** changes the draft to a new ID. A right-click or Escape during picking restores the manager's previous selection without changing saved targets or leaving a preview behind.

Saving an Add captured draft either updates the selected applicable rule or adds the explicitly more-specific draft, then returns to Browse state. Validation and persistence failures remain inline and preserve the draft and preview.

## Immediate management actions

Enable/Disable and confirmed Delete are immediate saved operations, including while another rule has an unsaved draft. Store change notifications refresh the affected rule row without silently replacing an unrelated draft. Toggling the rule currently being edited also updates the draft's enabled field so a later Save cannot accidentally undo the immediate change. Deleting the rule currently being edited discards that draft, clears its previews, and returns the workspace to Browse state.

Closing the manager discards any unsaved draft and temporary previews. Immediate Enable/Disable and Delete operations remain saved.

## Rule grouping and search

In ordinary Browse and Edit existing states, the list shows all saved rules and groups them by enabled and disabled state. Overlap warnings are visible on rows and in selection details.

In Add captured state, window applicability and overlap groups take precedence so duplicate or overly broad rules are visible before saving. Search filters within those groups and may also reveal otherwise unrelated matching rules.

Search is case-insensitive and matches:

- Rule name
- Hyphenated or compact rule ID, including substrings
- Window process
- Window class
- Title matcher kind and value
- Provider name
- Provider activity-process name

## Multi-window live preview

A preview coordinator owns a set of per-window preview sessions rather than a single captured HWND. On every relevant draft, process-tree, or window-set change it computes the matching HWND set, creates missing sessions, updates retained sessions, and disposes sessions that no longer match.

Normal pill suppression uses that exact current preview HWND set. Finishing, cancelling, closing, or failing the workspace disposes the coordinator and runs normal tracking once so no captured window remains hidden or duplicated.

The existing single-window preview session remains the unit responsible for paired Claude/Codex layout, dimming, placement, offsets, and window-closed handling. The coordinator composes those sessions rather than duplicating pill rendering logic.

In Add captured state, the process tree describes the selected window owner exactly as it does today. In Edit existing state, the process tree groups and combines inventories from every currently matching window owner, deduplicating provider dropdown choices by process name. Each group is labeled with its window title and owner PID so the displayed scope remains honest. When no live window matches, saved provider process names remain selectable and are marked as not currently running.

## Stable process inventory refresh

The current defect is caused by the two-second timer calling `SetInventory`, which clears the TreeView, recreates every node, and calls `ExpandAll`. Each rebuild resets the native TreeView viewport, so the user's scroll position is repeatedly lost.

The editor will compare a deterministic process-inventory fingerprint before touching process controls. If the hierarchy, helper visibility, warning state, and selectable names are unchanged, refresh is a no-op.

When a rebuild is required, the editor captures:

- Selected process ID
- Expanded process IDs
- Top visible process ID
- Current Claude and Codex activity-process values
- Current keyboard focus

It rebuilds the hierarchy, restores expansion, selection, and the top visible node, then restores focus without raising draft-change events. The tree expands automatically only on first load. Provider dropdown updates preserve existing selected or typed values, including values that are not currently running.

## Ownership and component boundaries

- `TargetManagerForm` becomes the single management UI and renders workspace state.
- A target-management workspace model owns Browse, Edit existing, and Add captured transitions plus search/group projections.
- A capture/picker coordinator hides and restores the modeless manager and converts a picked window into an inferred draft.
- A multi-window preview coordinator enumerates live candidates and owns per-HWND `TargetPreviewSession` instances.
- `TargetEditorControl` owns stable process-inventory rendering and editor-field change events.
- `OverlayController` wires tray, manager, picker, tracking, store, and diagnostics but does not own form-specific state.
- The old standalone manager grid and modal capture form are removed after their behavior is represented in the unified workspace.

## Error handling

- Picker cancellation restores the manager and previous Browse selection.
- Picker or window-introspection failures restore the manager and show an inline or tray warning.
- Save failures preserve the draft and all valid previews so the user can correct or retry.
- Process refresh failures preserve the last successful inventory and show an inline warning; they never clear the tree.
- A manager close or application shutdown always disposes timers, preview sessions, and picker/capture state idempotently.

## Testing and verification

Automated tests cover:

- Tray command removal and the single management entry point.
- Browse, Edit existing, and Add captured state transitions.
- Search across every documented field and state-appropriate grouping.
- Add hiding/restoring the manager and picker cancellation.
- Save/cancel/close cleanup and immediate Enable/Disable/Delete behavior.
- Multi-window preview reconciliation, normal-pill suppression, disappearing windows, and no-match status.
- Process inventory no-op refresh and preservation of expansion, selection, top node, dropdown values, and focus when a rebuild is necessary.
- Regression coverage for the readable resizable-frame option and existing placement controls.

Final verification runs the full test suite, publishes the self-contained Windows executable, and exercises the tray/manager/picker workflow manually where Windows automation can target the relevant UI.
