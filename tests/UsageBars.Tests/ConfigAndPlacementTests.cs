using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace UsageBars.Tests;

public class ConfigNormalizeTests
{
    [Fact]
    public void LegacyCreditAndAbsoluteColorSettingsAreIgnored()
    {
        var config = JsonSerializer.Deserialize<Config>("""
            { "RequestCreditUrl":"https://example.com/credit", "RequestCreditDomain":"company",
              "CodexRequestCreditUrl":"https://example.com/reset", "AmberAtPercent":50, "RedAtPercent":20 }
            """)!;
        config.Normalize();
        var model = UsageModel.BuildCodex(
            new CodexSnapshot(new CodexWindow(99, 300, null), null, DateTimeOffset.Now), true, null, config);
        Assert.Equal(PillAction.None, model.Action);
        Assert.Null(model.ActionUrl);
        var saved = JsonSerializer.Serialize(config);
        foreach (var key in new[] { "RequestCreditUrl", "RequestCreditDomain", "CodexRequestCreditUrl", "AmberAtPercent", "RedAtPercent" })
            Assert.DoesNotContain(key, saved);
    }

    [Fact]
    public void ZeroSamplesWouldTrustASingleReadingSoItIsClamped()
    {
        var config = new Config { MinVelocitySamples = 0 };

        config.Normalize();

        Assert.Equal(2, config.MinVelocitySamples);
    }

    [Theory]
    [InlineData(-5d)]
    [InlineData(0d)]
    public void NonPositiveTitleBarHeightIsClamped(double height)
    {
        var config = new Config { TitleBarHeight = height };

        config.Normalize();

        Assert.True(config.TitleBarHeight >= 8);
    }

    [Fact]
    public void NonFiniteValuesAreReplacedRatherThanPropagated()
    {
        var config = new Config
        {
            VelocityPaddingFactor = double.NaN,
            VelocityWindowHours = double.PositiveInfinity,
        };

        config.Normalize();

        Assert.True(double.IsFinite(config.VelocityPaddingFactor));
        Assert.True(double.IsFinite(config.VelocityWindowHours));
    }

    [Fact]
    public void AnInvalidTerminalRegexFallsBackToTheDefault()
    {
        var config = new Config { TerminalTitlePattern = "([unclosed" };

        config.Normalize();

        Assert.Equal(new Config().TerminalTitlePattern, config.TerminalTitlePattern);
        Assert.Contains(config.Adjustments, a => a.Contains(nameof(Config.TerminalTitlePattern)));
    }

    [Fact]
    public void DefaultsAreAlreadyValid()
    {
        var config = new Config();

        config.Normalize();

        Assert.Empty(config.Adjustments);
    }
}

public class OverlayPlacementTests
{
    private static Native.RECT Strip(int left, int top, int width, int height) =>
        new() { Left = left, Top = top, Right = left + width, Bottom = top + height };

    [Fact]
    public void CentresWithinTheSpaceLeftAfterTheReserve()
    {
        // 1000 wide, 200 reserved for window controls -> centre of the remaining 800.
        var position = OverlayPlacement.Calculate(
            Strip(0, 0, 1000, 40), pillWidth: 200, pillHeight: 24,
            rightReserve: 200, offsetX: 0, offsetY: 0);

        Assert.NotNull(position);
        Assert.Equal(300, position!.Value.X);
        Assert.Equal(8, position.Value.Y);
    }

    [Fact]
    public void ReturnsNullWhenTheStripCannotHoldThePill()
    {
        var position = OverlayPlacement.Calculate(
            Strip(0, 0, 300, 40), pillWidth: 260, pillHeight: 24,
            rightReserve: 160, offsetX: 0, offsetY: 0);

        Assert.Null(position);
    }

    [Fact]
    public void RequiresMarginOnBothSidesNotJustAFit()
    {
        // Exactly wide enough for the pill, but with no breathing room.
        var tight = OverlayPlacement.Calculate(
            Strip(0, 0, 200, 40), 200, 24, rightReserve: 0, offsetX: 0, offsetY: 0);
        Assert.Null(tight);

        var roomy = OverlayPlacement.Calculate(
            Strip(0, 0, 200 + OverlayPlacement.MinimumMargin * 2, 40),
            200, 24, rightReserve: 0, offsetX: 0, offsetY: 0);
        Assert.NotNull(roomy);
    }

