# Per-Target Pill Position and Target Manager Reliability Design

## Summary

UsageBars will make pill position part of each persisted window target. A target chooses an anchor (`TopCenter`, `TopRight`, or `BottomCenter`) plus signed X and Y offsets. The Add Target and Manage Targets dialogs will expose the anchor and per-target nudges, while the tray's global Position submenu and legacy global offsets will be retired.

The same change will fix `Manage targets...` so it reliably opens one visible manager window. Re-selecting the command will activate the existing manager instead of constructing another ownerless modeless form.

This design extends the dynamic-target design in `2026-08-22-dynamic-window-targets-design.md`; targeting identity, provider inference, matching, activation policy, and usage acquisition remain unchanged.

## Goals

- Make pill placement independently configurable for every target.
- Provide at least Top center, Top right, and Bottom center anchors.
- Preserve the intent of existing desktop, terminal, and IDE layouts through context-aware migration.
- Allow coarse directional nudging and exact X/Y entry in both add and management workflows.
- Persist anchor and offsets in `targets.json` and apply changes without restarting.
- Remove the now-conflicting global Position tray menu.
- Ensure the target manager opens visibly, remains unique, and can be brought forward reliably.

## Non-goals

- Arbitrary screen coordinates detached from the target window.
- Dragging pills directly with the mouse.
- Per-provider position overrides inside one target. All pills attached to a target share its anchor and offsets.
- Changing target identity matching, provider inference, usage refresh, pill contents, colors, or styling.
- Adding more anchors in this change. The persisted enum must reject unknown values safely so anchors can be extended later.

## Persisted model

`TargetDefinition` gains three fields:

- `PillAnchor Anchor`
  - `TopCenter`
  - `TopRight`
  - `BottomCenter`
- `int OffsetX`
- `int OffsetY`

The offsets are device-independent pixels relative to the resolved anchor. Positive X moves right, negative X moves left, positive Y moves down, and negative Y moves up. Both offsets default to zero.

Anchor is deliberately separate from the existing `TargetPlacementKind` (`Desktop`, `Terminal`, or `Ide`). Placement kind continues to describe the host context and support inference/host-specific behavior. Anchor describes only where the pill group is aligned within that host window.

The serialized property names remain stable and human-readable. Store validation accepts only known anchors and limits offsets to a conservative supported range of `-2000..2000` device-independent pixels. Dialog input is clamped to that range. During load, an explicit unknown anchor makes that entry invalid under the store's existing invalid-entry preservation rules; a missing anchor is migrated as described below. Out-of-range persisted offsets are clamped with a diagnostic before the target becomes active.

## Migration and defaults

Targets loaded without an anchor use a context-aware mapping from their existing placement kind:

- `Desktop` -> `TopCenter`
- `Terminal` -> `TopRight`
- `Ide` -> `BottomCenter`

Missing offsets migrate to zero. Newly inferred or captured targets use the same mapping, but the user can change the anchor and offsets before saving.

The legacy global `OffsetX` and `OffsetY` configuration values are not copied into targets. They are ignored by overlay placement after this migration and reset to zero when configuration is next saved. This avoids applying one historical adjustment to unrelated windows with different sizes and anchors.

The migration is idempotent: after the store is saved, every target has an explicit anchor and offsets, and subsequent loads do not recompute them from placement kind. User-selected values therefore remain authoritative even if the target's placement kind later changes.

If the target document format has an explicit schema version, this change increments it and migrates the preceding version. If it does not, missing fields are the migration signal; no parallel migration mechanism is introduced solely for this feature.

## Placement behavior

The resolver continues to select the winning target definition for each provider/window pair. The selected definition's anchor and offsets are carried with the resolved host information into overlay synchronization.

The placement engine first calculates the anchor point inside the target window's usable placement bounds:

- Top center aligns the pill group horizontally to the center and vertically to the top edge/inset.
- Top right aligns the group to the right edge/inset and top edge/inset.
- Bottom center aligns the group horizontally to the center and vertically to the bottom edge/inset.

It then adds `OffsetX` and `OffsetY` to the anchored coordinates. Existing host-specific insets and strip calculations remain responsible for avoiding title-bar controls or locating the IDE status area; the anchor is resolved within those calculated bounds rather than bypassing them.

After offsets are applied, the final rectangle is constrained enough to retain an accessible part of the pill group within the target window. The stored offset is not rewritten when temporary clamping occurs, so resizing a window restores the intended relative adjustment.

All pills belonging to the same target are laid out as one group using the existing ordering and spacing rules. The group, rather than each individual pill, is anchored and nudged.

## Add Target interaction

The capture confirmation/editor dialog adds a Position section containing:

- An Anchor selector with Top center, Top right, and Bottom center.
- Four directional nudge buttons.
- Numeric X and Y fields for precise signed values.
- A `Reset offset` command that restores X and Y to zero without changing the anchor.

Each nudge click changes the corresponding offset by four device-independent pixels:

- Left: X - 4
- Right: X + 4
- Up: Y - 4
- Down: Y + 4

Nudge buttons and numeric fields update the same draft values. Numeric input is validated before save and cannot exceed the supported range. The initially selected anchor comes from the inferred placement mapping, and offsets start at zero.

