using System;
using Xunit;

namespace UsageBars.Tests;

public class TargetPositionTests
{
    [Theory]
    [InlineData("Desktop", "TopCenter")]
    [InlineData("Terminal", "TopRight")]
    [InlineData("Ide", "BottomCenter")]
    public void ContextDefaultMapsPlacementToAnchor(
        string placement, string expected) =>
        Assert.Equal(Enum.Parse<PillAnchor>(expected),
            TargetPositionRules.DefaultFor(Enum.Parse<TargetPlacementKind>(placement)));

    [Theory]
    [InlineData(0, -1, -4)]
    [InlineData(0, 1, 4)]
    [InlineData(1999, 1, 2000)]
    [InlineData(-1999, -1, -2000)]
    public void NudgeMovesFourUnitsAndClamps(int value, int direction, int expected) =>
        Assert.Equal(expected, TargetPositionRules.Nudge(value, direction));

    [Fact]
    public void CapturedTargetGetsAnExplicitContextDefault()
    {
        var target = TargetDefinition.Captured(
            "IDE", "rider64", "SunAwtFrame", new[] { ProviderBinding.Claude },
            TargetPlacementKind.Ide, TargetActivationPolicy.AlwaysWhileOpen, false, 26);

        Assert.Equal(PillAnchor.BottomCenter, target.Anchor);
        Assert.Equal(0, target.OffsetX);
        Assert.Equal(0, target.OffsetY);
    }

    [Fact]
    public void SeededTargetInheritsItsPlacementDefault()
    {
        var target = TargetDefinition.Seeded(
            "Terminal", "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS",
            new[] { ProviderBinding.Codex }, TargetPlacementKind.Terminal,
            TargetActivationPolicy.AlwaysWhileOpen, true, 0);

        Assert.Equal(PillAnchor.TopRight, target.Anchor);
        Assert.Equal(0, target.OffsetX);
        Assert.Equal(0, target.OffsetY);
    }

    [Fact]
    public void PositionFormattingUsesFriendlyAnchorAndSignedOffsets()
    {
        Assert.Equal("Top right (+8, -4)",
            TargetPositionRules.Format(PillAnchor.TopRight, 8, -4));
        Assert.Equal("Bottom center (0, 0)",
            TargetPositionRules.Format(PillAnchor.BottomCenter, 0, 0));
    }

    [Fact]
    public void TopRightUsesTheRightReserveAndPerTargetOffsets()
    {
        var strip = new Native.RECT { Left = 100, Top = 50, Right = 1100, Bottom = 90 };
        var bounds = new Native.RECT { Left = 100, Top = 50, Right = 1100, Bottom = 850 };

        var position = OverlayPlacement.Calculate(
            strip, bounds, pillWidth: 200, pillHeight: 24, rightReserve: 160,
            offsetX: -12, offsetY: 8, align: PillAlign.Right);

        Assert.Equal(728, position!.Value.X);
        Assert.Equal(66, position.Value.Y);
    }

    [Fact]
    public void LargeOffsetsAreClampedInsideTheTargetBounds()
    {
        var strip = new Native.RECT { Left = 0, Top = 0, Right = 1000, Bottom = 40 };
        var bounds = new Native.RECT { Left = 0, Top = 0, Right = 1000, Bottom = 700 };

        var position = OverlayPlacement.Calculate(
            strip, bounds, 200, 24, rightReserve: 160,
            offsetX: 2000, offsetY: -2000, align: PillAlign.Centre);

        Assert.Equal(640, position!.Value.X);
        Assert.Equal(0, position.Value.Y);
    }

    [Fact]
    public void SharedLayoutRequiresTheSameTargetId()
    {
        var sharedTarget = Guid.Parse("70707070-7070-7070-7070-707070707070");
        Assert.True(SharedPillLayout.SharesPosition(
            new HostWindowInfo(IntPtr.Zero, HostKind.Desktop, "A", ProviderId.Claude,
                HostEvidence.Open, TargetId: sharedTarget, Anchor: PillAnchor.TopCenter),
            new HostWindowInfo(IntPtr.Zero, HostKind.Desktop, "A", ProviderId.Codex,
                HostEvidence.Open, TargetId: sharedTarget, Anchor: PillAnchor.TopCenter)));
        Assert.False(SharedPillLayout.SharesPosition(
            new HostWindowInfo(IntPtr.Zero, HostKind.Desktop, "A", ProviderId.Claude,
                HostEvidence.Open, TargetId: Guid.NewGuid(), Anchor: PillAnchor.TopCenter),
            new HostWindowInfo(IntPtr.Zero, HostKind.Desktop, "A", ProviderId.Codex,
                HostEvidence.Open, TargetId: Guid.NewGuid(), Anchor: PillAnchor.TopCenter)));
    }

    [Theory]
    [InlineData("Centre", 0, 2000)]
    [InlineData("Centre", 0, -2000)]
    [InlineData("Right", 160, 2000)]
    [InlineData("Right", 160, -2000)]
    public void SharedGroupClampsAsOneRectangleWithoutLosingItsGap(
        string alignment, int rightReserve, int offsetX)
    {
        var align = Enum.Parse<PillAlign>(alignment);
        const int claudeWidth = 239;
        const int codexWidth = 124;
        const int gap = 8;
        const int groupWidth = claudeWidth + gap + codexWidth;
        var strip = new Native.RECT { Left = 100, Top = 50, Right = 1100, Bottom = 90 };
        var bounds = new Native.RECT { Left = 100, Top = 50, Right = 1100, Bottom = 850 };

        var claude = OverlayPlacement.CalculateGroupMember(
            strip, bounds, groupWidth, claudeWidth, memberOffset: 0, pillHeight: 24,
            rightReserve, offsetX, offsetY: 0, align);
        var codex = OverlayPlacement.CalculateGroupMember(
            strip, bounds, groupWidth, codexWidth, memberOffset: claudeWidth + gap,
            pillHeight: 24, rightReserve, offsetX, offsetY: 0, align);

        Assert.NotNull(claude);
        Assert.NotNull(codex);
        Assert.Equal(claude!.Value.X + claudeWidth + gap, codex!.Value.X);
        var expectedGroupLeft = offsetX > 0
            ? strip.Right - rightReserve - groupWidth
            : bounds.Left;
        Assert.Equal(expectedGroupLeft, claude.Value.X);
        Assert.Equal(expectedGroupLeft + groupWidth, codex.Value.X + codexWidth);
    }

