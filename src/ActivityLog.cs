using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UsageBars;

/// <summary>
/// The first and last moment Claude Code was active on one working day.
/// <paramref name="Key"/> is the date of the day's start, so a session running past midnight still
/// belongs to the day it began in.
/// </summary>
internal readonly record struct ActiveDay(DateTime Key, DateTimeOffset Start, DateTimeOffset End)
{
    public TimeSpan Span => End - Start;
}

/// <summary>
/// When the user was actually working with Claude Code, read from the session transcripts under
/// <c>~/.claude/projects</c>. A working day starts at <c>dayStartHour</c> local (6am by default);
/// its active span runs from the earliest transcript message on that day to the latest.
///
/// Overnight and weekend gaps therefore never appear in a span, which is what lets a spend rate
/// mean "dollars per hour of use" rather than "dollars per hour of wall clock".
///
/// Transcripts total hundreds of megabytes, so each file is read once and then only from where the
/// previous read stopped. What was learned per file is cached on disk so a restart does not read
/// it all again.
/// </summary>
internal sealed class ActivityLog
{
    /// <summary>Anything older than this can no longer affect a monthly billing cycle.</summary>
    private static readonly TimeSpan Horizon = TimeSpan.FromDays(40);

    private readonly string _root;
    private readonly string? _cachePath;
    private readonly int _dayStartHour;
    private readonly object _gate = new();
    private Dictionary<string, FileState> _files = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ActiveDay> _days = Array.Empty<ActiveDay>();

    public ActivityLog(string root, int dayStartHour, string? cachePath = null)
    {
        _root = root;
        _dayStartHour = Math.Clamp(dayStartHour, 0, 23);
        _cachePath = cachePath;
        LoadCache();
        _days = Merge();
    }

    /// <summary>Where Claude Code keeps its transcripts, honouring <c>CLAUDE_CONFIG_DIR</c>.</summary>
    public static string DefaultRoot()
    {
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var home = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : configured;
        return Path.Combine(home, "projects");
    }

    /// <summary>Per-day spans, oldest first. A snapshot: safe to hold while a refresh runs.</summary>
    public IReadOnlyList<ActiveDay> Days => _days;

    /// <summary>
    /// The working day a moment belongs to: the local date, counting hours before the day start
    /// as the previous day.
    /// </summary>
    public static DateTime DayKey(DateTimeOffset moment, int dayStartHour) =>
        moment.ToLocalTime().DateTime.AddHours(-Math.Clamp(dayStartHour, 0, 23)).Date;

    /// <summary>
    /// Reads whatever the transcripts gained since the last call. Blocking; call it off the UI
    /// thread. Returns true when the per-day spans changed.
    /// </summary>
    public bool Refresh(DateTimeOffset now)
    {
        lock (_gate)
        {
            var changed = false;
            var cutoff = now - Horizon;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            IEnumerable<string> paths;
            try
            {
                paths = Directory.Exists(_root)
                    ? Directory.EnumerateFiles(_root, "*.jsonl", SearchOption.AllDirectories).ToList()
                    : Array.Empty<string>();
            }
            catch (Exception)
            {
                return false;
            }

            foreach (var path in paths)
            {
                FileInfo info;
                try { info = new FileInfo(path); }
                catch (Exception) { continue; }
                if (!info.Exists || info.LastWriteTimeUtc < cutoff.UtcDateTime) continue;

                seen.Add(path);
                if (!_files.TryGetValue(path, out var state))
                    _files[path] = state = new FileState();
                if (info.Length < state.Offset) // rewritten or truncated: start over
                {
                    state.Offset = 0;
                    state.Days.Clear();
                    changed = true;
                }
                if (info.Length == state.Offset) continue;

                changed |= ReadAppended(path, state);
            }

            foreach (var stale in _files.Keys.Where(p => !seen.Contains(p)).ToList())
            {
                _files.Remove(stale);
                changed = true;
            }

            if (changed)
            {
                _days = Merge();
                SaveCache();
            }
            return changed;
        }
    }

