using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace UsageBars;

/// <summary>
/// Wires the usage sources, the host windows and the pills together.
///
/// One controller owns everything shared — config, tray, timers, hooks, theme, diagnostics — and
/// one <see cref="ProviderPill"/> per provider owns what isn't: its window, its host and its
/// model. Claude and Codex are therefore independent on screen but cost a single pass over the
/// z-order and a single process snapshot per tick.
/// </summary>
internal sealed class OverlayController : IDisposable
{
    private static readonly TimeSpan TrackInterval = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan DataInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxFileAge = TimeSpan.FromSeconds(30);

    private readonly Config _config = Config.Load();
    private readonly DiagnosticLog _log;
    private readonly Tray _tray;
    private readonly StartupRegistration _startup = new();
    private readonly DispatcherTimer _trackTimer = new() { Interval = TrackInterval };
    private readonly DispatcherTimer _dataTimer = new() { Interval = DataInterval };
    private readonly uint _ownProcessId = (uint)Environment.ProcessId;

    // Kept as a field so the CLR doesn't collect the delegate the hooks call back into.
    private readonly Native.WinEventProc _winEventProc;
    private IntPtr _foregroundHook = IntPtr.Zero;
    private IntPtr _moveSizeHook = IntPtr.Zero;

    /// <summary>
    /// When the user started moving or resizing a window, or null when no drag is in progress.
    ///
    /// Timestamped rather than a bare flag so a missed MOVESIZEEND — the window closed mid-drag,
    /// the hook dropped an event — can't leave occlusion suppressed forever.
    /// </summary>
    private DateTime? _moveStartedUtc;

    private static readonly TimeSpan MaxMoveDuration = TimeSpan.FromSeconds(30);

    private bool MoveInProgress =>
        _moveStartedUtc is { } started && DateTime.UtcNow - started < MaxMoveDuration;

    /// <summary>Location hooks, keyed by the process they watch, so two hosts can both be tracked.</summary>
    private readonly Dictionary<uint, IntPtr> _locationHooks = new();

    private readonly TargetStore _targets;
    private readonly TargetResolver _targetResolver = new();
    private readonly TargetWindowSource _targetWindows;
    private readonly WindowPicker _windowPicker;
    private readonly TargetManagerCoordinator _targetManagerCoordinator;

    /// <summary>
    /// Every live pill, keyed by provider and the window it belongs to.
    ///
    /// One per qualifying window rather than one per provider: a Claude Code session open in three
    /// terminals puts a pill on all three, and each is disposed when its own window closes. The
    /// dictionary is the whole lifecycle — anything in <c>wanted</c> and not here is created,
    /// anything here and not in <c>wanted</c> is retired.
    /// </summary>
    private readonly Dictionary<(ProviderId Provider, IntPtr Hwnd), ProviderPill> _pills = new();
    private bool _applyingSharedLayout;
    private bool _disposed;

    /// <summary>
    /// The per-provider models. Held here rather than on the pills because every pill of a
    /// provider draws the same numbers — the usage is the account's, not the window's.
    /// </summary>
    private UsageModel _claudeModel = new();
    private UsageModel _codexModel = new();

    // ---- Claude sources ----
    private UsageSnapshot _file = UsageSnapshot.Failed("reading...");
    private ClaudeCodeUsage? _scraped;
    private readonly UsageHistory _history = UsageHistory.Load();

    private DateTime _lastFileReadUtc = DateTime.MinValue;
    private DateTime? _lastSeenWriteUtc;
    private bool _fileReadInFlight;

    private DateTime _lastScrapeUtc = DateTime.MinValue;
    private bool _scrapeInFlight;
    private int _scrapeFailures;

    /// <summary>
    /// Whether the local file has been read at least once.
    ///
    /// The scrape is only a fallback for accounts whose file carries no values, so it must not run
    /// before that question has been answered. Starting both at once raced: the scrape saw the
    /// not-yet-populated placeholder, decided the file was empty, and spun a 75-second hidden
    /// Claude Code session on every launch for accounts that never needed one.
    /// </summary>
    private bool _fileReadCompleted;

    // ---- Codex source ----
    private readonly CodexMonitor _codex;

    /// <summary>
    /// Readings accumulated for windows that report no history of their own. The Codex app-server
    /// gives a precise current number and a real resetsAt but nothing about the past, so the only
    /// way to have a burn rate for those windows is to watch them go by.
    /// </summary>
    private readonly BarHistory _barHistory = BarHistory.Load();

    private bool _lightTheme = IsLightTheme();

    public OverlayController()
    {
        _log = _config.WriteDiagnosticsLog
            ? DiagnosticLog.Default
            : new DiagnosticLog(AppPaths.DiagnosticsDir, maxBytes: 0, generations: 0);

        _tray = new Tray();
        _codex = new CodexMonitor(_config, _log);
        _targets = TargetStore.Load(new JsonTargetFileStorage(AppPaths.TargetsFile), _config);
        var introspection = new NativeWindowIntrospection();
        _targetWindows = new TargetWindowSource(
            _ownProcessId,
            ZOrder.TopLevelWindows,
            introspection.SnapshotOf);
        _windowPicker = new WindowPicker(
            new NativeWindowPickerNative(), new PickerBanner(),
            new WindowCaptureService(introspection, _ownProcessId));
        var targetManagerComposition = new TargetManagerComposition(
            _targets,
            _config,
            _ownProcessId,
            _targetWindows.EligibleWindows,
            ProcessTree.Capture,
            window => new TargetPreviewSession(
                window,
                new ProviderPreviewPillFactory(() => _lightTheme),
                ModelFor,
                _config,
                ProcessTree.Capture,
                ZOrder.TopLevelWindows,
                _ownProcessId),
            QueueTrackAfterPreviewChange);
        _targetManagerCoordinator = new TargetManagerCoordinator(
            action => Application.Current.Dispatcher.BeginInvoke(action),
            targetManagerComposition.CreateManager,
            new TargetManagerOwnerWindow(),
            ReportTargetManagerEvent,
            _windowPicker.PickAsync,
            targetManagerComposition.BuildCaptureContext);

        _winEventProc = OnWinEvent;

        // Seed from history so a number is on screen before the first scrape lands.
        _scraped = _history.Latest();

        _codex.Changed += OnCodexChanged;
        _targets.Changed += OnTargetsChanged;

        foreach (var issue in _targets.Issues)
            _log.Record("app", "targets", "recovery", DiagLevel.Warn, "recovered", issue);
        if (_targets.Issues.Count > 0)
            _tray.ShowWarning(TargetRecoveryWarning.Format(_targets));
        UpdateTargetSummary();

        _tray.RefreshRequested += () =>
        {
            BeginFileRead(force: true);
            BeginScrape(force: true);
            _codex.RequestRefresh();
        };
        _tray.ExitRequested += () => ShutdownSequence.Run(
            Dispose,
            () => Application.Current.Shutdown());
        _tray.DiagnosticsRequested += CopyDiagnostics;
        _tray.DiagnosticsFolderRequested += OpenDiagnosticsFolder;
        _tray.ManageTargetsRequested += ShowTargetManager;
        _tray.StartupToggleRequested += OnStartupToggleRequested;
    }

