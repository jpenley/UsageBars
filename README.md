# UsageBars

A single portable `.exe` that draws your remaining **Claude** and **Codex** usage as small bars,
tucked into each app's title bar.

```
Claude Desktop     5h ▰▰▰▰▱ 72%   7d ▰▰▱▱▱ 45%
Codex / ChatGPT    7d ▰▰▰▰▰ 91%
```

Each provider gets its own pill on its own window, so both can be on screen at once. The bars show
what's **left**, not what's used. A triangle marks the time remaining until reset. The fill
changes smoothly from red below the triangle, through yellow near it, to green above it.

## Running it

```bash
dist\UsageBars.exe
```

No install, no runtime, no admin. Everything is in the one file. It parks itself in the
notification area; **right-click the tray icon → Exit** to quit.

### Requirements

| | |
|---|---|
| .NET | **none** — the runtime is compiled into the exe (verified by running with `DOTNET_ROOT` pointed at a non-existent path) |
| VC++ redistributable | none — `vcruntime140_cor3.dll` ships inside |
| Admin rights | none |
| OS | Windows 10 1703+ or Windows 11, x64 (`GetDpiForWindow` and per-monitor-v2 DPI). Build with `-Runtime win-arm64` for ARM |
| Optional | the **Claude Code CLI**, only for the `/usage` fallback source. Without it the pill shows an install link |
| Optional | **Codex** (desktop app or CLI), for the Codex pill. Without it that pill shows an install link |

### Fully portable

Everything the app writes lives in **`UsageBars.data\` beside the exe**, so the folder can be
copied to a USB stick or a share and leaves nothing behind:

```
UsageBars.exe
UsageBars.config.json
UsageBars.data\winpty\{winpty.dll, winpty-agent.exe}   extracted at first run
UsageBars.data\usage-history.json                      burn-rate samples (scraped meter)
UsageBars.data\bar-history.json                        burn-rate samples (Codex windows)
UsageBars.data\targets.json                            persistent window-target rules
UsageBars.data\probe\                                  cwd for the hidden Claude Code session
UsageBars.data\diagnostics\usagebars*.jsonl            rotating event log (1 MB x 3)
```

The registry is read-only (the theme setting) unless you turn on **Start with Windows**, which
writes exactly one value — see below. There's no service and no COM registration. If the exe's own
folder isn't writable (Program Files, a read-only share) it falls back to `%LOCALAPPDATA%\UsageBars`;
existing history there is adopted automatically so moving the exe doesn't lose your burn-rate data.
`USAGEBARS_DATA` overrides the location.

The one thing outside that folder is Windows' own single-file cache: ~8 MB of WPF native DLLs
unpacked to `%TEMP%\.net\UsageBars\<hash>\` on first launch. That's inherent to
`PublishSingleFile`, is keyed per build, and is a temp cache rather than an install — but it isn't
zero, so it's worth knowing about. Publishing without `IncludeNativeLibrariesForSelfExtract` would
eliminate it at the cost of ~15 loose files next to the exe.

### Start with Windows

Tick **Start with Windows** in the tray menu. It writes the current-user Run value
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run\UsageBars` and nothing else — no admin, no
service, no scheduled task. Clearing the tick removes it.

If the write fails (group policy, a locked-down profile) the tick reverts rather than claiming a
state the registry doesn't have, and a balloon says why.

#### When the exe moves

A Run value is an absolute path, so moving or renaming the folder leaves it naming something that
isn't there any more — and Windows ignores a missing Run target **completely silently**: no error,
no notification, nothing in the event log. Ticked menu item, app that never appeared. This happened
three times here across two renamed build directories, which is why the tick no longer answers "does
a registry value exist" but "what will actually happen at the next logon":

| Menu item | Meaning |
|---|---|
| **Start with Windows** *(ticked)* | An entry naming this exe. This copy starts |
| **Start with Windows** *(unticked)* | No entry. Nothing starts |
| **Start with Windows — registered copy is missing** | The entry names an exe that is gone. Repaired automatically on the next launch; only shown if the rewrite itself failed |
| **Start with Windows — another copy is registered** | The entry names a different UsageBars that *does* exist. That one starts, not this one |

A missing target is repointed at the running copy on launch, logged as `startup / repaired`. A
working registration for another copy is deliberately **not** rewritten — otherwise launching a
Debug build once would quietly inherit your startup — so it is only surfaced, logged as
`startup / other-copy`. Either way, clicking the unticked item registers this copy.

## Where the numbers come from

Claude and Codex are read completely differently, and each pill only ever shows its own provider.

### Codex — the app-server (free, real, includes reset times)

UsageBars runs `codex app-server` as a hidden child process and speaks JSON-RPC to it over stdio:
`account/rateLimits/read` for the current numbers, plus the `account/rateLimits/updated`
notifications the server pushes whenever a request moves them. It reuses Codex's own sign-in, so
there are no cookies, tokens or API keys involved.

This is strictly better than anything available on the Claude side: it is free to call, needs no
scraping, and every window carries a real `resetsAt`. Polling is therefore just a safety net
(`CodexRefreshMinutes`, default 2) on top of the pushed updates.

Candidate executables are tried in order until one completes a handshake — the running ChatGPT
desktop bundle, a running `codex` process, `codex.exe`/`codex.cmd` on `PATH`, then the Store
package. A file existing proves nothing: the Store copy under `WindowsApps` denies direct
execution, so it is discovered, attempted, and skipped in favour of one that works. Both
`app-server --stdio` and bare `app-server` are attempted, so UsageBars isn't pinned to one CLI
generation.

