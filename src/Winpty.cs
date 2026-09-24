using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace UsageBars;

/// <summary>
/// A hidden pseudo-console driven through winpty's C API. Needed because Claude Code only runs
/// its interactive UI when it believes it has a real TTY; a hand-rolled ConPTY makes it fall
/// back to --print mode and exit.
/// </summary>
internal sealed class WinptySession : IDisposable
{
    private readonly StringBuilder _buffer = new();
    private readonly object _gate = new();
    private DateTime _lastGrowthUtc = DateTime.UtcNow;

    private readonly IntPtr _wp;
    private readonly SafeFileHandle _outHandle;
    private readonly SafeFileHandle _inHandle;
    private readonly FileStream _outStream;
    private readonly FileStream _inStream;
    private readonly Thread _reader;
    private bool _disposed;

    public WinptySession(string commandLine, string workingDir, int cols, int rows)
    {
        WinptyNative.EnsureLoaded();

        var cfg = WinptyNative.winpty_config_new(0, out var err);
        if (cfg == IntPtr.Zero) throw Error("winpty_config_new", err);
        try
        {
            WinptyNative.winpty_config_set_initial_size(cfg, cols, rows);
            _wp = WinptyNative.winpty_open(cfg, out err);
        }
        finally
        {
            WinptyNative.winpty_config_free(cfg);
        }
        if (_wp == IntPtr.Zero) throw Error("winpty_open", err);

        try
        {
            var conin = Marshal.PtrToStringUni(WinptyNative.winpty_conin_name(_wp))!;
            var conout = Marshal.PtrToStringUni(WinptyNative.winpty_conout_name(_wp))!;
            _outHandle = OpenPipe(conout, read: true);
            _inHandle = OpenPipe(conin, read: false);
            _outStream = new FileStream(_outHandle, FileAccess.Read);
            _inStream = new FileStream(_inHandle, FileAccess.Write);

            var spawnCfg = WinptyNative.winpty_spawn_config_new(
                WinptyNative.WINPTY_SPAWN_FLAG_AUTO_SHUTDOWN,
                null, commandLine, workingDir, null, out err);
            if (spawnCfg == IntPtr.Zero) throw Error("winpty_spawn_config_new", err);

            bool ok;
            uint createProcessError;
            try
            {
                ok = WinptyNative.winpty_spawn(_wp, spawnCfg, out _, out _, out createProcessError, out err);
            }
            finally
            {
                WinptyNative.winpty_spawn_config_free(spawnCfg);
            }
            if (!ok) throw Error($"winpty_spawn (CreateProcess error {createProcessError})", err);

            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "winpty-read" };
            _reader.Start();
        }
        catch
        {
            WinptyNative.winpty_free(_wp);
            throw;
        }
    }

    private void ReadLoop()
    {
        var bytes = new byte[8192];
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[8192];
        try
        {
            int n;
            while ((n = _outStream.Read(bytes, 0, bytes.Length)) > 0)
            {
                var c = decoder.GetChars(bytes, 0, n, chars, 0);
                lock (_gate)
                {
                    _buffer.Append(chars, 0, c);
                    _lastGrowthUtc = DateTime.UtcNow;
                }
            }
        }
        catch (Exception)
        {
            // Pipe closed during teardown.
        }
    }

    /// <summary>Everything received so far, with ANSI escape sequences removed.</summary>
    public string Snapshot()
    {
        string raw;
        lock (_gate) raw = _buffer.ToString();
        return Ansi.Strip(raw);
    }

    public void Send(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        try
        {
            _inStream.Write(bytes, 0, bytes.Length);
            _inStream.Flush();
        }
        catch (Exception)
        {
            // Child gone; the caller's timeout will handle it.
        }
    }

    /// <summary>True when no new output has arrived for <paramref name="quiet"/>.</summary>
    public bool QuietFor(TimeSpan quiet)
    {
        DateTime last;
        lock (_gate) last = _lastGrowthUtc;
        return DateTime.UtcNow - last >= quiet;
    }

    private static Exception Error(string where, IntPtr err)
    {
        var msg = err == IntPtr.Zero
            ? ""
            : Marshal.PtrToStringUni(WinptyNative.winpty_error_msg(err)) ?? "";
        if (err != IntPtr.Zero) WinptyNative.winpty_error_free(err);
        return new InvalidOperationException($"{where}: {msg}");
    }

    private static SafeFileHandle OpenPipe(string name, bool read)
    {
        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_RW = 0x1 | 0x2;
        const uint OPEN_EXISTING = 3;
        var h = WinptyNative.CreateFileW(name, read ? GENERIC_READ : GENERIC_WRITE,
            FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid)
            throw new IOException($"opening {name} failed: {Marshal.GetLastWin32Error()}");
        return h;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // AUTO_SHUTDOWN makes winpty_free terminate the child process.
        try { WinptyNative.winpty_free(_wp); } catch (Exception) { }
        try { _inStream.Dispose(); } catch (Exception) { }
        try { _outStream.Dispose(); } catch (Exception) { }
    }
}

