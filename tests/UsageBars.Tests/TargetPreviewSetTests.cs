using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace UsageBars.Tests;

public class TargetPreviewSetTests
{
    [Fact]
    public void FailedSessionUpdateIsDisposedWithoutPublishingSuppression()
    {
        var window = Window(10, "terminal");
        var session = new ThrowingPreviewSession(window);
        using var previews = new TargetPreviewSet(
            () => new[] { window }, _ => session);

        var error = Assert.Throws<InvalidOperationException>(() =>
            previews.Update(TerminalDraft(), TargetManagementMode.EditExisting, null));

        Assert.Equal("preview failed", error.Message);
        Assert.Empty(previews.SuppressedHwnds);
        Assert.Equal(1, session.DisposeCalls);
    }

    [Fact]
    public void LaterRetainedRefreshFailureRetiresSessionBeforePublishingFailure()
    {
        var windows = new List<WindowSnapshot> { Window(10, "terminal", "Initial") };
        var sessions = new RecordingPreviewSessionFactory();
        using var previews = NewPreviewSet(() => windows, sessions);
        previews.Update(TerminalDraft(), TargetManagementMode.EditExisting, capturedWindow: null);
        var failed = sessions.For(new IntPtr(10));
        failed.FailNextRefresh = true;
        windows[0] = windows[0] with { Title = "Renamed" };

        var error = Assert.Throws<InvalidOperationException>(() => previews.Refresh());

        Assert.Equal("refresh failed", error.Message);
        Assert.Empty(previews.SuppressedHwnds);
        Assert.Equal(1, failed.DisposeCalls);
        Assert.Equal(0, previews.Status.WindowCount);

        previews.Refresh();

        Assert.Equal(new[] { new IntPtr(10) }, previews.SuppressedHwnds);
        Assert.Equal(2, sessions.Created.Count);
    }

    [Fact]
    public void LaterRetainedUpdateFailureRetiresSessionBeforePublishingFailure()
    {
        var window = Window(10, "terminal");
        var sessions = new RecordingPreviewSessionFactory();
        using var previews = NewPreviewSet(() => new[] { window }, sessions);
        previews.Update(TerminalDraft(), TargetManagementMode.EditExisting, capturedWindow: null);
        var failed = sessions.For(window.Hwnd);
        failed.FailNextUpdate = true;

        var error = Assert.Throws<InvalidOperationException>(() =>
            previews.Update(
                TerminalDraft() with { Name = "Changed" },
                TargetManagementMode.EditExisting,
                capturedWindow: null));

        Assert.Equal("update failed", error.Message);
        Assert.Empty(previews.SuppressedHwnds);
        Assert.Equal(1, failed.DisposeCalls);
        Assert.Equal(0, previews.Status.WindowCount);
    }

    [Fact]
    public void EditingRuleAddsRetainsAndRemovesSessionsAsMatchingWindowsChange()
    {
        var windows = new List<WindowSnapshot> { Window(10, "terminal"), Window(20, "terminal") };
        var sessions = new RecordingPreviewSessionFactory();
        using var previews = NewPreviewSet(() => windows, sessions);

        previews.Update(TerminalDraft(), TargetManagementMode.EditExisting, capturedWindow: null);
        Assert.Equal(new[] { new IntPtr(10), new IntPtr(20) }, previews.SuppressedHwnds.Order());

        var retained = sessions.For(new IntPtr(20));
        windows.RemoveAt(0);
        previews.Refresh();

        Assert.Equal(1, sessions.For(new IntPtr(10)).DisposeCalls);
        Assert.Same(retained, sessions.For(new IntPtr(20)));
        Assert.Equal(new[] { new IntPtr(20) }, previews.SuppressedHwnds);
        Assert.Equal(2, sessions.Created.Count);
    }

