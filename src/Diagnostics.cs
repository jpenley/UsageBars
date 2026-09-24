using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace UsageBars;

internal enum DiagLevel
{
    Debug,
    Info,
    Warn,
}

/// <summary>One line in the diagnostics log.</summary>
internal sealed record DiagEvent(
    DateTimeOffset At,
    string Provider,
    string Component,
    string Event,
    DiagLevel Level,
    string Outcome,
    string? Detail = null,
    double? ElapsedMs = null);

/// <summary>
/// Rotating on-disk log of what each source is doing.
///
/// The pill only ever shows the current state, so an intermittent failure — a scrape that timed
/// out twenty minutes ago, a Codex app-server that died and reconnected — leaves no trace by the
/// time anyone looks. This keeps a bounded trail of those transitions.
///
/// Three rules it never breaks: it never throws into the code path it observes, it never grows
/// without bound, and it never writes a value that could be a credential.
/// </summary>
internal sealed class DiagnosticLog : IDisposable
{
    private const string CurrentName = "usagebars.jsonl";

    private readonly Channel<DiagEvent> _queue;
    private readonly Task _writer;
    private readonly long _maxBytes;
    private readonly int _generations;

    /// <summary>
    /// Last state seen per (provider, component), so a source that fails the same way every 15
    /// minutes writes one line rather than one per attempt.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _lastState = new();

    private readonly ConcurrentDictionary<string, DiagEvent> _latestByProvider = new();

    private int _disposed;

    public string Directory { get; }

    public DiagnosticLog(string directory, long maxBytes = 1024 * 1024, int generations = 3)
    {
        Directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _maxBytes = maxBytes;
        _generations = generations;

        // Bounded and drop-oldest: a runaway producer costs old diagnostics, never memory.
        _queue = Channel.CreateBounded<DiagEvent>(new BoundedChannelOptions(512)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        _writer = Task.Run(WriteLoopAsync);
    }

    /// <summary>The default log, under the app's data root.</summary>
    public static DiagnosticLog Default { get; } = new(AppPaths.DiagnosticsDir);

    public void Record(DiagEvent e)
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0) return;

            var sanitized = e with
            {
                Detail = Redact.Scrub(e.Detail),
            };

            _latestByProvider[sanitized.Provider] = sanitized;

            // Only transitions are interesting. Elapsed/timestamp are deliberately excluded from
            // the key, otherwise every event would look new and nothing would ever coalesce.
            var scope = sanitized.Provider + "\u001f" + sanitized.Component;
            var state = string.Join(
                "\u001f", sanitized.Event, sanitized.Outcome, sanitized.Detail ?? "");
            if (_lastState.TryGetValue(scope, out var previous) && previous == state) return;
            _lastState[scope] = state;

