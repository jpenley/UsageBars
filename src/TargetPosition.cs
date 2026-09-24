using System;

namespace UsageBars;

internal enum PillAnchor
{
    TopCenter,
    TopRight,
    BottomCenter,
}

internal static class TargetPositionRules
{
    public const int MinimumOffset = -2000;
    public const int MaximumOffset = 2000;
    public const int NudgeSize = 4;

    public static PillAnchor DefaultFor(TargetPlacementKind placement) => placement switch
    {
        TargetPlacementKind.Desktop => PillAnchor.TopCenter,
        TargetPlacementKind.Terminal => PillAnchor.TopRight,
        TargetPlacementKind.Ide => PillAnchor.BottomCenter,
        _ => PillAnchor.TopCenter,
    };

    public static int ClampOffset(int value) =>
        Math.Clamp(value, MinimumOffset, MaximumOffset);

    public static int Nudge(int value, int direction) =>
        ClampOffset(value + Math.Sign(direction) * NudgeSize);

    public static string Format(PillAnchor anchor, int offsetX, int offsetY)
    {
        var label = anchor switch
        {
            PillAnchor.TopCenter => "Top center",
            PillAnchor.TopRight => "Top right",
            PillAnchor.BottomCenter => "Bottom center",
            _ => anchor.ToString(),
        };
        static string Signed(int value) => value > 0 ? $"+{value}" : value.ToString();
        return $"{label} ({Signed(offsetX)}, {Signed(offsetY)})";
    }
}
