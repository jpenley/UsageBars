using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace UsageBars;

/// <summary>How the pill is kept above its host, in descending order of preference.</summary>
internal enum AttachMode
{
    /// <summary>
    /// The pill is an owned window of the host. The window manager then keeps it directly above
    /// that one window permanently — through activation, without re-assertion, and naturally
    /// occluded by anything in front. It also hides and restores with the owner for free.
    /// </summary>
    Owned,

    /// <summary>
    /// The pill is inserted immediately above the host in the z-order. Correct until the host is
    /// activated, which raises it back over the pill, so it has to be re-asserted.
    /// </summary>
    ZOrderAbove,

    /// <summary>
    /// The pill is topmost, above everything, and hidden by an explicit occlusion test. The only
    /// option when the host refuses ownership and is itself topmost.
    /// </summary>
    Topmost,
}

/// <summary>
/// A non-activating pill that sits on its host's title bar. Position and size are driven entirely
/// through SetWindowPos in physical pixels so mixed-DPI setups stay aligned with the host.
///
/// Stacking is delegated to the window manager wherever possible — see <see cref="AttachMode"/>.
/// Only the last-resort mode reimplements it.
///
/// Clicks pass through to the host's title bar everywhere except the small action icon, which is
/// done by answering WM_NCHITTEST with HTTRANSPARENT outside the icon rather than setting
/// WS_EX_TRANSPARENT on the whole window (which would make the icon unclickable too).
/// </summary>
internal sealed class OverlayWindow : Window
{
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_SETCURSOR = 0x0020;
    private const int HTTRANSPARENT = -1;
    private const int HTCLIENT = 1;

    public OverlayView View { get; } = new();

    /// <summary>Raised when the user clicks the action icon.</summary>
    public event Action? ActionClicked;

    private IntPtr _handle = IntPtr.Zero;

    /// <summary>Null until the first SetShown, so that call always takes effect.</summary>
    private bool? _shown;

    private double _scale = 1.0;

    /// <summary>
    /// How often the cursor is sampled for hover, and how long it must rest on the pill before
    /// the reset tooltip opens.
    ///
    /// Polled rather than driven by mouse messages because the pill never receives any: outside
    /// the action icon it answers HTTRANSPARENT so the host's title bar keeps its drag, double-
    /// click and system menu. WPF's own tooltip service is therefore never triggered.
    /// </summary>
    private static readonly TimeSpan HoverPoll = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan HoverDelay = TimeSpan.FromMilliseconds(500);

    private readonly DispatcherTimer _hoverTimer = new() { Interval = HoverPoll };
    private readonly ToolTip _toolTip;
    private DateTime? _hoverSince;

    /// <summary>
    /// The window the pill sits on, so hover is only credited when the cursor is actually over
    /// the pill rather than over something else that happens to cover it.
    /// </summary>
    public IntPtr HoverHost { get; set; }

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        // Not topmost by default: that is the fallback mode, applied explicitly when the better
        // relationships are unavailable.
        Topmost = false;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Focusable = false;

        Width = View.LogicalWidth;
        Height = OverlayView.LogicalHeight;

        // Off-screen until the first placement, so there's no flash at (0,0).
        // Visibility is deliberately NOT set here: assigning Hidden before Show() desyncs WPF's
        // visibility bookkeeping, leaving the HWND hidden while the property reads Visible — which
        // makes the first SetShown(true) a silent no-op and the pill never appears. The caller
        // hides it via SetShown(false) after Show() instead.
        Left = -30000;
        Top = -30000;

        Content = View;

