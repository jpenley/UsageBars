using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace UsageBars;

/// <summary>
/// One reading of Claude Desktop's plan-usage-history.json.
/// Percentages are <em>utilization</em> (0-100), matching the on-disk meaning;
/// the overlay converts to "remaining" when it renders.
/// </summary>
internal sealed record UsageSnapshot
{
    public double? FiveHourUsedPct { get; init; }
    public double? SevenDayUsedPct { get; init; }

    /// <summary>Timestamp of the newest sample in the file.</summary>
    public DateTimeOffset? SampleTime { get; init; }

    /// <summary>Logical label of the winning candidate (claude-msix-history / claude-roaming-history).</summary>
    public string? SourceLabel { get; init; }

    public string? SourcePath { get; init; }

    /// <summary>Populated when no candidate could be read or parsed.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// When each rolling window is next due to reset, derived from the file's own sample history.
    /// Null means "not knowable from the retained samples", which is reported as unknown rather
    /// than guessed. See <see cref="ResetEstimator"/>.
    /// </summary>
    public DateTimeOffset? FiveHourResetsAt { get; init; }

    public DateTimeOffset? SevenDayResetsAt { get; init; }

    /// <summary>
    /// The full sample series each window was derived from.
    ///
    /// Retained rather than discarded once the reset deadlines are computed, because it is also
    /// what makes a burn rate measurable for these two windows. The file already carries days of
    /// five-minute samples at no cost to us — a far better basis for a projection than anything
    /// UsageBars could accumulate on its own.
    /// </summary>
    public IReadOnlyList<UsagePoint> FiveHourSeries { get; init; } = Array.Empty<UsagePoint>();

    public IReadOnlyList<UsagePoint> SevenDaySeries { get; init; } = Array.Empty<UsagePoint>();

    /// <summary>Organisation the samples belong to, for diagnostics.</summary>
    public string? Org { get; init; }

    public bool HasValues => FiveHourUsedPct.HasValue || SevenDayUsedPct.HasValue;

    /// <summary>Soonest of the two derived resets.</summary>
    public DateTimeOffset? NextReset
    {
        get
        {
            if (FiveHourResetsAt is null) return SevenDayResetsAt;
            if (SevenDayResetsAt is null) return FiveHourResetsAt;
            return FiveHourResetsAt < SevenDayResetsAt ? FiveHourResetsAt : SevenDayResetsAt;
        }
    }

    /// <summary>
    /// Claude Desktop rewrites the file roughly every 5 minutes while running, so anything
    /// much older than that means the app is closed and the numbers are frozen.
    /// </summary>
    public TimeSpan? Age => SampleTime is null ? null : DateTimeOffset.Now - SampleTime.Value;

    public bool IsStale => Age is { TotalMinutes: > 15 };

    /// <summary>Staleness against a configured threshold.</summary>
    public bool IsStaleAfter(TimeSpan threshold) => Age is { } age && age > threshold;

    public static UsageSnapshot Failed(string error) => new() { Error = error };
}