    [Fact]
    public void OffsetsShiftThePill()
    {
        var position = OverlayPlacement.Calculate(
            Strip(100, 50, 1000, 40), 200, 24, rightReserve: 0, offsetX: 7, offsetY: -3);

        Assert.Equal(100 + 400 + 7, position!.Value.X);
        Assert.Equal(50 + 8 - 3, position.Value.Y);
    }

    [Fact]
    public void ARightReserveWiderThanTheStripHidesRatherThanPlacingOffScreen()
    {
        var position = OverlayPlacement.Calculate(
            Strip(0, 0, 400, 40), 200, 24, rightReserve: 500, offsetX: 0, offsetY: 0);

        Assert.Null(position);
    }

    [Fact]
    public void ZeroWidthPillIsRejected()
    {
        Assert.Null(OverlayPlacement.Calculate(Strip(0, 0, 1000, 40), 0, 24, 0, 0, 0));
    }

    /// <summary>
    /// Terminals put their tabs in the middle of the title bar, so the pill is pushed right until
    /// it meets the reserve — which is what "just short of the minimise button" means, the reserve
    /// being exactly the space kept for the window controls.
    /// </summary>
    [Fact]
    public void RightAlignmentSitsAgainstTheReserve()
    {
        // 1000 wide, 200 reserved -> the pill's right edge lands at 800, its left at 600.
        var position = OverlayPlacement.Calculate(
            Strip(0, 0, 1000, 40), pillWidth: 200, pillHeight: 24,
            rightReserve: 200, offsetX: 0, offsetY: 0, align: PillAlign.Right);

        Assert.NotNull(position);
        Assert.Equal(600, position!.Value.X);
        Assert.Equal(8, position.Value.Y);
    }

    /// <summary>
    /// Right-alignment never encroaches on the reserve, whatever the strip is doing — the whole
    /// point of the reserve is that the window controls stay clickable.
    /// </summary>
    [Theory]
    [InlineData(600)]
    [InlineData(1000)]
    [InlineData(2400)]
    public void RightAlignmentNeverCrossesIntoTheReserve(int stripWidth)
    {
        const int reserve = 160;
        const int pillWidth = 220;

        var position = OverlayPlacement.Calculate(
            Strip(50, 0, stripWidth, 40), pillWidth, 24, reserve, 0, 0, PillAlign.Right);

        Assert.NotNull(position);
        Assert.True(position!.Value.X + pillWidth <= 50 + stripWidth - reserve);
    }

    /// <summary>
    /// Centring needs clearance on both sides; right-alignment only on the left, because the
    /// reserve is already the clearance on the right. So a strip can be too narrow to centre in
    /// and still wide enough to right-align in.
    /// </summary>
    [Fact]
    public void RightAlignmentFitsAStripTooNarrowToCentreIn()
    {
        var strip = Strip(0, 0, 200 + OverlayPlacement.MinimumMargin, 40);

        Assert.Null(OverlayPlacement.Calculate(strip, 200, 24, 0, 0, 0, PillAlign.Centre));
        Assert.NotNull(OverlayPlacement.Calculate(strip, 200, 24, 0, 0, 0, PillAlign.Right));
    }

    [Fact]
    public void RightAlignmentStillHidesWhenTheStripCannotHoldThePill()
    {
        var position = OverlayPlacement.Calculate(
            Strip(0, 0, 300, 40), pillWidth: 260, pillHeight: 24,
            rightReserve: 160, offsetX: 0, offsetY: 0, align: PillAlign.Right);

        Assert.Null(position);
    }

    /// <summary>Per-target offsets have to work in either alignment.</summary>
    [Fact]
    public void RightAlignmentStillHonoursTheManualNudge()
    {
        var position = OverlayPlacement.Calculate(
            Strip(100, 50, 1000, 40), 200, 24, rightReserve: 200,
            offsetX: -30, offsetY: 4, align: PillAlign.Right);

        Assert.Equal(100 + 800 - 200 - 30, position!.Value.X);
        Assert.Equal(50 + 8 + 4, position.Value.Y);
    }

