# Target-Level Process Activity and Editable Title Matching

## Summary

UsageBars will separate three concepts that are currently coupled in the target editor:

1. **Window identity** decides which top-level windows a target applies to.
2. **Activity processes** decide when the target is active.
3. **Providers** decide which pills are displayed while the target is active.

A target may therefore match a Windows Terminal window and become active when either `omp` or
`codex` is running anywhere beneath the Terminal owner process. When active, every provider checked
on that target is displayed together. The title matcher remains an independent, optional window
condition.

The target editor will use the approved **checkbox tree plus persistent chips** interaction. Picking
a window starts a live preview immediately, and every placement or matching edit updates that
preview without saving.

## Goals

- Make the optional title matcher editable for new and saved targets.
- Make process activity a single target-level OR-list, independent of provider selection.
- Allow activity processes to be chosen from the captured window's process hierarchy.
- Allow a process name to be entered even when that process is not currently running.
- Keep the process list stable until the user explicitly refreshes it.
- Preserve selected activity filters when a process disappears from the current hierarchy.
- Show an unsaved pill immediately after window capture and update it in real time.
- Upgrade existing provider-specific activity rules to the new target-level model.
- Include activity filters in search, duplicate detection, rule descriptions, impact reporting, and
  diagnostics.
- Explain Windows Terminal ownership ambiguity instead of implying reliable per-tab attribution.
- Keep the editor footer usable at supported window sizes and DPI settings.

## Non-goals

- Reliably mapping a descendant process to a particular Windows Terminal tab or pane. Windows does
  not expose a stable top-level-window-to-ConPTY-tab relationship through the process tree used by
  UsageBars.
- Adding boolean expression editing beyond the defined window-AND/activity-OR model.
- Automatically discovering arbitrary semantic relationships between wrapper processes and the
  command they launch.
- Changing usage collection or the contents of Claude and Codex pills.
- Changing global provider enablement in the main configuration.

## Matching semantics

### Window identity

The existing top-level window fields remain window conditions:

- owning process name;
- window class;
- resizable-frame requirement; and
- optional title rule.

All configured window conditions use **AND** semantics. For example:

```text
process = WindowsTerminal
AND class = CASCADIA_HOSTING_WINDOW_CLASS
AND title starts with "WorldGen"
```

The title rule supports the existing `Literal`, `Prefix`, and `Regex` kinds. Matching for literal and
prefix values is case-insensitive. Regex validation and the existing execution timeout remain in
place.

### Activity process list

Each target owns a normalized list of activity process names:

```text
activity process = omp OR codex
```

- An empty list means **Always while the matching window is open**.
- A non-empty list means **When any selected process is running**.
- A process qualifies when it appears anywhere beneath the matching window owner's PID in the
  captured process tree.
- Matching is case-insensitive.
- A trailing `.exe` is removed for storage, display, comparison, search, and duplicate detection, so
  `codex` and `codex.exe` are equivalent.
- Duplicate and blank names are removed.
- UsageBars' own PID remains excluded by the existing process-tree safeguards.

The activity decision is made once per target/window pair. If it succeeds, all checked providers are
emitted for that window, subject to the existing global provider-enable settings. A checked provider
does not own or imply an activity process.

### Multiple rules

The existing winner selection remains: for a provider/window pair, the matching target with the
highest window specificity wins, followed by the newest revision. Activity process names do not add
window specificity because they control time, not window identity.

Strict duplicate identity includes the normalized activity process set. Overlap warnings remain
conservative: two rules that can match the same window and share a provider are potentially
overlapping even when their activity lists differ, because both processes may run simultaneously.

## Persistence and migration

`targets.json` advances from version 2 to version 3.

`TargetDefinition` gains a target-level activity process collection. The in-memory activation model
uses these states:

- `AlwaysWhileOpen` with an empty activity process list; or
- `RequireActivityProcess` with one or more activity process names.

Version 3 no longer uses provider-specific CLI process names or provider-specific activity-title
patterns to decide which provider appears. Provider bindings remain provider selections at runtime.
Compatibility DTOs may retain the old fields only at the version-2 read boundary.

