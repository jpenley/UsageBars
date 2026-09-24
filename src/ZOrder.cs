using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace UsageBars;

/// <summary>
/// Z-order inspection used to keep the pill "always on" without it floating over unrelated
/// windows.
///
/// The pill stays topmost so activating its host can never bury it (which is what a literal
/// "one above the host" z-order would suffer: activation raises the host back above the pill,
/// causing a visible flicker every time you click it). Instead the pill is hidden whenever
/// something is genuinely covering that patch of screen, which produces the same result — visible
/// exactly where the host's title bar is unobstructed — with no z-order fighting.
/// </summary>
internal static class ZOrder
{
    /// <summary>Top-level windows, front-most first. EnumWindows already returns z-order.</summary>
    public static List<IntPtr> TopLevelWindows()
    {
        var list = new List<IntPtr>(256);
        EnumWindows((h, _) => { list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }

    /// <summary>
    /// Whether a window actually occupies pixels on screen. Cloaked windows matter here: DWM
    /// keeps background UWP windows around with IsWindowVisible true even though nothing is drawn,
    /// so without this check stray cloaked windows would appear to cover the pill.
    /// </summary>
    public static bool IsRealVisibleWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !Native.IsWindow(hWnd)) return false;
        if (!Native.IsWindowVisible(hWnd) || Native.IsIconic(hWnd)) return false;
        if (IsCloaked(hWnd)) return false;
        if (!Native.GetWindowRect(hWnd, out var r)) return false;
        return r.Width > 0 && r.Height > 0;
    }

    public static bool IsCloaked(IntPtr hWnd)
    {
        try
        {
            if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0)
                return cloaked != 0;
        }
        catch (Exception)
        {
            // dwmapi unavailable: assume not cloaked.
        }
        return false;
    }

    /// <summary>
    /// True when any window sitting above <paramref name="host"/> overlaps <paramref name="pill"/>'s
    /// rectangle. The pill itself and the host are excluded, as are windows belonging to
    /// <paramref name="ownPid"/> (our tray menu).
    /// </summary>
    public static bool IsRegionCovered(
        List<IntPtr> zOrder, IntPtr host, IntPtr pillHwnd, Native.RECT pill, uint ownPid,
        out string? coveredBy)
    {
        coveredBy = null;

        foreach (var hWnd in zOrder)
        {
            // Everything from the host downwards is behind us; stop.
            if (hWnd == host) return false;

            if (hWnd == pillHwnd) continue;
            if (Native.ProcessIdOf(hWnd) == ownPid) continue;
            if (!IsRealVisibleWindow(hWnd)) continue;

            var cls = Native.ClassNameOf(hWnd);
            if (IsTransientChrome(cls)) continue;

            if (!Native.GetWindowRect(hWnd, out var r)) continue;
            if (!Intersects(r, pill)) continue;

            coveredBy = $"{cls} 0x{hWnd.ToInt64():X}";
            return true;
        }

        // Host wasn't found in the list (closed mid-scan); treat as covered so we hide.
        coveredBy = "host not in z-order";
        return true;
    }

    /// <summary>
    /// Window classes that are never "covering" the pill in any meaningful sense: desktop and
    /// shell backdrops, and the transient overlays Windows paints during a drag.
    ///
    /// The drag ones matter because they are genuine top-level windows, sit above everything, and
    /// span the area the pill occupies — so a literal reading of the z-order says the pill is
    /// covered, and it blinks out for the length of every drag. <c>SysDragImage</c> is the
    /// drag-and-drop cursor image, observed hiding the pill while dragging content; unlike a
    /// window move it raises no MOVESIZESTART, so it cannot be handled by suppressing during a
    /// move and has to be recognised here.
    /// </summary>
    public static bool IsTransientChrome(string className) => className is
        // Desktop and shell backdrops.
        "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or
        // Drag and snap feedback.
        "SysDragImage" or "DragWindow" or "SnapAssistWindow" or
        // Tooltips and other momentary popups drawn above everything.
        "tooltips_class32";

    private static bool Intersects(Native.RECT a, Native.RECT b) =>
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    private const int DWMWA_CLOAKED = 14;

    /// <summary>Window-manager queries used by the z-order walk, abstracted so it can be tested.</summary>
    internal interface IZOrderApi
    {
        bool IsWindow(IntPtr hWnd);

        /// <summary>The window immediately above <paramref name="hWnd"/>, or zero at the top.</summary>
        IntPtr PreviousWindow(IntPtr hWnd);

        bool IsTopmost(IntPtr hWnd);
    }

    internal sealed class SystemZOrderApi : IZOrderApi
    {
        public static readonly SystemZOrderApi Instance = new();

        public bool IsWindow(IntPtr hWnd) => Native.IsWindow(hWnd);
        public IntPtr PreviousWindow(IntPtr hWnd) => Native.GetWindow(hWnd, Native.GW_HWNDPREV);
        public bool IsTopmost(IntPtr hWnd) => Native.IsTopmost(hWnd);
    }

    /// <summary>Where to insert the pill so it sits directly above its host.</summary>
    internal readonly record struct ZOrderPlacement(
        bool Success, bool AlreadyPlaced, IntPtr InsertAfter);

    /// <summary>
    /// Works out the <c>hWndInsertAfter</c> that puts the pill immediately above its host, without
    /// making it topmost.
    ///
    /// Walks upwards from the host skipping topmost windows, since those live in a separate band
    /// that a non-topmost window can never be inserted into. Refuses outright when the host is
    /// itself topmost — there is no position above it available to us.
    /// </summary>
    internal static ZOrderPlacement FindInsertAfter(IZOrderApi api, IntPtr overlay, IntPtr host)
    {
        if (!api.IsWindow(overlay) || !api.IsWindow(host)) return default;
        if (api.IsTopmost(host)) return default;

        var visited = new HashSet<IntPtr>();
        var current = host;

        while (visited.Add(current))
        {
            var previous = api.PreviousWindow(current);
            if (!api.IsWindow(overlay) || !api.IsWindow(host)) return default;

            // Nothing above the host at all: go to the top of the non-topmost band.
            if (previous == IntPtr.Zero)
                return new ZOrderPlacement(true, false, Native.HWND_TOP);

            // Already sitting directly above, provided we are not in the topmost band.
            if (previous == overlay)
                return new ZOrderPlacement(!api.IsTopmost(previous), true, IntPtr.Zero);

            if (!api.IsTopmost(previous))
                return new ZOrderPlacement(true, false, previous);

            current = previous;
        }

        // Cycled, which means the z-order changed underneath us; let the caller fall back.
        return default;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);
}