    /// <summary>
    /// Two pills sharing one terminal, which is the ordinary case rather than a corner: Windows
    /// Terminal runs every window in one process, so process-ancestry detection credits the same
    /// front-most window to both providers whenever a Claude Code and a Codex session are both
    /// open. Undetected they draw exactly on top of each other.
    ///
    /// Geometry taken from a live measurement of that arrangement.
    /// </summary>
    [Fact]
    public void TwoPillsSharingAHostSitSideBySideWithoutOverlapping()
    {
        const int reserve = 160;
        const int codexWidth = 124;
        const int claudeWidth = 239;
        const int gap = 8;

        var strip = Strip(1268, 688, 1769, 40);

        var codex = OverlayPlacement.Calculate(
            strip, codexWidth, 24, reserve, offsetX: 0, offsetY: 0, align: PillAlign.Right);

        // Claude is shifted left by exactly the room Codex occupies, plus the gap.
        var claude = OverlayPlacement.Calculate(
            strip, claudeWidth, 24, reserve,
            offsetX: -(codexWidth + gap), offsetY: 0, align: PillAlign.Right);

        Assert.NotNull(codex);
        Assert.NotNull(claude);

        // Claude ends exactly one gap before Codex begins — adjacent, never overlapping.
        Assert.Equal(codex!.Value.X - gap, claude!.Value.X + claudeWidth);

        // And the outer pill still stops dead on the reserve boundary.
        Assert.Equal(strip.Right - reserve, codex.Value.X + codexWidth);
    }

    /// <summary>
    /// A pill on an IDE's status bar is centred across the whole strip. There are no window
    /// controls down there to keep clear of, so the reserve is passed as zero and the centre of
    /// the strip is the real centre of the window — "the middle of the bottom bar".
    /// </summary>
    [Fact]
    public void TheStatusBarPillCentresAcrossTheWholeStrip()
    {
        // A 1600-wide Rider window, status bar 26 logical px tall at the bottom.
        var position = OverlayPlacement.Calculate(
            Strip(0, 1000, 1600, 26), pillWidth: 240, pillHeight: 24,
            rightReserve: 0, offsetX: 0, offsetY: 0);

        Assert.NotNull(position);
        Assert.Equal((1600 - 240) / 2, position!.Value.X);
        Assert.Equal(1000 + 1, position.Value.Y);
    }

    /// <summary>
    /// The reserve is a title-bar concept. Applying it to a status bar would push the pill off
    /// centre by half the reserve, which is exactly what the placement is meant to avoid.
    /// </summary>
    [Fact]
    public void AReserveWouldPushTheStatusBarPillOffCentre()
    {
        var strip = Strip(0, 1000, 1600, 26);

        var centred = OverlayPlacement.Calculate(strip, 240, 24, rightReserve: 0, 0, 0);
        var reserved = OverlayPlacement.Calculate(strip, 240, 24, rightReserve: 160, 0, 0);

        Assert.Equal(centred!.Value.X - 80, reserved!.Value.X);
    }

    /// <summary>
    /// Two pills sharing a centred strip — Claude Code and Codex both running under one Rider
    /// window, which is routine given both qualify per process. Centred, the *pair* has to
    /// straddle the centre: centring one and pushing the other off it would leave the group
    /// visibly off to one side of the bar it was put on to sit in the middle of.
    /// </summary>
    [Fact]
    public void TwoPillsSharingACentredStripStraddleTheCentre()
    {
        const int claudeWidth = 239;
        const int codexWidth = 124;
        const int gap = 8;

        var strip = Strip(0, 1000, 1600, 26);

        // The shifts ApplySharedHostLayout hands each pill in the centred arrangement.
        var claude = OverlayPlacement.Calculate(
            strip, claudeWidth, 24, rightReserve: 0,
            offsetX: -((codexWidth + gap) / 2), offsetY: 0);
        var codex = OverlayPlacement.Calculate(
            strip, codexWidth, 24, rightReserve: 0,
            offsetX: (claudeWidth + gap) / 2, offsetY: 0);

        Assert.NotNull(claude);
        Assert.NotNull(codex);

        // Adjacent with exactly one gap between them, and never overlapping.
        Assert.Equal(claude!.Value.X + claudeWidth + gap, codex!.Value.X);

        // And the pair's own centre is the strip's centre, to within a rounding pixel.
        var pairCentre = (claude.Value.X + codex.Value.X + codexWidth) / 2.0;
        Assert.InRange(pairCentre, 800 - 1, 800 + 1);
    }

