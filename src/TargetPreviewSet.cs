using System;
using System.Collections.Generic;
using System.Linq;

namespace UsageBars;

internal sealed record TargetPreviewSetStatus(
    int WindowCount,
    TargetPreviewState? WorstState,
    string Message);

internal interface ITargetPreviewSet : IDisposable
{
    IReadOnlySet<IntPtr> SuppressedHwnds { get; }
    TargetPreviewSetStatus Status { get; }
    void Update(TargetDefinition draft, TargetManagementMode mode, WindowSnapshot? capturedWindow);
    void Refresh();
    void Clear();
}

internal sealed class TargetPreviewSet : ITargetPreviewSet
{
    private readonly Func<IReadOnlyList<WindowSnapshot>> _windows;
    private readonly Func<WindowSnapshot, ITargetPreviewSession> _createSession;
    private readonly Func<IntPtr, bool> _isWindowValid;
    private readonly Dictionary<IntPtr, ITargetPreviewSession> _sessions = new();
    private TargetDefinition? _draft;
    private TargetManagementMode _mode;
    private WindowSnapshot? _capturedWindow;
    private bool _capturedWindowUnavailable;
    private TargetPreviewEvaluation? _capturedWindowLoss;
    private bool _disposed;

    public TargetPreviewSet(
        Func<IReadOnlyList<WindowSnapshot>> windows,
        Func<WindowSnapshot, ITargetPreviewSession> createSession,
        Func<IntPtr, bool>? isWindowValid = null)
    {
        _windows = windows;
        _createSession = createSession;
        _isWindowValid = isWindowValid ?? Native.IsWindow;
    }

    public IReadOnlySet<IntPtr> SuppressedHwnds => _sessions.Keys.ToHashSet();

    public TargetPreviewSetStatus Status { get; private set; } =
        new(0, null, "No preview is active.");

    public void Update(
        TargetDefinition draft,
        TargetManagementMode mode,
        WindowSnapshot? capturedWindow)
    {
        if (_disposed) return;
        _draft = draft;
        _mode = mode;
        _capturedWindow = capturedWindow;
        Reconcile();
    }

    public void Refresh()
    {
        if (_disposed || _draft is null) return;
        Reconcile();
    }

    public void Clear()
    {
        foreach (var session in _sessions.Values) session.Dispose();
        _sessions.Clear();
        _draft = null;
        _capturedWindow = null;
        _capturedWindowUnavailable = false;
        _capturedWindowLoss = null;
        Status = new(0, null, "No preview is active.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        Clear();
        _disposed = true;
    }

    private void Reconcile()
    {
        var draft = _draft!;
        var desired = DesiredWindows(draft)
            .GroupBy(window => window.Hwnd)
            .ToDictionary(group => group.Key, group => group.Last());

        _capturedWindowLoss = null;
        if (_capturedWindowUnavailable
            && _capturedWindow is { Hwnd: var capturedHwnd }
            && _sessions.TryGetValue(capturedHwnd, out var closed))
        {
            closed.WindowClosed();
            _capturedWindowLoss = closed.Evaluation;
        }
        else if (_capturedWindowUnavailable)
        {
            _capturedWindowLoss = new(
                TargetPreviewState.WindowClosed,
                "Preview unavailable—the selected window closed.",
                Array.Empty<ProviderId>());
        }

        foreach (var hwnd in _sessions.Keys.Where(hwnd => !desired.ContainsKey(hwnd)).ToList())
        {
            _sessions[hwnd].Dispose();
            _sessions.Remove(hwnd);
        }

        foreach (var (hwnd, window) in desired)
        {
            if (_sessions.TryGetValue(hwnd, out var retained))
            {
                try
                {
                    retained.Update(window, draft);
                }
                catch
                {
                    Retire(hwnd, retained);
                    throw;
                }
                continue;
            }

            var created = _createSession(window);
            try
            {
                created.Update(draft);
                _sessions.Add(hwnd, created);
            }
            catch
            {
                created.Dispose();
                UpdateStatus();
                throw;
            }
        }

        UpdateStatus();
    }

    private void Retire(IntPtr hwnd, ITargetPreviewSession session)
    {
        _sessions.Remove(hwnd);
        session.Dispose();
        UpdateStatus();
    }

    private IEnumerable<WindowSnapshot> DesiredWindows(TargetDefinition draft)
    {
        _capturedWindowUnavailable = false;
        var current = _windows();
        if (_mode == TargetManagementMode.EditExisting)
            return current.Where(draft.Matches).ToList();

        if (_mode != TargetManagementMode.AddCaptured
            || _capturedWindow is not { Hwnd: var hwnd } captured
            || hwnd == IntPtr.Zero)
            return Array.Empty<WindowSnapshot>();

        var latest = current.LastOrDefault(window => window.Hwnd == hwnd);
        if (latest is not null) return new[] { latest };
        if (_isWindowValid(hwnd)) return new[] { captured };
        _capturedWindowUnavailable = true;
        return Array.Empty<WindowSnapshot>();
    }

    private void UpdateStatus()
    {
        if (_sessions.Count == 0)
        {
            if (_capturedWindowLoss is { } loss)
            {
                Status = new(
                    0,
                    loss.State,
                    loss.Reason ?? "Preview unavailable—the selected window closed.");
                return;
            }
            Status = new(0, null, _mode == TargetManagementMode.EditExisting
                ? "No matching windows are currently available for preview."
                : "No preview is active.");
            return;
        }

        var worst = _sessions.Values
            .Select(session => session.Evaluation)
            .OrderByDescending(evaluation => Severity(evaluation.State))
            .First();
        var noun = _sessions.Count == 1 ? "window" : "windows";
        var detail = string.IsNullOrWhiteSpace(worst.Reason) ? "" : $" — {worst.Reason}";
        if (_mode == TargetManagementMode.AddCaptured
            && worst.State == TargetPreviewState.Dimmed)
        {
            Status = new(
                _sessions.Count,
                worst.State,
                $"Preview shown; saved rule would currently be inactive{detail}");
            return;
        }
        Status = new(
            _sessions.Count,
            worst.State,
            $"Previewing {_sessions.Count} {noun} {StateText(worst.State)}{detail}");
    }

    private static int Severity(TargetPreviewState state) => state switch
    {
        TargetPreviewState.Failed => 4,
        TargetPreviewState.WindowClosed => 3,
        TargetPreviewState.Dimmed => 2,
        TargetPreviewState.Neutral => 1,
        _ => 0,
    };

    private static string StateText(TargetPreviewState state) => state switch
    {
        TargetPreviewState.Normal => "normally",
        TargetPreviewState.Neutral => "with a neutral placement pill",
        TargetPreviewState.Dimmed => "dimmed",
        TargetPreviewState.WindowClosed => "unavailable",
        _ => "with failures",
    };
}
