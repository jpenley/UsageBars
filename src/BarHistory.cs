using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UsageBars;

/// <summary>
/// Rolling utilization readings for bars that have no sample history of their own, keyed by bar.
///
/// Claude's history file arrives with days of five-minute samples already in it, so its 5h and 7d
/// windows can be projected the moment the app starts. Codex has the opposite shape: the
/// app-server reports a precise current number and a real <c>resetsAt</c>, but nothing at all about
/// the past, so a burn rate for those windows has to be accumulated by watching them. That is all
/// this is — the missing series, persisted so it survives a restart.
///
/// Keyed by an opaque string rather than modelled per provider: what a window is called is the
/// app-server's business (5h, 7d, weekly, spend), and new ones must not need a code change here.
/// </summary>
internal sealed class BarHistory
{
    /// <summary>Roughly a week at the spacing below, which is as far as any window reaches.</summary>
    private const int MaxPointsPerSeries = 2500;

    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    /// <summary>
    /// Minimum gap between retained points. The app-server pushes an update on every request that
    /// moves the meter, which is far denser than a burn rate needs; recording all of it would fill
    /// the series with a single busy hour.
    /// </summary>
    private static readonly TimeSpan MinSpacing = TimeSpan.FromMinutes(5);

    /// <summary>A fall of at least this many points is a reset, and is always kept — see below.</summary>
    private const double ResetDropPoints = 5;

    [JsonPropertyName("series")]
    public Dictionary<string, List<UsageSample>> Series { get; set; } = new();

    private static string FilePath => AppPaths.BarHistoryFile;

    public static BarHistory Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new BarHistory();

            var loaded = JsonSerializer.Deserialize<BarHistory>(File.ReadAllText(FilePath));
            if (loaded is null) return new BarHistory();

            loaded.Series ??= new Dictionary<string, List<UsageSample>>();
            foreach (var points in loaded.Series.Values)
                points.Sort((a, b) => a.At.CompareTo(b.At));
            return loaded;
        }
        catch (Exception)
        {
            // Corrupt history costs accuracy until it refills, which is not worth failing over.
            return new BarHistory();
        }
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception)
        {
            // Persisting is a nicety; failing only costs the rate after a restart.
        }
    }

    /// <summary>
    /// Adds a reading for one bar, then prunes. Returns true when the point was kept, so the
    /// caller can save only when something actually changed on disk.
    ///
    /// A drop in utilization is always kept regardless of spacing: it is the reset signal every
    /// consumer keys off, and thinning it away would make the projection carry the previous
    /// window's consumption into the new one.
    /// </summary>
    public bool Record(string key, double usedPct, DateTimeOffset at)
    {
        if (string.IsNullOrEmpty(key) || !double.IsFinite(usedPct)) return false;

        if (!Series.TryGetValue(key, out var points))
        {
            points = new List<UsageSample>();
            Series[key] = points;
        }

        if (points.Count > 0)
        {
            var last = points[^1];
            if (at < last.At) return false;

            var isReset = usedPct <= last.UsedPct - ResetDropPoints;
            if (!isReset && at - last.At < MinSpacing) return false;
        }

        points.Add(new UsageSample { At = at, UsedPct = usedPct });

        var cutoff = DateTimeOffset.Now - MaxAge;
        points.RemoveAll(p => p.At < cutoff);
        if (points.Count > MaxPointsPerSeries)
            points.RemoveRange(0, points.Count - MaxPointsPerSeries);

        return true;
    }

    /// <summary>The recorded series for one bar, as the shared projection wants it.</summary>
    public IReadOnlyList<UsagePoint> Points(string key) =>
        Series.TryGetValue(key, out var points)
            ? points.Select(p => new UsagePoint(p.At, p.UsedPct)).ToList()
            : Array.Empty<UsagePoint>();

    /// <summary>Total retained points across every bar, for the diagnostics dump.</summary>
    [JsonIgnore]
    public int TotalPoints => Series.Values.Sum(p => p.Count);
}
