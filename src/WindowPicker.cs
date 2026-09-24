using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace UsageBars;

internal interface IWindowPickerNative
{
    Native.POINT CursorPoint { get; }
    IntPtr InstallMouseHook(Func<int, bool> callback);
    IntPtr InstallKeyboardHook(Func<int, bool> callback);
    bool Unhook(IntPtr hook);
    void SetCrosshair();
    void RestoreCursor();
}

internal sealed class NativeWindowPickerNative : IWindowPickerNative
{
    // These delegates must remain rooted for the entire adapter lifetime, not merely while
    // SetWindowsHookExW is executing.
    private readonly Native.HookProc _mouseHookProc;
    private readonly Native.HookProc _keyboardHookProc;
    private Func<int, bool>? _mouseCallback;
    private Func<int, bool>? _keyboardCallback;
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;
    private IntPtr _previousCursor;
    private bool _crosshairSet;
    private readonly IntPtr _moduleHandle;
    private readonly int _moduleHandleError;

    public NativeWindowPickerNative()
    {
        _mouseHookProc = MouseHookProc;
        _keyboardHookProc = KeyboardHookProc;
        _moduleHandle = Native.GetModuleHandleW(null);
        _moduleHandleError = _moduleHandle == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
    }

    public Native.POINT CursorPoint
    {
        get
        {
            if (!Native.GetCursorPos(out var point))
                throw new InvalidOperationException("Could not read the cursor position.");
            return point;
        }
    }

    public IntPtr InstallMouseHook(Func<int, bool> callback)
    {
        _mouseCallback = callback;
        _mouseHook = Native.SetWindowsHookExW(Native.WH_MOUSE_LL, _mouseHookProc, ModuleHandle(), 0);
        if (_mouseHook == IntPtr.Zero)
        {
            _mouseCallback = null;
            throw HookInstallFailure("mouse");
        }
        return _mouseHook;
    }

    public IntPtr InstallKeyboardHook(Func<int, bool> callback)
    {
        _keyboardCallback = callback;
        _keyboardHook = Native.SetWindowsHookExW(Native.WH_KEYBOARD_LL, _keyboardHookProc, ModuleHandle(), 0);
        if (_keyboardHook == IntPtr.Zero)
        {
            _keyboardCallback = null;
            throw HookInstallFailure("keyboard");
        }
        return _keyboardHook;
    }

    public bool Unhook(IntPtr hook)
    {
        if (hook == IntPtr.Zero) return true;
        var unhooked = Native.UnhookWindowsHookEx(hook);
        if (!unhooked) return false;
        if (hook == _mouseHook)
        {
            _mouseHook = IntPtr.Zero;
            _mouseCallback = null;
        }
        if (hook == _keyboardHook)
        {
            _keyboardHook = IntPtr.Zero;
            _keyboardCallback = null;
        }
        return true;
    }

    public void SetCrosshair()
    {
        var crosshair = Native.LoadCursor(IntPtr.Zero, Native.IDC_CROSS);
        if (!_crosshairSet)
        {
            _previousCursor = Native.SetCursor(crosshair);
            _crosshairSet = true;
            return;
        }

        Native.SetCursor(crosshair);
    }

    public void RestoreCursor()
    {
        if (!_crosshairSet) return;
        var previousCursor = _previousCursor;
        _crosshairSet = false;
        _previousCursor = IntPtr.Zero;
        Native.SetCursor(previousCursor);
    }

    private IntPtr MouseHookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _mouseCallback?.Invoke(wParam.ToInt32()) == true)
            return new IntPtr(1);
        return Native.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private IntPtr KeyboardHookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _keyboardCallback?.Invoke(Marshal.ReadInt32(lParam)) == true)
            return new IntPtr(1);
        return Native.CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private IntPtr ModuleHandle()
    {
        if (_moduleHandle != IntPtr.Zero) return _moduleHandle;
        throw new InvalidOperationException(
            $"Could not get a module handle for window-picker hook installation (Win32 error {_moduleHandleError}).");
    }

    private static InvalidOperationException HookInstallFailure(string hookName) =>
        new($"Could not install the window-picker {hookName} hook (Win32 error {Marshal.GetLastWin32Error()}).");
}

