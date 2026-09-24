using Xunit;

namespace UsageBars.Tests;

/// <summary>
/// Which windows count as covering the pill. Every class named here was observed on a real
/// desktop hiding the pill for no useful reason.
/// </summary>
public class TransientChromeTests
{
    /// <summary>
    /// The drag-and-drop cursor image. Observed hiding the Claude pill mid-drag:
    /// <c>covered by SysDragImage 0x1800D38</c>. Unlike a window move it raises no
    /// MOVESIZESTART, so suppressing during a move does not catch it.
    /// </summary>
    [Fact]
    public void TheDragImageIsNotTreatedAsCovering()
    {
        Assert.True(ZOrder.IsTransientChrome("SysDragImage"));
    }

    [Theory]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("Shell_TrayWnd")]
    [InlineData("Shell_SecondaryTrayWnd")]
    public void DesktopAndShellBackdropsAreNotCovering(string className)
    {
        Assert.True(ZOrder.IsTransientChrome(className));
    }

    [Theory]
    [InlineData("tooltips_class32")]
    [InlineData("DragWindow")]
    [InlineData("SnapAssistWindow")]
    public void MomentaryOverlaysAreNotCovering(string className)
    {
        Assert.True(ZOrder.IsTransientChrome(className));
    }

    /// <summary>
    /// Real application windows must still count, or the pill would float over the app in front
    /// of it. MozillaWindowClass is here because Firefox legitimately covering the pill is the
    /// behaviour that must survive all of the above.
    /// </summary>
    [Theory]
    [InlineData("MozillaWindowClass")]
    [InlineData("Chrome_WidgetWin_1")]
    [InlineData("CASCADIA_HOSTING_WINDOW_CLASS")]
    [InlineData("ConsoleWindowClass")]
    [InlineData("")]
    public void RealApplicationWindowsStillCover(string className)
    {
        Assert.False(ZOrder.IsTransientChrome(className));
    }
}