Beyond the bars, the response also carries the plan type, any spend-control budget (drawn as a
`spend` bar, the Codex analogue of Claude's credit meter), per-model metering buckets and the
number of redeemable rate-limit reset credits. Per-model and reset-credit metadata remain in
diagnostics. The tray shows current remaining percentages.

### Claude — two sources, in priority order. The first that has data wins.

#### 1. `plan-usage-history.json` (preferred — free, no side effects)

Claude Desktop writes this file every ~5 minutes. Two install layouts are checked:

| Label | Path |
|---|---|
| `claude-msix-history` | `%LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude\plan-usage-history.json` |
| `claude-roaming-history` | `%APPDATA%\Claude\plan-usage-history.json` |

Existing candidates are sorted by last-write time, newest first, so if both installs are present
the more recently active one wins. The MSIX package folder is globbed rather than hardcoded,
because the publisher hash differs between Store listings.

Reading is deliberately defensive: opened read-only with `FileShare.ReadWrite | FileShare.Delete`
so it never blocks Claude Desktop's own writes, size capped at 1 MB, and up to 3 attempts with
50 ms / 150 ms backoff to ride out a torn write before falling through to the next candidate.

Parsing accepts schema version 1 or 2 and keeps the sample with the largest `t` (Unix ms), reading
`fh` (five-hour) and `sd` (seven-day) — nested under `u` in version 2, on the sample itself in
version 1. Values that are non-finite or outside 0–100 are rejected.

The file carries no reset timestamps, but it does carry several days of five-minute samples, and
that is enough to **derive** them — see below.

**This file is empty on usage-based and enterprise seats.** Claude Desktop writes a sample every
5 minutes but leaves `u` as `{}`, because a spend budget replaces the 5h/7d rate-limit windows.
That's what source 2 is for.

#### 2. Claude Code's `/usage` screen (fallback — real, all-device)

When the file has no values, UsageBars drives the **Claude Code CLI** in a hidden pseudo-console,
sends `/usage`, and scrapes the meter:

```
Claude Code and Cowork credit
█▊                                    5% used
One-time credit · Expires October 14
```

This is server-backed and reflects **all** your usage, including the desktop app — not just this
machine. It reuses Claude Code's own authentication, so there are no cookies, tokens, or API keys
involved anywhere in UsageBars.

Parsing is deliberately **label-agnostic**: it matches `N% used` regardless of the surrounding
text, so when the credit pool runs out and Claude switches that row to a spend limit, the bar
keeps working and just relabels itself (`credit` → `spend`).

Because each scrape spins a hidden Claude Code session for ~20 seconds, it runs **every 15
minutes** (configurable), and the last good reading is cached so the pill shows a number
immediately on start. Repeated failures back off exponentially rather than re-running a 20-second
session on the normal cadence. Scraping is skipped while source 1 has fresh values.

The meter's own second line is parsed for a deadline — "One-time credit · Expires October 14",
"Resets 3pm" — since there is no sample history behind a scrape to derive one from.

## Reset times

Every reset UsageBars shows is **measured**. Nothing is inferred from a configured weekday.

| Source | How the deadline is known |
|---|---|
| Codex | `resetsAt` on each window, straight from the app-server |
| Claude file | Derived from the sample series (below) |
| Claude scrape | Parsed from the meter's own "Expires …" / "Resets …" line |

### Deriving Claude's from the data

Claude's 5h and 7d windows are rolling sessions: the clock starts on first use after the previous
window expired, and closes exactly one window-length later. Nothing reports that deadline, but it
is recoverable. A reset appears in `plan-usage-history.json` as utilization dropping to zero, and
the next deadline is one window-length after usage *resumed* — which is not the same as one
window-length after the reset, because the gap between them is idle time.

Real data from this machine, four days of samples:

```
07/30 06:10:30   13 -> 0     reset observed
07/30 15:50:31    7 -> 0     reset observed
07/30 16:35:31    0 -> 4     window starts here
                             => next reset 21:35, five hours later
```

Three cases report **unknown** rather than guessing, because each is genuinely unknowable: no usage
recorded yet, history that doesn't reach back to the start of the current window, and a deadline
already passed without the drop being observed (which happens when Claude Desktop is closed over
the boundary and simply stops writing samples).

`ResetDayOfWeek` / `ResetHour` survive only as an opt-in fallback behind `UseConfiguredResetDay`,
off by default. It never moves the bar, the rate, or the warning, and when it is used the
diagnostics label it `(not measured)` so a guess is never presented as a measurement.

## Behaviour

| | |
|---|---|
| Where it appears | Each provider's own desktop app title bar, **and** terminal windows running its CLI — see below. Two pills can be on screen at once |
| Placement | Each target persists its own **Top center**, **Top right**, or **Bottom center** anchor plus exact X/Y offsets. New Desktop, Terminal, and IDE targets default to Top center, Top right, and Bottom center respectively. `RightReservePx` protects title-bar window controls; `TitleBarHeight` supplies the fallback strip height. A strip too narrow to hold the pill hides it rather than drawing over the close button |
| IDEs | On **Rider** and **VS Code**, both budgets appear whenever the main frame is open — no session needs to be detected — and the seeded targets use Bottom center in the measured **status bar**, because both fill their own title bar end to end and leave nowhere up there for them to go (see below) |
| Click-through | Clicks and drags pass straight to the host's title bar — **except** the action icon. Done by answering `WM_NCHITTEST` with `HTTRANSPARENT` outside the icon, rather than making the whole window transparent (which would make the icon unclickable) |
| Focus | `WS_EX_NOACTIVATE` + `WS_EX_TOOLWINDOW`: never takes focus, never appears in Alt+Tab |
| Visibility | **Always on** — stays put while you work in other apps, so you can watch usage as it runs. Goes away with its host when that is minimised or closed, and is covered by whatever covers the host. Under the default `Owned` attach mode all of that is the window manager's doing rather than ours |
| Tracking | Follows moves, resizes, maximise and monitor changes; per-monitor-v2 DPI aware |
| Theme | Follows the Windows light/dark app setting |
| Width | Adapts to the number of bars and their labels, re-centring as it changes |

## Window targets

`UsageBars.data\targets.json` version 3 is the persistent authority for deciding which windows receive
Claude and Codex pills. It contains durable matching rules, not HWNDs, PIDs, or other transient
process state, so a saved target matches a newly opened instance of the same application. Older
files migrate automatically in memory and version 3 is written atomically on the next successful
target change.

Each rule separates three decisions:

- **Window identity** uses the owning process, window class, resizable-frame requirement, and an
  optional editable title matcher. These conditions are combined with **AND**.
- **Activity** is either **Always while the window is open** or **When any selected process is
  running**. A process-activated target owns one normalized **OR-list**, such as `omp OR codex`.
- **Providers** are display outputs. When a target is active, every checked provider is shown;
  checking Claude or Codex does not add a hidden provider-specific activity rule.

Title matching can be turned off or edited as case-insensitive **Literal**, **Prefix**, or bounded
**Regex** matching. **Use current title** copies the captured title into the value field. A title
rule remains an independent window condition, so it can narrow otherwise-shared Terminal windows
without changing process activity.

Version-2 always-open rules migrate with an empty activity list. Provider-activity rules migrate
with the normalized union of their provider process names. Legacy provider-specific activity-title
patterns are intentionally not converted into target-wide title conditions because that would make
the rule narrower; affected rules are marked with a management warning for review. IDs, providers,
window conditions, placement, offsets, source, enabled state, and revision are preserved.

On the first run that has no `targets.json`, UsageBars creates it by **seeding** the familiar
Claude Desktop, Codex/ChatGPT, terminal, editor, Rider, and VS Code rules. The legacy config fields
`AttachToClaudeCodeTerminals`, `AttachToCodexTerminals`, and `AlwaysShowOnIdes` are **first-run seed
inputs**, not live targeting controls. Changing one after `targets.json` exists never rewrites,
re-enables, or recreates its saved targets; a deleted seeded target stays deleted.

`TerminalTitlePattern` and `CodexTitlePattern` are different: they are capture-time detection hints
consulted whenever **Add** captures a new window and tries to infer Claude or Codex. They are not
persisted as version-3 title conditions and never become live provider activation rules, so changing
them can affect future capture suggestions but does not rewrite saved targets. The global
`EnableClaude` and `EnableCodex` switches remain live and suppress that provider across every
enabled target.

Every target owns its semantic placement and offsets. Desktop targets default to **Top center**,
terminal targets to **Top right**, and IDE targets to **Bottom center**, all at `(0, 0)`. Changes are
stored with that target and survive UsageBars restarts; there is no global placement override.

### Add and manage targets

Use **tray icon → Targets → Manage targets...** for every target workflow. This opens one reusable,
modeless two-pane manager; choosing the command again restores and activates that same window rather
than opening a duplicate. The tray submenu has no separate capture command.

Choose **Add** inside the manager to hide it temporarily and start the crosshair picker. The next
left click is intercepted, so the clicked application or child control is not activated. UsageBars
captures its top-level process and window class, restores the same manager with its previous bounds
and search, and immediately previews the draft on that window. Press **Escape** or right-click
before selecting a window to restore the manager's prior selection without changing saved targets.

The left pane finds saved rules that already apply or could overlap the proposed rule. It searches
by name, full or partial rule ID, process, window class, title matcher, provider, and target activity
process name. The strongest applicable rule is selected for editing by default, which
prevents accidental duplicates; **Create more-specific rule** deliberately starts a new ID instead.
Enable/Disable and confirmed Delete in this pane persist immediately.

The right pane preselects Claude for Claude Desktop and Codex for Codex/ChatGPT, can infer providers
from an existing matching active session, and otherwise lets you choose Claude, Codex, or both.
It also provides the optional title controls described above. Each target stores its own placement,
anchor, exact X/Y offsets, provider selections, and single target-level activity OR-list.

Choosing process activation reveals a checkbox tree captured from the selected window owner's
process hierarchy. Checking any occurrence adds one persistent, normalized chip; duplicate PIDs
with the same executable still create only one filter. A chip stays present and is marked **not
currently running** when its process disappears. Type a base executable name and choose **Add** to
create a filter that is not currently in the tree. The list is captured once and remains still
until the compact refresh button is clicked; refresh preserves chips, expansion, selection, and the
visible position where possible.

The hierarchy covers every descendant under the top-level owner, including all tabs and panes.
Windows Terminal may share one owner with sibling windows, so the manager warns that a selected
process may activate every otherwise-matching window under that shared owner. UsageBars does not
claim a reliable process-to-tab mapping; use the optional title condition when visible titles can
narrow the rule.

Every right-pane change updates the pinned temporary pill in real time: provider selection, title
rule, activity mode and chips, enabled state, placement, resizable-frame requirement, IDE status-bar
height, anchor, and X/Y offsets. Both checked providers preview immediately. When the current title
or process state would reject the saved rule, the preview stays available for placement tuning and
dims with **Preview shown; saved rule would currently be inactive** plus the failed condition. A
draft with no inferred provider uses a neutral Preview pill. Position controls choose Top center,
Top right, or Bottom center, move four pixels per arrow click, accept exact positive or negative X/Y
values, and include **Reset offset**. The resizable-frame option has its own readable row, while
**Cancel changes** and **Save changes** stay side by side in the fixed footer at the minimum manager
size. Cancel discards only the temporary draft and restores the saved pill; Save performs one add or
update and hands every previewed window back to normal targeting.

Editing a saved rule previews it on every currently open window matching the draft, not just one
captured HWND. As the match, provider, process, placement, or offsets change, windows enter and leave
the preview set together and their normal pills are suppressed only while their temporary previews
exist. A valid rule with no open match remains editable and says that no preview window is available.

Enable/Disable and confirmed Delete persist immediately. The details pane calls out overlapping
enabled rules, and closing the manager discards only an unsaved draft and its temporary previews.

For the same provider and window, exactly one pill is created. When several enabled targets match,
the most specific one wins (title match, then process, class, and resizable-frame constraints);
an equally specific tie goes to the most recently edited rule. Claude and Codex may each win once
on the same window, producing their usual side-by-side pills.

### Recovery and diagnostics

If `targets.json` is malformed, unreadable, has an unsupported version, or contains invalid
entries, UsageBars keeps usable rules in memory and reports the issue in **Copy diagnostics**. On
the first successful add, edit, enable/disable, or delete, it preserves the old file beside it as
`targets.corrupt.<timestamp>.json` or `targets.invalid.<timestamp>.json` and atomically writes a
fresh valid `targets.json`. To recover manually, close UsageBars, copy the recovery file somewhere
safe for inspection, then remove or rename `targets.json` and relaunch to seed a fresh set; use
Manage targets to recreate only the rules you want. Diagnostics lists the target-file location,
enabled/disabled counts, each rule's match/placement/anchor/offsets/activation/revision, recovery
issues, manager lifecycle events, and why an enabled provider had no matching host.

## Always on

A pill doesn't need its host focused. It stays on screen while you work elsewhere, so you can
watch usage climb in the background. Every qualifying window carries its own pill, so there is no
"which window has it right now" to follow — the desktop app and each terminal each have theirs, and
focus never enters into it.

With `AlwaysOn: false` each pill instead hides unless its own host is the focused window. Same rule
as before; it is just evaluated per pill rather than by choosing one winner.

It is hidden whenever another window **covers that patch of screen**, so it never floats over
unrelated apps: visible exactly where its host's title bar is unobstructed, gone when the host is
buried. The Claude and Codex pills never hide each other — occlusion testing skips windows
belonging to UsageBars itself.

### How a pill is kept above its host

Three relationships, tried best-first per host and reported in diagnostics as the *attach mode*.
`PlacementMode` pins one if a particular host misbehaves.

| Mode | Mechanism | Occlusion handling |
|---|---|---|
| `Owned` | The pill is an **owned window** of the host (`GWLP_HWNDPARENT`, cross-process) | None needed — the window manager does it |
| `ZOrderAbove` | Inserted immediately above the host, skipping the topmost band | None needed — it is below every other app |
| `Topmost` | Always-on-top | Explicit occlusion test, `HideWhenCovered` |

**Ownership is strongly preferred**, because it makes stacking the window manager's problem rather
than ours. An owned window is kept directly above its owner permanently: activation doesn't disturb
it, nothing needs re-asserting, it is occluded by other applications automatically, and it hides
and restores with its owner for free. With two pills owned by two different hosts they order
against those hosts rather than against each other.

The documented cost of ownership is that **an owned window is destroyed along with its owner**, so
closing the host app should take the pill with it. `ProviderPill` therefore rebuilds its window on
`Closed` and re-attaches on the next tick.

**Measured, that does not happen here.** Closing and reopening the Codex app three times — window
handle going `0x3025A` → `0x603AA` → `0x1104FA`, so genuinely destroyed and recreated — produced no
rebuild: the pill survived and simply re-attached to each new host window. Ownership set after
creation via `SetWindowLongPtr(GWLP_HWNDPARENT)` evidently gives the z-order relationship without
the destruction propagation that ownership passed to `CreateWindowEx` would.

The rebuild path is kept as a safety net rather than removed, because that behaviour is a quirk
rather than a documented guarantee and could differ by host or Windows version. It logs
`window / rebuilt` if it ever fires. The host-change log line records both handles so the two
cases stay distinguishable.

Ownership is not always granted, which is why the fallbacks exist. Inserting directly above the
host has a known weakness — **activating the host raises it back over the pill**, so it must be
re-asserted on every tick — and it is refused outright when the host is itself topmost, since
there is no reachable position above it. Topmost is last because it is the only mode that has to
re-derive occlusion by hand, and every window that briefly appears above the host (snap previews,
drag images, tooltips, toasts) is a chance to get that wrong.

### Why not a child window

`SetParent` with `WS_CHILD` looks like the stronger relationship, and is worse here. Cross-process
parenting attaches the two threads' input queues, so a hang in the host's UI thread would hang the
pill; and a child window is clipped to its parent's client area, which makes the real-OS-caption
case (classic console windows, where the caption is *non-client*) impossible. Ownership gives the
stacking guarantee without the coupling.

Occlusion deliberately ignores DWM **cloaked** windows: background UWP windows report
`IsWindowVisible = true` while drawing nothing, and without that check stray cloaked windows would
blank the pill for no reason.

It is also suspended entirely while a window is being dragged or resized, between
`EVENT_SYSTEM_MOVESIZESTART` and `EVENT_SYSTEM_MOVESIZEEND`. Dragging a window near a screen edge
makes Windows put a **snap preview** on screen — a real top-level window, above the host, covering
exactly the strip the pill sits in. Counting that as "something is covering the pill" hid it for
the duration of every such drag and brought it back on release. Drag feedback is transient chrome,
not a window competing for the space. The suppression is timestamped rather than a flag, so a
dropped `MOVESIZEEND` can't leave it suspended indefinitely.

Every hide is recorded in the diagnostics log with its reason (`covered by MozillaWindowClass
0x780286`, `title bar too narrow to hold the pill`), because a pill that vanishes for a bad reason
is otherwise impossible to diagnose after the fact.

Set `AlwaysOn: false` for the old behaviour (only while the host is focused), or
`HideWhenCovered: false` to make it a true always-on-top badge visible no matter what's in front.

## Terminals running a CLI

The initial targets cover Windows Terminal, conhost, ConEmu, mintty, Alacritty, and WezTerm; use
the manager to retain, change, or capture other terminal rules. At runtime, only the saved version-3
target is authoritative: its process, class, resizable-frame requirement, and optional title rule
are window conditions, while its target-level activity list decides whether any selected process is
running beneath the window owner. When active, every checked provider is displayed.

When **Add** captures a new window, provider/activity suggestions use two inference signals:

1. **Capture-time title hint.** The current `TerminalTitlePattern` or `CodexTitlePattern` can infer
   its provider when the corresponding CLI is also running, so a stale title alone cannot preselect
   a provider. The pattern is not copied into the draft's title matcher or activity rule and is not
   consulted after the target is saved.

   Two measured caveats. Claude Code shows `✳` only when **idle** — while it is working it animates
   the title with a braille spinner (`⠐ Access previous session from different folder`), so the
   default hint matches the braille block too. And the **Codex CLI sets no title at all** — a live
   Codex session's window read `someuser`, the shell's own title — so `CodexTitlePattern` rarely
   provides capture-time evidence and Codex is normally inferred by signal 2.
2. **Capture-time process ancestry.** A matching CLI beneath the captured window owner can infer
   the provider and add that CLI name to the draft's target-level activity list. This also covers
   editors whose integrated terminal is running one.

After capture, the title matcher and activity list are edited and saved independently. An empty
activity list means always while open; otherwise the saved names use OR semantics. The two config
patterns never become live provider activation rules.

### Why capture inference also considers the title

Process ancestry alone doesn't work for the common cases:

- **Windows Terminal** is COM-activated from `svchost`, so it is not itself under the shell —
  measured: `WindowsTerminal.exe`'s parent is `svchost`. Whether the CLI is under *it* depends on
  how the session was launched. Opening a tab from within WT does parent the shell to it
  (`codex <- node <- pwsh <- WindowsTerminal`, so ancestry works); a console app that WT attaches
  to afterwards does not.

  Ancestry also can't distinguish **which window**: WT runs all of its windows in one process, so
  "a session is running under this process" is the only question that can be answered. That is why
  two providers can both credit the same window — see [Both pills on one
  terminal](#both-pills-on-one-terminal).
- **Classic conhost** makes `claude.exe` and `conhost.exe` *siblings* under the shell, so the
  window's owner isn't an ancestor either.

Ancestry is also actively dangerous on its own, and a denylist of "processes that can't be hosts"
is not enough to tame it. Measured failure:

```
codex <- node <- cmd <- node <- node <- cmd <- firefox
```

A build chain launched from Firefox put a `codex` process six levels beneath the browser, so the
Codex pill attached to the Firefox window whenever it gained focus. `explorer.exe` is the same
problem in the general case — it is an ancestor of nearly every user process.

Capture-time ancestry is considered only for the selected window, whose process/class fingerprint
supplies the guard. That inference walk stops at shell and service processes and is capped at three
levels — an integrated terminal is `claude <- pwsh <- Code`, and anything deeper is more likely a
coincidental build chain. Once saved, explicitly selected activity names are checked throughout the
owner's descendant tree with the same shell/service and own-process safeguards.

Two guards worth knowing about, both covered by tests:

- A desktop app is identified by window class **and** owning process. Chrome (and every other
  Electron/CEF app) shares the `Chrome_WidgetWin_1` class, so class alone matched Chrome.
  Comparison is whole-name and case-insensitive, never a substring — see
  `ProviderTargetTests.OtherElectronAppsAreNotClaudeDesktop`.
- The two providers key off different processes: Claude Desktop and the Claude Code CLI are both
  `claude`, but the Codex desktop app is `ChatGPT` while its CLI is `codex`. Conflating them would
  put the Codex pill on nothing.

Windows Terminal reports the **active tab's** title, which can make an optional saved title matcher
useful for narrowing a rule. It does not associate a descendant process with a tab: sibling Terminal
windows can share one owner, and selected activity may therefore activate every otherwise-matching
window under that owner.

### Why the pill sits right on a terminal

A terminal's tabs occupy the middle of its title bar — exactly where a centred pill lands, so the
two collided constantly. Terminal targets therefore default to **Top right**, which pushes the pill
as far right as `RightReservePx` allows and puts it in the gap between the last tab and the minimise
button: the one part of that bar reliably free of tabs at any tab count.

It stops *at* the reserve, never inside it, so the window controls stay clickable however wide the
window is. The reserve is what "just short of the minimise button" means here, and widening it
moves the pill further left. Desktop targets default to **Top center**. Either target can choose a
different anchor and carry its own exact X/Y offset through the target editor.

Right-alignment also fits in a narrower strip than centring does. Centring needs clearance on both
sides of the pill; right-alignment only needs it on the left, because the reserve is already the
clearance on the right.

### IDEs — Rider and VS Code

An IDE is neither a terminal nor a desktop app frame, and it differs on both counts: **when** it
gets a pill and **where** the pill goes.

**Both budgets, whenever it is open.** Every other host has to earn its pill by having a CLI session
detected beneath it. An IDE doesn't. There are too many routes to a provider from inside one — an
integrated terminal tab, an AI assistant, a run configuration, an external tool — for "is a CLI
process descended from it right this instant" to be a useful question: the answer flickers as you
work, and most of the time it's no even while you're mid-task. The budgets are the *account's*, not
any session's, so there is nothing to withhold. If the IDE is open, both pills are up.

Only the **main frame** qualifies. Both were enumerated live, because both surround theirs with
windows that would otherwise pass:

| IDE | Process | Frame class | Status bar | Excluded siblings |
|---|---|---|---|---|
| Rider | `rider64` | `SunAwtFrame` | 26 px | `SunAwtDialog`, `SunAwtWindow`, `SunAwtToolkit`, the GDI+ hook window |
| VS Code | `Code`, `Code - Insiders` | `Chrome_WidgetWin_1` | 22 px | hidden `Chrome_WidgetWin_0` (one of them full-screen-sized), `Electron_SystemPreferencesHostWindow`, `Base_PowerMessageWindow`, `crashpad_SessionEndWatcher` |

`Chrome_WidgetWin_1` is also Claude Desktop's class, since that is Electron too; the process name is
what keeps the two apart.

**Why the frame class, and not a list of popup classes.** An IDE's context menus, completion
popups, tool-window popups and dialogs all share its process, so they inherit whatever session is
running underneath it and every one of them used to qualify as a host — a pill drawn across the top
of every menu for as long as it was open. Naming those classes would mean chasing them forever
(Rider's menus are heavyweight AWT windows, VS Code's are `#32768`, and the list is never finished).
Naming the one class that *is* a host answers all of them at once, including the ones nobody has
hit yet.

For an editor with no recorded frame class the fallback is a resizable-frame test — application
frames resize, menus don't. That test is deliberately *not* also applied to the IDEs above: a
full-screened editor can drop `WS_THICKFRAME`, which would take the pill away exactly when the
window is largest.

**On the status bar, centred.** Neither IDE has a free title bar. Rider draws its own and fills it
end to end at every window width — project widget and burger menu, run configurations, search and AI
actions, VCS widget, window controls — and VS Code's holds the command centre. Neither centring nor
right-alignment finds an empty strip up there; the pill just lands on a different control. The
status bar is the opposite shape in both: transient messages at the left end, caret position /
encoding / branch widgets at the right end, and a permanently clear middle.

So the reserve doesn't apply — there are no window controls down there to stop short of — and the
pill sits on the true centre of the window, in a strip measured up from the bottom of the client
area. The saved IDE target owns any X/Y adjustment.

The two status bar heights differ by product, so each frame carries its own rather than sharing a
config value that would mis-centre one of them. For an IDE-level zoom, update that target's measured
status-bar height and offset in the target editor instead of forcing one height on every IDE.

For a newly seeded target set, `AlwaysShowOnIdes: false` selects the session-required activation
policy instead; after migration, edit the saved IDE target. `EnableClaude` / `EnableCodex` remain
live everywhere, so a provider you have turned off never shows up on an IDE either.

Not every editor with a status bar is seeded — these two are the ones measured. Open **Targets →
Manage targets...**, choose **Add**, pick VSCodium, Cursor, Windsurf, or another JetBrains IDE, then
confirm its placement and measured status-bar height in the manager.

### Both pills on one terminal

Windows Terminal runs every one of its windows in a single process, so the process-ancestry signal
below can only answer "is a session running under this *process*" — never under this *window*. Left
alone, that makes both providers credit whichever WT window is front-most.

When two pills do share a window they are laid out side by side — marked `C:` and `X:`, Claude on
the left — the way the host would have placed a single one. Right-aligned, the outer pill stops on
the reserve and the inner one steps left of it. Centred, which is what a Rider status bar gets, the
two straddle the centre so the **pair** is centred; centring one and pushing the other off it would
leave the group visibly off to one side.

### One pill per qualifying window

A pill is bound to exactly one window for its whole life. Every window that qualifies for a
provider gets its own, created when the window appears and disposed when it goes away — so a Claude
Code session open in three terminals puts a pill on all three, not just on whichever is in front.

Nothing migrates any more, and that removed a class of problem rather than just a behaviour. The old
design picked a single winner per provider and re-picked it every 400 ms, so dragging a tab between
windows sent both pills chasing the transient windows Windows Terminal creates mid-drag — five
different host handles inside seventy seconds, measured. With a pill per window there is no winner
to re-pick.

The cost is the flip side of "per process". Qualification is answered per **process** — Windows
Terminal hosts all of its windows in one — so with both CLIs running, *every* WT window qualifies
for *both* providers, whether or not that particular window is the one running the session. You get
a `C:` and an `X:` pill on each. That is a deliberate trade: the alternative is silently dropping
windows with an equally good claim, and the signals Windows exposes cannot tell them apart. Only a
window title can identify a single window, Claude Code sets one and the Codex CLI does not, and
Windows Terminal reports only the active tab's.

Each pill records in the diagnostics log how its window qualified — `Terminal by Title`,
`Terminal by Ancestry`, `Desktop by Desktop` — because a pill on a window otherwise looks identical
whether it belongs there or merely shares the host's process.

### Both pills on one window

Wherever a window carries both providers' pills they sit side by side rather than on top of each
other, each with a one-character marker:

```
C: 5h ▰▰▰▰▱ 72%   7d ▰▰▱▱▱ 45%   X: 7d ▰▰▰▰▰ 83%
```

Claude on the left, Codex on the right — the same order as the tray tooltip's `C … X …`. The
outer pill still stops dead on the reserve, and the inner one is shifted left by exactly the outer
one's width plus a small gap.

The markers appear **only** while a host is shared. A pill alone on a window is unambiguous — it is
the only one there — so a marker would be noise.

## Reset pointer and pace colors

Each timed bar has a small triangle just above its track. The triangle moves from the right
edge to the left edge as the reset approaches: its position is the percentage of the window
still remaining. A five-hour window halfway to reset has its triangle at 50%.

The whole fill changes color according to the distance between the allowance remaining and
the triangle:

- Yellow within 2 percentage points of the triangle.
- Smoothly toward red when the allowance falls below the triangle, reaching full red at 15 points below.
- Smoothly toward green when the allowance extends beyond the triangle, reaching full green at 15 points above.

This compares consumption with the elapsed fraction of the current window. Each Codex window
uses the duration reported by the app-server. Claude's windows use five hours and seven days,
with reset times derived from Desktop history or parsed from the CLI's reported date, time,
and time zone. The triangle advances every five seconds, even when the usage reading stays the same.

When a reset timestamp or window duration is unknown, the bar has a neutral fill and no triangle.
A reset that has passed stays at the left edge until a new deadline is reported.
Hover over a pill to see its reset details and the age of its displayed reading.

## Install prompt

An amber **!** appears when the provider's CLI is missing. Click it to open that CLI's
installation documentation. Usage bars have no request-credit or request-reset action.

## Diagnostic history

Copy diagnostics includes the measured rates and estimated exhaustion times when enough
history exists. These estimates do not control the pill colors and do not appear in the tray menu.
Claude Desktop supplies its own history; scraped meters use `UsageBars.data\usage-history.json`,
and Codex readings accumulate in `UsageBars.data\bar-history.json`.

## Tray menu

The menu starts with **Refresh now** and contains commands only. Current usage remains on
the pills and in the tray icon's hover text. Reset details remain on pill hover; rates and
projections are available in diagnostics.

- **Refresh now**: immediately re-read the local file and request a Codex poll. Retry the Claude CLI
  fallback when the file is missing or stale and the fallback is enabled. Also available by
  left-clicking the tray icon. Refreshing does not change allowance; identical readings keep
  the same percentages.
- **Targets → Manage targets...** — open or reactivate the one reusable manager; its **Add** button
  starts window capture
- **Start with Windows** — registers/removes the per-user Run entry. Ticked only when the entry
  names *this* copy; a missing or foreign registration is spelled out in the item's own label
- **Copy diagnostics** — full dump to the clipboard: both providers, which file won, every
  candidate path and write time, the scraped meter and its age, resolved CLI paths, Codex windows
  and buckets, host-window resolution for each pill, any clamped config values, and the last 40
  log events. Start here when the numbers look wrong.
- **Open diagnostics folder**
- **Exit**

There is no **Position** submenu. Placement belongs to each saved target and is edited in the
capture or manager flow.

## Diagnostics

`UsageBars.data\diagnostics\lifecycle.jsonl` records startup, tray Exit requests, Windows
session ending, unhandled exceptions and process exit. Each entry is flushed directly to disk
and includes a run ID, process ID, pill count and last pill activity. A heartbeat is written
every 30 seconds. The file rotates at 1 MB and keeps one previous file (`lifecycle.jsonl.1`).
This lifecycle journal remains enabled when `WriteDiagnosticsLog` is false.

On startup, `previous-run-incomplete` means the previous run left no final exit record.
A forced kill, power loss or native crash may prevent final logging; the journal cannot identify
the terminating process in those cases. Unhandled exceptions are logged without suppressing them.

Beyond the clipboard dump, UsageBars keeps a rotating log at
`UsageBars.data\diagnostics\usagebars.jsonl` (1 MB, three generations). It records **state
changes** rather than every poll, so a source failing the same way every 15 minutes writes one
line, not one per attempt — which is what makes an intermittent failure visible hours later.

Three rules it never breaks: it never throws into the code path it observes, it never grows without
bound, and it never writes a value that could be a credential. Your profile path becomes
`%USERPROFILE%`, and token-shaped values are redacted before anything reaches disk. Set
`WriteDiagnosticsLog: false` to turn it off.

## When the bars say `--` or look frozen

The display distinguishes the cases:

- **`--` with empty tracks** — no source produced a value. Check **Copy diagnostics**: if
  the CLI path is `(not installed)`, click the **!** icon.
- **Dimmed bars** — the reading is stale. For the file, the newest sample is older than
  `FileStaleMinutes` (normal if Claude Desktop has been closed). For a scrape or the app-server,
  the last success is older than `ReadingStaleHours`.
- **A pill missing entirely** — its host isn't in front, it's covered, that provider is disabled,
  or the title bar is too narrow to hold it. The per-pill section of the diagnostics dump says
  which.
- **A message in the tray menu** — the specific error, per provider.

Values only advance while the provider is actually being used, so a flat bar usually means exactly
that.

## Configuration

`build.ps1` drops **`dist\UsageBars.config.json`** beside the exe, commented and ready to edit
(an existing one is never overwritten). Edit it and restart. It's also read from
`%LOCALAPPDATA%\UsageBars\config.json` if the exe directory isn't writable.

Out-of-range values are **clamped at load** rather than rejected, and whatever was corrected is
listed under "config adjustments" in the diagnostics dump. So `MinVelocitySamples: 0` becomes `2`
instead of making the projection trust a single reading. An invalid regex falls back to the
default.

| Key | Default | Meaning |
|---|---|---|
| `EnableClaude` | `true` | Show the Claude pill |
| `EnableCodex` | `true` | Show the Codex pill |
| `CodexRefreshMinutes` | `2` | How often to poll the Codex app-server (it also pushes updates) |
| `UseConfiguredResetDay` | `false` | Fall back to `ResetDayOfWeek` when a reset can't be measured |
| `ResetDayOfWeek` | `"Friday"` | Fallback reset day. **Display only, and only when the above is on.** |
| `ResetHour` | `0` | Local hour of the fallback reset. Display only. |
| `WarnHoursToEmpty` | `2` | Diagnostic low-usage estimate horizon in hours |
| `VelocityPaddingFactor` | `1.25` | Assume you'll burn this much faster than measured |
| `LowThresholdPct` | `10` | Absolute floor — always warn at or below this remaining % |
| `VelocityWindowHours` | `3` | How far back to measure the burn rate |
| `MinVelocitySamples` | `3` | Readings needed before a rate is trusted |
| `MinVelocitySpanMinutes` | `20` | Time the readings must span before a rate is trusted |
| `FileStaleMinutes` | `15` | Age at which the file reading is drawn dimmed |
| `ReadingStaleHours` | `2` | Age at which a scrape or app-server reading is drawn dimmed |
| `ScrapeIntervalMinutes` | `15` | How often to re-read Claude Code's `/usage`; failures back off |
| `UseClaudeCodeFallback` | `true` | `false` = JSON file only, never launch Claude Code |
| `AlwaysOn` | `true` | Stay on screen even when the host isn't focused |
| `HideWhenCovered` | `true` | With `AlwaysOn`, hide when another window covers the pill |
| `AttachToClaudeCodeTerminals` | `true` | **First-run seed only:** initial Claude terminal targets are enabled |
| `AttachToCodexTerminals` | `true` | **First-run seed only:** initial Codex terminal targets are enabled |
| `TerminalTitlePattern` | `"(?i)^\s*[✳⠀-⣿]\|\bclaude code\b\|^\s*claude\s*$"` | **Capture-time hint:** helps infer Claude for each new Add/capture; not persisted as a title/activity rule. The braille block covers Claude's spinner |
| `CodexTitlePattern` | `"(?i)^\s*codex\b\|\bcodex\s+(?:cli\|session)\b"` | **Capture-time hint:** helps infer Codex for each new Add/capture; not persisted as a title/activity rule. Codex generally uses ancestry because it sets no title |
| `TitleBarHeight` | `40` | Strip a pill sits in, in logical pixels |
| `RightReservePx` | `160` | Space kept clear for the host's window controls |
| `AlwaysShowOnIdes` | `true` | **First-run seed only:** initial Rider/VS Code targets use always-open rather than session-required activation |
| `WriteDiagnosticsLog` | `true` | Keep the rotating JSONL event log |

Comments and trailing commas are tolerated. Legacy credit-action settings (`RequestCreditUrl`,
`RequestCreditDomain`, `CodexRequestCreditUrl`) and absolute color thresholds (`AmberAtPercent`,
`RedAtPercent`) are ignored and omitted on the next config save.

Older configuration files may still contain
`OffsetX`, `OffsetY`, `TerminalOffsetY`, `RightAlignOnTerminals`, `PlaceOnIdeStatusBar`, or
`StatusBarHeightOverride`; these legacy global placement keys are ignored. A later unrelated config
save writes the current model without them. Per-target anchor, offset, title-condition, and activity-
process data lives in version 3 of `targets.json` instead.

Environment overrides, useful for testing and support:

| Variable | Effect |
|---|---|
| `USAGEBARS_FILE` | pin the file reader to one specific JSON |
| `USAGEBARS_CLAUDE` | use a specific `claude.exe` instead of auto-discovery |
| `USAGEBARS_CODEX` | use a specific `codex.exe` instead of auto-discovery |
| `USAGEBARS_DATA` | put the data folder somewhere specific |

## Building

Requires the .NET 8 SDK (or newer) — no C++ toolchain needed.

```bash
pwsh -File build.ps1
```

Output is `dist\UsageBars.exe`, ~63 MB, self-contained win-x64. For Windows on ARM:

```bash
pwsh -File build.ps1 -Runtime win-arm64
```

A running instance is stopped first (it holds a lock on the exe) and relaunched afterwards if it
was running. `-NoRelaunch` leaves it stopped; `-Run` starts it either way.

Instances are matched by **executable path**, not process name. The older UsageBars project builds
an executable with the same name, and may well be registered to start with Windows — a build here
must never kill it.

The runtime identifier and self-contained flag are passed by `build.ps1` rather than set in the
csproj, so the test project can reference the app without inheriting a RID-specific build.

### Tests

```bash
dotnet test UsageBars.sln
```

The automated suite requires no Windows UI — it covers the parsers, the reset derivation, the velocity
model and the time-to-0 % projection, config clamping, placement in both alignments and the shared
two-pill arrangement, diagnostics rotation and redaction, and the provider-targeting guards. The Codex parser tests run against a
verbatim capture of a real `account/rateLimits/read` reply rather than a hand-written fixture.

### Layout

| File | Role |
|---|---|
| `src/UsageSource.cs` | Candidate discovery, resilient read, v1/v2 parsing, sample series |
| `src/ClaudeCodeUsage.cs` | Drives `/usage`, scrapes the meter, caches the last reading |
| `src/Winpty.cs` | Hidden pseudo-console via libwinpty, plus embedded-binary extraction |
| `src/ClaudeCli.cs` | Finds the Claude Code CLI |
| `src/CodexCli.cs` | Finds the Codex CLI across its install layouts |
| `src/CodexAppServer.cs` | JSON-RPC session with `codex app-server` over stdio |
| `src/CodexMonitor.cs` | Owns the app-server connection, polling, pushes and backoff |
| `src/CodexUsage.cs` | Codex snapshot model and rate-limit JSON parsing |
| `src/ResetEstimator.cs` | Derives reset deadlines from the sample series and meter text |
| `src/UsageModel.cs` | Merges sources into bars + action icon, per provider |
| `src/UsageVelocity.cs` | Reading history, burn rate, the shared time-to-0 % projection, warning decision |
| `src/BarHistory.cs` | Accumulated per-bar readings for windows that report no history of their own |
| `src/DesktopFrame.cs` | Recognises an Electron app frame (paired with a process check) |
| `src/HostWindow.cs` | Host records, process tree, title-bar strip, placement maths |
| `src/WindowTargets.cs` | Persistent target document, match definitions, validation, and specificity |
| `src/TargetDefaults.cs` | First-run translation of legacy config values into seeded targets |
| `src/TargetStore.cs` | Atomic `targets.json` persistence and corrupt/invalid-file recovery backups |
| `src/TargetResolver.cs` | Applies enabled targets, provider switches, activity evidence, and tie-breaking |
| `src/TargetCapture.cs` / `src/WindowPicker.cs` | Captures a top-level window and runs the cancellable crosshair picker |
| `src/TargetEditorControl.cs` / `src/TargetManagerForm.cs` | Shared target editor and unified management/capture UI |
| `src/ZOrder.cs` | Z-order walk, cloaked-window filtering, occlusion test |
| `src/ProviderPill.cs` | One pill: an overlay window bound to one host window, for one provider |
| `src/OverlayWindow.cs` | Non-activating topmost pill; per-pixel hit testing |
| `src/OverlayView.cs` | All the drawing |
| `src/OverlayController.cs` | Timers, WinEvent hooks, pill lifecycle, visibility rules, diagnostics dump |
| `src/Tray.cs` | Tray icon and menu (icon generated at runtime, no embedded image) |
| `src/Diagnostics.cs` | Rotating JSONL log and redaction |
| `src/StartupRegistration.cs` | The per-user Run entry |
| `src/RetrySchedule.cs` | Exponential backoff, capped |
| `src/Config.cs` | Portable settings and load-time clamping |
| `third_party/winpty/` | Vendored `winpty.dll` + `winpty-agent.exe` (MIT) — see its README for why |
| `tools/probe-usage.*` | Standalone probes for Claude's usage API. Not used by the app — see below |

### Why the Claude usage API isn't wired in

`tools/probe-usage.py` reads Claude's own `/api/organizations/{org}/usage`, which returns real
reset times and per-model windows — a richer source than either of the two the app uses. It stays
a standalone script because it cannot work inside UsageBars: reaching it means decrypting Claude
Desktop's session cookie, and Chromium holds `Network\Cookies` open exclusively. Any read fails
with a sharing violation while Claude Desktop is running, which is precisely when UsageBars runs.

Deriving resets from the sample series turned out better anyway: no secret handling, no SQLite
dependency, and it works while Claude Desktop is open.

### Why winpty and not ConPTY

Claude Code only runs its interactive UI when it believes it has a real TTY. A hand-rolled ConPTY
is **not** accepted — Claude Code falls back to `--print` mode and exits with *"Input must be
provided either through stdin or as a prompt argument"*. winpty's pseudo-console is accepted.

Only `winpty.dll` and `winpty-agent.exe` are used, via winpty's C API. The `winpty.exe` CLI is
avoided because it's an MSYS build needing `msys-2.0.dll`; the two files above are native and
dependency-free. They're embedded in the exe and extracted to `%LOCALAPPDATA%\UsageBars\winpty\`
on first use, keeping the published artifact a single file.
