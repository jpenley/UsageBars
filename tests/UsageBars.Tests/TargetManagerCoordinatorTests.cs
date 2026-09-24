using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xunit;

namespace UsageBars.Tests;

public class TargetManagerCoordinatorTests
{
    [Fact]
    public void FirstOpenIsDispatchedAndShowsOneOwnedManager()
    {
        Action? posted = null;
        var manager = new FakeManager();
        using var owner = new FakeOwner();
        using var coordinator = new TargetManagerCoordinator(
            action => posted = action, () => manager, owner, (_, _) => { },
            CancelledPick, _ => CaptureContext());

        coordinator.Open();

        Assert.False(manager.ShowCalled);
        posted!();
        Assert.True(manager.ShowCalled);
        Assert.Same(owner, manager.Owner);
    }

    [Fact]
    public void OpenReportsRequestedBeforeDispatch()
    {
        var order = new List<string>();
        using var coordinator = new TargetManagerCoordinator(
            _ => order.Add("dispatch"), () => new FakeManager(), new FakeOwner(),
            (name, _) => order.Add(name), CancelledPick, _ => CaptureContext());

        coordinator.Open();

        Assert.Equal(new[] { "requested", "dispatch" }, order);
    }

    [Fact]
    public void DispatchFailureIsReportedAndDoesNotEscape()
    {
        var reports = new List<(string Event, Exception? Error)>();
        using var coordinator = new TargetManagerCoordinator(
            _ => throw new InvalidOperationException("dispatch failed"),
            () => new FakeManager(), new FakeOwner(),
            (name, error) => reports.Add((name, error)), CancelledPick, _ => CaptureContext());

        var escaped = Record.Exception(() => coordinator.Open());

        Assert.Null(escaped);
        Assert.Equal("requested", reports[0].Event);
        Assert.Contains(reports, report =>
            report.Event == "dispatch-failed" && report.Error?.Message == "dispatch failed");
    }

    [Fact]
    public void RepeatedOpenRestoresAndActivatesTheExistingManager()
    {
        var manager = new FakeManager { WindowState = FormWindowState.Minimized };
        using var coordinator = CoordinatorWithImmediateDispatch(manager);

        coordinator.Open();
        coordinator.Open();

        Assert.Equal(1, manager.ShowCount);
        Assert.Equal(FormWindowState.Normal, manager.WindowState);
        Assert.Equal(2, manager.ActivateCount);
        Assert.Equal(2, manager.BringToFrontCount);
    }

    [Fact]
    public void RepeatedOpenShowsAHiddenExistingManager()
    {
        var manager = new FakeManager();
        var factoryCount = 0;
        using var coordinator = CoordinatorWithFactory(() =>
        {
            factoryCount++;
            return manager;
        });

        coordinator.Open();
        manager.Hide();
        coordinator.Open();

        Assert.Equal(1, factoryCount);
        Assert.True(manager.Visible);
        Assert.Equal(2, manager.ShowCount);
    }

    [Fact]
    public void ReuseShowFailureIsReportedWithoutLosingTheManagerSlot()
    {
        var reports = new List<(string Event, Exception? Error)>();
        var manager = new FakeManager();
        var factoryCount = 0;
        using var coordinator = CoordinatorWithFactory(
            () =>
            {
                factoryCount++;
                return manager;
            },
            (name, error) => reports.Add((name, error)));
        coordinator.Open();
        manager.Hide();
        manager.NextShowError = new InvalidOperationException("reuse show failed");

        var escaped = Record.Exception(() => coordinator.Open());
        coordinator.Open();

        Assert.Null(escaped);
        Assert.Equal(1, factoryCount);
        Assert.True(manager.Visible);
        Assert.Contains(reports, report =>
            report.Event == "activation-failed" && report.Error?.Message == "reuse show failed");
    }

    [Fact]
    public void ReuseActivationFailureIsReportedWithoutLosingTheManagerSlot()
    {
        var reports = new List<(string Event, Exception? Error)>();
        var manager = new FakeManager();
        var factoryCount = 0;
        using var coordinator = CoordinatorWithFactory(
            () =>
            {
                factoryCount++;
                return manager;
            },
            (name, error) => reports.Add((name, error)));
        coordinator.Open();
        manager.NextActivateError = new InvalidOperationException("activation failed");

        var escaped = Record.Exception(() => coordinator.Open());
        coordinator.Open();

        Assert.Null(escaped);
        Assert.Equal(1, factoryCount);
        Assert.Equal(3, manager.ActivateCount);
        Assert.Contains(reports, report =>
            report.Event == "activation-failed" && report.Error?.Message == "activation failed");
    }