    public void Start()
    {
        _log.Record("app", "lifecycle", "started", DiagLevel.Info, "started",
            $"portable={AppPaths.IsPortable} claude={_config.EnableClaude} codex={_config.EnableCodex}");

        foreach (var note in _config.Adjustments)
            _log.Record("app", "config", "adjusted", DiagLevel.Warn, "clamped", note);

        SyncStartupState(repair: true);

        // Pills are created by the first Track(), once the qualifying windows are known.
        Rebuild();

        _trackTimer.Tick += (_, _) => Track();
        _trackTimer.Start();

        _dataTimer.Tick += (_, _) =>
        {
            var light = IsLightTheme();
            if (light != _lightTheme)
            {
                _lightTheme = light;
                Rebuild();
            }
            BeginFileRead(force: false);
            BeginScrape(force: false);
            foreach (var pill in _pills.Values) pill.RefreshTime();
        };
        _dataTimer.Start();

        _foregroundHook = Native.SetWinEventHook(
            Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventProc, 0, 0,
            Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);

        // System-wide, because the snap preview that appears while dragging any window is what
        // makes the pill vanish mid-drag — it isn't limited to our hosts.
        _moveSizeHook = Native.SetWinEventHook(
            Native.EVENT_SYSTEM_MOVESIZESTART, Native.EVENT_SYSTEM_MOVESIZEEND,
            IntPtr.Zero, _winEventProc, 0, 0,
            Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);

        if (_config.EnableCodex) _codex.Start();

        // The scrape is deliberately not started here: it is kicked off by the first file read
        // completing, and only when that read finds nothing.
        BeginFileRead(force: true);
        Track();
    }

    /// <summary>Rebuilds every provider's model and pushes it to its pill and the tray.</summary>
    private void Rebuild()
    {
        RecordCodexHistory();

        _claudeModel = UsageModel.Build(_file, _scraped, _history, _config);
        _codexModel = UsageModel.BuildCodex(
            _codex.Latest, _codex.CodexInstalled, _codex.Error, _config, _barHistory);

        foreach (var (key, pill) in _pills)
            pill.UpdateModel(ModelFor(key.Provider), _lightTheme, _config);

        _tray.Update(
            _config.EnableClaude ? _claudeModel : null,
            _config.EnableCodex ? _codexModel : null);
    }

    private UsageModel ModelFor(ProviderId provider) =>
        provider == ProviderId.Codex ? _codexModel : _claudeModel;

    /// <summary>
    /// Records the Codex windows' current utilization, so a rate can be measured for bars the
    /// app-server reports no history for.
    ///
    /// Called from <see cref="Rebuild"/> rather than only from the change event, so a value that
    /// moved without a push still gets sampled. Self-throttling: <see cref="BarHistory.Record"/>
    /// drops anything closer together than its own spacing, so calling it often costs nothing and
    /// the file is only written when a point was actually kept.
    /// </summary>
    private void RecordCodexHistory()
    {
        if (_codex.Latest is not { HasValues: true } snapshot) return;

        var kept = false;

        foreach (var window in new[] { snapshot.Primary, snapshot.Secondary })
        {
            if (window is null) continue;
            kept |= _barHistory.Record(
                UsageModel.CodexHistoryKey(window.Label), window.UsedPct, snapshot.ReadAt);
        }

        if (snapshot.SpendLimit is { } spend)
        {
            kept |= _barHistory.Record(
                UsageModel.CodexHistoryKey("spend"), 100 - spend.RemainingPct, snapshot.ReadAt);
        }

        if (kept) _barHistory.Save();
    }

