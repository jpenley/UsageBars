using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UsageBars;

internal interface ITargetManagerOwner : IWin32Window, IDisposable { }

internal interface IModelessTargetManager : IDisposable
{
    bool IsDisposed { get; }
    bool Visible { get; }
    FormWindowState WindowState { get; set; }
    event FormClosedEventHandler? FormClosed;
    event EventHandler? AddRequested;
    Guid? SelectedRuleId { get; }
    IReadOnlySet<IntPtr> SuppressedHwnds { get; }
    void Show(IWin32Window owner);
    void Activate();
    void BringToFront();
    void Hide();
    void RestoreBrowseSelection(Guid? selectedRuleId);
    void BeginCaptured(TargetCaptureContext context);
    void RefreshLiveState();
    void Close();
}

internal sealed record TargetCaptureContext(
    WindowSnapshot Window, TargetDraft Draft, ProcessInventory Inventory);

internal sealed class TargetManagerOwnerWindow : NativeWindow, ITargetManagerOwner
{
    private bool _disposed;

    public TargetManagerOwnerWindow()
    {
        CreateHandle(new CreateParams
        {
            Caption = "UsageBars target manager owner",
            ExStyle = Native.WS_EX_TOOLWINDOW,
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DestroyHandle();
        GC.SuppressFinalize(this);
    }
}

internal sealed class TargetManagerCoordinator : IDisposable
{
    private readonly Action<Action> _dispatch;
    private readonly Func<IModelessTargetManager> _factory;
    private readonly ITargetManagerOwner _owner;
    private readonly Func<Task<WindowCaptureResult>> _pick;
    private readonly Func<WindowCaptureResult, TargetCaptureContext> _captureContext;
    private readonly Action<string, Exception?> _report;
    private static readonly IReadOnlySet<IntPtr> EmptySuppressedHwnds = new HashSet<IntPtr>();
    private IModelessTargetManager? _manager;
    private bool _pickActive;
    private bool _disposed;

    public TargetManagerCoordinator(
        Action<Action> dispatch,
        Func<IModelessTargetManager> factory,
        ITargetManagerOwner owner,
        Action<string, Exception?> report,
        Func<Task<WindowCaptureResult>> pick,
        Func<WindowCaptureResult, TargetCaptureContext> captureContext)
    {
        _dispatch = dispatch;
        _factory = factory;
        _owner = owner;
        _report = report;
        _pick = pick;
        _captureContext = captureContext;
    }

    public IReadOnlySet<IntPtr> SuppressedHwnds =>
        _manager is { IsDisposed: false } manager
            ? manager.SuppressedHwnds
            : EmptySuppressedHwnds;

    public void Open()
    {
        if (_disposed) return;
        _report("requested", null);
        try
        {
            _dispatch(OpenOnUiThread);
        }
        catch (Exception ex)
        {
            _report("dispatch-failed", ex);
        }
    }

    private void OpenOnUiThread()
    {
        if (_disposed) return;
        if (_pickActive)
        {
            _report("pick-active", null);
            return;
        }

        if (_manager is { IsDisposed: false } current)
        {
            try
            {
                if (current.WindowState == FormWindowState.Minimized)
                    current.WindowState = FormWindowState.Normal;
                if (!current.Visible) current.Show(_owner);
                current.Activate();
                current.BringToFront();
                _report("activated", null);
            }
            catch (Exception ex)
            {
                // A hidden manager may fail to re-show transiently. Keep the known instance in
                // the slot so a later request can recover it instead of constructing a duplicate.
                _report("activation-failed", ex);
            }
            return;
        }

        IModelessTargetManager? manager = null;
        try
        {
            manager = _factory();
            _manager = manager;
            manager.FormClosed += OnManagerClosed;
            manager.AddRequested += OnAddRequested;
            manager.Show(_owner);
            if (!ReferenceEquals(_manager, manager) || manager.IsDisposed) return;
            manager.Activate();
            manager.BringToFront();
            _report("created", null);
        }
        catch (Exception ex)
        {
            if (manager is not null)
            {
                if (ReferenceEquals(_manager, manager)) _manager = null;
                manager.FormClosed -= OnManagerClosed;
                manager.AddRequested -= OnAddRequested;
                manager.Dispose();
            }
            _report("display-failed", ex);
        }
    }

    private void OnManagerClosed(object? sender, FormClosedEventArgs e)
    {
        if (sender is not IModelessTargetManager manager || !ReferenceEquals(_manager, manager))
            return;

        manager.FormClosed -= OnManagerClosed;
        manager.AddRequested -= OnAddRequested;
        _manager = null;
        _report("closed", null);
    }

    private async void OnAddRequested(object? sender, EventArgs e)
    {
        if (_pickActive
            || sender is not IModelessTargetManager manager
            || !ReferenceEquals(_manager, manager)
            || manager.IsDisposed)
            return;

        _pickActive = true;
        var selectedRuleId = manager.SelectedRuleId;
        try
        {
            manager.Hide();
            var result = await _pick();
            if (!IsCurrent(manager)) return;

            if (!result.Success || result.Window is null)
            {
                if (!IsCancellation(result))
                    _report("capture-failed", new InvalidOperationException(
                        result.Error ?? "The window picker did not return a target."));
                RestoreBrowseSelection(manager, selectedRuleId);
                return;
            }

            var context = _captureContext(result);
            if (!IsCurrent(manager)) return;

            if (!manager.Visible) manager.Show(_owner);
            manager.BeginCaptured(context);
        }
        catch (Exception ex)
        {
            if (!IsCurrent(manager)) return;
            _report("capture-failed", ex);
            RestoreBrowseSelection(manager, selectedRuleId);
        }
        finally
        {
            _pickActive = false;
        }
    }

    private bool IsCurrent(IModelessTargetManager manager) =>
        !_disposed && ReferenceEquals(_manager, manager) && !manager.IsDisposed;

    private static bool IsCancellation(WindowCaptureResult result) =>
        !result.Success && string.Equals(result.Error, "Cancelled.", StringComparison.OrdinalIgnoreCase);

    private void RestoreBrowseSelection(IModelessTargetManager manager, Guid? selectedRuleId)
    {
        try
        {
            if (!IsCurrent(manager)) return;
            if (!manager.Visible) manager.Show(_owner);
            if (!IsCurrent(manager)) return;
            manager.RestoreBrowseSelection(selectedRuleId);
        }
        catch (Exception ex)
        {
            _report("restore-failed", ex);
        }
    }

    public void RefreshLiveState()
    {
        if (_manager is not { IsDisposed: false } manager || _disposed) return;

        try
        {
            manager.RefreshLiveState();
        }
        catch (Exception ex)
        {
            _report("refresh-failed", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var manager = _manager;
        _manager = null;
        if (manager is not null)
        {
            manager.FormClosed -= OnManagerClosed;
            manager.AddRequested -= OnAddRequested;
            manager.Close();
            manager.Dispose();
        }

        _owner.Dispose();
    }
}