The target is not persisted until the user confirms the dialog. Cancel discards all draft position changes along with the rest of the draft target.

## Manage Targets interaction

The target list adds a concise Position column such as `Top right (+8, -4)`. The selected target's edit surface exposes the same anchor selector, nudge buttons, X/Y numeric fields, and reset command used by Add Target. Shared controls or a shared position-edit model should be used so validation and labels do not diverge between dialogs.

Saving an edit updates the target atomically through `TargetStore`. The store change notification triggers an immediate tracking/repositioning pass. Cancel leaves the persisted target and current overlay unchanged.

Changing the target's placement kind does not silently replace a user-selected anchor. A context-aware anchor is only a creation/migration default. If the edit UI offers an explicit reset-to-context-default action in the future, that will be a separate command.

## Reliable manager window lifecycle

The current tray event reaches the controller, but the manager is created as an ownerless modeless WinForms form in an application whose primary lifetime and ownership are WPF-based. That leaves no stable owner or activation policy and can result in a manager that does not surface as a usable foreground window.

The controller will own a single manager reference and marshal all manager operations onto the UI dispatcher:

1. When `Manage targets...` is selected, execute the open/activate operation on the UI thread.
2. If a non-disposed manager already exists, restore it if minimized, call `Show`, bring it to front, and activate it.
3. Otherwise construct one manager, assign a stable UsageBars-owned native owner, register its closed/disposed cleanup, and show it modelessly.
4. When the manager closes, clear the controller reference only if it still points to that instance.
5. Application shutdown explicitly closes the manager before disposing tray/controller resources.

The stable owner may be the existing hidden WPF/native owner used for UsageBars auxiliary windows or a dedicated invisible owner created for this purpose. It must have application lifetime, must not appear in Alt+Tab, and must not cause closing the manager to exit UsageBars.

Exceptions during construction or display are logged and surfaced through the existing user-visible error path instead of being swallowed. Diagnostic entries cover request received, manager created, existing manager activated, manager closed, and display failure.

## Tray behavior

The global `Position` submenu, its nudge commands, and `Reset position` command are removed. The tray retains target capture and `Manage targets...`, which are now the only position-editing entry points.

Global configuration properties may remain readable for backward-compatible deserialization, but they no longer influence rendering or appear in the UI. They should be marked obsolete in code or isolated in migration compatibility so new features cannot accidentally reuse them.

## Error handling

- Unknown or invalid saved anchors are handled through the target-store validation path and reported with the target ID.
- Invalid numeric dialog input prevents save and identifies the affected field.
- A failed target save leaves both the previous in-memory definition and current overlay position active.
- Manager construction/display failures are logged and presented without terminating the tray application.
- A target whose offsets place its group beyond the current host bounds is temporarily clamped for accessibility; its persisted values remain intact.
- Store migration uses the existing atomic-write and corrupt-document preservation behavior.

## Testing strategy

Automated tests cover:

- Serialization round trips for all three anchors and signed X/Y offsets.
- Migration of missing position fields using Desktop, Terminal, and IDE context-aware defaults.
- Missing offsets becoming zero and migration remaining idempotent after save/reload.
- Legacy global offsets no longer affecting overlay coordinates.
- Anchor calculations for different target and pill-group sizes.
- Offset direction, four-unit nudge increments, bounds validation, and final accessibility clamping.
- Multiple provider pills being positioned as one anchored group.
- Add/editor draft behavior, cancel behavior, reset, validation, and saved mutation.
- Manager position summaries and edits flowing through `TargetStore` notifications.
- The tray model no longer containing a Position submenu.
- The manager coordinator creating only one instance, activating an existing instance, clearing a closed instance, dispatching to the UI thread, and reporting display failures.

A Windows smoke-test verifies:

1. Select `Manage targets...` and confirm a visible manager opens.
2. Select it again and confirm the same manager comes to the foreground without duplication.
3. Close it and confirm it can be opened again while UsageBars remains running.
4. Capture desktop, terminal, and IDE targets and confirm their initial anchors are Top center, Top right, and Bottom center respectively.
5. Change each anchor in the Add and Manage dialogs and confirm the saved result takes effect immediately.
6. Nudge in all four directions, enter exact positive and negative X/Y values, reset offsets, and verify movement semantics.
7. Restart UsageBars and confirm each target retains its anchor and offsets.
8. Resize and move target windows across different-DPI monitors and confirm placement remains relative and accessible.
9. Confirm the tray has no global Position submenu.

## Acceptance criteria

- `Manage targets...` always opens or activates one visible manager window.
- Every persisted target has an explicit supported anchor and signed X/Y offsets.
- Existing and newly inferred targets receive the approved context-aware default mapping.
- Add Target and Manage Targets both support anchor selection, four-pixel directional nudging, exact X/Y entry, and offset reset.
- Saved position changes apply without restarting and survive application restart.
- Global offsets no longer affect rendering, and the tray Position submenu is absent.
- Placement remains relative to the target window and retains an accessible pill region after resizing or offsetting.
- Existing targeting, inference, provider association, matching, and usage behavior remain unchanged.
- Automated tests and the Windows smoke-test checklist pass.
