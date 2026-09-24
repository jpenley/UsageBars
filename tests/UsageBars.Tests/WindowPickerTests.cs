using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Xunit;

namespace UsageBars.Tests;

public class WindowPickerTests
{
    [Fact]
    public async Task EscapeCancelsAndAlwaysCleansUp()
    {
        var native = new FakePickerNative();
        var banner = new FakeBanner();
        using var picker = new WindowPicker(native, banner, FakeCapture.Success());

        var pending = picker.PickAsync();
        var consumed = native.RaiseKeyDown(WindowPicker.VirtualKeyEscape);
        var result = await pending;

        Assert.True(consumed);
        Assert.False(result.Success);
        Assert.Equal(2, native.UnhookCount);
        Assert.False(banner.Visible);
        Assert.True(native.CursorRestored);
    }

    [Fact]
    public async Task LeftClickIsConsumedAndReturnsTheCapturedRoot()
    {
        var native = new FakePickerNative { CursorPoint = new Native.POINT { X = 10, Y = 20 } };
        using var picker = new WindowPicker(native, new FakeBanner(), FakeCapture.Success());

        var pending = picker.PickAsync();
        var consumed = native.RaiseMouseDown(WindowPicker.LeftButtonDown);
        var result = await pending;

        Assert.True(consumed);
        Assert.True(result.Success);
        Assert.Equal(new IntPtr(42), result.Window!.Hwnd);
        Assert.Equal(2, native.UnhookCount);
        Assert.True(native.CursorRestored);
    }

    [Fact]
    public async Task RightClickCancelsAndCleansUp()
    {
        var native = new FakePickerNative();
        var banner = new FakeBanner();
        using var picker = new WindowPicker(native, banner, FakeCapture.Success());

        var pending = picker.PickAsync();
        var consumed = native.RaiseMouseDown(WindowPicker.RightButtonDown);
        var result = await pending;

        Assert.True(consumed);
        Assert.False(result.Success);
        Assert.Equal(2, native.UnhookCount);
        Assert.False(banner.Visible);
        Assert.True(native.CursorRestored);
    }

    [Fact]
    public void ASecondPickerCannotStartWhileOneIsActive()
    {
        using var picker = new WindowPicker(
            new FakePickerNative(), new FakeBanner(), FakeCapture.Success());

        _ = picker.PickAsync();

        Action startSecondPicker = () => { _ = picker.PickAsync(); };
        Assert.Throws<InvalidOperationException>(startSecondPicker);
    }

