using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace UsageBars;

internal sealed record ClaudeUsageWindow(string Label, double UsedPct, string? Detail)
{
    public TimeSpan? WindowDuration { get; init; }
}

/// <summary>One reading of the Claude Code <c>/usage</c> meter.</summary>
internal sealed record ClaudeCodeUsage
{
    public IReadOnlyList<ClaudeUsageWindow> Windows { get; init; } = Array.Empty<ClaudeUsageWindow>();
    /// <summary>Percent of the meter already consumed (0-100).</summary>
    public double? UsedPct { get; init; }

    public double? RemainingPct => UsedPct is null ? null : 100.0 - UsedPct.Value;

    /// <summary>Meter title, e.g. "Claude Code and Cowork credit".</summary>
    public string? MeterLabel { get; init; }

    /// <summary>Secondary line, e.g. "One-time credit · Expires October 14".</summary>
    public string? MeterDetail { get; init; }

    public bool ClaudeInstalled { get; init; } = true;

    public DateTimeOffset? ReadAt { get; init; }

    public string? Error { get; init; }

    public bool HasValue => UsedPct is not null;

    /// <summary>
    /// A short bar label derived from whatever the meter currently is. Deliberately
    /// label-agnostic: when the credit pool runs out and Claude switches this row to a spend
    /// limit, the bar keeps working and just relabels itself.
    /// </summary>
    public string ShortLabel
    {
        get
        {
            var label = MeterLabel?.ToLowerInvariant() ?? "";
            if (label.Contains("credit")) return "credit";
            if (label.Contains("spend") || label.Contains("limit")) return "spend";
            return "usage";
        }
    }

    public static ClaudeCodeUsage NotInstalled() =>
        new() { ClaudeInstalled = false, Error = "Claude Code CLI not found" };

    public static ClaudeCodeUsage Failed(string error) =>
        new() { Error = error, ReadAt = DateTimeOffset.Now };
}