internal static class Ansi
{
    private static readonly System.Text.RegularExpressions.Regex Pattern = new(
        @"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(\x07|\x1B\\)|\x1B[()][A-Za-z]|\x1B[=>]|\x1B.",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public static string Strip(string s) => Pattern.Replace(s, "");
}

internal static class WinptyNative
{
    public const ulong WINPTY_SPAWN_FLAG_AUTO_SHUTDOWN = 1;

    private static bool _loaded;
    private static readonly object Gate = new();

    /// <summary>
    /// Extracts the embedded winpty binaries next to each other and points the loader at them.
    /// winpty.dll launches winpty-agent.exe from its own directory, so both must land together.
    /// </summary>
    public static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_loaded) return;

            var dir = AppPaths.WinptyDir;
            Directory.CreateDirectory(dir);

            Extract("winpty.dll", Path.Combine(dir, "winpty.dll"));
            Extract("winpty-agent.exe", Path.Combine(dir, "winpty-agent.exe"));

            if (!SetDllDirectory(dir))
                throw new InvalidOperationException($"SetDllDirectory({dir}) failed");

            _loaded = true;
        }
    }

    private static void Extract(string resourceLeaf, string destination)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = Array.Find(asm.GetManifestResourceNames(),
            n => n.EndsWith(resourceLeaf, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"embedded resource {resourceLeaf} missing");

        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"could not open resource {name}");

        // Skip the write when an identical copy is already in place, so a running
        // winpty.dll is never overwritten underneath us.
        if (File.Exists(destination))
        {
            try
            {
                if (new FileInfo(destination).Length == stream.Length) return;
            }
            catch (Exception)
            {
                // Fall through and try to rewrite.
            }
        }

        try
        {
            using var file = File.Create(destination);
            stream.CopyTo(file);
        }
        catch (IOException)
        {
            // Locked by another UsageBars instance: an existing copy is good enough.
            if (!File.Exists(destination)) throw;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string lpPathName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFileW(string name, uint access, uint share,
        IntPtr securityAttributes, uint disposition, uint flags, IntPtr template);

    [DllImport("winpty.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr winpty_config_new(ulong agentFlags, out IntPtr err);

    [DllImport("winpty.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void winpty_config_set_initial_size(IntPtr cfg, int cols, int rows);

    [DllImport("winpty.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void winpty_config_free(IntPtr cfg);

    [DllImport("winpty.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr winpty_open(IntPtr cfg, out IntPtr err);

    [DllImport("winpty.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr winpty_conin_name(IntPtr wp);

    [DllImport("winpty.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr winpty_conout_name(IntPtr wp);

    [DllImport("winpty.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    public static extern IntPtr winpty_spawn_config_new(ulong flags, string? appname,
        string cmdline, string? cwd, string? env, out IntPtr err);

    [DllImport("winpty.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void winpty_spawn_config_free(IntPtr cfg);

    [DllImport("winpty.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern bool winpty_spawn(IntPtr wp, IntPtr cfg, out IntPtr process,
        out IntPtr thread, out uint createProcessError, out IntPtr err);

    [DllImport("winpty.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void winpty_free(IntPtr wp);

    [DllImport("winpty.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr winpty_error_msg(IntPtr err);

    [DllImport("winpty.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void winpty_error_free(IntPtr err);
}