    [Fact]
    public void ClickingAnIgnoredWindowKeepsPickerModeActive()
    {
        var native = new FakePickerNative();
        var banner = new FakeBanner();
        using var picker = new WindowPicker(native, banner, FakeCapture.Failure("UsageBars cannot target itself."));

        _ = picker.PickAsync();
        var consumed = native.RaiseMouseDown(WindowPicker.LeftButtonDown);

        Assert.True(consumed);
        Assert.True(picker.IsActive);
        Assert.Contains("cannot target itself", banner.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task KeyboardHookInstallationFailureCleansUpTheMouseHookAndBanner()
    {
        var native = new FakePickerNative { KeyboardHookHandle = IntPtr.Zero };
        var banner = new FakeBanner();
        using var picker = new WindowPicker(native, banner, FakeCapture.Success());

        var result = await picker.PickAsync();

        Assert.False(result.Success);
        Assert.Equal(1, native.UnhookCount);
        Assert.False(banner.Visible);
        Assert.True(native.CursorRestored);
    }

    [Fact]
    public async Task DisposingAnActivePickerCancelsAndCleansUp()
    {
        var native = new FakePickerNative();
        var banner = new FakeBanner();
        var picker = new WindowPicker(native, banner, FakeCapture.Success());

        var pending = picker.PickAsync();
        picker.Dispose();
        var result = await pending;

        Assert.False(result.Success);
        Assert.Equal(2, native.UnhookCount);
        Assert.False(banner.Visible);
        Assert.True(native.CursorRestored);
        Assert.True(banner.Disposed);
    }

    [Fact]
    public void LowLevelKeyboardHookPassesTheVirtualKeyFromKeyboardData()
    {
        var native = new NativeWindowPickerNative();
        var callbackField = typeof(NativeWindowPickerNative).GetField(
            "_keyboardCallback", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var hookMethod = typeof(NativeWindowPickerNative).GetMethod(
            "KeyboardHookProc", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var receivedVirtualKey = 0;
        callbackField.SetValue(native, (Func<int, bool>)(virtualKey =>
        {
            receivedVirtualKey = virtualKey;
            return true;
        }));

        var keyboardData = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(keyboardData, WindowPicker.VirtualKeyEscape);

            var result = (IntPtr)hookMethod.Invoke(native, new object[]
            {
                0, new IntPtr(Native.WM_KEYDOWN), keyboardData,
            })!;

            Assert.Equal(new IntPtr(1), result);
            Assert.Equal(WindowPicker.VirtualKeyEscape, receivedVirtualKey);
        }
        finally
        {
            Marshal.FreeHGlobal(keyboardData);
        }
    }

    [Fact]
    public async Task CleanupAttemptsBothHooksAndCompletesWhenMouseUnhookThrows()
    {
        var native = new FakePickerNative { ThrowWhenUnhooking = new IntPtr(1) };
        var banner = new FakeBanner();
        using var picker = new WindowPicker(native, banner, FakeCapture.Success());

        var pending = picker.PickAsync();
        var exception = Record.Exception(() => native.RaiseKeyDown(WindowPicker.VirtualKeyEscape));
        var result = await pending;

        Assert.Null(exception);
        Assert.Contains(new IntPtr(1), native.UnhookedHooks);
        Assert.Contains(new IntPtr(2), native.UnhookedHooks);
        Assert.False(result.Success);
        Assert.False(banner.Visible);
        Assert.True(native.CursorRestored);
    }

    [Fact]
    public async Task FailedUnhookLeavesSurvivingCallbacksInertAndRetriesWhenDisposed()
    {
        var native = new FakePickerNative { UnhookSucceeds = false };
        var picker = new WindowPicker(native, new FakeBanner(), FakeCapture.Success());

        var pending = picker.PickAsync();
        Assert.True(native.RaiseKeyDown(WindowPicker.VirtualKeyEscape));
        await pending;

        Assert.False(native.RaiseMouseDown(WindowPicker.LeftButtonDown));
        Assert.False(native.RaiseMouseDown(WindowPicker.RightButtonDown));
        Assert.False(native.RaiseKeyDown(WindowPicker.VirtualKeyEscape));

        native.UnhookSucceeds = true;
        picker.Dispose();

        Assert.Equal(4, native.UnhookCount);
    }

    [Fact]
    public async Task MouseMoveReappliesCrosshairOnlyWhilePicking()
    {
        var native = new FakePickerNative();
        using var picker = new WindowPicker(native, new FakeBanner(), FakeCapture.Success());

        var pending = picker.PickAsync();
        Assert.False(native.RaiseMouseDown(WindowPicker.MouseMove));
        Assert.Equal(2, native.CrosshairSetCount);

        native.RaiseKeyDown(WindowPicker.VirtualKeyEscape);
        await pending;
        native.RaiseMouseDown(WindowPicker.MouseMove);

        Assert.Equal(2, native.CrosshairSetCount);
        Assert.Equal(1, native.CursorRestoreCount);
    }

    [Fact]
    public async Task NewPickRetriesRetainedHooksBeforeInstallingDistinctReplacementHandles()
    {
        var native = new FakePickerNative { UnhookSucceeds = false };
        using var picker = new WindowPicker(native, new FakeBanner(), FakeCapture.Success());

        var firstPick = picker.PickAsync();
        native.RaiseKeyDown(WindowPicker.VirtualKeyEscape);
        await firstPick;

        var blockedTask = picker.PickAsync();

        Assert.True(blockedTask.IsCompleted);
        var blockedPick = await blockedTask;
        Assert.False(blockedPick.Success);
        Assert.Equal(1, native.MouseHookInstallCount);
        Assert.Equal(1, native.KeyboardHookInstallCount);
        Assert.Equal(new[] { new IntPtr(1), new IntPtr(2), new IntPtr(1), new IntPtr(2) }, native.UnhookedHooks);

        native.UnhookSucceeds = true;
        native.MouseHookHandle = new IntPtr(3);
        native.KeyboardHookHandle = new IntPtr(4);
        var replacementPick = picker.PickAsync();
        native.RaiseKeyDown(WindowPicker.VirtualKeyEscape);
        await replacementPick;

        Assert.Equal(2, native.MouseHookInstallCount);
        Assert.Equal(2, native.KeyboardHookInstallCount);
        Assert.Contains(new IntPtr(3), native.UnhookedHooks);
        Assert.Contains(new IntPtr(4), native.UnhookedHooks);
    }

    [Fact]
    public async Task DisposedPickerRejectsNewPicksAndRetriesFailedCleanupOnEveryDispose()
    {
        var native = new FakePickerNative { UnhookSucceeds = false };
        var picker = new WindowPicker(native, new FakeBanner(), FakeCapture.Success());

        var pending = picker.PickAsync();
        native.RaiseKeyDown(WindowPicker.VirtualKeyEscape);
        await pending;

        picker.Dispose();
        picker.Dispose();

        Assert.Equal(6, native.UnhookCount);
        Action startDisposedPicker = () => { _ = picker.PickAsync(); };
        Assert.Throws<ObjectDisposedException>(startDisposedPicker);

        native.UnhookSucceeds = true;
        picker.Dispose();

        Assert.Equal(8, native.UnhookCount);
    }

    private sealed class FakePickerNative : IWindowPickerNative
    {
        private Func<int, bool>? _mouse;
        private Func<int, bool>? _keyboard;

        public Native.POINT CursorPoint { get; init; }
        public IntPtr MouseHookHandle { get; set; } = new(1);
        public IntPtr KeyboardHookHandle { get; set; } = new(2);
        public IntPtr ThrowWhenUnhooking { get; init; }
        public bool UnhookSucceeds { get; set; } = true;
        public int UnhookCount { get; private set; }
        public List<IntPtr> UnhookedHooks { get; } = new();
        public bool CursorRestored { get; private set; }
        public int CrosshairSetCount { get; private set; }
        public int CursorRestoreCount { get; private set; }
        public int MouseHookInstallCount { get; private set; }
        public int KeyboardHookInstallCount { get; private set; }

        public IntPtr InstallMouseHook(Func<int, bool> callback)
        {
            MouseHookInstallCount++;
            _mouse = callback;
            return MouseHookHandle;
        }

        public IntPtr InstallKeyboardHook(Func<int, bool> callback)
        {
            KeyboardHookInstallCount++;
            _keyboard = callback;
            return KeyboardHookHandle;
        }

        public bool Unhook(IntPtr hook)
        {
            UnhookCount++;
            UnhookedHooks.Add(hook);
            if (hook == ThrowWhenUnhooking) throw new InvalidOperationException("Unhook failed.");
            return UnhookSucceeds;
        }
        public void SetCrosshair() => CrosshairSetCount++;
        public void RestoreCursor()
        {
            CursorRestored = true;
            CursorRestoreCount++;
        }
        public bool RaiseMouseDown(int message) => _mouse?.Invoke(message) ?? false;
        public bool RaiseKeyDown(int virtualKey) => _keyboard?.Invoke(virtualKey) ?? false;
    }

    private sealed class FakeBanner : IPickerBanner
    {
        public bool Visible { get; private set; }
        public bool Disposed { get; private set; }
        public string Message { get; private set; } = string.Empty;

        public void Show() => Visible = true;
        public void ShowMessage(string message) => Message = message;
        public void Hide() => Visible = false;
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeCapture : IWindowCaptureService
    {
        private readonly WindowCaptureResult _result;

        private FakeCapture(WindowCaptureResult result) => _result = result;

        public static FakeCapture Success() => new(new WindowCaptureResult(
            true, new WindowSnapshot(new IntPtr(42), 7, "rider64", "SunAwtFrame", "Project", true), null));

        public static FakeCapture Failure(string error) => new(new WindowCaptureResult(false, null, error));

        public WindowCaptureResult CaptureAt(Native.POINT point) => _result;
    }
}