            _queue.Writer.TryWrite(sanitized);
        }
        catch (Exception)
        {
            // Diagnostics must never disturb the path they observe.
        }
    }

    public void Record(
        string provider,
        string component,
        string @event,
        DiagLevel level,
        string outcome,
        string? detail = null,
        double? elapsedMs = null) =>
        Record(new DiagEvent(
            DateTimeOffset.Now, provider, component, @event, level, outcome, detail, elapsedMs));

    /// <summary>Most recent event for a provider, for the tray tooltip and the clipboard dump.</summary>
    public DiagEvent? Latest(string provider) =>
        _latestByProvider.TryGetValue(provider, out var e) ? e : null;

    /// <summary>Tail of the current log file, newest last. Used by the clipboard dump.</summary>
    public IReadOnlyList<string> Tail(int lines)
    {
        try
        {
            var path = Path.Combine(Directory, CurrentName);
            if (!File.Exists(path)) return Array.Empty<string>();

            // Shared read: the writer task may be appending as we read.
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var ring = new Queue<string>(lines);
            while (reader.ReadLine() is { } line)
            {
                if (ring.Count == lines) ring.Dequeue();
                ring.Enqueue(line);
            }
            return ring.ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private async Task WriteLoopAsync()
    {
        await foreach (var e in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var path = Path.Combine(Directory, CurrentName);
                var line = JsonSerializer.Serialize(new
                {
                    at = e.At.ToString("o", CultureInfo.InvariantCulture),
                    provider = e.Provider,
                    component = e.Component,
                    ev = e.Event,
                    level = e.Level.ToString().ToLowerInvariant(),
                    outcome = e.Outcome,
                    detail = e.Detail,
                    elapsedMs = e.ElapsedMs,
                });

                var length = File.Exists(path) ? new FileInfo(path).Length : 0;
                if (length > 0 && length + line.Length + 1 > _maxBytes) Rotate();

                await using var stream = new FileStream(
                    path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite,
                    bufferSize: 4096, useAsync: true);
                await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n")).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A dropped diagnostic line is always preferable to a crash.
            }
        }
    }

    /// <summary>usagebars.jsonl -> usagebars.1.jsonl -> ... dropping the oldest generation.</summary>
    private void Rotate()
    {
        var current = Path.Combine(Directory, CurrentName);
        if (_generations <= 0)
        {
            File.Delete(current);
            return;
        }

        File.Delete(Generation(_generations));
        for (var g = _generations - 1; g >= 1; g--)
        {
            var source = Generation(g);
            if (File.Exists(source)) File.Move(source, Generation(g + 1));
        }
        if (File.Exists(current)) File.Move(current, Generation(1));
    }

    private string Generation(int index) => Path.Combine(Directory, $"usagebars.{index}.jsonl");

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        try { _writer.Wait(TimeSpan.FromSeconds(2)); }
        catch (Exception) { /* best effort on shutdown */ }
    }
}

/// <summary>
/// Keeps user-identifying and secret-shaped values out of anything written to disk or copied to
/// the clipboard. UsageBars handles one real secret — the Claude Desktop session cookie it reads
/// to call the usage API — and the diagnostics dump is meant to be pasted into a support thread,
/// so both matter.
/// </summary>
internal static class Redact
{
    private const int MaxLength = 512;
    private const string Mark = "<redacted>";

    private static readonly RegexOptions Options =
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    private static readonly Regex KeyValue = new(
        @"\b(sessionKey|session_key|token|access[_-]?token|refresh[_-]?token|api[_-]?key|cookie|authorization|password|secret)\b\s*[:=]\s*(""[^""]*""|'[^']*'|[^\s,;&]+)",
        Options);

    private static readonly Regex Bearer = new(@"\bBearer\s+[^\s,;""']+", Options);

    private static readonly Regex ApiKey = new(@"(?<![\w-])sk-(?:ant-)?[A-Za-z0-9._-]{8,}", Options);

    private static readonly Regex Jwt = new(
        @"\b[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b", Options);

    private static readonly Regex Email = new(
        @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", Options);

    private static readonly Lazy<string?> UserProfile = new(() =>
    {
        try
        {
            var path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (Exception)
        {
            return null;
        }
    });

    /// <summary>
    /// Scrubs a value for logging. Returns null for null so "field absent" stays distinguishable
    /// from "field redacted".
    /// </summary>
    public static string? Scrub(string? value)
    {
        if (value is null) return null;

        var text = Paths(value);

        // Order matters. The key/value rule stops at the first whitespace, so on
        // "Authorization: Bearer <token>" it would consume only the word "Bearer" and leave the
        // token exposed. Anything that recognises a secret by its own shape therefore runs first,
        // and the key/value sweep is left to catch what's still named but unrecognised.
        text = Bearer.Replace(text, "Bearer " + Mark);
        text = ApiKey.Replace(text, Mark);
        text = Jwt.Replace(text, Mark);
        text = KeyValue.Replace(text, m => m.Groups[1].Value + "=" + Mark);
        text = Email.Replace(text, Mark);
        text = text.Replace('\r', ' ').Replace('\n', ' ');

        return text.Length <= MaxLength ? text : text[..MaxLength];
    }

    /// <summary>
    /// Replaces the user's profile directory with %USERPROFILE%. Applied on its own to the
    /// clipboard dump, which is mostly paths and wants to stay readable.
    /// </summary>
    public static string Paths(string value)
    {
        var profile = UserProfile.Value;
        return string.IsNullOrEmpty(profile)
            ? value
            : value.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
    }
}