    /// <summary>
    /// Desktop app frames must be unaffected by the terminal change, so the default has to stay
    /// centred.
    /// </summary>
    [Fact]
    public void CentringRemainsTheDefault()
    {
        var implicitly_ = OverlayPlacement.Calculate(Strip(0, 0, 1000, 40), 200, 24, 200, 0, 0);
        var explicitly = OverlayPlacement.Calculate(
            Strip(0, 0, 1000, 40), 200, 24, 200, 0, 0, PillAlign.Centre);

        Assert.Equal(explicitly, implicitly_);
        Assert.Equal(300, implicitly_!.Value.X);
    }
}

/// <summary>
/// Which windows count as an IDE frame — the hosts that carry a pill whenever they are open, on
/// their status bar rather than their title bar.
/// </summary>
public class IdeFrameTests
{
    private static IReadOnlyList<TargetDefinition> IdeTargets() =>
        TargetDefaults.Create(new Config())
            .Where(target => target.Placement == TargetPlacementKind.Ide)
            .ToList();

    private static WindowSnapshot Window(string processName, string className) =>
        new(IntPtr.Zero, 0, processName, className, "", true);

    /// <summary>Both enumerated live: one visible main frame each, of these classes.</summary>
    [Theory]
    [InlineData("rider64", "SunAwtFrame")]
    [InlineData("Rider64", "SunAwtFrame")]
    [InlineData("rider", "SunAwtFrame")]
    [InlineData("Code", "Chrome_WidgetWin_1")]
    [InlineData("code", "Chrome_WidgetWin_1")]
    [InlineData("Code - Insiders", "Chrome_WidgetWin_1")]
    public void AnIdeMainFrameIsRecognised(string processName, string className)
    {
        Assert.Contains(IdeTargets(), target => target.Matches(Window(processName, className)));
    }

    /// <summary>
    /// Only the main frame has a status bar, and both products surround theirs with windows that
    /// would otherwise qualify. A pill centred along the bottom edge of one of these would sit on
    /// content, or on nothing — so the class is half the key, not decoration.
    /// </summary>
    [Theory]
    [InlineData("rider64", "SunAwtDialog")]
    [InlineData("rider64", "SunAwtWindow")]
    [InlineData("rider64", "SunAwtToolkit")]
    [InlineData("rider64", "GDI+ Hook Window Class")]
    [InlineData("Code", "Chrome_WidgetWin_0")]
    [InlineData("Code", "Electron_SystemPreferencesHostWindow")]
    [InlineData("Code", "Base_PowerMessageWindow")]
    [InlineData("Code", "crashpad_SessionEndWatcher")]
    public void AnIdesOtherWindowsAreNot(string processName, string className)
    {
        Assert.DoesNotContain(IdeTargets(), target => target.Matches(Window(processName, className)));
    }

    /// <summary>
    /// Every other host still has to earn its pill by having a session under it. Claude Desktop is
    /// the one that matters: it is Electron too, so it shares VS Code's window class exactly, and
    /// only the process name keeps the two apart.
    /// </summary>
    [Theory]
    [InlineData("claude", "Chrome_WidgetWin_1")]
    [InlineData("ChatGPT", "Chrome_WidgetWin_1")]
    [InlineData("chrome", "Chrome_WidgetWin_1")]
    [InlineData("WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS")]
    [InlineData("pwsh", "ConsoleWindowClass")]
    [InlineData("idea64", "SunAwtFrame")]
    [InlineData("cursor", "Chrome_WidgetWin_1")]
    [InlineData("unknown", "SunAwtFrame")]
    public void EveryOtherWindowIsNot(string processName, string className)
    {
        Assert.DoesNotContain(IdeTargets(), target => target.Matches(Window(processName, className)));
    }

