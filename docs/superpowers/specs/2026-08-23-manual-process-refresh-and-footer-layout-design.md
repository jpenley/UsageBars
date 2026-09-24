# Manual Process Refresh and Stable Editor Footer Design

## Goal

Stop the target editor's process tree and provider-process choices from changing automatically while retaining automatic live pill previews. Keep **Cancel changes** and **Save changes** side by side and fully visible at the manager's minimum supported size.

## Current Causes

`TargetManagerForm.RefreshLiveState()` currently has two responsibilities: it reconciles live preview sessions and rebuilds process inventory. The form's two-second timer calls it, and overlay tracking can also call it through `TargetManagerCoordinator.RefreshLiveState()`. Although `TargetEditorControl` skips inventories with identical fingerprints, normal process creation and exit changes the fingerprint, so the visible process tree and choices can rebuild repeatedly.

The editor footer places its two buttons in a wrapping, right-to-left `FlowLayoutPanel`. Under constrained width or layout measurement, that panel can wrap the buttons onto separate rows, leaving **Save changes** partly outside the visible client area.

## Approved Behavior

### Automatic live refresh

Automatic refresh continues to:

- reconcile preview sessions with eligible open windows;
- update preview status and warnings;
- release or acquire normal-overlay suppression as preview ownership changes.

Automatic refresh must not enumerate processes, replace provider-process choices, or rebuild the process tree.

### Initial process inventory

Process inventory is populated once when an editor becomes active:

- Add mode uses the inventory captured with the selected window;
- Edit mode builds the combined inventory for all currently matching eligible windows.

The form retains that inventory for the lifetime of the active draft. Normal model rerenders and draft edits reuse the retained inventory and must not enumerate processes. A new inventory is requested only when a new editor/draft becomes active or the user clicks **Refresh processes**. No periodic or overlay-tracking path may request a new inventory.

### User-requested process refresh

The process section includes a compact refresh button using the `↻` glyph with the accessible name and tooltip **Refresh processes**. It sits beside the process-scope label above the helper checkbox and tree.

Clicking it:

1. asks `TargetManagerForm` to rebuild inventory for the current Add/Edit mode;
2. updates the process tree and provider-process choices exactly once;
3. uses the existing viewport restoration to preserve selection, expansion, top visible node, and focus when possible;
4. retains the previous inventory and displays the existing inline process-list warning if enumeration fails.

The button is disabled when no editor is active because the editor control is not present in Browse mode.

### Footer layout

The footer uses a fixed, non-wrapping two-column button container anchored to the right. **Cancel changes** is left of **Save changes**. Both buttons retain their preferred widths, remain on the same row, and remain fully within the editor pane at the manager's minimum size. Error and refresh-warning text continues to use the remaining footer width and may grow vertically without pushing either button off-screen.

## Component Changes

- `TargetEditorControl` owns the compact refresh button and raises an `InventoryRefreshRequested` event. It does not enumerate processes itself.
- `TargetManagerForm` subscribes while an editor is active, retains the active draft's inventory, performs the mode-aware inventory build, and unsubscribes when replacing or disposing the editor.
- `TargetManagerForm.RefreshLiveState()` becomes preview-only. Initial inventory loading remains in the existing model-change/editor-binding path.
- The editor footer replaces the wrapping flow layout with a non-wrapping two-column layout.

This preserves the existing separation: the editor renders inventory, the form knows which windows belong to the current mode, and preview reconciliation remains independent.

## Error Handling

Manual inventory failure leaves the current tree and dropdown contents untouched and appends `Process list could not be refreshed: ...` to the inline refresh warning. Preview refresh errors retain their existing handling. One failure does not block the other subsystem.

## Testing

Regression tests use real WinForms controls and production events:

- repeated `RefreshLiveState()` calls refresh previews but do not call the inventory builder or change the process tree;
- activating Add/Edit populates initial inventory once;
- clicking **Refresh processes** invokes one inventory rebuild and applies the result;
- a manual refresh failure retains the prior tree and shows the inline warning;
- manual refresh preserves process-tree viewport state through the existing `SetInventory` contract;
- at minimum manager size, **Cancel changes** and **Save changes** have the same vertical position, do not overlap, and their bounds remain inside the editor pane.

The focused form/editor suites run first, followed by the complete solution test suite, publish, and a live smoke check.

## Out of Scope

- Changing the two-second preview reconciliation interval.
- Adding configurable refresh intervals.
- Changing process ownership, Windows Terminal tab attribution, provider inference, target persistence, or preview placement behavior.