    private bool ReadAppended(string path, FileState state)
    {
        byte[] buffer;
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length <= state.Offset) return false;
            stream.Seek(state.Offset, SeekOrigin.Begin);
            buffer = new byte[stream.Length - state.Offset];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = stream.Read(buffer, read, buffer.Length - read);
                if (n <= 0) break;
                read += n;
            }
            if (read < buffer.Length) Array.Resize(ref buffer, read);
        }
        catch (Exception)
        {
            return false; // locked or vanished; try again next time
        }

        // Only whole lines: the last one may still be being written.
        var lastNewline = Array.LastIndexOf(buffer, (byte)'\n');
        if (lastNewline < 0) return false;

        var changed = false;
        var text = System.Text.Encoding.UTF8.GetString(buffer, 0, lastNewline + 1);
        foreach (var line in text.Split('\n'))
        {
            if (!TryTimestamp(line, out var at)) continue;
            var key = DayKey(at, _dayStartHour).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (state.Days.TryGetValue(key, out var span))
            {
                var first = Math.Min(span[0], at.UtcTicks);
                var last = Math.Max(span[1], at.UtcTicks);
                if (first != span[0] || last != span[1])
                {
                    state.Days[key] = new[] { first, last };
                    changed = true;
                }
            }
            else
            {
                state.Days[key] = new[] { at.UtcTicks, at.UtcTicks };
                changed = true;
            }
        }
        state.Offset += lastNewline + 1;
        return changed;
    }

    /// <summary>
    /// The line's own top-level <c>timestamp</c>. Parsed rather than searched for, because tool
    /// results nest objects that carry timestamps of their own.
    /// </summary>
    internal static bool TryTimestamp(string line, out DateTimeOffset at)
    {
        at = default;
        if (line.IndexOf("\"timestamp\":\"", StringComparison.Ordinal) < 0) return false;
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("timestamp", out var value) ||
                value.ValueKind != JsonValueKind.String)
                return false;
            return DateTimeOffset.TryParse(
                value.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out at);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private IReadOnlyList<ActiveDay> Merge()
    {
        var merged = new Dictionary<string, long[]>();
        foreach (var file in _files.Values)
        foreach (var (key, span) in file.Days)
        {
            if (merged.TryGetValue(key, out var existing))
            {
                existing[0] = Math.Min(existing[0], span[0]);
                existing[1] = Math.Max(existing[1], span[1]);
            }
            else
            {
                merged[key] = new[] { span[0], span[1] };
            }
        }

        return merged
            .Select(pair => new ActiveDay(
                DateTime.ParseExact(pair.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                new DateTimeOffset(pair.Value[0], TimeSpan.Zero).ToLocalTime(),
                new DateTimeOffset(pair.Value[1], TimeSpan.Zero).ToLocalTime()))
            .OrderBy(day => day.Key)
            .ToList();
    }

    // ---- Persistence ---------------------------------------------------------------

    private sealed class FileState
    {
        [JsonPropertyName("off")] public long Offset { get; set; }

        /// <summary>Working-day key -> [first, last] UTC ticks.</summary>
        [JsonPropertyName("days")] public Dictionary<string, long[]> Days { get; set; } = new();
    }

    private sealed class CacheFile
    {
        [JsonPropertyName("dayStartHour")] public int DayStartHour { get; set; }
        [JsonPropertyName("files")] public Dictionary<string, FileState> Files { get; set; } = new();
    }

    private void LoadCache()
    {
        if (_cachePath is null) return;
        try
        {
            if (!File.Exists(_cachePath)) return;
            var cache = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(_cachePath));
            // Day keys depend on the day start; a different hour means the cache means nothing.
            if (cache is null || cache.DayStartHour != _dayStartHour) return;
            _files = new Dictionary<string, FileState>(cache.Files, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // A bad cache just costs one full re-read.
        }
    }

    private void SaveCache()
    {
        if (_cachePath is null) return;
        try
        {
            var dir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(
                new CacheFile { DayStartHour = _dayStartHour, Files = _files }));
        }
        catch (Exception)
        {
            // Not persisting only costs a full re-read after a restart.
        }
    }
}