        _toolTip = new ToolTip { PlacementTarget = View, Placement = PlacementMode.Bottom };
        _hoverTimer.Tick += OnHoverTick;
        Closed += (_, _) =>
        {
            _hoverTimer.Stop();
            _toolTip.IsOpen = false;
        };
    }

    private void OnHoverTick(object? sender, EventArgs e)
    {
        if (!CursorOverPill())
        {
            _hoverSince = null;
            _toolTip.IsOpen = false;
            return;
        }

        var now = DateTime.UtcNow;
        _hoverSince ??= now;
        if (!_toolTip.IsOpen && now - _hoverSince < HoverDelay) return;

        // Refreshed while open so the countdown and any new reading stay current.
        var text = View.ResetTooltip(DateTimeOffset.Now);
        if (text is null)
        {
            _toolTip.IsOpen = false;
            return;
        }

        if (!Equals(_toolTip.Content, text)) _toolTip.Content = text;
        _toolTip.IsOpen = true;
    }

    private bool CursorOverPill()
    {
        if (_shown != true || _handle == IntPtr.Zero) return false;
        if (!Native.GetCursorPos(out var point)) return false;
        if (!Native.GetWindowRect(_handle, out var rect)) return false;
        if (point.X < rect.Left || point.X >= rect.Right ||
            point.Y < rect.Top || point.Y >= rect.Bottom) return false;

        // The pill is hit-test transparent, so the window found under the cursor is the one the
        // pill sits on when nothing covers it — or the pill itself over the action icon.
        var hit = Native.WindowFromPoint(point);
        if (hit == IntPtr.Zero) return false;
        var root = Native.GetAncestor(hit, Native.GA_ROOT);
        if (root == IntPtr.Zero) root = hit;
        return root == _handle || HoverHost == IntPtr.Zero || root == HoverHost;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handle = new WindowInteropHelper(this).Handle;

        // NOACTIVATE + TOOLWINDOW: never steals focus, never appears in Alt+Tab.
        // Note: no WS_EX_TRANSPARENT — hit testing is handled in WndProc instead.
        Native.AddExStyle(_handle, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);

        HwndSource.FromHwnd(_handle)?.AddHook(WndProc);
        _hoverTimer.Start();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                handled = true;
                return (IntPtr)(PointOverIcon(lParam) ? HTCLIENT : HTTRANSPARENT);

            case WM_SETCURSOR:
                // Only reached when the cursor is over the icon (elsewhere is HTTRANSPARENT).
                Native.SetCursor(Native.LoadCursor(IntPtr.Zero, Native.IDC_HAND));
                handled = true;
                return (IntPtr)1;

            case WM_LBUTTONUP:
                handled = true;
                ActionClicked?.Invoke();
                return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    /// <summary>True when the screen point in lParam falls inside the action icon.</summary>
    private bool PointOverIcon(IntPtr lParam)
    {
        var bounds = View.ActionIconBounds;
        if (bounds is null || _shown != true) return false;
        if (!Native.GetWindowRect(_handle, out var window)) return false;

        // lParam packs signed screen coordinates.
        var raw = lParam.ToInt64();
        var screenX = (short)(raw & 0xFFFF);
        var screenY = (short)((raw >> 16) & 0xFFFF);

        // Compare in physical pixels relative to the window's top-left.
        var localX = screenX - window.Left;
        var localY = screenY - window.Top;
        var icon = new Rect(
            bounds.Value.X * _scale, bounds.Value.Y * _scale,
            bounds.Value.Width * _scale, bounds.Value.Height * _scale);

        return icon.Contains(localX, localY);
    }

    public IntPtr Handle => _handle;

    /// <summary>Current screen rect of the pill, or null before it has been placed.</summary>
    public Native.RECT? CurrentRect()
    {
        if (_handle == IntPtr.Zero) return null;
        return Native.GetWindowRect(_handle, out var r) ? r : null;
    }

    /// <summary>Last geometry actually applied, so an unchanged tick can be skipped entirely.</summary>
    private (int X, int Y, int Width, int Height)? _applied;

    /// <summary>
    /// Places the pill in the given anchor strip, keeping clear of the host's window controls.
    /// Returns false when the strip is too narrow to hold it, in which case nothing is moved and
    /// the caller should hide the pill.
    /// </summary>
    public bool TryPlaceIn(
        Native.RECT strip,
        Native.RECT accessibleBounds,
        double scale,
        double offsetX,
        double offsetY,
        double rightReserveLogical,
        PillAlign align = PillAlign.Centre,
        double groupWidthLogical = 0,
        double memberOffsetLogical = 0)
    {
        if (_handle == IntPtr.Zero) return false;
        _scale = scale;

        var scaledGroup = PillGroupScale.Member(
            groupWidthLogical > 0 ? groupWidthLogical : View.LogicalWidth,
            View.LogicalWidth,
            groupWidthLogical > 0 ? memberOffsetLogical : 0,
            scale);
        var width = scaledGroup.MemberWidth;
        var height = (int)Math.Round(OverlayView.LogicalHeight * scale);
        var reserve = (int)Math.Round(rightReserveLogical * scale);

        var position = OverlayPlacement.CalculateGroupMember(
            strip,
            accessibleBounds,
            scaledGroup.GroupWidth,
            width,
            scaledGroup.MemberOffset,
            height,
            reserve,
            (int)Math.Round(offsetX * scale),
            (int)Math.Round(offsetY * scale),
            align);

        if (position is not { } p) return false;

        var target = (p.X, p.Y, width, height);

        // Only move when something actually changed. Tracking runs several times a second, and an
        // unconditional SetWindowPos forces a repaint of a layered window every time. Z-order is
        // deliberately untouched here — that is the attach mode's business, not geometry's.
        if (_applied == target) return true;

        Native.SetWindowPos(
            _handle, IntPtr.Zero, p.X, p.Y, width, height,
            Native.SWP_NOACTIVATE | Native.SWP_NOZORDER | Native.SWP_NOOWNERZORDER);
        _applied = target;
        return true;
    }

    // ---- attach modes -------------------------------------------------------

    /// <summary>Current owner, so ownership is only re-applied when the host actually changes.</summary>
    private IntPtr _owner = IntPtr.Zero;

    /// <summary>
    /// Makes the pill an owned window of <paramref name="host"/>. Returns false when the window
    /// manager declines, which the caller treats as a reason to fall back rather than to fail.
    /// </summary>
    public bool TryAttachOwner(IntPtr host)
    {
        if (_handle == IntPtr.Zero || host == IntPtr.Zero) return false;
        if (_owner == host && Native.OwnerOf(_handle) == host) return true;

        SetTopmost(false);
        if (!Native.TrySetOwner(_handle, host)) return false;

        _owner = host;
        return true;
    }

    public void DetachOwner()
    {
        if (_handle == IntPtr.Zero || _owner == IntPtr.Zero) return;
        Native.TrySetOwner(_handle, IntPtr.Zero);
        _owner = IntPtr.Zero;
    }

    public void SetTopmost(bool topmost)
    {
        if (_handle == IntPtr.Zero) return;

        // Kept in step with the HWND style. WPF caches Topmost separately and re-applies it on
        // some operations, so setting only one of the two lets them drift apart.
        if (Topmost != topmost) Topmost = topmost;

        if (Native.IsTopmost(_handle) == topmost) return;

        Native.SetWindowPos(
            _handle,
            topmost ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST,
            0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>
    /// Inserts the pill directly above <paramref name="host"/> without making it topmost.
    /// Re-asserted by the caller, because activating the host raises it back over the pill.
    /// </summary>
    public bool TryPlaceAbove(IntPtr host)
    {
        if (_handle == IntPtr.Zero) return false;

        var placement = ZOrder.FindInsertAfter(ZOrder.SystemZOrderApi.Instance, _handle, host);
        if (!placement.Success) return false;
        if (placement.AlreadyPlaced) return true;

        return Native.SetWindowPos(
            _handle, placement.InsertAfter, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
    }

    /// <summary>
    /// Forgets the applied geometry so the next placement re-asserts position and z-order. Used
    /// when the pill is re-shown, since it may have been buried while hidden.
    /// </summary>
    public void InvalidatePlacement() => _applied = null;

    public void SetShown(bool shown)
    {
        // Deliberately not a pure latch on our own belief. The pill is an owned window, and the
        // window manager hides owned windows itself when the owner is minimised — without WPF's
        // Visibility property changing. Latching on _shown alone left the pill hidden permanently
        // the first time that happened: every later tick asked for "shown", saw it already
        // believed shown, and returned. Measured on a live pill: WS_VISIBLE clear on the HWND
        // while Visibility still read Visible, and no amount of tracking brought it back.
        var handleAgrees = _handle == IntPtr.Zero || Native.IsWindowVisible(_handle) == shown;
        if (shown == _shown && handleAgrees) return;

        _shown = shown;
        Visibility = shown ? Visibility.Visible : Visibility.Hidden;

        // WPF skips its own work when the property already holds the requested value, so in the
        // drifted case the assignment above is a no-op and the HWND has to be told directly.
        if (!handleAgrees && _handle != IntPtr.Zero)
            Native.ShowWindow(_handle, shown ? Native.SW_SHOWNOACTIVATE : Native.SW_HIDE);

        // Coming back from hidden, the window may have lost its place in the topmost band, so the
        // next placement must re-assert it rather than being skipped as unchanged.
        if (shown) InvalidatePlacement();
    }
}

/// <summary>
/// Converts a logical group member to physical pixels by rounding its boundaries in one shared
/// coordinate system. This prevents fractional DPI from making a member one pixel wider than its
/// already-rounded group rectangle.
/// </summary>
internal static class PillGroupScale
{
    internal readonly record struct ScaledMember(
        int GroupWidth, int MemberWidth, int MemberOffset);

    public static ScaledMember Member(
        double groupWidthLogical,
        double memberWidthLogical,
        double memberOffsetLogical,
        double scale)
    {
        var groupWidth = (int)Math.Round(groupWidthLogical * scale);
        var memberOffset = (int)Math.Round(memberOffsetLogical * scale);
        var memberRight = (int)Math.Round(
            (memberOffsetLogical + memberWidthLogical) * scale);
        return new ScaledMember(
            groupWidth,
            MemberWidth: memberRight - memberOffset,
            memberOffset);
    }
}
