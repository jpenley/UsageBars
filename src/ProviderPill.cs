using System;
using System.Collections.Generic;

namespace UsageBars;

/// <summary>
/// One pill: an overlay window bound to exactly one host window, for exactly one provider.
///
/// The binding is fixed for the pill's lifetime. A pill no longer hunts for a host and migrates
/// between windows — <see cref="OverlayController"/> creates one per (provider, window) that
/// qualifies and disposes it when that window goes away. That is what lets every qualifying window
/// carry its own pill instead of only the front-most one, and it removes the "which single window
/// wins" question that made pills chase each other around the desktop.
///
/// Under the preferred attach mode the pills never contend: each is owned by its own host, so the
/// window manager orders them against those windows rather than against each other.
/// </summary>
internal sealed class ProviderPill : IDisposable
{
    /// <summary>
    /// Consecutive ticks that must agree before the pill is hidden. Tracking runs several times a
    /// second and a single tick can transiently fail — a window mid-move, a z-order snapshot taken
    /// a moment before a repaint. Hiding waits for confirmation while showing is immediate:
    /// blinking off for one frame is far more noticeable than staying up one frame too long.
    /// </summary>
    private const int HideTicksRequired = 2;

    private readonly uint _ownPid = (uint)Environment.ProcessId;

    private OverlayWindow _window;
    private int _hideTicks;
    private bool _moveInProgress;
    private bool _disposed;

    /// <summary>Whether the ownership relationship with the host has been established yet.</summary>
    private bool _attached;

    // Retained so a rebuilt window can be restored to exactly what the old one was showing.
    private UsageModel _model = new();
    private bool _lightTheme = true;
    private Config? _config;
    private bool? _previewDimmed;

    public ProviderTarget Target { get; }

    public UsageModel Model => _model;

    /// <summary>The window this pill belongs to. Never null and never changes identity.</summary>
    public HostWindowInfo Host { get; private set; }

    /// <summary>How the pill is currently kept above its host.</summary>
    public AttachMode Mode { get; private set; } = AttachMode.Topmost;

    /// <summary>What is currently covering the pill, when it is hidden for that reason.</summary>
    public string? CoveredBy { get; private set; }

    /// <summary>Set when the pill is hidden because the title bar is too narrow to hold it.</summary>
    public bool TooNarrow { get; private set; }

    public bool IsVisible { get; private set; }

    /// <summary>Why the pill is hidden, when it is. Logged so a wrong decision is diagnosable.</summary>
    public string? HiddenReason { get; private set; }

    public IntPtr HostHandle => Host.Hwnd;

    /// <summary>The pill's own window handle, logged so a rebuild is distinguishable from a survival.</summary>
    public IntPtr WindowHandle => _window.Handle;

    /// <summary>Drawn width in logical pixels, so a companion pill can be placed clear of it.</summary>
    public double LogicalWidth => _window.View.LogicalWidth;

    /// <summary>
    /// Width of the complete shared group in logical pixels, or zero for a standalone pill.
    /// </summary>
    public double SharedGroupWidth { get; private set; }

    /// <summary>Logical X position of this pill inside its shared group.</summary>
    public double SharedMemberOffset { get; private set; }

    /// <summary>
    /// Marks this pill as sharing a host by giving it a "C:" / "X:" prefix; null clears it, which
    /// is the normal state when each pill has its own window.
    ///
    /// Separate from <see cref="SetSharedGroup"/> because the prefix changes the drawn width and
    /// group geometry is measured from the final widths.
    /// </summary>
    public void SetSharedPrefix(string? prefix) => _window.View.SetPrefix(prefix);

    /// <summary>
    /// Assigns this pill's coordinates inside one group. Zero width restores standalone layout.
    /// </summary>
    public void SetSharedGroup(double groupWidth, double memberOffset)
    {
        SharedGroupWidth = groupWidth;
        SharedMemberOffset = memberOffset;
    }

    /// <summary>
    /// Where this pill sits horizontally on its host.
    ///
    /// Exposed because the side-by-side arrangement of two pills depends on it: right-aligned they
    /// stack inwards from the reserve, centred they straddle the centre. Kept as one expression so
    /// the layout and the placement can never disagree about which it is.
    /// </summary>
    public PillAlign Alignment() =>
        Host.Anchor == PillAnchor.TopRight ? PillAlign.Right : PillAlign.Centre;

    /// <summary>
    /// Whether this pill sits in the host's bottom client strip rather than its title bar.
    /// </summary>
    private bool OnBottomEdge() => Host.Anchor == PillAnchor.BottomCenter;

