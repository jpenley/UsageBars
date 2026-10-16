using System;
using System.Windows.Media;

namespace UsageBars;

/// <summary>The reset position and uniform fill colour for a bar with a known window.</summary>
internal readonly record struct ResetPace(double TimeRemainingPct, Color FillColor)
{
    internal const double YellowRadius = 2;
    internal const double FullColorDistance = 15;

    /// <summary>
    /// Compares remaining allowance with the fraction of the window still to run. A missing
    /// deadline or duration is unknown, never an invented pace. Expired deadlines stay at zero
    /// until a provider reports a new window.
    /// </summary>
    public static ResetPace? For(UsageBar bar, DateTimeOffset now)
    {
        if (bar.RemainingPct is not { } remaining || !double.IsFinite(remaining) ||
            bar.ResetsAt is not { } reset || bar.WindowDuration is not { } duration || duration <= TimeSpan.Zero)
            return null;

        var time = Math.Clamp((reset - now).TotalSeconds / duration.TotalSeconds * 100, 0, 100);
        var delta = Math.Clamp(remaining, 0, 100) - time;
        var ratio = Math.Clamp((Math.Abs(delta) - YellowRadius) / (FullColorDistance - YellowRadius), 0, 1);
        var blend = ratio * ratio * (3 - 2 * ratio);
        var yellow = Color.FromRgb(232, 197, 71);
        var endpoint = delta < 0 ? Color.FromRgb(248, 81, 73) : Color.FromRgb(63, 185, 80);
        byte Channel(byte from, byte to) => (byte)Math.Round(from + (to - from) * blend);
        return new ResetPace(time, Color.FromRgb(
            Channel(yellow.R, endpoint.R), Channel(yellow.G, endpoint.G), Channel(yellow.B, endpoint.B)));
    }
}
