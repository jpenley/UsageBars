using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UsageBars;

/// <summary>
/// Keeps a live Codex app-server session and the latest rate limits from it.
///
/// Runs its own async loop rather than hanging off the UI timer, because the connection is
/// long-lived and the server pushes updates between polls. The loop owns the whole lifecycle:
/// pick a candidate executable, hold the session while it works, and back off when it doesn't.
/// </summary>
internal sealed class CodexMonitor : IDisposable
{
    private readonly Config _config;
    private readonly DiagnosticLog _log;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _refreshSignal = new(0);
    private readonly object _stateLock = new();

    private Task? _loop;
    private CodexSnapshot? _latest;
    private string? _error;
    private string? _executable;
    private bool _installed = true;
    private int _consecutiveFailures;

    /// <summary>Raised on a background thread whenever the visible state changes.</summary>
    public event Action? Changed;

    public CodexMonitor(Config config, DiagnosticLog log)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public CodexSnapshot? Latest { get { lock (_stateLock) return _latest; } }

    public string? Error { get { lock (_stateLock) return _error; } }

    /// <summary>False when no Codex executable could be found at all, so the UI can offer a link.</summary>
    public bool CodexInstalled { get { lock (_stateLock) return _installed; } }

    /// <summary>Which candidate is actually in use, for diagnostics.</summary>
    public string? SelectedExecutable { get { lock (_stateLock) return _executable; } }

    public int ConsecutiveFailures { get { lock (_stateLock) return _consecutiveFailures; } }

    public void Start()
    {
        if (_loop is not null) return;
        _loop = Task.Run(() => RunAsync(_lifetime.Token));
    }

    /// <summary>Breaks the current wait so the next poll happens now.</summary>
    public void RequestRefresh()
    {
        try
        {
            // Only ever one pending signal: a burst of clicks shouldn't queue a burst of reads.
            if (_refreshSignal.CurrentCount == 0) _refreshSignal.Release();
        }
        catch (Exception)
        {
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            CodexAppServer? server = null;
            var connectStarted = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                server = await ConnectAsync(ct).ConfigureAwait(false);

                server.RateLimitsPushed += OnPushed;
                lock (_stateLock)
                {
                    _installed = true;
                    _executable = server.Executable;
                    _consecutiveFailures = 0;
                    _error = null;
                }

                // Worth timing: discovery may spawn a PowerShell Appx query and try several
                // candidates, so a slow connect has a cause worth seeing.
                _log.Record("codex", "app-server", "connected", DiagLevel.Info, "succeeded",
                    Redact.Paths(server.Executable), connectStarted.Elapsed.TotalMilliseconds);

                await PollAsync(server, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var attempt = RecordFailure(ex);
                var delay = RetrySchedule.ForAttempt(attempt, PollInterval);

                _log.Record("codex", "app-server", "failed", DiagLevel.Warn, "failed",
                    $"{ex.GetType().Name}: {ex.Message}; retry in {delay.TotalSeconds:0}s");

                Changed?.Invoke();

                try { await Task.Delay(delay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            finally
            {
                if (server is not null)
                {
                    server.RateLimitsPushed -= OnPushed;
                    server.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Tries every discovered executable, and both argument spellings for each, until one
    /// completes a handshake. A file that exists proves nothing — the ChatGPT bundle and an old
    /// npm shim can both be present while only one speaks the protocol.
    /// </summary>
    private async Task<CodexAppServer> ConnectAsync(CancellationToken ct)
    {
        var candidates = await Task.Run(() => CodexCli.Candidates(), ct).ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            lock (_stateLock)
            {
                _installed = false;
                _error = "Codex CLI not found";
            }
            throw new System.IO.FileNotFoundException("no Codex executable was discovered");
        }

        var failures = new List<string>();

        foreach (var candidate in candidates)
        {
            foreach (var arguments in CodexCli.ArgumentSets)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    // The handshake has to be bounded: a wedged child would otherwise hold the
                    // loop open forever with no output and no exit.
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));

                    return await CodexAppServer
                        .ConnectAsync(candidate, arguments, timeout.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failures.Add(
                        $"{System.IO.Path.GetFileName(candidate)} {string.Join(' ', arguments)}: {ex.Message}");
                }
            }
        }

        throw new InvalidOperationException(
            "no Codex executable completed the app-server handshake — " + string.Join(" | ", failures));
    }

    private async Task PollAsync(CodexAppServer server, CancellationToken ct)
    {
        // Read immediately on connect so the pill has a number without waiting a full interval.
        Publish(await server.ReadRateLimitsAsync(ct).ConfigureAwait(false));

        while (!ct.IsCancellationRequested)
        {
            await _refreshSignal.WaitAsync(PollInterval, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            // Surfacing this as an exception sends the outer loop into reconnect-with-backoff,
            // which is the correct response to a server that has gone away.
            if (server.IsFaulted)
                throw new InvalidOperationException("the Codex app-server session ended");

            Publish(await server.ReadRateLimitsAsync(ct).ConfigureAwait(false));
        }
    }

    private TimeSpan PollInterval =>
        TimeSpan.FromMinutes(Math.Max(0.25, _config.CodexRefreshMinutes));

    private void OnPushed(string json)
    {
        try
        {
            CodexSnapshot merged;
            lock (_stateLock)
            {
                merged = CodexRateLimitParser.MergeNotification(_latest, json, DateTimeOffset.Now);
            }
            Publish(merged);
        }
        catch (Exception ex)
        {
            // A bad push is not worth dropping a working session over; the next poll corrects it.
            _log.Record("codex", "app-server", "push.unparsed", DiagLevel.Warn, "failed", ex.Message);
        }
    }

    private void Publish(CodexSnapshot snapshot)
    {
        lock (_stateLock)
        {
            _latest = snapshot;
            _error = null;
            _consecutiveFailures = 0;
        }

        _log.Record("codex", "rate-limits", "read", DiagLevel.Info, "succeeded",
            Describe(snapshot));

        Changed?.Invoke();
    }

    private static string Describe(CodexSnapshot snapshot)
    {
        var parts = new List<string>();
        if (snapshot.Primary is { } p)
            parts.Add($"{p.Label} {p.RemainingPct:0}% left");
        if (snapshot.Secondary is { } s)
            parts.Add($"{s.Label} {s.RemainingPct:0}% left");
        return parts.Count == 0 ? "no windows" : string.Join(", ", parts);
    }

    private int RecordFailure(Exception ex)
    {
        lock (_stateLock)
        {
            var attempt = _consecutiveFailures;
            _consecutiveFailures++;
            // The last good reading is kept deliberately: a dropped connection doesn't mean the
            // numbers changed, and a stale bar beats an empty one. Staleness is drawn dimmed.
            _error = ex is System.IO.FileNotFoundException ? "Codex CLI not found" : ex.Message;
            return attempt;
        }
    }

    public void Dispose()
    {
        try { _lifetime.Cancel(); } catch (Exception) { }
        try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch (Exception) { }
        _lifetime.Dispose();
        _refreshSignal.Dispose();
    }
}