    /// <summary>Raised when the user clicks this pill's action icon.</summary>
    public event Action<ProviderPill>? ActionClicked;

    /// <summary>Raised when the drawn width changes, so the controller can re-centre it.</summary>
    public event Action<ProviderPill>? WidthChanged;

    /// <summary>Raised when the window had to be rebuilt, so the controller can re-log its state.</summary>
    public event Action<ProviderPill>? Rebuilt;

    public ProviderPill(ProviderTarget target, HostWindowInfo host)
    {
        Target = target;
        Host = host;
        _window = CreateWindow();
    }

    private OverlayWindow CreateWindow()
    {
        var window = new OverlayWindow();
        window.ActionClicked += OnActionClicked;
        window.View.WidthChanged += OnWidthChanged;
        window.Closed += OnWindowClosed;
        return window;
    }

    private void OnActionClicked() => ActionClicked?.Invoke(this);

    private void OnWidthChanged() => WidthChanged?.Invoke(this);

    /// <summary>
    /// An owned window is destroyed along with its owner, so closing the host app takes the pill
    /// with it. That is the cost of letting the window manager do the stacking, and it is paid
    /// here: rebuild, restore what was on screen, and let the next tick re-attach.
    /// </summary>
    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_disposed) return;

        _window.ActionClicked -= OnActionClicked;
        _window.View.WidthChanged -= OnWidthChanged;
        _window.Closed -= OnWindowClosed;

        _window = CreateWindow();
        Mode = AttachMode.Topmost;
        // The binding to the host survives; only the window backing it was lost, so the next
        // update re-establishes the ownership relationship on the new one.
        _attached = false;
        IsVisible = false;
        _hideTicks = HideTicksRequired;

        _window.Show();
        _window.SetShown(false);
        if (_config is not null)
            _window.View.Update(
                _model, _lightTheme, _config.AmberAtPercent, _config.RedAtPercent, _previewDimmed);

        Rebuilt?.Invoke(this);
    }

    public void Show()
    {
        _window.Show();
        // Hidden via SetShown rather than the Visibility property before Show(), which would
        // desync WPF's bookkeeping and make the first SetShown(true) a silent no-op.
        _window.SetShown(false);
    }

    public void UpdateModel(UsageModel model, bool lightTheme, Config config)
    {
        _model = model;
        _lightTheme = lightTheme;
        _config = config;
        _window.View.Update(model, lightTheme, config.AmberAtPercent, config.RedAtPercent, _previewDimmed);
    }

    public void SetPreviewDimmed(bool? dimmed)
    {
        _previewDimmed = dimmed;
        if (_config is not null)
            _window.View.Update(
                _model, _lightTheme, _config.AmberAtPercent, _config.RedAtPercent, _previewDimmed);
    }

    /// <summary>
    /// Re-places the pill on its host, refreshing the host's description (a terminal's title
    /// changes as you work) without ever changing which window it is bound to.
    ///
    /// With <c>AlwaysOn: false</c> the pill is additionally hidden unless its own host is the
    /// focused window — which is the same rule as before, just evaluated per pill rather than by
    /// choosing a single winner.
    /// </summary>
    public void Update(HostWindowInfo host, List<IntPtr> zOrder, Config config, bool moveInProgress)
    {
        _config = config;
        _moveInProgress = moveInProgress;
        Host = host;

        if (!_attached)
        {
            Attach(host, config);
            _attached = true;
        }

        if (!config.AlwaysOn && !IsFocusedHost())
        {
            VoteHide("host window is not focused");
            return;
        }

        Place(host, zOrder, config);
    }

    /// <summary>Whether this pill's own host is what the user is currently working in.</summary>
    private bool IsFocusedHost()
    {
        var foreground = Native.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;

        var root = Native.GetAncestor(foreground, Native.GA_ROOTOWNER);
        if (root == IntPtr.Zero) root = foreground;

        // Our own windows — the tray menu, the pills themselves — must not count as losing focus.
        if (Native.ProcessIdOf(root) == _ownPid) return IsVisible;

        return root == Host.Hwnd;
    }

    /// <summary>Repositions against the known host, for drag tracking and width changes.</summary>
    public void Reposition(List<IntPtr> zOrder, Config config, bool moveInProgress = false)
    {
        _config = config;
        _moveInProgress = moveInProgress;
        Place(Host, zOrder, config);
    }

    /// <summary>
    /// Picks the strongest relationship the host will accept, best first.
    ///
    /// Ownership is preferred because it makes the window manager responsible for stacking, which
    /// removes the need to detect occlusion at all. The other two modes exist because ownership
    /// can be refused, and a topmost pill that hides itself is better than no pill.
    /// </summary>
    private void Attach(HostWindowInfo host, Config config)
    {
        var forced = config.PlacementMode?.Trim().ToLowerInvariant();

        if (forced is not ("zorder" or "topmost") && _window.TryAttachOwner(host.Hwnd))
        {
            Mode = AttachMode.Owned;
            return;
        }

        _window.DetachOwner();

        if (forced != "topmost" && _window.TryPlaceAbove(host.Hwnd))
        {
            _window.SetTopmost(false);
            Mode = AttachMode.ZOrderAbove;
            return;
        }

        _window.SetTopmost(true);
        Mode = AttachMode.Topmost;
    }

    private void Place(HostWindowInfo host, List<IntPtr> zOrder, Config config)
    {
        TooNarrow = false;

        if (!ZOrder.IsRealVisibleWindow(host.Hwnd))
        {
            VoteHide("host window is not visible");
            return;
        }

        _window.HoverHost = host.Hwnd;

        var bottomEdge = OnBottomEdge();
        var barName = bottomEdge ? "bottom strip" : "title bar";

        if (!TryMeasureStrip(host, bottomEdge, config, out var strip, out var scale))
        {
            VoteHide($"could not measure the host's {barName}");
            return;
        }

        if (!Native.GetWindowRect(host.Hwnd, out var accessibleBounds))
        {
            VoteHide("could not measure the host bounds");
            return;
        }

        var align = Alignment();
        var rightReserve = bottomEdge ? 0 : config.RightReservePx;

        // A strip too narrow to hold the pill without colliding with the host's own window
        // controls is left alone entirely — drawing over the close button is worse than not
        // drawing at all.
        if (!_window.TryPlaceIn(
                strip, accessibleBounds, scale, host.OffsetX, host.OffsetY, rightReserve, align,
                SharedGroupWidth, SharedMemberOffset))
        {
            TooNarrow = true;
            VoteHide($"{barName} too narrow to hold the pill");
            return;
        }

        // Only the topmost fallback needs the pill's visibility managed by hand. In the other two
        // modes the pill is below every other application window by construction, so whatever is
        // in front of the host is already in front of the pill.
        if (Mode == AttachMode.ZOrderAbove)
        {
            // Activation of the host raises it back over the pill, so this has to be re-asserted.
            // Cheap: SetWindowPos is skipped entirely when already correctly placed.
            _window.TryPlaceAbove(host.Hwnd);
        }
        else if (Mode == AttachMode.Topmost
                 && config.AlwaysOn && config.HideWhenCovered && !_moveInProgress)
        {
            var pillRect = _window.CurrentRect();
            if (pillRect is { } rect &&
                ZOrder.IsRegionCovered(zOrder, host.Hwnd, _window.Handle, rect, _ownPid,
                    out var coveredBy))
            {
                CoveredBy = coveredBy;
                VoteHide($"covered by {coveredBy}");
                return;
            }
        }

        CoveredBy = null;
        HiddenReason = null;
        _hideTicks = 0;
        IsVisible = true;
        _window.SetShown(true);
    }

    /// <summary>Measures the strip this pill belongs in, whichever end of the host it is on.</summary>
    private static bool TryMeasureStrip(
        HostWindowInfo host, bool bottomEdge, Config config,
        out Native.RECT strip, out double scale)
    {
        if (bottomEdge)
        {
            var height = PlacementStripHeight.Bottom(host.StatusBarHeight, config.TitleBarHeight);

            return WindowGeometry.TryGetStatusBarStrip(host.Hwnd, height, out strip, out scale);
        }

        return WindowGeometry.TryGetTitleBarStrip(
            host.Hwnd, config.TitleBarHeight, out strip, out scale);
    }

    /// <summary>Hides only once enough consecutive ticks agree. See <see cref="HideTicksRequired"/>.</summary>
    private void VoteHide(string reason)
    {
        if (_hideTicks < HideTicksRequired) _hideTicks++;
        if (_hideTicks < HideTicksRequired) return;

        HiddenReason = reason;
        IsVisible = false;
        _window.SetShown(false);
    }

    public void Dispose()
    {
        _disposed = true;
        _window.Closed -= OnWindowClosed;
        _window.DetachOwner();
        _window.Close();
    }
}

/// <summary>Pure selection of the logical height used to measure a bottom placement strip.</summary>
internal static class PlacementStripHeight
{
    public static double Bottom(double statusBarHeight, double titleBarHeight) =>
        statusBarHeight > 0 ? statusBarHeight : titleBarHeight;
}