    [Fact]
    public void ClosingClearsTheSlotSoTheNextRequestCreatesANewManager()
    {
        var created = new List<FakeManager>();
        using var coordinator = CoordinatorWithFactory(() =>
        {
            var manager = new FakeManager();
            created.Add(manager);
            return manager;
        });

        coordinator.Open();
        created[0].RaiseFormClosed();
        coordinator.Open();

        Assert.Equal(2, created.Count);
    }

    [Fact]
    public void ClosingDuringInitialShowDoesNotLeaveAStaleManagerInTheSlot()
    {
        var first = new FakeManager { CloseDuringShow = true };
        var second = new FakeManager();
        var created = new Queue<FakeManager>(new[] { first, second });
        using var coordinator = CoordinatorWithFactory(() => created.Dequeue());

        coordinator.Open();
        coordinator.Open();

        Assert.True(second.ShowCalled);
    }

    [Fact]
    public void ShowFailureIsReportedAndDoesNotOccupyTheSlot()
    {
        var reports = new List<(string Event, Exception? Error)>();
        var first = new FakeManager { ShowError = new InvalidOperationException("show failed") };
        var second = new FakeManager();
        var queue = new Queue<FakeManager>(new[] { first, second });
        using var coordinator = CoordinatorWithFactory(
            () => queue.Dequeue(), (name, error) => reports.Add((name, error)));

        coordinator.Open();
        coordinator.Open();

        Assert.Contains(reports, report => report.Event == "display-failed");
        Assert.True(second.ShowCalled);
    }

    [Fact]
    public void FactoryFailureIsReportedAndDoesNotPreventALaterOpen()
    {
        var reports = new List<(string Event, Exception? Error)>();
        var healthy = new FakeManager();
        var attempts = 0;
        using var coordinator = CoordinatorWithFactory(
            () =>
            {
                attempts++;
                if (attempts == 1) throw new InvalidOperationException("construction failed");
                return healthy;
            },
            (name, error) => reports.Add((name, error)));

        var escaped = Record.Exception(() => coordinator.Open());
        coordinator.Open();

        Assert.Null(escaped);
        Assert.Contains(reports, report =>
            report.Event == "display-failed" && report.Error?.Message == "construction failed");
        Assert.True(healthy.ShowCalled);
    }

    [Fact]
    public void DisposeClosesAndDisposesTheActiveManagerExactlyOnce()
    {
        var manager = new FakeManager();
        var owner = new FakeOwner();
        var coordinator = new TargetManagerCoordinator(
            action => action(), () => manager, owner, (_, _) => { },
            CancelledPick, _ => CaptureContext());
        coordinator.Open();

        coordinator.Dispose();
        coordinator.Dispose();

        Assert.Equal(1, manager.CloseCount);
        Assert.Equal(1, manager.DisposeCount);
        Assert.True(owner.Disposed);
    }

    [Fact]
    public void DisposePreventsQueuedAndFutureOpenRequests()
    {
        Action? posted = null;
        var dispatchCount = 0;
        var factoryCount = 0;
        var manager = new FakeManager();
        var coordinator = new TargetManagerCoordinator(
            action =>
            {
                dispatchCount++;
                posted = action;
            },
            () =>
            {
                factoryCount++;
                return manager;
            },
            new FakeOwner(),
            (_, _) => { }, CancelledPick, _ => CaptureContext());

        coordinator.Open();
        coordinator.Dispose();
        coordinator.Open();
        posted!();

        Assert.Equal(1, dispatchCount);
        Assert.Equal(0, factoryCount);
        Assert.False(manager.ShowCalled);
    }

    [Fact]
    public async Task AddHidesManagerAndSuccessfulPickRestoresSameInstanceInCapturedMode()
    {
        var pending = new TaskCompletionSource<WindowCaptureResult>();
        var manager = new FakeManager { SelectedRuleId = Guid.NewGuid() };
        var context = CaptureContext();
        using var coordinator = Coordinator(manager, () => pending.Task, _ => context);
        coordinator.Open();

        manager.RaiseAddRequested();
        Assert.False(manager.Visible);

        pending.SetResult(CaptureSuccess());
        await manager.CapturedStarted.Task;

        Assert.Equal(1, manager.HideCount);
        Assert.Equal(2, manager.ShowCount);
        Assert.Same(context, manager.CapturedContext);
    }