internal static class UsageSource
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly int[] RetryDelaysMs = { 0, 50, 150 };

    /// <summary>
    /// Candidate files, newest write first. Both installs can coexist; the more recently
    /// active one wins.
    /// </summary>
    /// <summary>Environment variable that pins the reader to one file, for testing and support.</summary>
    public const string OverrideVariable = "USAGEBARS_FILE";

    public static List<(string Path, string Label)> Candidates()
    {
        var found = new List<(string Path, string Label, DateTime Written)>();

        var overridePath = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return new List<(string, string)> { (Path.GetFullPath(overridePath), "override") };

        void Consider(string path, string label)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists) found.Add((info.FullName, label, info.LastWriteTimeUtc));
            }
            catch (Exception)
            {
                // Unreadable candidate: skip it and let the next one win.
            }
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var packages = Path.Combine(localAppData, "Packages");
        if (Directory.Exists(packages))
        {
            IEnumerable<string> dirs;
            try
            {
                // Globbed rather than hardcoded: the publisher hash differs between Store listings.
                dirs = Directory.EnumerateDirectories(packages, "Claude_*");
            }
            catch (Exception)
            {
                dirs = Array.Empty<string>();
            }

            foreach (var dir in dirs)
                Consider(Path.Combine(dir, "LocalCache", "Roaming", "Claude", "plan-usage-history.json"), "claude-msix-history");
        }

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Consider(Path.Combine(roaming, "Claude", "plan-usage-history.json"), "claude-roaming-history");

        return found
            .OrderByDescending(c => c.Written)
            .Select(c => (c.Path, c.Label))
            .ToList();
    }

    /// <summary>Newest LastWriteTimeUtc across candidates; used to skip needless re-parses.</summary>
    public static DateTime? NewestWriteTime()
    {
        DateTime? newest = null;
        foreach (var (path, _) in Candidates())
        {
            try
            {
                var written = File.GetLastWriteTimeUtc(path);
                if (newest is null || written > newest) newest = written;
            }
            catch (Exception)
            {
                // Ignored: a candidate that vanished between enumeration and stat.
            }
        }
        return newest;
    }

    public static UsageSnapshot Read()
    {
        var candidates = Candidates();
        if (candidates.Count == 0)
            return UsageSnapshot.Failed("plan-usage-history.json not found (is Claude Desktop installed?)");

        string? lastError = null;
        foreach (var (path, label) in candidates)
        {
            var snapshot = TryReadCandidate(path, label, out lastError);
            if (snapshot is not null) return snapshot;
        }

        return UsageSnapshot.Failed(lastError ?? "could not read plan-usage-history.json");
    }

    /// <summary>
    /// Reads and parses one candidate. Up to three attempts with 50 ms / 150 ms backoff so a
    /// torn write (Claude Desktop rewriting the file mid-read) doesn't fall through to a
    /// staler candidate unnecessarily.
    /// </summary>
    private static UsageSnapshot? TryReadCandidate(string path, string label, out string? error)
    {
        error = null;

        for (var attempt = 0; attempt < RetryDelaysMs.Length; attempt++)
        {
            if (RetryDelaysMs[attempt] > 0) Thread.Sleep(RetryDelaysMs[attempt]);

            byte[] bytes;
            try
            {
                // Share ReadWrite|Delete so we never block Claude Desktop's own writes.
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 64 * 1024, FileOptions.SequentialScan);

                if (stream.Length > MaxBytes)
                {
                    error = $"{label}: file larger than 1 MB ({stream.Length:N0} bytes)";
                    return null;
                }

                bytes = new byte[stream.Length];
                var read = 0;
                while (read < bytes.Length)
                {
                    var n = stream.Read(bytes, read, bytes.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read != bytes.Length)
                {
                    error = $"{label}: short read";
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = $"{label}: {ex.Message}";
                continue;
            }

            try
            {
                return Parse(bytes, label, path);
            }
            catch (JsonException ex)
            {
                // Most likely a torn write; retry before giving up on this candidate.
                error = $"{label}: {ex.Message}";
            }
            catch (Exception ex)
            {
                error = $"{label}: {ex.Message}";
                return null;
            }
        }

        return null;
    }

    private static UsageSnapshot Parse(byte[] bytes, string label, string path)
    {
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("root is not an object");

        if (!root.TryGetProperty("version", out var versionElement) ||
            !versionElement.TryGetInt32(out var version) ||
            version is not (1 or 2))
        {
            throw new JsonException("unsupported schema version");
        }

        if (!root.TryGetProperty("samples", out var samples) || samples.ValueKind != JsonValueKind.Array)
            throw new JsonException("missing samples array");

        long newestT = long.MinValue;
        double? fiveHour = null, sevenDay = null;
        string? org = null;

        // The whole series is collected, not just the newest sample: it is what makes the reset
        // deadlines derivable, and it is already on disk at no extra cost.
        var fiveHourSeries = new List<UsagePoint>();
        var sevenDaySeries = new List<UsagePoint>();

        foreach (var sample in samples.EnumerateArray())
        {
            if (sample.ValueKind != JsonValueKind.Object) continue;
            if (!sample.TryGetProperty("t", out var tElement) || !tElement.TryGetInt64(out var t)) continue;

            // v2 nests the percentages under "u"; v1 puts them on the sample itself.
            var holder = sample;
            if (version == 2)
            {
                if (!sample.TryGetProperty("u", out var u) || u.ValueKind != JsonValueKind.Object) continue;
                holder = u;
            }

            var fh = Percentage(holder, "fh");
            var sd = Percentage(holder, "sd");

            DateTimeOffset at;
            try { at = DateTimeOffset.FromUnixTimeMilliseconds(t).ToLocalTime(); }
            catch (ArgumentOutOfRangeException) { continue; }

            if (fh is { } fhValue) fiveHourSeries.Add(new UsagePoint(at, fhValue));
            if (sd is { } sdValue) sevenDaySeries.Add(new UsagePoint(at, sdValue));

            if (t <= newestT) continue;

            newestT = t;
            fiveHour = fh;
            sevenDay = sd;
            org = sample.TryGetProperty("org", out var orgElement)
                  && orgElement.ValueKind == JsonValueKind.String
                ? orgElement.GetString()
                : org;
        }

        if (newestT == long.MinValue)
            return new UsageSnapshot { SourceLabel = label, SourcePath = path, Error = "no usable samples" };

        // Samples are written in order in practice, but deriving a reset from an out-of-order
        // series would silently produce nonsense, so sorting is cheap insurance.
        fiveHourSeries.Sort((a, b) => a.At.CompareTo(b.At));
        sevenDaySeries.Sort((a, b) => a.At.CompareTo(b.At));

        var now = DateTimeOffset.Now;

        return new UsageSnapshot
        {
            FiveHourUsedPct = fiveHour,
            SevenDayUsedPct = sevenDay,
            SampleTime = DateTimeOffset.FromUnixTimeMilliseconds(newestT).ToLocalTime(),
            SourceLabel = label,
            SourcePath = path,
            Org = org,
            FiveHourResetsAt = ResetEstimator.NextReset(
                fiveHourSeries, ResetEstimator.FiveHourWindow, now),
            SevenDayResetsAt = ResetEstimator.NextReset(
                sevenDaySeries, ResetEstimator.SevenDayWindow, now),
            FiveHourSeries = fiveHourSeries,
            SevenDaySeries = sevenDaySeries,
        };
    }

    /// <summary>Reads a 0-100 percentage, rejecting non-finite and out-of-range values.</summary>
    private static double? Percentage(JsonElement holder, string name)
    {
        if (!holder.TryGetProperty(name, out var element)) return null;
        if (element.ValueKind != JsonValueKind.Number) return null;
        if (!element.TryGetDouble(out var value)) return null;
        if (!double.IsFinite(value) || value < 0 || value > 100) return null;
        return value;
    }
}
