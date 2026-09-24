# Task 6 Report: Unified Target Manager Integration

## Status

PASS. The tray and overlay controller now route every target workflow
through one modeless manager. The retired modal capture flow and its obsolete tests are deleted.

## Implementation

- Reduced the Targets tray submenu to **Manage targets...**, followed by its separator and
  enabled/disabled summary. Removed `Tray.AddTargetRequested` and the controller subscription.
- Replaced all Task 5 `#if false` controller bridges and removed the old modal editor/capture fields,
  methods, flags, diagnostics path, and single-preview tracking.
- Added `TargetManagerComposition`, the runtime seam used by `OverlayController` to construct:
  - a real `TargetManagementWorkspaceModel`;
  - a real `TargetPreviewSet` of per-window `TargetPreviewSession` instances;
  - native live top-level-window enumeration;
  - captured-owner and edit-mode process inventories built from real process snapshots;
  - capture contexts inferred through `ProviderInference.CreateDraft`;
  - preview-change tracking callbacks so normal pills are reconciled after preview ownership changes.
- Wired `TargetManagerCoordinator` to the real `_windowPicker.PickAsync`, capture-context builder,
  manager factory, modeless owner, dispatcher, diagnostics, and warning callback.
- Added queued preview-change tracking to avoid recursively entering `Track()` when live refresh
  changes the suppression set.
- Updated `Track()` to refresh manager live state and remove every coordinator-suppressed HWND from
  normal target resolution through `TargetPreviewSuppression.RemovePreviewWindows`.
- Preserved shutdown ownership order: coordinator first, then picker, then the normal pill hosts.
  Manager disposal therefore clears and disposes all previews before either host layer is torn down.
- Deleted `TargetCaptureFlow.cs` and `TargetCaptureFlowTests.cs`. Save/failure/cancel/close cleanup is
  already represented in `TargetManagementWorkspaceModelTests`, `TargetManagerFormTests`, and
  `TargetManagerCoordinatorTests`; the still-relevant multi-HWND suppression behavior moved to
  `TargetPreviewSuppressionTests`.
- Removed obsolete modal-only gate/editor-slot production helpers and their tests.
- Rewrote README target-management guidance around the manager's Add button, singleton modeless
  behavior, multi-window edit previews, per-target position/offset/provider-process choices, and
  stable process-tree refresh.

## TDD Evidence

1. Baseline GREEN: `dotnet test UsageBars.sln --nologo --verbosity minimal` passed 568/568.
2. Focused RED: the new tray, multi-HWND suppression, and real composition tests failed compilation
   because `TargetManagerComposition` did not exist. This was the expected missing production seam.
3. Focused GREEN: 64/64 passed for
   `TargetViewModelTests|TargetPreviewSuppressionTests|TargetManagerCompositionTests|TargetManagerCoordinatorTests|TargetManagerFormTests`.
4. Initial full GREEN: 564/564 passed after deleting eight obsolete modal-flow/gate tests and adding
   four focused unified-integration tests.
5. Initial `git diff --check` found one trailing blank line in `TargetResolverTests.cs`; it was
   removed before final verification.

## Focused Coverage Added

- The target submenu exposes only **Manage targets...**.
- Suppression removes all provider/window entries for every previewed HWND while retaining normal
  entries for non-preview windows.
- Capture-context composition uses provider inference and one process/window snapshot to build the
  captured owner's real grouped inventory, including sibling-window scope warnings.
- The real unified-manager factory owns a real preview set and notifies normal tracking when its
  suppression set changes.

## Review

The first fresh read-only review found no Critical issues and three Important integration defects:

1. preview/inventory enumeration used raw top-level windows instead of normal tracking eligibility;
2. a newly created preview session was published as suppressed before its first update succeeded;
3. a repeated Manage command could re-show the hidden manager while the picker remained active.

It also found two Minor gaps: suppression-removal tracking was not asserted at the composition seam,
and README overstated successful-capture selection restoration while omitting two search fields.

### Fix Round 1

- Added `TargetWindowSource`, one injected production eligibility seam used by both `Track()` and the
  manager's preview/inventory snapshot source. Its regression failed to compile before the seam and
  passed after both consumers used it.
- Made preview creation transactional. The regression failed because the thrown first update left
  HWND 10 suppressed; after the fix the session is disposed and no suppression is published.
- Prevented `TargetManagerCoordinator.OpenOnUiThread()` from showing a manager while `_pickActive`.
  The regression failed with two Show calls and passed with one hidden manager after the fix.
- Extended the real composition test through manager disposal. A mutation removing the
  `PreviewHandlesChanged` subscription failed with zero tracking callbacks; restoring the runtime
  subscription produced callbacks for both suppression addition and removal.
- Corrected README capture-selection semantics and documented provider/activity-process search.
- Focused fix-round suite: 74 passed, 0 failed, 0 skipped.

A second fresh read-only review found no remaining Critical or Important issues. Its two Minor
findings were closed before commit: Save and Cancel now assert their suppression-removal callbacks
(a mutation removing the reconciliation notification failed both tests), and the README grammar was
corrected. A final read-only self-review found no remaining requirement or lifecycle gaps.

## Final Verification

- Required command: `dotnet test UsageBars.sln --nologo --verbosity minimal`
- Result: 567 passed, 0 failed, 0 skipped.
- `git diff --check`: clean; only expected LF-to-CRLF working-copy notices were printed.
- Obsolete-symbol audit: no Task 5 `#if false` bridge, modal capture/editor state, separate Add event,
  `TargetCaptureFlow`, single-HWND suppression API, or old gate/editor-slot helper remains.