    [Fact]
    public void EscapeOrRightClickCancellationRestoresThePriorBrowseSelection()
    {
        var priorSelection = Guid.NewGuid();
        var manager = new FakeManager { SelectedRuleId = priorSelection };
        using var coordinator = Coordinator(manager,
            () => Task.FromResult(new WindowCaptureResult(false, null, "Cancelled.")));
        coordinator.Open();

        manager.RaiseAddRequested();

        Assert.Equal(2, manager.ShowCount);
        Assert.Equal(priorSelection, manager.SelectedRuleId);
        Assert.Equal(1, manager.RestoreBrowseSelectionCount);
    }

    [Fact]
    public void CaptureFailureRestoresThePriorSelectionAndReportsTheFailure()
    {
        var priorSelection = Guid.NewGuid();
        var manager = new FakeManager { SelectedRuleId = priorSelection };
        var reports = new List<(string Event, Exception? Error)>();
        using var coordinator = Coordinator(manager,
            () => Task.FromResult(new WindowCaptureResult(false, null, "No window was found.")),
            report: (name, error) => reports.Add((name, error)));
        coordinator.Open();

        manager.RaiseAddRequested();

        Assert.Equal(priorSelection, manager.SelectedRuleId);
        Assert.Equal(1, manager.RestoreBrowseSelectionCount);
        Assert.Contains(reports, report =>
            report.Event == "capture-failed" && report.Error?.Message == "No window was found.");
    }