    [Fact]
    public void AddingCapturedTargetPreviewsOnlyCapturedWindowEvenWhenDraftDoesNotMatch()
    {
        var captured = Window(10, "terminal");
        var windows = new List<WindowSnapshot>
        {
            captured with { Title = "Renamed" },
            Window(20, "terminal"),
        };
        var sessions = new RecordingPreviewSessionFactory();
        using var previews = NewPreviewSet(() => windows, sessions);

        previews.Update(
            TerminalDraft() with { WindowTitle = new(TitleMatchKind.Literal, "Different") },
            TargetManagementMode.AddCaptured,
            captured);

        var preview = sessions.For(captured.Hwnd);
        Assert.Equal(new[] { captured.Hwnd }, previews.SuppressedHwnds);
        Assert.Equal("Renamed", preview.LatestWindow.Title);
        Assert.Equal(TargetPreviewState.Dimmed, preview.Evaluation.State);
    }

    [Fact]
    public void CapturedTitleMismatchKeepsPinnedPreviewAndExplainsSavedInactivity()
    {
        var captured = Window(10, "terminal");
        var sessions = new RecordingPreviewSessionFactory();
        using var previews = NewPreviewSet(() => new[] { captured }, sessions);

        previews.Update(
            TerminalDraft() with { WindowTitle = new(TitleMatchKind.Literal, "Different") },
            TargetManagementMode.AddCaptured,
            captured);

        Assert.Equal(new[] { captured.Hwnd }, previews.SuppressedHwnds);
        Assert.Equal(TargetPreviewState.Dimmed, previews.Status.WorstState);
        Assert.Equal(
            "Preview shown; saved rule would currently be inactive — mismatch",
            previews.Status.Message);
    }

    [Fact]
    public void EditingExistingPreviewsOnlyWindowsMatchingIdentityAndTitle()
    {
        var matching = Window(10, "terminal", "Project");
        var wrongTitle = Window(20, "terminal", "Other");
        var wrongProcess = Window(30, "browser", "Project");
        var sessions = new RecordingPreviewSessionFactory();
        using var previews = NewPreviewSet(
            () => new[] { matching, wrongTitle, wrongProcess }, sessions);

        previews.Update(
            TerminalDraft() with { WindowTitle = new(TitleMatchKind.Literal, "Project") },
            TargetManagementMode.EditExisting,
            capturedWindow: null);

        Assert.Equal(new[] { matching.Hwnd }, previews.SuppressedHwnds);
    }

    [Fact]
    public void AddingCapturedTargetKeepsCapturedSnapshotWhenEnumerationTemporarilyOmitsIt()
    {
        var captured = Window(10, "terminal");
        var sessions = new RecordingPreviewSessionFactory();
        using var previews = NewPreviewSet(
            () => Array.Empty<WindowSnapshot>(), sessions, _ => true);

        previews.Update(TerminalDraft(), TargetManagementMode.AddCaptured, captured);

        Assert.Equal(new[] { captured.Hwnd }, previews.SuppressedHwnds);
        Assert.Equal(captured, sessions.For(captured.Hwnd).LatestWindow);
    }

    [Fact]
    public void AddingCapturedTargetDisposesSessionWhenOmittedWindowBecomesInvalid()
    {
        var captured = Window(10, "terminal");
        var valid = true;
        var sessions = new RecordingPreviewSessionFactory();
        using var previews = NewPreviewSet(
            () => Array.Empty<WindowSnapshot>(), sessions, _ => valid);
        previews.Update(TerminalDraft(), TargetManagementMode.AddCaptured, captured);
        var session = sessions.For(captured.Hwnd);

        valid = false;
        previews.Refresh();
        previews.Refresh();

        Assert.Empty(previews.SuppressedHwnds);
        Assert.Equal(1, session.DisposeCalls);
        Assert.Equal(0, previews.Status.WindowCount);
        Assert.Equal(TargetPreviewState.WindowClosed, previews.Status.WorstState);
        Assert.Equal("Preview unavailable—the selected window closed.", previews.Status.Message);
    }

