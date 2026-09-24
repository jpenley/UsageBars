using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UsageBars;

/// <summary>
/// A JSON-RPC session with <c>codex app-server</c>, spoken over the child process's stdin/stdout
/// as one JSON object per line.
///
/// This is the whole reason the Codex side needs no scraping: the app-server reuses the sign-in
/// Codex desktop already has, answers <c>account/rateLimits/read</c> with real numbers including
/// reset timestamps, and pushes <c>account/rateLimits/updated</c> whenever a request moves them.
/// </summary>
internal sealed class CodexAppServer : IDisposable
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Cap on a single line, so a wedged child can't be read into unbounded memory.</summary>
    private const int MaxLineLength = 1024 * 1024;

    private readonly Process _process;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<string>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Queue<string> _stderr = new();
    private readonly object _stderrLock = new();
    private readonly Task _readerTask;
    private readonly Task _stderrTask;

    private long _nextId;
    private int _disposed;
    private Exception? _readerEnded;

    /// <summary>Raised on the reader thread when the server pushes a rate-limit update.</summary>
    public event Action<string>? RateLimitsPushed;

    public string Executable { get; }

    private CodexAppServer(Process process, string executable)
    {
        _process = process;
        Executable = executable;
        _readerTask = Task.Run(ReadLoopAsync);
        _stderrTask = Task.Run(ReadStandardErrorAsync);
    }

    /// <summary>
    /// Spawns the app-server and completes the handshake. Throws if the executable can't start or
    /// doesn't answer <c>initialize</c>, which is how the caller discards a bad candidate.
    /// </summary>
    public static async Task<CodexAppServer> ConnectAsync(
        string executable, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var info = CodexCli.StartInfoFor(executable, arguments);

        Process? process = null;
        CodexAppServer? server = null;
        try
        {
            process = Process.Start(info)
                ?? throw new InvalidOperationException($"could not start {executable}");
            process.StandardInput.AutoFlush = true;

            server = new CodexAppServer(process, executable);
            await server.InitializeAsync(ct).ConfigureAwait(false);
            return server;
        }
        catch
        {
            if (server is not null) server.Dispose();
            else process?.Dispose();
            throw;
        }
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        await RequestAsync("initialize", new
        {
            clientInfo = new { name = "usage_bars", title = "UsageBars", version = "1.0.0" },
        }, ct).ConfigureAwait(false);

        await NotifyAsync("initialized", ct).ConfigureAwait(false);
    }

    /// <summary>Reads the current rate limits. The returned snapshot is stamped with local now.</summary>
    public async Task<CodexSnapshot> ReadRateLimitsAsync(CancellationToken ct)
    {
        var line = await RequestAsync("account/rateLimits/read", null, ct).ConfigureAwait(false);
        return CodexRateLimitParser.ParseReadResponse(line, DateTimeOffset.Now);
    }

    /// <summary>Last few stderr lines from the child, for diagnosing a candidate that won't start.</summary>
    public IReadOnlyList<string> StandardErrorTail()
    {
        lock (_stderrLock) return _stderr.ToArray();
    }

    // ---- protocol -----------------------------------------------------------

    private async Task<string> RequestAsync(string method, object? parameters, CancellationToken ct)
    {
        ThrowIfReaderEnded();

        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        // Ties the caller's cancellation to this one request rather than the whole session, so a
        // timed-out read doesn't tear down a connection that is otherwise healthy.
        await using var registration = ct.Register(() =>
        {
            if (_pending.TryRemove(id, out var pending)) pending.TrySetCanceled(ct);
        }).ConfigureAwait(false);

        try
        {
            await SendAsync(method, id, parameters, ct).ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }

        return await completion.Task.ConfigureAwait(false);
    }

    private Task NotifyAsync(string method, CancellationToken ct) =>
        SendAsync(method, id: null, parameters: null, ct);

    private async Task SendAsync(string method, long? id, object? parameters, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);

        var message = JsonSerializer.Serialize(
            new Envelope("2.0", method, id, parameters), WriteOptions);

        await _writeLock.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteLineAsync(message.AsMemory(), linked.Token)
                .ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        Exception? ended = null;
        try
        {
            while (true)
            {
                var line = await _process.StandardOutput.ReadLineAsync(_lifetime.Token)
                    .ConfigureAwait(false);
                if (line is null) break;
                if (line.Length is 0 or > MaxLineLength) continue;

                try { Dispatch(line); }
                catch (Exception) { /* a malformed line must not kill the session */ }
            }

            ended = new IOException("the Codex app-server closed its output stream");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            ended = new ObjectDisposedException(nameof(CodexAppServer));
        }
        catch (Exception ex)
        {
            ended = ex;
        }
        finally
        {
            Interlocked.CompareExchange(ref _readerEnded, ended, null);

            // Nothing further will ever arrive, so waiting callers are failed rather than hung.
            foreach (var key in _pending.Keys)
            {
                if (_pending.TryRemove(key, out var pending))
                    pending.TrySetException(_readerEnded!);
            }
        }
    }

    private void Dispatch(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;

        // Pushed update: no id, just a method. Handed to the monitor as raw JSON so all the
        // parsing rules live in one place.
        if (root.TryGetProperty("method", out var method)
            && method.ValueKind == JsonValueKind.String
            && method.GetString() == CodexRateLimitParser.UpdatedMethod)
        {
            RateLimitsPushed?.Invoke(line);
            return;
        }

        if (!root.TryGetProperty("id", out var idElement)
            || idElement.ValueKind != JsonValueKind.Number
            || !idElement.TryGetInt64(out var id))
        {
            return;
        }

        if (!_pending.TryRemove(id, out var completion)) return;

        if (root.TryGetProperty("error", out var error))
        {
            completion.TrySetException(new InvalidOperationException(DescribeError(error)));
            return;
        }

        completion.TrySetResult(line);
    }

    private static string DescribeError(JsonElement error)
    {
        if (error.ValueKind != JsonValueKind.Object) return "malformed JSON-RPC error";

        var code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number
            ? c.GetRawText()
            : "?";
        var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()
            : null;

        return $"Codex app-server error {code}: {message ?? "(no message)"}";
    }

    private async Task ReadStandardErrorAsync()
    {
        try
        {
            while (true)
            {
                var line = await _process.StandardError.ReadLineAsync(_lifetime.Token)
                    .ConfigureAwait(false);
                if (line is null) break;

                lock (_stderrLock)
                {
                    _stderr.Enqueue(line.Length > 512 ? line[..512] : line);
                    while (_stderr.Count > 20) _stderr.Dequeue();
                }
            }
        }
        catch (Exception)
        {
            // stderr is best-effort colour for diagnostics; losing it is not a failure.
        }
    }

    private void ThrowIfReaderEnded()
    {
        if (Volatile.Read(ref _readerEnded) is { } ended)
            throw new InvalidOperationException("the Codex app-server session has ended", ended);
    }

    /// <summary>True once the child has exited or its streams have closed.</summary>
    public bool IsFaulted => Volatile.Read(ref _readerEnded) is not null;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _lifetime.Cancel();

        try { _process.StandardInput.Close(); }
        catch (Exception) { /* already gone */ }

        try
        {
            if (!_process.HasExited)
            {
                // Give it a moment to leave on its own after stdin closes, then insist.
                if (!_process.WaitForExit(1000)) _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Racing a process that exited between the check and the kill is fine.
        }

        try { Task.WaitAll(new[] { _readerTask, _stderrTask }, TimeSpan.FromSeconds(2)); }
        catch (Exception) { }

        _process.Dispose();
        _writeLock.Dispose();
        _lifetime.Dispose();
    }

    private sealed record Envelope(string jsonrpc, string method, long? id, object? @params);
}
