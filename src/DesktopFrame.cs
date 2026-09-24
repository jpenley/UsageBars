using System;

namespace UsageBars;

/// <summary>
/// Recognises an Electron app's main frame window.
///
/// Both desktop apps UsageBars attaches to — Claude Desktop and Codex/ChatGPT — are Electron, and
/// so is Chrome and every other CEF app. The class name alone is therefore never sufficient; the
/// caller pairs this with an owning-process check. That pairing is the guard that stopped the pill
/// appearing on Chrome windows, and it is the reason this is a predicate rather than a finder.
/// </summary>
internal static class DesktopFrame
{
    private const string ElectronFrameClass = "Chrome_WidgetWin_1";
    private const long WS_THICKFRAME = 0x00040000;

    /// <summary>
    /// True for a visible, resizable top-level frame — what a user would point at and call "the
    /// window".
    ///
    /// Resizability is what separates a real frame from the tooltips, dropdowns and hidden message
    /// windows that share its class. Electron is the worst offender, reusing
    /// <c>Chrome_WidgetWin_1</c> for all of them, which is why this test exists at all; it is
    /// equally the right guard for any other frame identified by class.
    /// </summary>
    public static bool IsResizableFrame(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !Native.IsWindow(hWnd) || !Native.IsWindowVisible(hWnd)) return false;
        return (Native.GetWindowStyle(hWnd, Native.GWL_STYLE) & WS_THICKFRAME) != 0;
    }

    /// <summary>True for a visible, resizable Electron frame.</summary>
    public static bool IsElectronFrame(IntPtr hWnd) =>
        IsResizableFrame(hWnd) && Native.ClassNameOf(hWnd) == ElectronFrameClass;
}