    /// <summary>
    /// The heights differ by product, which is why they live with the frame rather than in config:
    /// one number would mis-centre one of the two by two pixels.
    /// </summary>
    [Theory]
    [InlineData("rider64", 26)]
    [InlineData("Code", 22)]
    [InlineData("Code - Insiders", 22)]
    public void EachIdeCarriesItsOwnStatusBarHeight(string processName, double expected)
    {
        var target = Assert.Single(IdeTargets(), target =>
            target.ProcessName!.Equals(processName, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(expected, target.StatusBarHeight);
    }

    [Fact]
    public void AnythingThatIsNotAnIdeHasNoStatusBar()
    {
        Assert.DoesNotContain(IdeTargets(), target =>
            target.Matches(Window("WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS")));
    }

    /// <summary>
    /// The measured regression: opening context menus in Rider produced nine distinct short-lived
    /// hosts in forty-five seconds, each logged as "Terminal by Ancestry: rider64" and each drawing
    /// a pill across the top of the menu. A popup shares its IDE's process, so it inherits the
    /// session running underneath — the process test cannot tell them apart, and the frame class is
    /// what does.
    /// </summary>
    [Theory]
    [InlineData("rider64", "SunAwtWindow")]
    [InlineData("rider64", "SunAwtDialog")]
    [InlineData("rider64", "#32768")]
    [InlineData("Code", "#32768")]
    [InlineData("Code", "Chrome_WidgetWin_0")]
    [InlineData("Code - Insiders", "#32768")]
    public void AnIdesPopupsAreNotHosts(string processName, string className)
    {
        Assert.DoesNotContain(IdeTargets(), target => target.Matches(Window(processName, className)));
    }

    [Theory]
    [InlineData("rider64", "SunAwtFrame")]
    [InlineData("Code", "Chrome_WidgetWin_1")]
    public void TheIdesOwnFrameIsNotAPopup(string processName, string className)
    {
        Assert.Contains(IdeTargets(), target => target.Matches(Window(processName, className)));
    }

    /// <summary>
    /// Only applies to IDEs whose frame class is recorded. Everything else — terminals, and the
    /// editors not in the map — is filtered by the resizable-frame test at the call site instead,
    /// so claiming them here would reject windows that legitimately host a session.
    /// </summary>
    [Theory]
    [InlineData("devenv", "#32768")]
    [InlineData("cursor", "Chrome_WidgetWin_1")]
    [InlineData("WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS")]
    [InlineData("pwsh", "ConsoleWindowClass")]
    public void ProcessesWithNoRecordedFrameAreLeftToTheGeneralTest(
        string processName, string className)
    {
        Assert.DoesNotContain(IdeTargets(), target => target.Matches(Window(processName, className)));
    }
}

public class RetryScheduleTests
{
    [Fact]
    public void BackoffDoubles()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), RetrySchedule.ForAttempt(0));
        Assert.Equal(TimeSpan.FromSeconds(2), RetrySchedule.ForAttempt(1));
        Assert.Equal(TimeSpan.FromSeconds(4), RetrySchedule.ForAttempt(2));
    }

    [Fact]
    public void BackoffIsCapped()
    {
        Assert.Equal(RetrySchedule.Cap, RetrySchedule.ForAttempt(50));
        Assert.Equal(RetrySchedule.Cap, RetrySchedule.ForAttempt(int.MaxValue));
    }

    [Fact]
    public void NegativeAttemptsAreTreatedAsTheFirst()
    {
        Assert.Equal(RetrySchedule.ForAttempt(0), RetrySchedule.ForAttempt(-3));
    }

    [Fact]
    public void BackoffNeverExceedsTheNormalInterval()
    {
        var interval = TimeSpan.FromSeconds(5);

        Assert.Equal(interval, RetrySchedule.ForAttempt(20, interval));
        Assert.Equal(TimeSpan.FromSeconds(1), RetrySchedule.ForAttempt(0, interval));
    }
}