internal sealed class WindowPicker : IDisposable
{
    internal const int LeftButtonDown = Native.WM_LBUTTONDOWN;
    internal const int RightButtonDown = Native.WM_RBUTTONDOWN;
    internal const int MouseMove = Native.WM_MOUSEMOVE;
    internal const int VirtualKeyEscape = Native.VK_ESCAPE;

    private readonly IWindowPickerNative _native;
    private readonly IPickerBanner _banner;
    private readonly IWindowCaptureService _capture;
    private TaskCompletionSource<WindowCaptureResult>? _pending;
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;
    private bool _disposed;

    public WindowPicker(IWindowPickerNative native, IPickerBanner banner, IWindowCaptureService capture)
    {
        _native = native;
        _banner = banner;
        _capture = capture;
    }

    internal bool IsActive => _pending is not null;

    public Task<WindowCaptureResult> PickAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WindowPicker));
        if (_pending is not null) throw new InvalidOperationException("A window pick is already active.");

        RetryOutstandingHooks();
        if (_mouseHook != IntPtr.Zero || _keyboardHook != IntPtr.Zero)
        {
            return Task.FromResult(new WindowCaptureResult(
                false, null, "Could not clean up the previous window-picker hooks."));
        }

        var completion = new TaskCompletionSource<WindowCaptureResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = completion;
        try
        {
            _banner.Show();
            _native.SetCrosshair();
            _mouseHook = _native.InstallMouseHook(OnMouse);
            _keyboardHook = _native.InstallKeyboardHook(OnKeyboard);
            if (_mouseHook == IntPtr.Zero || _keyboardHook == IntPtr.Zero)
                Finish(new WindowCaptureResult(false, null, "Could not install the window-picker hooks."));
        }
        catch (Exception ex)
        {
            Finish(new WindowCaptureResult(false, null, ex.Message));
        }

        return completion.Task;
    }

    private bool OnMouse(int message)
    {
        if (_pending is null) return false;

        if (message == MouseMove)
        {
            _native.SetCrosshair();
            return false;
        }

        if (message == RightButtonDown)
        {
            Finish(new WindowCaptureResult(false, null, "Cancelled."));
            return true;
        }

        if (message != LeftButtonDown) return false;

        WindowCaptureResult capture;
        try
        {
            capture = _capture.CaptureAt(_native.CursorPoint);
        }
        catch (Exception ex)
        {
            _banner.ShowMessage(ex.Message);
            return true;
        }

        if (!capture.Success)
        {
            _banner.ShowMessage(capture.Error ?? "Choose a top-level application window.");
            return true;
        }

        Finish(capture);
        return true;
    }

    private bool OnKeyboard(int virtualKey)
    {
        if (_pending is null) return false;
        if (virtualKey != VirtualKeyEscape) return false;
        Finish(new WindowCaptureResult(false, null, "Cancelled."));
        return true;
    }

    private void Finish(WindowCaptureResult result)
    {
        var completion = _pending;
        if (completion is null) return;

        _pending = null;
        RetryOutstandingHooks();
        TryRestoreCursor();
        TryHideBanner();
        completion.TrySetResult(result);
    }

    private void TryUnhook(ref IntPtr hook)
    {
        if (hook == IntPtr.Zero) return;
        try
        {
            if (_native.Unhook(hook)) hook = IntPtr.Zero;
        }
        catch { }
    }

    private void RetryOutstandingHooks()
    {
        TryUnhook(ref _mouseHook);
        TryUnhook(ref _keyboardHook);
    }

    private void TryRestoreCursor()
    {
        try { _native.RestoreCursor(); }
        catch { }
    }

    private void TryHideBanner()
    {
        try { _banner.Hide(); }
        catch { }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_pending is not null)
                Finish(new WindowCaptureResult(false, null, "UsageBars is closing."));
            try { _banner.Dispose(); }
            catch { }
        }

        RetryOutstandingHooks();
    }
}