The version-2 loader converts rules as follows:

- `AlwaysWhileOpen` becomes `AlwaysWhileOpen` with an empty activity list.
- `RequireProviderActivity` becomes `RequireActivityProcess` with the case-insensitive union of every
  selected provider's `CliProcessName`.
- Existing provider selections, window conditions, placement, anchor, offsets, enabled state,
  status-bar height, ID, source, and revision are preserved.
- Provider-specific `ActivityTitlePattern` values are not converted into the optional window title
  rule. The former was provider-specific activity evidence with OR-like behavior, while the latter
  is a target-wide window condition using AND semantics; silently translating it would narrow the
  rule incorrectly.
- A migrated rule that contained an activity-title pattern receives a load/management warning so the
  user can review its new process-only activation behavior.

Migration is performed in memory on load and written atomically as version 3 on the next successful
target mutation. The existing invalid/corrupt-file preservation behavior remains. New writes contain
only version-3 semantics.

The user's existing WindowsTerminal target is currently `AlwaysWhileOpen`; migration therefore
leaves it always-on. Checking `omp` and `codex` in the editor changes it to
`RequireActivityProcess` when saved.

## Target editor design

### Window section

The current owning process and class remain visible and read-only because they came from capture or
the saved rule identity.

Title matching becomes a real editor instead of the current read-only summary:

- **Use title matcher** checkbox;
- matcher-kind combo: `Literal`, `Prefix`, or `Regex`;
- editable matcher-value field;
- read-only **Current title** text; and
- **Use current title** action.

Turning the checkbox off stores no title rule. Turning it on initializes a blank literal rule unless
the target already has a rule. A blank enabled matcher is a validation error. Invalid regexes are
reported inline and prevent saving.

### Providers section

Claude and Codex remain independent checkboxes. At least one provider is required. Provider-specific
process combo boxes are removed because they incorrectly suggest that each provider controls an
activity filter.

### Activity section

The activation UI is expressed directly rather than exposing internal enum names:

- **Always while the window is open**; or
- **When any selected process is running**.

Choosing process activation reveals:

- the captured owner-process hierarchy with checkboxes;
- a compact manual refresh button next to the hierarchy label;
- persistent chips for every saved/selected activity process;
- a text field and **Add** action for a process that is not running; and
- scope/status text.

Checking a process node adds its normalized executable name to the OR-list and creates its chip.
Unchecking a node removes that name if it is present. Removing a chip also unchecks every currently
visible node with that normalized name. Multiple PIDs with the same executable create one filter.

The hierarchy is captured once when the window is selected or the existing target is opened. It is
not polled. The refresh button explicitly recaptures it while preserving:

- activity-filter chips;
- tree expansion where corresponding nodes still exist;
- the selected row where possible; and
- the user's current scroll position where possible.

Saved filters that are absent from the refreshed hierarchy remain as chips and are visually marked
`not currently running`; they are never silently deleted.

### Windows Terminal scope

For Windows Terminal, the process tree represents descendants of the owner PID. Tabs, panes, and
even multiple visible Terminal windows may share that owner. The editor therefore says that a
selected activity process may activate every otherwise-matching window under the shared owner.

At runtime, UsageBars applies the target to all such windows. It does not guess which tab or pane owns
the process. Adding a separate optional title matcher is the supported way to narrow top-level
windows when their visible titles distinguish them.

### Layout

The editor uses a scrollable content area and a fixed footer. Labels receive dedicated width and
must not share cramped cells with controls. The resizable-frame option receives a full row so its
label cannot wrap into an unreadable sliver.

Save and Cancel remain horizontally adjacent in the fixed footer at the minimum supported manager
size and under high-DPI scaling. The activity tree gets flexible vertical space; the footer never
enters the scrollable region.

## Live preview

After **Add** and window selection, UsageBars immediately creates an unsaved preview on the exact
captured HWND. The preview displays all currently checked providers and uses the draft's placement,
anchor, offsets, and dimensions.

Every relevant editor change updates the preview synchronously:

- provider checkboxes;
- placement and status-bar height;
- anchor and X/Y offsets;
- window title rule;
- activation mode; and
- activity process chips.