    [Fact]
    public void PersistentRestoreFailureIsReportedWithoutEscapingTheAsyncEventHandler()
    {
        var reports = new List<(string Event, Exception? Error)>();
        var manager = new FakeManager
        {
            RestoreBrowseSelectionError = new InvalidOperationException("restore failed"),
        };
        using var coordinator = Coordinator(manager,
            () => Task.FromResult(new WindowCaptureResult(false, null, "Cancelled.")),
            report: (name, error) => reports.Add((name, error)));
        coordinator.Open();
        var previousContext = SynchronizationContext.Current;
        var recordingContext = new RecordingSynchronizationContext();
        try
        {
            SynchronizationContext.SetSynchronizationContext(recordingContext);
            manager.RaiseAddRequested();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        Assert.Equal(1, manager.RestoreBrowseSelectionCount);
        Assert.Null(recordingContext.Exception);
        Assert.Contains(reports, report =>
            report.Event == "restore-failed" && report.Error?.Message == "restore failed");
    }

    [Fact]
    public async Task RepeatedAddRequestsAreIgnoredWhileAPickIsActive()
    {
        var pending = new TaskCompletionSource<WindowCaptureResult>();
        var manager = new FakeManager();
        var pickCount = 0;
        using var coordinator = Coordinator(manager, () =>
        {
            pickCount++;
            return pending.Task;
        });
        coordinator.Open();

        manager.RaiseAddRequested();
        manager.RaiseAddRequested();

        Assert.Equal(1, pickCount);
        Assert.Equal(1, manager.HideCount);

        pending.SetResult(CaptureSuccess());
        await manager.CapturedStarted.Task;
    }

    [Fact]
    public void RepeatedManageRequestDoesNotRevealManagerWhilePickIsActive()
    {
        var pending = new TaskCompletionSource<WindowCaptureResult>();
        var manager = new FakeManager();
        using var coordinator = Coordinator(manager, () => pending.Task);
        coordinator.Open();
        manager.RaiseAddRequested();

        coordinator.Open();

        Assert.Equal(1, manager.ShowCount);
        Assert.False(manager.Visible);
        pending.SetResult(new WindowCaptureResult(false, null, "Cancelled."));
    }

    [Fact]
    public async Task PickCompletionAfterManagerCloseDoesNotRestoreOrBeginCapturedMode()
    {
        var pending = new TaskCompletionSource<WindowCaptureResult>();
        var manager = new FakeManager();
        using var coordinator = Coordinator(manager, () => pending.Task);
        coordinator.Open();
        manager.RaiseAddRequested();

        manager.RaiseFormClosed();
        pending.SetResult(CaptureSuccess());
        await Task.Yield();

        Assert.Equal(1, manager.ShowCount);
        Assert.Null(manager.CapturedContext);
    }

    [Fact]
    public async Task PickCompletionAfterCoordinatorDisposalDoesNotRestoreOrBeginCapturedMode()
    {
        var pending = new TaskCompletionSource<WindowCaptureResult>();
        var manager = new FakeManager();
        var coordinator = Coordinator(manager, () => pending.Task);
        coordinator.Open();
        manager.RaiseAddRequested();

        coordinator.Dispose();
        pending.SetResult(CaptureSuccess());
        await Task.Yield();

        Assert.Equal(1, manager.ShowCount);
        Assert.Null(manager.CapturedContext);
    }

    private sealed class FakeOwner : ITargetManagerOwner
    {
        public IntPtr Handle { get; } = new(123);
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeManager : IModelessTargetManager
    {
        public bool IsDisposed { get; private set; }
        public bool Visible { get; private set; }
        public FormWindowState WindowState { get; set; }
        public event FormClosedEventHandler? FormClosed;
        public event EventHandler? AddRequested;
        public Guid? SelectedRuleId { get; set; }
        public IReadOnlySet<IntPtr> SuppressedHwnds { get; } = new HashSet<IntPtr>();
        public int ShowCount { get; private set; }
        public int HideCount { get; private set; }
        public int RestoreBrowseSelectionCount { get; private set; }
        public int ActivateCount { get; private set; }
        public int BringToFrontCount { get; private set; }
        public int CloseCount { get; private set; }
        public int DisposeCount { get; private set; }
        public bool ShowCalled => ShowCount > 0;
        public IWin32Window? Owner { get; private set; }
        public Exception? ShowError { get; init; }
        public Exception? NextShowError { get; set; }
        public Exception? NextActivateError { get; set; }
        public bool CloseDuringShow { get; init; }
        public Exception? RestoreBrowseSelectionError { get; init; }
        public TargetCaptureContext? CapturedContext { get; private set; }
        public TaskCompletionSource<object?> CapturedStarted { get; } = new();

        public void Show(IWin32Window owner)
        {
            ShowCount++;
            Owner = owner;
            if (ShowError is not null) throw ShowError;
            if (NextShowError is { } error)
            {
                NextShowError = null;
                throw error;
            }
            Visible = true;
            if (CloseDuringShow) RaiseFormClosed();
        }

        public void Activate()
        {
            ActivateCount++;
            if (NextActivateError is not { } error) return;
            NextActivateError = null;
            throw error;
        }
        public void BringToFront() => BringToFrontCount++;
        public void Close() => CloseCount++;
        public void Dispose()
        {
            DisposeCount++;
            IsDisposed = true;
        }

        public void RaiseFormClosed() =>
            FormClosed?.Invoke(this, new FormClosedEventArgs(CloseReason.UserClosing));

        public void RaiseAddRequested() => AddRequested?.Invoke(this, EventArgs.Empty);

        public void Hide()
        {
            HideCount++;
            Visible = false;
        }

        public void RestoreBrowseSelection(Guid? selectedRuleId)
        {
            RestoreBrowseSelectionCount++;
            if (RestoreBrowseSelectionError is { } error) throw error;
            SelectedRuleId = selectedRuleId;
        }

        public void BeginCaptured(TargetCaptureContext context)
        {
            CapturedContext = context;
            CapturedStarted.TrySetResult(null);
        }

        public void RefreshLiveState() { }
    }

    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        public Exception? Exception { get; private set; }

        public override void Post(SendOrPostCallback callback, object? state)
        {
            try
            {
                callback(state);
            }
            catch (Exception ex)
            {
                Exception = ex;
            }
        }
    }

    private static TargetManagerCoordinator CoordinatorWithImmediateDispatch(FakeManager manager) =>
        Coordinator(manager);

    private static TargetManagerCoordinator CoordinatorWithFactory(
        Func<FakeManager> factory, Action<string, Exception?>? report = null) =>
        new(action => action(), () => factory(), new FakeOwner(), report ?? ((_, _) => { }),
            CancelledPick, _ => CaptureContext());

    private static TargetManagerCoordinator Coordinator(
        FakeManager manager,
        Func<Task<WindowCaptureResult>>? pick = null,
        Func<WindowCaptureResult, TargetCaptureContext>? context = null,
        Action<string, Exception?>? report = null) =>
        new(action => action(), () => manager, new FakeOwner(), report ?? ((_, _) => { }),
            pick ?? CancelledPick,
            context ?? (_ => CaptureContext()));

    private static Task<WindowCaptureResult> CancelledPick() =>
        Task.FromResult(new WindowCaptureResult(false, null, "Cancelled."));

    private static WindowCaptureResult CaptureSuccess() =>
        new(true, CapturedWindow(), null);

    private static TargetCaptureContext CaptureContext() =>
        new(CapturedWindow(), new TargetDraft(
            TargetDefinition.Captured("Captured", "captured.exe", "CapturedWindow",
                new[] { ProviderBinding.Claude }, TargetPlacementKind.Desktop,
                TargetActivationPolicy.AlwaysWhileOpen, false, 0),
            "Captured window", Array.Empty<string>()), ProcessInventory.Empty);

    private static WindowSnapshot CapturedWindow() =>
        new(new IntPtr(100), 42, "captured.exe", "CapturedWindow", "Captured window", true);
}
