using System;

namespace UsageBars;

/// <summary>
/// Backoff for a source that keeps failing. Doubles per attempt and caps at two minutes, so a
/// provider that is simply absent (Codex not installed, signed out) costs one cheap probe every
/// couple of minutes instead of hammering the same failure on the normal refresh cadence.
/// </summary>
internal static class RetrySchedule
{
    /// <summary>Longest a backoff ever grows to, however many attempts have failed.</summary>
    public static readonly TimeSpan Cap = TimeSpan.FromMinutes(2);

    /// <param name="attempt">Zero-based count of consecutive failures already seen.</param>
    public static TimeSpan ForAttempt(int attempt)
    {
        if (attempt < 0) attempt = 0;

        // Clamped before the shift so a long-running failure can't overflow the exponent.
        var seconds = Math.Pow(2, Math.Min(attempt, 7));
        return TimeSpan.FromSeconds(Math.Min(Cap.TotalSeconds, seconds));
    }

    /// <summary>
    /// The backoff, but never longer than the normal refresh interval — once the two converge
    /// there's nothing to be gained by waiting past the next scheduled poll anyway.
    /// </summary>
    public static TimeSpan ForAttempt(int attempt, TimeSpan normalInterval)
    {
        var backoff = ForAttempt(attempt);
        return backoff <= normalInterval ? backoff : normalInterval;
    }
}