    [Fact]
    public void EditingRuleWithoutMatchesReportsNoPreview()
    {
        var sessions = new RecordingPreviewSessionFactory();
        using var previews = NewPreviewSet(
            () => new[] { Window(10, "browser") }, sessions);

        previews.Update(TerminalDraft(), TargetManagementMode.EditExisting, capturedWindow: null);

        Assert.Empty(previews.SuppressedHwnds);
        Assert.Equal(0, previews.Status.WindowCount);
        Assert.Null(previews.Status.WorstState);
        Assert.Contains("No matching windows", previews.Status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClearAndDisposeDisposeEverySessionExactlyOnce()
    {
        var sessions = new RecordingPreviewSessionFactory();
        var previews = NewPreviewSet(
            () => new[] { Window(10, "terminal"), Window(20, "terminal") }, sessions);
        previews.Update(TerminalDraft(), TargetManagementMode.EditExisting, capturedWindow: null);

        previews.Clear();
        previews.Clear();
        previews.Dispose();

        Assert.Empty(previews.SuppressedHwnds);
        Assert.All(sessions.Created, session => Assert.Equal(1, session.DisposeCalls));
    }

    [Fact]
    public void RefreshPassesLatestTitlesIntoRetainedSessions()
    {
        var windows = new List<WindowSnapshot> { Window(10, "terminal", "Initial") };
        var sessions = new RecordingPreviewSessionFactory();
        using var previews = NewPreviewSet(() => windows, sessions);
        previews.Update(TerminalDraft(), TargetManagementMode.EditExisting, capturedWindow: null);
        var retained = sessions.For(new IntPtr(10));

        windows[0] = windows[0] with { Title = "Renamed" };
        previews.Refresh();

        Assert.Same(retained, sessions.For(new IntPtr(10)));
        Assert.Equal("Renamed", retained.LatestWindow.Title);
        Assert.Equal(1, retained.RefreshCalls);
    }

    [Fact]
    public void RetainedReconciliationRendersOnlyTheCurrentDraftOnce()
    {
        var windows = new List<WindowSnapshot> { Window(10, "terminal", "Initial") };
        var sessions = new RecordingPreviewSessionFactory();
        using var previews = NewPreviewSet(() => windows, sessions);
        previews.Update(TerminalDraft(), TargetManagementMode.EditExisting, capturedWindow: null);
        var retained = sessions.For(new IntPtr(10));
        retained.ClearRenderLog();
        windows[0] = windows[0] with { Title = "Renamed" };
        var changed = TerminalDraft() with { Name = "Changed" };

        previews.Update(changed, TargetManagementMode.EditExisting, capturedWindow: null);

        var render = Assert.Single(retained.Renders);
        Assert.Equal("Renamed", render.WindowTitle);
        Assert.Equal("Changed", render.DraftName);
    }

    private static TargetPreviewSet NewPreviewSet(
        Func<IReadOnlyList<WindowSnapshot>> windows,
        RecordingPreviewSessionFactory sessions,
        Func<IntPtr, bool>? isWindowValid = null) =>
        isWindowValid is null
            ? new(windows, sessions.Create)
            : new(windows, sessions.Create, isWindowValid);

    private static WindowSnapshot Window(long hwnd, string processName, string title = "Terminal") =>
        new(new IntPtr(hwnd), (uint)hwnd, processName, "frame", title, true);

    private static TargetDefinition TerminalDraft() => TargetDefinition.Captured(
        "Terminal", "terminal", "frame", new[] { ProviderBinding.Claude },
        TargetPlacementKind.Terminal, TargetActivationPolicy.AlwaysWhileOpen, true, 0);
}

internal sealed class RecordingPreviewSessionFactory
{
    public List<RecordingSetPreviewSession> Created { get; } = new();

    public ITargetPreviewSession Create(WindowSnapshot window)
    {
        var session = new RecordingSetPreviewSession(window);
        Created.Add(session);
        return session;
    }

    public RecordingSetPreviewSession For(IntPtr hwnd) =>
        Created.Single(session => session.Hwnd == hwnd);
}

internal sealed class RecordingSetPreviewSession : ITargetPreviewSession
{
    private TargetDefinition? _latestDraft;

    public RecordingSetPreviewSession(WindowSnapshot window)
    {
        LatestWindow = window;
        Evaluation = EvaluationFor(window, draft: null);
    }

    public IntPtr Hwnd => LatestWindow.Hwnd;
    public WindowSnapshot LatestWindow { get; private set; }
    public TargetPreviewEvaluation Evaluation { get; private set; }
    public int RefreshCalls { get; private set; }
    public int DisposeCalls { get; private set; }
    public bool FailNextRefresh { get; set; }
    public bool FailNextUpdate { get; set; }
    public List<(string WindowTitle, string DraftName)> Renders { get; } = new();

    public bool Suppresses(IntPtr hwnd) => DisposeCalls == 0 && Hwnd == hwnd;

    public void Update(TargetDefinition draft)
    {
        _latestDraft = draft;
        Evaluation = EvaluationFor(LatestWindow, draft);
        Renders.Add((LatestWindow.Title, draft.Name));
        if (!FailNextUpdate) return;
        FailNextUpdate = false;
        throw new InvalidOperationException("update failed");
    }

    public void Update(WindowSnapshot latestWindow, TargetDefinition draft)
    {
        RefreshCalls++;
        LatestWindow = latestWindow;
        if (FailNextRefresh)
        {
            FailNextRefresh = false;
            throw new InvalidOperationException("refresh failed");
        }
        Update(draft);
    }

    public void Refresh(WindowSnapshot? latestWindow = null)
    {
        RefreshCalls++;
        if (latestWindow is not null) LatestWindow = latestWindow;
        if (_latestDraft is { } draft)
            Renders.Add((LatestWindow.Title, draft.Name));
        if (!FailNextRefresh) return;
        FailNextRefresh = false;
        throw new InvalidOperationException("refresh failed");
    }

    public void ClearRenderLog() => Renders.Clear();

    public void WindowClosed() => Evaluation = new(
        TargetPreviewState.WindowClosed,
        "Preview unavailable—the selected window closed.",
        Array.Empty<ProviderId>());

    public void Dispose()
    {
        if (DisposeCalls == 0) DisposeCalls++;
    }

    private static TargetPreviewEvaluation EvaluationFor(
        WindowSnapshot window, TargetDefinition? draft)
    {
        var state = draft is null || draft.Matches(window)
            ? TargetPreviewState.Normal
            : TargetPreviewState.Dimmed;
        IReadOnlyList<ProviderId> providers = draft is null
            ? Array.Empty<ProviderId>()
            : draft.Providers.Select(binding => binding.Provider).ToList();
        return new(state, state == TargetPreviewState.Dimmed ? "mismatch" : null, providers);
    }
}

internal sealed class ThrowingPreviewSession : ITargetPreviewSession
{
    public ThrowingPreviewSession(WindowSnapshot window) => Hwnd = window.Hwnd;

    public IntPtr Hwnd { get; }
    public TargetPreviewEvaluation Evaluation { get; } =
        new(TargetPreviewState.Failed, "preview failed", Array.Empty<ProviderId>());
    public int DisposeCalls { get; private set; }

    public bool Suppresses(IntPtr hwnd) => DisposeCalls == 0 && hwnd == Hwnd;
    public void Update(TargetDefinition draft) => throw new InvalidOperationException("preview failed");
    public void Update(WindowSnapshot latestWindow, TargetDefinition draft) => Update(draft);
    public void Refresh(WindowSnapshot? latestWindow = null) { }
    public void WindowClosed() { }
    public void Dispose() => DisposeCalls++;
}