    [Theory]
    [InlineData("Centre")]
    [InlineData("Right")]
    public void SharedGroupIsRejectedWhenOnlyItsIndividualMembersWouldFit(string alignment)
    {
        var align = Enum.Parse<PillAlign>(alignment);
        const int firstWidth = 160;
        const int secondWidth = 120;
        const int gap = 8;
        const int groupWidth = firstWidth + gap + secondWidth;
        var strip = new Native.RECT { Left = 0, Top = 0, Right = 300, Bottom = 40 };

        Assert.NotNull(OverlayPlacement.Calculate(
            strip, firstWidth, 24, rightReserve: 0, 0, 0, align));
        Assert.NotNull(OverlayPlacement.Calculate(
            strip, secondWidth, 24, rightReserve: 0, 0, 0, align));
        Assert.Null(OverlayPlacement.CalculateGroupMember(
            strip, strip, groupWidth, firstWidth, memberOffset: 0, pillHeight: 24,
            rightReserve: 0, offsetX: 0, offsetY: 0, align));
        Assert.Null(OverlayPlacement.CalculateGroupMember(
            strip, strip, groupWidth, secondWidth, memberOffset: firstWidth + gap,
            pillHeight: 24, rightReserve: 0, offsetX: 0, offsetY: 0, align));
    }

    [Fact]
    public void CurrentResolvedHostsBreakAStalePreviousTargetGroupImmediately()
    {
        var previousTarget = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var currentClaudeTarget = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var currentCodexTarget = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var hwnd = new IntPtr(42);
        var previousClaude = Host(hwnd, ProviderId.Claude, previousTarget, PillAnchor.TopCenter);
        var previousCodex = Host(hwnd, ProviderId.Codex, previousTarget, PillAnchor.TopCenter);
        var wantedClaude = Host(hwnd, ProviderId.Claude, currentClaudeTarget, PillAnchor.TopRight);
        var wantedCodex = Host(hwnd, ProviderId.Codex, currentCodexTarget, PillAnchor.TopCenter);

        Assert.NotNull(SharedPillLayout.Create(
            previousClaude, firstWidth: 200, previousCodex, secondWidth: 120, gap: 8));
        Assert.Null(SharedPillLayout.Create(
            wantedClaude, firstWidth: 200, wantedCodex, secondWidth: 120, gap: 8));
    }

    [Fact]
    public void SharedLayoutPreservesProviderOrderAndGap()
    {
        var target = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var hwnd = new IntPtr(42);

        var layout = SharedPillLayout.Create(
            Host(hwnd, ProviderId.Claude, target, PillAnchor.TopRight), firstWidth: 200,
            Host(hwnd, ProviderId.Codex, target, PillAnchor.TopRight), secondWidth: 120,
            gap: 8);

        Assert.NotNull(layout);
        Assert.Equal(328, layout!.Value.First.GroupWidth);
        Assert.Equal(0, layout.Value.First.MemberOffset);
        Assert.Equal(208, layout.Value.Second.MemberOffset);
    }

    [Fact]
    public void FractionalDpiRoundsMemberBoundariesInsideOneSharedGroup()
    {
        // At 125%, independently rounding 102 DIPs gives 128 px, which would make this second
        // member end at 263 even though the 210-DIP group rounds to 262. Shared boundaries must
        // instead give the member the 127 physical pixels that remain inside the group.
        var scaled = PillGroupScale.Member(
            groupWidthLogical: 210, memberWidthLogical: 102, memberOffsetLogical: 108,
            scale: 1.25);

        Assert.Equal(262, scaled.GroupWidth);
        Assert.Equal(135, scaled.MemberOffset);
        Assert.Equal(127, scaled.MemberWidth);
        Assert.Equal(scaled.GroupWidth, scaled.MemberOffset + scaled.MemberWidth);

        var strip = new Native.RECT { Left = 0, Top = 0, Right = 1000, Bottom = 40 };
        Assert.NotNull(OverlayPlacement.CalculateGroupMember(
            strip, strip, scaled.GroupWidth, scaled.MemberWidth, scaled.MemberOffset,
            pillHeight: 30, rightReserve: 0, offsetX: 2000, offsetY: 0));
    }

    [Fact]
    public void PositiveStatusBarHeightWinsOverTheTitleBarFallback() =>
        Assert.Equal(26, PlacementStripHeight.Bottom(statusBarHeight: 26, titleBarHeight: 40));

    [Fact]
    public void MissingStatusBarHeightUsesTheTitleBarFallback() =>
        Assert.Equal(40, PlacementStripHeight.Bottom(statusBarHeight: 0, titleBarHeight: 40));

    private static HostWindowInfo Host(
        IntPtr hwnd, ProviderId provider, Guid targetId, PillAnchor anchor) =>
        new(hwnd, HostKind.Desktop, "host", provider, HostEvidence.Open,
            TargetId: targetId, Anchor: anchor);
}