The preview remains pinned to the captured HWND even when the draft's new title or activity
conditions would currently reject it. In that case, status text clearly says **Preview shown; saved
rule would currently be inactive** and identifies the failed condition. This keeps placement tuning
possible without misrepresenting saved runtime behavior.

Editing an existing target uses the existing preview-set behavior across matching windows. Cancel
restores saved pills and leaves `targets.json` unchanged. Save replaces the preview with normal
resolver output. Closing the manager is equivalent to Cancel for unsaved preview state.

## Management, search, and diagnostics

The unified manager remains the sole add/edit/delete surface.

- Search matches normalized activity process names in addition to the existing ID, name, provider,
  application, class, title, placement, position, and activation fields.
- Rule summaries show `activity=always` or `activity=omp|codex`.
- Impact details explain the window conditions and the target-level OR-list separately.
- Exact duplicate checks compare normalized activity sets independent of ordering.
- Overlap warnings identify shared window space and note when activity filters differ.
- Diagnostics include activation mode, activity names, and shared-owner scope warnings.
- Migrated rules with discarded legacy title evidence are visible as needing review until their next
  successful edit/save.

## Validation and failure handling

- Process activation requires at least one valid activity name.
- Activity names are trimmed, normalized, de-duplicated, and limited to executable base names; path
  separators and invalid filename characters are rejected.
- Title regex errors are displayed before Save and use the existing bounded regex validation.
- A failed process-tree refresh keeps the previous hierarchy and chips and displays a non-modal
  warning.
- A disappeared captured window ends only that window's preview and reports the loss; it does not
  save or discard the draft automatically.
- A persistence failure leaves the editor and preview open with the draft intact.
- A v2 migration problem follows the existing partial-load and preservation policies rather than
  overwriting the source file.

## Test strategy

### Model and persistence

- Version-2 always-open migration produces an empty activity list.
- Version-2 provider-activity migration produces the normalized union of provider process names.
- Migration preserves all unrelated target fields.
- Legacy activity-title evidence produces a review warning and is not converted to a window title
  rule.
- Version-3 round trips preserve activity names and title rules.
- Validation rejects process activation with an empty list, invalid names, blank title matchers, and
  invalid regexes.
- Duplicate identity ignores activity ordering and `.exe` spelling differences.

### Resolver

- `omp` OR `codex` beneath a WindowsTerminal owner activates both checked providers.
- No selected descendant process rejects the target.
- An activity process under an unrelated owner does not qualify.
- Empty activity filters preserve always-open behavior.
- Optional title rules combine with window identity and activity using AND.
- Shared-owner Terminal windows all qualify when their other window conditions match.
- Global provider enablement still suppresses a checked provider.

### Editor and manager

- Title controls load, edit, clear, validate, and persist each matcher kind.
- Checking tree nodes and removing chips keep both representations synchronized.
- Manual entry supports a process absent from the tree.
- The hierarchy does not change without the refresh action.
- Refresh preserves filters and avoids forced scrolling.
- Search finds rules by activity process.
- Migrated-rule and shared-owner warnings appear in management details.
- Save and Cancel remain adjacent and visible at minimum size and representative DPI scales.
- The resizable-frame label remains readable.

### Preview

- Capturing a window immediately displays the unsaved pill.
- Providers, placement, offsets, title, and activity changes update the preview.
- A currently inactive draft keeps the preview visible with accurate status text.
- Cancel, manager close, disappearing windows, and save failure preserve the established rollback
  guarantees.

## Acceptance example

For the user's WindowsTerminal target:

1. Open the manager and edit `WindowsTerminal`.
2. Leave both Claude and Codex checked.
3. Choose **When any selected process is running**.
4. Check `omp` in the captured process tree.
5. Check `codex` if present, or add `codex` manually if it is not running.
6. Optionally enable a title matcher to narrow which top-level Terminal windows qualify.
7. Adjust placement and offsets while watching the live preview.
8. Save.

The saved target shows both pills on every otherwise-matching Windows Terminal window sharing that
owner whenever either `omp` or `codex` is found beneath it. When neither process is present, no pill
is shown.