/// <summary>
/// Reads the usage meter by driving the Claude Code CLI's interactive <c>/usage</c> screen inside
/// a hidden winpty pseudo-console and scraping the rendered panel. This is the fallback source
/// for accounts where plan-usage-history.json never carries utilization values.
/// </summary>
internal static class ClaudeCodeUsageReader
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(75);

    private static readonly Regex UsedPercent =
        new(@"(\d{1,3})%\s+used", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Any of these means the input prompt is up and ready for a command.
    private static readonly string[] ReadyMarkers =
        { "for shortcuts", "auto mode on", "Welcome back", "Tips for getting started" };

    /// <summary>Blocking; expect roughly 15-25 seconds. Call from a background thread.</summary>
    public static ClaudeCodeUsage Read()
    {
        var claude = ClaudeCli.Find();
        if (claude is null) return ClaudeCodeUsage.NotInstalled();

        try
        {
            // A stable, dedicated cwd: the folder-trust prompt is then only answered once,
            // and later probes start straight at the prompt.
            var workDir = AppPaths.ProbeDir;
            Directory.CreateDirectory(workDir);

            // Suppress transcript persistence so repeated probes don't litter session history.
            Environment.SetEnvironmentVariable("CLAUDE_CODE_CHILD_SESSION", "1");

            // --ax-screen-reader flattens the TUI to plain text, which parses cleanly.
            using var session = new WinptySession(
                $"\"{claude}\" --ax-screen-reader", workDir, cols: 120, rows: 50);

            return Drive(session);
        }
        catch (Exception ex)
        {
            return ClaudeCodeUsage.Failed(ex.Message);
        }
    }

    private static ClaudeCodeUsage Drive(WinptySession session)
    {
        var deadline = DateTime.UtcNow + Timeout;
        var trustHandled = false;
        var usageSent = false;

        while (DateTime.UtcNow < deadline)
        {
            var text = session.Snapshot();

            // Clear the folder-trust prompt before anything else.
            if (!trustHandled && text.Contains("trust this folder", StringComparison.OrdinalIgnoreCase))
            {
                session.Send("y\r");
                trustHandled = true;
                Thread.Sleep(500);
                continue;
            }

            if (!usageSent)
            {
                // Only ask once a readiness marker is on screen AND drawing has paused. Waiting
                // on "quiet" alone would fire into an empty buffer before Claude Code starts.
                var ready = false;
                foreach (var marker in ReadyMarkers)
                {
                    if (text.Contains(marker, StringComparison.OrdinalIgnoreCase)) { ready = true; break; }
                }

                if (ready && session.QuietFor(TimeSpan.FromSeconds(1.0)))
                {
                    session.Send("/usage\r");
                    usageSent = true;
                }
                Thread.Sleep(150);
                continue;
            }

            // The panel repaints a few times while it loads; wait for a number AND a settled screen.
            if (UsedPercent.IsMatch(text) && session.QuietFor(TimeSpan.FromSeconds(2)))
                return Parse(session.Snapshot());

            Thread.Sleep(150);
        }

        var final = session.Snapshot();
        return UsedPercent.IsMatch(final)
            ? Parse(final)
            : ClaudeCodeUsage.Failed(
                $"timed out (trust={trustHandled} asked={usageSent} chars={final.Length})");
    }

    private static ClaudeCodeUsage Parse(string screen)
    {
        var lines = screen.Replace("\r", "").Split('\n');

        // winpty retains successive frames. Match readings to their own headings, and
        // replace each window independently when a subsequent frame refreshes it.
        var windows = new Dictionary<string, ClaudeUsageWindow>();
        string? heading = null, firstHeading = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Equals("Current session", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Current week", StringComparison.OrdinalIgnoreCase))
            {
                heading = line;
                firstHeading ??= line;
                continue;
            }
            if (line.Length == 0) continue;
            var match = UsedPercent.Match(line);
            if (match.Success && heading is not null &&
                int.TryParse(match.Groups[1].Value, out var pct) && pct is >= 0 and <= 100)
            {
                // A partial redraw can start at ANY meter. Without its heading we cannot
                // attribute it safely, so retain the last explicitly labelled reading.
                var key = heading;
                heading = null;
                var label = key.Equals("Current session", StringComparison.OrdinalIgnoreCase) ? "5h"
                    : key.Equals("Current week (all models)", StringComparison.OrdinalIgnoreCase) ? "7d"
                    : key.StartsWith("Current week (", StringComparison.OrdinalIgnoreCase) && key.EndsWith(")")
                        ? key.Substring("Current week (".Length, key.Length - "Current week (".Length - 1) : key;
                string? detail = null;
                for (var next = i + 1; next < lines.Length && next <= i + 2; next++)
                {
                    var candidate = lines[next].Trim();
                    if (candidate.Length == 0) continue;
                    if (candidate.StartsWith("Resets", StringComparison.OrdinalIgnoreCase)) detail = candidate;
                    break;
                }
                windows[key] = new ClaudeUsageWindow(label, pct, detail)
                {
                    WindowDuration = key.Equals("Current session", StringComparison.OrdinalIgnoreCase)
                        ? ResetEstimator.FiveHourWindow : ResetEstimator.SevenDayWindow,
                };
                continue;
            }
            // Reset lines belong to the active meter; unrelated panel content ends it.
            if (!line.StartsWith("Resets", StringComparison.OrdinalIgnoreCase)) heading = null;
        }
        if (windows.Count > 0)
        {
            var first = windows[firstHeading!];
            return new ClaudeCodeUsage
            {
                UsedPct = first.UsedPct,
                MeterLabel = firstHeading,
                MeterDetail = first.Detail,
                Windows = new List<ClaudeUsageWindow>(windows.Values),
                ReadAt = DateTimeOffset.Now,
            };
        }

        // Value from the LAST match (earlier frames are mid-refresh); label from the FIRST
        // match, whose preceding line still holds the meter title — later partial redraws
        // repaint only the number.
        double? used = null;
        int firstIdx = -1, lastIdx = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var m = UsedPercent.Match(lines[i]);
            if (!m.Success) continue;
            if (!int.TryParse(m.Groups[1].Value, out var pct) || pct is < 0 or > 100) continue;
            used = pct;
            lastIdx = i;
            if (firstIdx < 0) firstIdx = i;
        }

        if (used is null) return ClaudeCodeUsage.Failed("no '% used' figure found");

        string? Nearby(int from, int direction, int span)
        {
            for (var i = from + direction;
                 i >= 0 && i < lines.Length && Math.Abs(i - from) <= span;
                 i += direction)
            {
                var t = lines[i].Trim();
                if (t.Length > 0 && !UsedPercent.IsMatch(t)) return t;
            }
            return null;
        }

        return new ClaudeCodeUsage
        {
            UsedPct = used,
            MeterLabel = Nearby(firstIdx, -1, 3),
            MeterDetail = Nearby(lastIdx, +1, 2) ?? Nearby(firstIdx, +1, 2),
            ReadAt = DateTimeOffset.Now,
        };
    }
}

// The last good reading and the sample history it feeds now live in UsageHistory
// (see UsageVelocity.cs), which serves both instant display and the burn-rate projection.