    private void OnCodexChanged()
    {
        // Raised on the monitor's own thread; all UI state lives on the dispatcher.
        try
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                Rebuild();
                foreach (var (key, pill) in _pills)
                    if (key.Provider == ProviderId.Codex) RepositionOne(pill);
            });
        }
        catch (Exception)
        {
            // Shutting down: the dispatcher is gone and there's nothing left to update.
        }
    }

    private void OnTargetsChanged()
    {
        // Target manager notifications can originate from WinForms, while picker hooks may finish
        // off-dispatcher. Keep all overlay and tray updates on the WPF dispatcher.
        try
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                UpdateTargetSummary();
                Track();
            });
        }
        catch (Exception)
        {
            // The dispatcher is already shutting down.
        }
    }

    private void UpdateTargetSummary()
    {
        var enabled = _targets.Targets.Count(target => target.Enabled);
        _tray.SetTargetSummary(enabled, _targets.Targets.Count - enabled);
    }

    private void ShowTargetManager() => _targetManagerCoordinator.Open();

    private void QueueTrackAfterPreviewChange()
    {
        if (_disposed) return;
        try { Application.Current.Dispatcher.BeginInvoke((Action)Track); }
        catch (Exception) { /* the dispatcher is already shutting down */ }
    }

    private void ReportTargetManagerEvent(string @event, Exception? error)
    {
        var failed = error is not null;
        _log.Record(
            "app", "target-manager", @event,
            failed ? DiagLevel.Warn : DiagLevel.Info,
            failed ? "failed" : "succeeded",
            error?.Message);

        if (failed)
            _tray.ShowWarning($"Target management failed: {error!.Message}");
    }

    private void OpenActionUrl(ProviderPill pill)
    {
        var url = pill.Model.ActionUrl;
        if (string.IsNullOrWhiteSpace(url)) return;

        // Install documentation is the only pill action. Keep the scheme check at the shell
        // boundary so a future caller cannot turn an action into arbitrary process execution.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            _log.Record(pill.Target.Name, "action", "rejected", DiagLevel.Warn, "blocked",
                $"non-http action URL: {url}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Record(pill.Target.Name, "action", "failed", DiagLevel.Warn, "failed", ex.Message);
        }
    }

    // ---- window tracking ----------------------------------------------------

    /// <summary>
    /// Places every enabled pill. The z-order snapshot and the process tree are taken once and
    /// shared, so adding a provider costs a resolve rather than a second enumeration.
    /// </summary>
    private void Track()
    {
        if (_disposed) return;
        var zOrder = ZOrder.TopLevelWindows();
        var candidates = _targetWindows.EligibleHosts(zOrder);

        var moving = MoveInProgress;

        // Every (provider, window) pair that should have a pill on it right now.
        var wanted = _targetResolver.FindAll(candidates, _targets.Targets, _config)
            .ToDictionary(host => (host.Provider, host.Hwnd));

        _targetManagerCoordinator.RefreshLiveState();
        TargetPreviewSuppression.RemovePreviewWindows(
            wanted, _targetManagerCoordinator.SuppressedHwnds);

        RetirePillsMissingFrom(wanted);
        CreatePillsFor(wanted);

        // Which windows carry two pills is answerable before placing, now that the full set is
        // known — so the side-by-side arrangement is applied first and nothing ever draws overlapped
        // even for a single frame.
        ApplySharedHostLayout(wanted);

        foreach (var (key, pill) in _pills)
        {
            var visibleBefore = pill.IsVisible;

            pill.Update(wanted[key], zOrder, _config, moving);

            if (pill.IsVisible != visibleBefore)
            {
                _log.Record(pill.Target.Name, "visibility",
                    pill.IsVisible ? "shown" : "hidden",
                    DiagLevel.Info,
                    pill.IsVisible ? "shown" : "hidden",
                    pill.IsVisible
                        ? $"host 0x{pill.HostHandle.ToInt64():X}"
                        : pill.HiddenReason);
            }
        }

        SyncLocationHooks();
    }

    /// <summary>
    /// Disposes pills whose window no longer qualifies — it closed, the session under it ended, or
    /// the provider was turned off. This is the "kill them when the owner goes away" half of the
    /// lifecycle, and it is driven entirely by the window no longer appearing in the resolved set
    /// rather than by trying to catch a destroy notification.
    /// </summary>
    private void RetirePillsMissingFrom(Dictionary<(ProviderId, IntPtr), HostWindowInfo> wanted)
    {
        List<(ProviderId, IntPtr)>? gone = null;

        foreach (var (key, _) in _pills)
        {
            if (wanted.ContainsKey(key)) continue;
            (gone ??= new List<(ProviderId, IntPtr)>()).Add(key);
        }

        if (gone is null) return;

        foreach (var key in gone)
        {
            var pill = _pills[key];
            _log.Record(pill.Target.Name, "pill", "retired", DiagLevel.Info, "disposed",
                $"{pill.Host.Description} (host 0x{pill.HostHandle.ToInt64():X})");
            pill.Dispose();
            _pills.Remove(key);
        }
    }

    private void CreatePillsFor(Dictionary<(ProviderId, IntPtr), HostWindowInfo> wanted)
    {
        foreach (var (key, host) in wanted)
        {
            if (_pills.ContainsKey(key)) continue;

            var target = key.Item1 == ProviderId.Codex ? ProviderTarget.Codex : ProviderTarget.Claude;
            var pill = new ProviderPill(target, host);

            pill.ActionClicked += OpenActionUrl;
            // Content width changes when the bar count or labels change; re-place to stay put.
            pill.WidthChanged += ReflowSharedHost;
            pill.Rebuilt += p => _log.Record(p.Target.Name, "window", "rebuilt", DiagLevel.Info,
                "recreated", "owner window was destroyed; pill rebuilt");

            pill.Show();
            pill.UpdateModel(ModelFor(key.Item1), _lightTheme, _config);
            _pills[key] = pill;

            // Evidence is logged because without it a misattribution is invisible: a pill on a
            // window looks the same whether it belongs there or merely shares the host's process.
            _log.Record(target.Name, "pill", "created", DiagLevel.Info, "attached",
                $"{host.Kind} by {host.Evidence}: {host.Description} " +
                $"(host 0x{host.Hwnd.ToInt64():X}, pill 0x{pill.WindowHandle.ToInt64():X})");
        }
    }

    /// <summary>Clear space between two pills sharing one title bar, in logical pixels.</summary>
    private const double SharedPillGap = 8;

    /// <summary>
    /// Arranges pills side by side on any window that carries both providers', and restores plain
    /// single-pill layout everywhere else.
    ///
    /// A shared window is routine rather than exotic. Windows Terminal runs all of its windows in
    /// one process, and a CLI session is credited per process, so with both CLIs running every WT
    /// window qualifies for both providers. Undetected they draw exactly on top of each other.
    ///
    /// Claude goes on the left and Codex on the right, matching the "C … X …" order of the tray
    /// tooltip.
    ///
    /// How the pair is arranged follows the alignment the host would give a lone pill. Right-
    /// aligned, the outer one stops on the reserve and the inner one steps left of it. Centred —
    /// which is what a Rider status bar and a desktop frame get — the two straddle the centre, so
    /// the *pair* is centred rather than one of them being centred and the other pushed off it.
    /// </summary>
    private void ApplySharedHostLayout(
        IReadOnlyDictionary<(ProviderId Provider, IntPtr Hwnd), HostWindowInfo> currentHosts)
    {
        _applyingSharedLayout = true;
        try
        {
            // Prefixes for every pill first, because the marker widens the pill it is on and group
            // geometry is measured off the final widths. Interleaving the two leaves a gap exactly
            // the width of an "X:" marker. Resolver output is passed in explicitly so a save or
            // winner change cannot accidentally group against ProviderPill.Host from the prior tick.
            foreach (var (key, pill) in _pills)
            {
                var companionKey =
                    (key.Provider == ProviderId.Codex
                        ? ProviderId.Claude
                        : ProviderId.Codex, key.Hwnd);
                var shares = _pills.ContainsKey(companionKey)
                             && SharedPillLayout.SharesPosition(
                                 currentHosts[key], currentHosts[companionKey]);
                pill.SetSharedPrefix(
                    shares ? (key.Provider == ProviderId.Codex ? "X:" : "C:") : null);
            }

            // Driven from the Claude side: a pair is exactly one Claude pill and one Codex pill on
            // the same window, so iterating one provider visits each pair once. Both receive
            // coordinates inside one combined rectangle; the placement layer anchors, nudges,
            // fits, and clamps that rectangle once before adding either member offset.
            foreach (var (key, pill) in _pills)
            {
                if (key.Provider != ProviderId.Claude) continue;

                if (!_pills.TryGetValue((ProviderId.Codex, key.Hwnd), out var codex))
                {
                    pill.SetSharedGroup(0, 0);
                    continue;
                }

                var layout = SharedPillLayout.Create(
                    currentHosts[key], pill.LogicalWidth,
                    currentHosts[(ProviderId.Codex, key.Hwnd)], codex.LogicalWidth,
                    SharedPillGap);
                if (layout is not { } shared)
                {
                    pill.SetSharedGroup(0, 0);
                    codex.SetSharedGroup(0, 0);
                }
                else
                {
                    pill.SetSharedGroup(shared.First.GroupWidth, shared.First.MemberOffset);
                    codex.SetSharedGroup(shared.Second.GroupWidth, shared.Second.MemberOffset);
                }
            }

            // Codex pills with no Claude companion; the loop above never reaches them.
            foreach (var (key, pill) in _pills)
            {
                if (key.Provider != ProviderId.Codex) continue;
                if (!_pills.TryGetValue((ProviderId.Claude, key.Hwnd), out var claude)
                    || !SharedPillLayout.SharesPosition(
                        currentHosts[key], currentHosts[(ProviderId.Claude, key.Hwnd)]))
                    pill.SetSharedGroup(0, 0);
            }
        }
        finally
        {
            _applyingSharedLayout = false;
        }
    }

    /// <summary>
    /// Repositions against known hosts without re-running host discovery.
    ///
    /// Still takes a fresh z-order snapshot: this is the drag path, and a drag is precisely when
    /// what covers each pill changes. Enumerating windows is far cheaper than resolving hosts,
    /// which is what makes this worth keeping separate from <see cref="Track"/>.
    /// </summary>
    private void TrackLite()
    {
        var zOrder = ZOrder.TopLevelWindows();
        var moving = MoveInProgress;
        foreach (var pill in _pills.Values) pill.Reposition(zOrder, _config, moving);
    }

    /// <summary>Repositions a single pill, for events that only affect one of them.</summary>
    private void RepositionOne(ProviderPill pill) =>
        pill.Reposition(ZOrder.TopLevelWindows(), _config, MoveInProgress);

    /// <summary>
    /// A member width changes the group's width, so recompute and reposition every pill on that
    /// HWND. Prefix changes happen inside the layout pass and are deliberately coalesced into the
    /// caller's following placement loop.
    /// </summary>
    private void ReflowSharedHost(ProviderPill changed)
    {
        if (_applyingSharedLayout) return;

        var currentHosts = _pills.ToDictionary(pair => pair.Key, pair => pair.Value.Host);
        ApplySharedHostLayout(currentHosts);
        var zOrder = ZOrder.TopLevelWindows();
        foreach (var pill in _pills.Values)
            if (pill.HostHandle == changed.HostHandle)
                pill.Reposition(zOrder, _config, MoveInProgress);
    }

    /// <summary>
    /// Keeps one location hook per host process. Scoped to the hosts' processes so dragging
    /// tracks smoothly without listening to every location change on the desktop.
    /// </summary>
    private void SyncLocationHooks()
    {
        var wanted = new HashSet<uint>();
        foreach (var pill in _pills.Values)
        {
            var hwnd = pill.HostHandle;
            if (hwnd == IntPtr.Zero) continue;
            var pid = Native.ProcessIdOf(hwnd);
            if (pid != 0) wanted.Add(pid);
        }

        List<uint>? stale = null;
        foreach (var (pid, hook) in _locationHooks)
        {
            if (wanted.Contains(pid)) continue;
            Native.UnhookWinEvent(hook);
            (stale ??= new List<uint>()).Add(pid);
        }
        if (stale is not null)
            foreach (var pid in stale) _locationHooks.Remove(pid);

        foreach (var pid in wanted)
        {
            if (_locationHooks.ContainsKey(pid)) continue;
            var hook = Native.SetWinEventHook(
                Native.EVENT_OBJECT_LOCATIONCHANGE, Native.EVENT_OBJECT_LOCATIONCHANGE,
                IntPtr.Zero, _winEventProc, pid, 0, Native.WINEVENT_OUTOFCONTEXT);
            if (hook != IntPtr.Zero) _locationHooks[pid] = hook;
        }
    }

    private void OnWinEvent(
        IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (eventType == Native.EVENT_SYSTEM_MOVESIZESTART)
        {
            _moveStartedUtc = DateTime.UtcNow;
            return;
        }

        if (eventType == Native.EVENT_SYSTEM_MOVESIZEEND)
        {
            _moveStartedUtc = null;
            // Re-evaluate immediately: the drag chrome is gone, so whatever it was hiding behind
            // should be reassessed now rather than up to a tick later.
            Track();
            return;
        }

        // LOCATIONCHANGE fires for child objects too; only a tracked frame matters.
        if (eventType == Native.EVENT_OBJECT_LOCATIONCHANGE)
        {
            var relevant = false;
            foreach (var pill in _pills.Values)
            {
                if (pill.HostHandle == hwnd) { relevant = true; break; }
            }
            if (!relevant) return;

            // Fires continuously while dragging: reposition only, so a drag never triggers a
            // window enumeration per event.
            TrackLite();
            return;
        }
        Track();
    }

    // ---- data sources -------------------------------------------------------

    private void BeginFileRead(bool force)
    {
        if (!_config.EnableClaude || _fileReadInFlight) return;

        if (!force)
        {
            // Cheap stat first: only re-parse when the file actually changed, and re-read
            // at least every 30 s so a vanished or replaced file still gets noticed.
            var newest = UsageSource.NewestWriteTime();
            var changed = newest != _lastSeenWriteUtc;
            if (!changed && DateTime.UtcNow - _lastFileReadUtc < MaxFileAge) return;
            _lastSeenWriteUtc = newest;
        }
        else
        {
            _lastSeenWriteUtc = UsageSource.NewestWriteTime();
        }

        _fileReadInFlight = true;
        var dispatcher = Dispatcher.CurrentDispatcher;

        Task.Run(() =>
        {
            UsageSnapshot snapshot;
            try { snapshot = UsageSource.Read(); }
            catch (Exception ex) { snapshot = UsageSnapshot.Failed(ex.Message); }

            dispatcher.BeginInvoke(() =>
            {
                _fileReadInFlight = false;
                _lastFileReadUtc = DateTime.UtcNow;
                _file = snapshot;

                // Now that the preferred source has answered, the fallback can decide whether it
                // is needed at all.
                var firstRead = !_fileReadCompleted;
                _fileReadCompleted = true;

                _log.Record("claude", "history-file",
                    snapshot.HasValues ? "read" : "empty",
                    snapshot.Error is null ? DiagLevel.Info : DiagLevel.Warn,
                    snapshot.Error is null ? "succeeded" : "failed",
                    snapshot.Error ?? $"{snapshot.SourceLabel}: 5h={snapshot.FiveHourUsedPct} 7d={snapshot.SevenDayUsedPct}; sampleAt={snapshot.SampleTime:O}");

                Rebuild();

                // A frozen Desktop file must not prevent a fresh all-device reading.
                if (UsageModel.NeedsClaudeFallback(snapshot, _config)) BeginScrape(force: firstRead);
            });
        });
    }

    /// <summary>
    /// Scrapes Claude Code's /usage screen when the local file is empty or stale.
    /// </summary>
    private void BeginScrape(bool force)
    {
        if (!_config.EnableClaude || !_config.UseClaudeCodeFallback || _scrapeInFlight) return;
        if (!_fileReadCompleted || !UsageModel.NeedsClaudeFallback(_file, _config)) return;

        var interval = TimeSpan.FromMinutes(Math.Max(1, _config.ScrapeIntervalMinutes));

        // Repeated failures back off rather than re-running a 20-second hidden session on the
        // normal cadence. The backoff is capped at the interval, so a working scrape is unaffected.
        if (_scrapeFailures > 0)
        {
            var backoff = RetrySchedule.ForAttempt(_scrapeFailures - 1, interval);
            if (backoff > interval) interval = backoff;
        }

        if (!force && DateTime.UtcNow - _lastScrapeUtc < interval) return;

        _scrapeInFlight = true;
        _lastScrapeUtc = DateTime.UtcNow;
        var dispatcher = Dispatcher.CurrentDispatcher;

        // Blocking and slow (~20 s): always off the UI thread.
        Task.Run(() =>
        {
            var started = Stopwatch.StartNew();
            ClaudeCodeUsage result;
            try { result = ClaudeCodeUsageReader.Read(); }
            catch (Exception ex) { result = ClaudeCodeUsage.Failed(ex.Message); }
            var elapsedMs = started.Elapsed.TotalMilliseconds;

            dispatcher.BeginInvoke(() =>
            {
                _scrapeInFlight = false;

                // Keep the last good reading rather than replacing it with an error.
                if (result.HasValue)
                {
                    _scrapeFailures = 0;
                    _scraped = result;
                    if (result.Windows.Count == 0 && _history.Record(result.UsedPct!.Value, result.MeterLabel, result.MeterDetail,
                            result.ReadAt ?? DateTimeOffset.Now))
                    {
                        _history.Save();
                    }
                    _log.Record("claude", "usage-scrape", "read", DiagLevel.Info, "succeeded",
                        result.Windows.Count > 0
                            ? string.Join(", ", result.Windows.Select(w => $"{w.Label}: {w.UsedPct}% used")) + $"; readAt={result.ReadAt:O}"
                            : $"{result.MeterLabel}: {result.UsedPct}% used; readAt={result.ReadAt:O}", elapsedMs);
                }
                else
                {
                    _scrapeFailures++;
                    if (_scraped is not { HasValue: true }) _scraped = result;
                    _log.Record("claude", "usage-scrape", "failed", DiagLevel.Warn, "failed",
                        $"{result.Error} (attempt {_scrapeFailures})", elapsedMs);
                }
                Rebuild();
            });
        });
    }

    private void OnStartupToggleRequested(bool enabled)
    {
        try
        {
            _startup.SetEnabled(enabled);
            _tray.SetStartupState(_startup.Current());
            _log.Record("app", "startup", enabled ? "enabled" : "disabled",
                DiagLevel.Info, "succeeded");
        }
        catch (Exception ex)
        {
            // Roll the tick back so the menu never claims a state the registry doesn't have.
            _tray.SetStartupState(_startup.Current());
            _tray.ShowWarning($"Could not change the startup setting: {ex.Message}");
            _log.Record("app", "startup", "failed", DiagLevel.Warn, "failed", ex.Message);
        }
    }

    /// <summary>
    /// Reads the startup registration, optionally repairing it, and shows the result in the tray.
    ///
    /// The repair is worth doing unprompted because a dangling entry has no symptom: Windows
    /// silently ignores a Run value whose target is missing, so the user sees a ticked menu item
    /// and an app that never started. Rewriting it to this copy restores exactly what they asked
    /// for when they ticked it. An entry naming a different copy that still works is left alone
    /// and merely surfaced — see <see cref="StartupRegistration.TryRepair"/>.
    /// </summary>
    private void SyncStartupState(bool repair)
    {
        var before = _startup.Current();
        var registered = _startup.RegisteredCommand();

        if (repair && before == StartupState.Dangling)
        {
            try
            {
                if (_startup.TryRepair())
                {
                    _log.Record("app", "startup", "repaired", DiagLevel.Warn, "rewritten",
                        $"registered copy was missing: {Redact.Paths(registered ?? "(none)")}");
                }
            }
            catch (Exception ex)
            {
                _log.Record("app", "startup", "repair-failed", DiagLevel.Warn, "failed", ex.Message);
            }
        }

        var state = _startup.Current();
        _tray.SetStartupState(state);

        if (state == StartupState.OtherCopy)
        {
            // Not repaired, so it stays true at every launch until the user acts on it.
            _log.Record("app", "startup", "other-copy", DiagLevel.Warn, "mismatch",
                $"a different copy starts at logon: {Redact.Paths(registered ?? "(none)")}");
        }
        else if (state == StartupState.Dangling)
        {
            _log.Record("app", "startup", "dangling", DiagLevel.Warn, "unrepaired",
                $"registered copy is missing and could not be rewritten: " +
                Redact.Paths(registered ?? "(none)"));
        }
    }

    private void OpenDiagnosticsFolder()
    {
        try
        {
            Directory.CreateDirectory(_log.Directory);
            Process.Start(new ProcessStartInfo(_log.Directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _tray.ShowWarning($"Could not open the diagnostics folder: {ex.Message}");
        }
    }

    private static bool IsLightTheme()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            return value is not int i || i != 0;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private void CopyDiagnostics()
    {
        var now = DateTimeOffset.Now;
        var text = new StringBuilder();

        text.AppendLine($"UsageBars diagnostics — {now:yyyy-MM-dd HH:mm:ss zzz}");
        text.AppendLine();

        text.AppendLine("== Claude ==");
        AppendModel(text, _config.EnableClaude ? _claudeModel : null, "(disabled)");
        text.AppendLine();
        text.AppendLine("-- plan-usage-history.json --");
        text.AppendLine($"5h utilization : {Format(_file.FiveHourUsedPct)}");
        text.AppendLine($"7d utilization : {Format(_file.SevenDayUsedPct)}");
        text.AppendLine($"5h resets at   : {Describe(_file.FiveHourResetsAt)}");
        text.AppendLine($"7d resets at   : {Describe(_file.SevenDayResetsAt)}");
        text.AppendLine($"newest sample  : {_file.SampleTime?.ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "(none)"}");
        text.AppendLine($"sample age     : {(_file.Age is { } age ? $"{age.TotalMinutes:F1} min" : "(none)")}");
        text.AppendLine($"source label   : {_file.SourceLabel ?? "(none)"}");
        text.AppendLine($"source path    : {Redact.Paths(_file.SourcePath ?? "(none)")}");
        text.AppendLine($"org            : {_file.Org ?? "(none)"}");
        text.AppendLine($"error          : {_file.Error ?? "(none)"}");
        text.AppendLine();
        text.AppendLine("candidates (newest write first):");
        foreach (var (path, label) in UsageSource.Candidates())
        {
            string written;
            try { written = File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); }
            catch (Exception) { written = "?"; }
            text.AppendLine($"  {label}  written={written}  {Redact.Paths(path)}");
        }
        text.AppendLine();
        text.AppendLine("-- claude code /usage scrape --");
        text.AppendLine($"cli path       : {Redact.Paths(ClaudeCli.Find() ?? "(not installed)")}");
        text.AppendLine($"installed      : {_scraped?.ClaudeInstalled.ToString() ?? "(unknown)"}");
        text.AppendLine($"used%          : {_scraped?.UsedPct?.ToString(CultureInfo.InvariantCulture) ?? "(none)"}");
        text.AppendLine($"remaining%     : {_scraped?.RemainingPct?.ToString(CultureInfo.InvariantCulture) ?? "(none)"}");
        text.AppendLine($"meter label    : {_scraped?.MeterLabel ?? "(none)"}");
        text.AppendLine($"meter detail   : {_scraped?.MeterDetail ?? "(none)"}");
        text.AppendLine($"read at        : {_scraped?.ReadAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "(never)"}");
        text.AppendLine($"error          : {_scraped?.Error ?? "(none)"}");
        text.AppendLine($"interval (min) : {_config.ScrapeIntervalMinutes} · consecutive failures {_scrapeFailures}");
        text.AppendLine();
        text.AppendLine("-- velocity / projection --");
        text.AppendLine($"warn reason    : {_claudeModel.WarnReason ?? "(none)"}");
        if (_claudeModel.Velocity is { } v)
        {
            text.AppendLine($"rate           : {v.PercentPerHour?.ToString("0.000", CultureInfo.InvariantCulture) ?? "(none)"} %/hour");
            text.AppendLine($"hours to empty : {v.HoursToEmpty?.ToString("0.00", CultureInfo.InvariantCulture) ?? "(none)"}");
            text.AppendLine($"samples used   : {v.SampleCount} spanning {v.SpanHours:0.00} h");
            text.AppendLine($"reset basis    : {v.ResetBasis}");
            text.AppendLine($"unavailable ex : {v.Reason ?? "(n/a)"}");
        }
        else
        {
            text.AppendLine("(no velocity estimate — file source in use, which carries its own resets)");
        }
        text.AppendLine($"history samples: {_history.Samples.Count}");
        text.AppendLine($"reset basis    : {_claudeModel.ResetBasis}");
        text.AppendLine($"warn horizon   : {_config.WarnHoursToEmpty} h · padding x{_config.VelocityPaddingFactor}");
        text.AppendLine($"velocity window: {_config.VelocityWindowHours} h · min {_config.MinVelocitySamples} samples / {_config.MinVelocitySpanMinutes} min");
        text.AppendLine();

        text.AppendLine("== Codex ==");
        AppendModel(text, _config.EnableCodex ? _codexModel : null, "(disabled)");
        text.AppendLine();
        text.AppendLine("-- codex app-server --");
        text.AppendLine($"selected exe   : {Redact.Paths(_codex.SelectedExecutable ?? "(none)")}");
        text.AppendLine($"installed      : {_codex.CodexInstalled}");
        text.AppendLine($"error          : {_codex.Error ?? "(none)"}");
        text.AppendLine($"failures       : {_codex.ConsecutiveFailures}");
        text.AppendLine($"refresh (min)  : {_config.CodexRefreshMinutes}");
        if (_codex.Latest is { } snapshot)
        {
            text.AppendLine($"read at        : {snapshot.ReadAt:yyyy-MM-dd HH:mm:ss}");
            text.AppendLine($"plan           : {snapshot.PlanType ?? "(none)"}");
            text.AppendLine($"primary        : {DescribeWindow(snapshot.Primary)}");
            text.AppendLine($"secondary      : {DescribeWindow(snapshot.Secondary)}");
            text.AppendLine($"spend limit    : {(snapshot.SpendLimit is { } s ? $"{s.RemainingPct:0}% left, used {s.Used} of {s.Limit}, resets {Describe(s.ResetsAt)}" : "(none)")}");
            text.AppendLine($"reset credits  : {snapshot.ResetCreditsAvailable}");
            text.AppendLine($"watched points : {_barHistory.TotalPoints} across {_barHistory.Series.Count} window(s)" +
                            " (accumulated here; the app-server reports no history)");
            foreach (var bucket in snapshot.Buckets)
                text.AppendLine($"  bucket       : {bucket.Display} — {DescribeWindow(bucket.Primary)}");
        }
        text.AppendLine();
        text.AppendLine("candidates:");
        foreach (var candidate in CodexCli.Candidates())
            text.AppendLine($"  {Redact.Paths(candidate)}");
        text.AppendLine();

        TargetDiagnostics.Append(text, _targets, _targetResolver.LastRejections);
        text.AppendLine("== target positions ==");
        foreach (var target in _targets.Targets)
            text.AppendLine($"{target.Id} · {target.Name} · placement={target.Placement} · position={TargetPositionRules.Format(target.Anchor, target.OffsetX, target.OffsetY)}");
        text.AppendLine();

        text.AppendLine($"== pills ({_pills.Count} live) ==");
        if (_pills.Count == 0) text.AppendLine("(none — no qualifying windows on screen)");
        foreach (var pill in _pills.Values)
        {
            text.AppendLine($"-- {pill.Target.Name} on 0x{pill.HostHandle.ToInt64():X} --");
            text.AppendLine($"  host         : {pill.Host.Description}");
            text.AppendLine($"  kind         : {pill.Host.Kind} by {pill.Host.Evidence}");
            text.AppendLine($"  sits on      : {(pill.Host.Anchor == PillAnchor.BottomCenter ? "the bottom strip, centre-aligned" : $"the title bar, {pill.Alignment()}-aligned")}");
            text.AppendLine($"  position     : {TargetPositionRules.Format(pill.Host.Anchor, pill.Host.OffsetX, pill.Host.OffsetY)}");
            text.AppendLine($"  attach mode  : {pill.Mode}");
            text.AppendLine($"  covered by   : {pill.CoveredBy ?? "(not covered)"}");
            text.AppendLine($"  too narrow   : {pill.TooNarrow}");
            text.AppendLine($"  shared slot  : {DescribeGroup(pill)}");
            text.AppendLine($"  visible      : {pill.IsVisible}{(pill.IsVisible ? "" : $" — {pill.HiddenReason ?? "(no reason)"}")}");
        }
        text.AppendLine();
        text.AppendLine("== environment ==");
        text.AppendLine($"always on      : {_config.AlwaysOn} · hideWhenCovered={_config.HideWhenCovered}");
        text.AppendLine($"move in progress: {MoveInProgress}");
        text.AppendLine($"claude terminals: {_config.AttachToClaudeCodeTerminals} · pattern '{_config.TerminalTitlePattern}'");
        text.AppendLine($"codex terminals : {_config.AttachToCodexTerminals} · pattern '{_config.CodexTitlePattern}'");
        text.AppendLine($"data root      : {Redact.Paths(AppPaths.DataRoot)} (portable={AppPaths.IsPortable})");
        if (AppPaths.PortableRejectedBecause is { } why)
            text.AppendLine($"portable note  : {Redact.Paths(why)}");
        text.AppendLine($"diagnostics    : {Redact.Paths(_log.Directory)}");
        text.AppendLine($"config saved to: {Redact.Paths(_config.SavedTo ?? "(defaults, not yet saved)")}");
        text.AppendLine($"start with win : {_startup.Current()}" +
                        $" · registered={Redact.Paths(_startup.RegisteredCommand() ?? "(none)")}");
        text.AppendLine($"titleBarHeight : {_config.TitleBarHeight} · rightReserve={_config.RightReservePx}");
        text.AppendLine($"IDE activation : alwaysShowOnIdes={_config.AlwaysShowOnIdes} · status heights=per-target measurement");
        text.AppendLine($"pace colours   : yellow +/-{ResetPace.YellowRadius} points; full red/green at +/-{ResetPace.FullColorDistance} points");
        if (_config.Adjustments.Count > 0)
        {
            text.AppendLine("config adjustments (values clamped at load):");
            foreach (var note in _config.Adjustments) text.AppendLine($"  {note}");
        }
        text.AppendLine();

        text.AppendLine("== recent diagnostics ==");
        var tail = _log.Tail(40);
        if (tail.Count == 0) text.AppendLine("(none recorded yet)");
        foreach (var line in tail) text.AppendLine(line);

        try { Clipboard.SetText(text.ToString()); }
        catch (Exception) { /* clipboard locked by another process */ }

        static string Format(double? used) =>
            used is null ? "(none)" : $"{used.Value:F1}% used → {100 - used.Value:F1}% left";

        string Describe(DateTimeOffset? at) =>
            at is null ? "(unknown)" : $"{at:yyyy-MM-dd HH:mm} ({ResetEstimator.Describe(at, now)})";

        static string DescribeGroup(ProviderPill pill) => pill.SharedGroupWidth > 0
            ? $"{pill.SharedGroupWidth:0} px group; member starts at +{pill.SharedMemberOffset:0} px"
            : "(sole pill on this target)";

        string DescribeWindow(CodexWindow? window) =>
            window is null
                ? "(none)"
                : $"{window.Label} {window.RemainingPct:0}% left, resets {Describe(window.ResetsAt)}";

        static void AppendModel(StringBuilder sb, UsageModel? model, string disabledText)
        {
            if (model is null) { sb.AppendLine($"showing        : {disabledText}"); return; }
            sb.AppendLine($"showing        : {model.StatusText}");
            sb.AppendLine($"at this rate   : " +
                          $"{(model.ProjectionText is { Length: > 0 } p ? p : "(nothing projectable yet)")}");
            sb.AppendLine($"source         : {model.SourceText}");
            sb.AppendLine($"action         : {model.Action}{(model.ActionUrl is null ? "" : " -> " + model.ActionUrl)}");
            sb.AppendLine($"reset basis    : {model.ResetBasis}");

            // Per bar, because a missing projection is the thing people ask about and the answer
            // is always one of: no rate measured, or a rate so slow the horizon was past the cap.
            foreach (var bar in model.Bars)
            {
                sb.AppendLine(
                    $"  bar {bar.Label,-7}: " +
                    $"{(bar.RemainingPct is { } r ? $"{r:0.#}% left" : "--")}" +
                    $" · rate {(bar.PercentPerHour is { } rate ? $"{rate:0.000} %/h" : "(none)")}" +
                    $" · 0% at {(bar.EmptyAt is { } e ? e.ToString("yyyy-MM-dd HH:mm") : "(not projected)")}");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _trackTimer.Stop();
        _dataTimer.Stop();

        if (_foregroundHook != IntPtr.Zero) Native.UnhookWinEvent(_foregroundHook);
        if (_moveSizeHook != IntPtr.Zero) Native.UnhookWinEvent(_moveSizeHook);
        foreach (var hook in _locationHooks.Values) Native.UnhookWinEvent(hook);
        _locationHooks.Clear();

        _codex.Changed -= OnCodexChanged;
        _codex.Dispose();

        _targets.Changed -= OnTargetsChanged;
        _targetManagerCoordinator.Dispose();
        _windowPicker.Dispose();

        _log.Record("app", "lifecycle", "stopped", DiagLevel.Info, "stopped");
        _log.Dispose();

        _tray.Dispose();
        foreach (var pill in _pills.Values) pill.Dispose();
    }
}

internal static class ShutdownSequence
{
    public static void Run(Action disposeOwnedWindows, Action shutdownApplication)
    {
        disposeOwnedWindows();
        shutdownApplication();
    }
}

internal static class SharedPillLayout
{
    internal readonly record struct Member(double GroupWidth, double MemberOffset);

    public static bool SharesPosition(HostWindowInfo first, HostWindowInfo second) =>
        first.Hwnd == second.Hwnd
        && first.TargetId != Guid.Empty
        && first.TargetId == second.TargetId
        && first.Anchor == second.Anchor
        && first.OffsetX == second.OffsetX
        && first.OffsetY == second.OffsetY;

    public static (Member First, Member Second)? Create(
        HostWindowInfo firstHost,
        double firstWidth,
        HostWindowInfo secondHost,
        double secondWidth,
        double gap)
    {
        if (!SharesPosition(firstHost, secondHost)
            || firstWidth <= 0 || secondWidth <= 0 || gap < 0)
            return null;

        var groupWidth = firstWidth + gap + secondWidth;
        return (
            new Member(groupWidth, MemberOffset: 0),
            new Member(groupWidth, MemberOffset: firstWidth + gap));
    }
}

internal static class TargetRecoveryWarning
{
    public static string Format(TargetStore store) =>
        $"UsageBars recovered window targets from a problem in {store.Location}. "
        + "Open Copy diagnostics from the tray for details before editing targets.";
}

internal sealed class TargetManagerComposition
{
    private readonly ITargetRuleStore _store;
    private readonly Config _config;
    private readonly uint _ownPid;
    private readonly Func<IReadOnlyList<WindowSnapshot>> _liveWindows;
    private readonly Func<ProcessTree> _processTree;
    private readonly Func<WindowSnapshot, ITargetPreviewSession> _previewSession;
    private readonly Action _previewsChanged;

    public TargetManagerComposition(
        ITargetRuleStore store,
        Config config,
        uint ownPid,
        Func<IReadOnlyList<WindowSnapshot>> liveWindows,
        Func<ProcessTree> processTree,
        Func<WindowSnapshot, ITargetPreviewSession> previewSession,
        Action previewsChanged)
    {
        _store = store;
        _config = config;
        _ownPid = ownPid;
        _liveWindows = liveWindows;
        _processTree = processTree;
        _previewSession = previewSession;
        _previewsChanged = previewsChanged;
    }

    public TargetCaptureContext BuildCaptureContext(WindowCaptureResult capture)
    {
        var window = capture.Window
            ?? throw new InvalidOperationException("The window picker did not return a target.");
        var windows = capture.TopLevelWindows ?? _liveWindows();
        var tree = _processTree();
        return new TargetCaptureContext(
            window,
            ProviderInference.CreateDraft(
                window, _store.Targets, tree, _config, _ownPid, windows),
            ProcessInventoryBuilder.Build(window, windows, tree));
    }

    public IModelessTargetManager CreateManager()
    {
        var model = new TargetManagementWorkspaceModel(_store);
        var previews = new TargetPreviewSet(_liveWindows, _previewSession);
        try
        {
            var manager = new TargetManagerForm(
                model, previews, _liveWindows, BuildInventoryForWindows);
            manager.PreviewHandlesChanged += (_, _) => _previewsChanged();
            return manager;
        }
        catch
        {
            previews.Dispose();
            model.Dispose();
            throw;
        }
    }

    private ProcessInventory BuildInventoryForWindows(
        IReadOnlyList<WindowSnapshot> selectedWindows,
        IReadOnlyList<WindowSnapshot> liveWindows)
    {
        if (selectedWindows.Count == 0) return ProcessInventory.Empty;
        var tree = _processTree();
        return ProcessInventory.Combine(selectedWindows
            .Select(window => ProcessInventoryBuilder.Build(window, liveWindows, tree)));
    }
}

internal sealed class TargetWindowSource
{
    private readonly uint _ownPid;
    private readonly Func<List<IntPtr>> _zOrder;
    private readonly Func<IntPtr, WindowSnapshot?> _snapshotOf;
    private readonly Func<List<IntPtr>, uint, List<HostCandidate>> _eligibleHosts;

    public TargetWindowSource(
        uint ownPid,
        Func<List<IntPtr>> zOrder,
        Func<IntPtr, WindowSnapshot?> snapshotOf,
        Func<List<IntPtr>, uint, List<HostCandidate>>? eligibleHosts = null)
    {
        _ownPid = ownPid;
        _zOrder = zOrder;
        _snapshotOf = snapshotOf;
        _eligibleHosts = eligibleHosts ?? HostWindowEnumerator.EligibleHosts;
    }

    public List<HostCandidate> EligibleHosts(List<IntPtr> zOrder) =>
        _eligibleHosts(zOrder, _ownPid);

    public IReadOnlyList<WindowSnapshot> EligibleWindows()
    {
        var zOrder = _zOrder();
        return EligibleHosts(zOrder)
            .Select(host => (Host: host, Window: _snapshotOf(host.Hwnd)))
            .Where(pair => pair.Window is not null && pair.Window.Pid == pair.Host.Pid)
            .Select(pair => pair.Window!)
            .ToList();
    }
}

internal static class TargetPreviewSuppression
{
    public static void RemovePreviewWindows(
        Dictionary<(ProviderId Provider, IntPtr Hwnd), HostWindowInfo> wanted,
        IReadOnlySet<IntPtr> suppressedHwnds)
    {
        foreach (var key in wanted.Keys
                     .Where(key => suppressedHwnds.Contains(key.Hwnd))
                     .ToList())
            wanted.Remove(key);
    }
}
