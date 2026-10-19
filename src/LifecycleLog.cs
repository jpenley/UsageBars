using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace UsageBars;

/// <summary>
/// Synchronous, bounded evidence that survives loss of the asynchronous diagnostics queue.
/// An unfinished run means no exit was observed; it does not identify who killed the process.
/// </summary>
internal sealed class LifecycleLog : IDisposable
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly string _run = Guid.NewGuid().ToString("N");
    private readonly Timer _heartbeat;
    private int _pillCount;
    private string? _lastPillActivity;
    private bool _disposed;

    public LifecycleLog(string directory, long maxBytes = 1024 * 1024)
    {
        _path = Path.Combine(directory, "lifecycle.jsonl");
        _maxBytes = maxBytes;
        try
        {
            // Read only our bounded journal and carry forward the previous run's last state.
            if (File.Exists(_path) && new FileInfo(_path).Length <= _maxBytes + 32768)
            {
                var last = File.ReadLines(_path).LastOrDefault();
                if (last is not null)
                {
                    using var previous = JsonDocument.Parse(last);
                    var ev = previous.RootElement.GetProperty("ev").GetString();
                    if (ev is not ("process-exit" or "main-return"))
                    {
                        var root = previous.RootElement;
                        Record("previous-run-incomplete",
                            $"run={root.GetProperty("run")} pid={root.GetProperty("pid")} "
                            + $"lastEvent={ev} at={root.GetProperty("at")} "
                            + $"pills={root.GetProperty("pills")} lastPillActivity={root.GetProperty("lastPillActivity")}");
                    }
                }
            }
        }
        catch (Exception) { /* A corrupt/unreadable journal cannot prevent startup. */ }
        Record("started", $"exe={Environment.ProcessPath}; build={typeof(Program).Module.ModuleVersionId}");
        _heartbeat = new Timer(_ => Record("heartbeat"), null,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public void PillActivity(int count, string activity)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _pillCount = count;
            _lastPillActivity = Redact.Scrub(activity);
            Record("pill-count-changed");
        }
    }

    public void Exception(string source, Exception exception)
    {
        // Redact each stack line separately so long messages cannot hide every stack frame.
        var frames = (exception.StackTrace ?? "").Split('\n').Take(16)
            .Select(line => Redact.Scrub(line));
        Record(source, $"{exception.GetType().FullName}: {Redact.Scrub(exception.Message)}",
            string.Join("\n", frames));
        if (exception.InnerException is { } inner)
            Record(source + "-inner", $"{inner.GetType().FullName}: {Redact.Scrub(inner.Message)}");
    }

    public void Record(string ev, string? detail = null, string? stack = null)
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    at = DateTimeOffset.Now, run = _run, pid = Environment.ProcessId,
                    ev, exitCode = Environment.ExitCode, pills = _pillCount,
                    lastPillActivity = _lastPillActivity,
                    detail = Redact.Scrub(detail), stack,
                }) + "\n");
                if (File.Exists(_path) && new FileInfo(_path).Length + bytes.Length > _maxBytes)
                    File.Move(_path, _path + ".1", true);
                using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write,
                    FileShare.ReadWrite, 4096, FileOptions.WriteThrough);
                stream.Write(bytes);
                stream.Flush(true);
            }
            catch (Exception) { /* Logging must never change termination behavior. */ }
        }
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; }
        _heartbeat.Dispose();
    }
}
