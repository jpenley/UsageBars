using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace UsageBars;

internal enum ProviderId
{
    Claude,
    Codex,
}

internal enum HostKind
{
    /// <summary>The provider's own desktop app frame.</summary>
    Desktop,

    /// <summary>A terminal window with the provider's CLI running under it.</summary>
    Terminal,

    /// <summary>
    /// An IDE's main frame. Distinguished from a terminal because both of the things that follow
    /// from "this is a window running a session" differ for it: it carries a pill whenever it is
    /// open rather than only while a session is detected, and the pill goes on its status bar
    /// rather than its title bar. The configured target determines the placement.
    /// </summary>
    Ide,
}

/// <summary>
/// Display metadata retained by provider pills. Dynamic target definitions own all matching,
/// activation, and placement behavior.
/// </summary>
internal sealed record ProviderTarget(ProviderId Id, string Name)
{
    public static readonly ProviderTarget Claude = new(ProviderId.Claude, "Claude");

    public static readonly ProviderTarget Codex = new(ProviderId.Codex, "Codex");
}

/// <summary>
/// How strongly a window was tied to a provider. The distinction only matters when two providers
/// want the same window, which is routine rather than exotic — the target resolver emits both.
/// </summary>
internal enum HostEvidence
{
    /// <summary>The provider's own desktop frame: class and owning process both matched.</summary>
    Desktop,

    /// <summary>The window title identifies this provider's session. Specific to one window.</summary>
    Title,

    /// <summary>
    /// A CLI process is running somewhere under the window's process. The weakest signal, because
    /// it is answered per <em>process</em>: Windows Terminal hosts every one of its windows in a
    /// single process, so this cannot tell two WT windows apart and says yes to both.
    /// </summary>
    Ancestry,

    /// <summary>
    /// No session was looked for: the window is an IDE frame, and those carry a pill for as long
    /// as they are open. Not a weaker reading of the same signal but a different question — see
    /// configured for an always-open activation policy.
    /// </summary>
    Open,
}

internal sealed record HostWindowInfo(
    IntPtr Hwnd, HostKind Kind, string Description, ProviderId Provider, HostEvidence Evidence,
    double StatusBarHeight = 0, Guid TargetId = default,
    PillAnchor Anchor = PillAnchor.TopCenter, int OffsetX = 0, int OffsetY = 0);

/// <summary>A window that could host some provider's pill, with its owning process resolved.</summary>
internal readonly record struct HostCandidate(IntPtr Hwnd, uint Pid);

/// <summary>
/// Shared native window enumeration primitives used by the target resolver.
/// </summary>
internal static class HostWindowEnumerator
{
    /// <summary>
    /// Reduces a raw z-order list to the windows big and real enough to host any pill, with their
    /// owning process ids. This deliberately remains independent of configured targets.
    /// </summary>
    public static List<HostCandidate> EligibleHosts(List<IntPtr> zOrder, uint ownPid)
    {
        var candidates = new List<HostCandidate>(zOrder.Count);
        foreach (var hWnd in zOrder)
        {
            if (!ZOrder.IsRealVisibleWindow(hWnd)) continue;
            if (!Native.GetWindowRect(hWnd, out var r) || r.Width < 120 || r.Height < 60) continue;

            var pid = Native.ProcessIdOf(hWnd);
            if (pid == 0 || pid == ownPid) continue;

            candidates.Add(new HostCandidate(hWnd, pid));
        }
        return candidates;
    }
}

/// <summary>
/// One snapshot of the running processes, used to answer "is there a CLI session running
/// underneath this window?". Built with Toolhelp so there's no WMI dependency and no per-process
/// handle opening.
/// </summary>
internal sealed class ProcessTree
{
    private readonly Dictionary<uint, (uint Parent, string Name)> _byPid;

    private ProcessTree(Dictionary<uint, (uint, string)> byPid) => _byPid = byPid;

    /// <summary>
    /// Builds a tree from an explicit pid -> (parent, name) map. Lets the host-crediting rules be
    /// tested against real measured process chains without needing those processes to exist.
    /// </summary>
    internal static ProcessTree FromMap(IEnumerable<(uint Pid, uint Parent, string Name)> entries)
    {
        var map = new Dictionary<uint, (uint, string)>();
        foreach (var (pid, parent, name) in entries) map[pid] = (parent, name);
        return new ProcessTree(map);
    }

    public static ProcessTree Capture()
    {
        var map = new Dictionary<uint, (uint, string)>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == INVALID_HANDLE) return new ProcessTree(map);

        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snapshot, ref entry)) return new ProcessTree(map);
            do
            {
                var name = entry.szExeFile ?? "";
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    name = name[..^4];
                map[entry.th32ProcessID] = (entry.th32ParentProcessID, name);
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return new ProcessTree(map);
    }

    public string NameOf(uint pid) => _byPid.TryGetValue(pid, out var e) ? e.Name : "";

    internal IReadOnlyList<ProcessInventoryEntry> InventoryFor(uint ownerPid)
    {
        var rows = new List<ProcessInventoryEntry>();
        foreach (var (pid, entry) in _byPid)
        {
            if (!TryPathFromOwner(ownerPid, pid, out var path)) continue;
            var names = path.Select(pathPid =>
                ProcessInventoryHelpers.NormalizeName(NameOf(pathPid))).ToArray();
            var name = ProcessInventoryHelpers.NormalizeName(entry.Name);
            rows.Add(new ProcessInventoryEntry(
                pid,
                entry.Parent,
                name,
                path.Count - 1,
                string.Join(" > ", names),
                ProcessInventoryHelpers.IsKnownHelper(name)));
        }

        return rows
            .OrderBy(row => row.Depth)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Pid)
            .ToList();
    }

    private bool TryPathFromOwner(uint ownerPid, uint pid, out IReadOnlyList<uint> path)
    {
        const int maxDepth = 64;
        var reversePath = new List<uint>();
        var visited = new HashSet<uint>();
        var current = pid;

        for (var depth = 0; depth < maxDepth; depth++)
        {
            if (!visited.Add(current)) break;
            reversePath.Add(current);
            if (current == ownerPid)
            {
                reversePath.Reverse();
                path = reversePath;
                return true;
            }

            if (!_byPid.TryGetValue(current, out var entry)
                || entry.Parent == 0
                || entry.Parent == current)
                break;
            current = entry.Parent;
        }

        path = Array.Empty<uint>();
        return false;
    }

    /// <summary>Walks up from <paramref name="pid"/>, yielding ancestors nearest-first.</summary>
    private IEnumerable<uint> Ancestors(uint pid, int maxDepth)
    {
        var current = pid;
        for (var depth = 0; depth < maxDepth; depth++)
        {
            if (!_byPid.TryGetValue(current, out var entry)) yield break;
            var parent = entry.Parent;
            // 0 and self-parenting both mean "no further up". Also guard the idle process.
            if (parent == 0 || parent == current) yield break;
            yield return parent;
            current = parent;
        }
    }

    /// <summary>
    /// Processes that must never be credited as the terminal hosting a session. explorer.exe in
    /// particular is an ancestor of virtually every user process, so without this the desktop and
    /// every File Explorer window would qualify. The walk stops on reaching one of these.
    /// </summary>
    private static readonly HashSet<string> NotAHost = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "services", "svchost", "winlogon", "wininit", "csrss", "smss", "lsass",
        "dwm", "fontdrvhost", "sihost", "taskhostw", "ctfmon", "runtimebroker",
        "System", "Idle", "Registry", "Memory Compression",
    };

    /// <summary>
    /// How far up the process tree a session may be credited to a window.
    ///
    /// An integrated terminal is short: <c>claude &lt;- pwsh &lt;- Code</c>. Anything much deeper is
    /// a build-tool chain that happens to share an ancestor, not a session someone is looking at.
    /// Measured failure this guards: <c>codex &lt;- node &lt;- cmd &lt;- node &lt;- node &lt;- cmd
    /// &lt;- firefox</c>, which put the Codex pill on a browser window.
    /// </summary>
    public const int MaxHostDepth = 3;

    /// <summary>
    /// True when a process named <paramref name="processName"/> is running somewhere beneath
    /// <paramref name="hostPid"/>. Descendants of <paramref name="excludePid"/> (our own hidden
    /// probe, and the Codex app-server we spawn) are ignored, so the overlay never reacts to a
    /// session it launched itself.
    /// </summary>
    public bool HasProcessUnder(
        uint hostPid, string processName, uint excludePid, int maxDepth = MaxHostDepth)
    {
        // A generic ancestor like explorer can never be the host, whatever runs beneath it.
        if (NotAHost.Contains(NameOf(hostPid))) return false;

        foreach (var (pid, entry) in _byPid)
        {
            if (!NamesMatch(entry.Name, processName)) continue;

            foreach (var ancestor in Ancestors(pid, maxDepth))
            {
                // Our own probe's session, or a chain that has climbed past any plausible
                // terminal into the shell/service layer: stop without crediting.
                if (ancestor == excludePid) break;
                if (NotAHost.Contains(NameOf(ancestor))) break;
                if (ancestor == hostPid) return true;
            }
        }
        return false;
    }

    private static bool NamesMatch(string processName, string requestedName) =>
        processName.Equals(requestedName, StringComparison.OrdinalIgnoreCase)
        || (ActivityProcessName.TryNormalize(processName, out var normalizedProcess)
            && ActivityProcessName.TryNormalize(requestedName, out var normalizedRequested)
            && normalizedProcess.Equals(normalizedRequested, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when any explicitly configured activity process is a descendant of the host. User
    /// selections may sit deeper than the legacy terminal-crediting limit, but retain the same
    /// own-process, non-host, and cycle safeguards as a single-process lookup.
    /// </summary>
    public bool HasAnyProcessUnder(
        uint hostPid,
        IReadOnlyCollection<string> processNames,
        uint excludePid)
    {
        var normalized = ActivityProcessName.NormalizeMany(processNames)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return normalized.Count != 0
            && normalized.Any(name => HasProcessUnder(hostPid, name, excludePid, maxDepth: 64));
    }

    /// <summary>
    /// True when any matching process is running that isn't our own hidden probe. Used as
    /// corroboration for title-based terminal detection, so a stale window title alone can't
    /// summon the pill.
    /// </summary>
    public bool AnyRunning(string processName, uint excludePid, int maxDepth = 8)
    {
        // Deliberately a deeper walk than HasProcessUnder: here we only need to know whether the
        // process is ours, and our own children can sit several levels down.
        foreach (var (pid, entry) in _byPid)
        {
            if (!entry.Name.Equals(processName, StringComparison.OrdinalIgnoreCase)) continue;

            var isOurs = false;
            foreach (var ancestor in Ancestors(pid, maxDepth))
            {
                if (ancestor == excludePid) { isOurs = true; break; }
            }
            if (!isOurs) return true;
        }
        return false;
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr INVALID_HANDLE = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}

/// <summary>
/// Shared, briefly cached process snapshot. With two providers each resolving a host on every
/// tick, capturing a Toolhelp snapshot per resolver would double the cost for no benefit — the
/// answer is identical for both.
/// </summary>
internal static class ProcessTreeCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(3);
    private static readonly object Sync = new();

    private static ProcessTree? _tree;
    private static DateTime _capturedUtc = DateTime.MinValue;

    public static ProcessTree Current()
    {
        lock (Sync)
        {
            var now = DateTime.UtcNow;
            if (_tree is null || now - _capturedUtc >= Ttl)
            {
                _tree = ProcessTree.Capture();
                _capturedUtc = now;
            }
            return _tree;
        }
    }

    public static void Invalidate()
    {
        lock (Sync)
        {
            _tree = null;
            _capturedUtc = DateTime.MinValue;
        }
    }
}

/// <summary>
/// Decides which windows should carry a particular provider's pill. One instance per provider, so
/// each keeps its own rejection reason.
///
/// There is no memoisation here any more. It existed to keep the rapid-fire location-change events
/// of a window drag from re-enumerating processes for a single chosen host; the answer is now the
/// whole qualifying set, computed once per tick, and the process snapshot behind it is already
/// cached for 3 s in <see cref="ProcessTreeCache"/>.
/// </summary>
internal static class WindowGeometry
{
    private const long WS_CAPTION = 0x00C00000;

    /// <summary>
    /// Screen-pixel rect of the strip the pill should sit in, handling both frame styles:
    ///
    /// - **Real OS caption** (classic console windows): the caption sits above the client area, so
    ///   the strip is window-top to client-top. Placing in the client area would land on the
    ///   terminal's text.
    /// - **Custom/frameless title bar** (Electron, Windows Terminal): the app draws its own bar
    ///   inside the client area, so the strip is the top <paramref name="customTitleBarHeight"/>
    ///   logical pixels of the client.
    ///
    /// Uses the client origin rather than the window rect for the horizontal extent, since
    /// GetWindowRect includes invisible resize borders on the sides.
    /// </summary>
    public static bool TryGetTitleBarStrip(
        IntPtr hWnd, double customTitleBarHeight, out Native.RECT strip, out double scale)
    {
        strip = default;
        scale = 1.0;

        if (!Native.GetClientRect(hWnd, out var client) || client.Width <= 0 || client.Height <= 0)
            return false;
        if (!Native.GetWindowRect(hWnd, out var window)) return false;

        var origin = new Native.POINT { X = 0, Y = 0 };
        if (!Native.ClientToScreen(hWnd, ref origin)) return false;

        var dpi = Native.GetDpiForWindow(hWnd);
        scale = dpi <= 0 ? 1.0 : dpi / 96.0;

        // The top edge of GetWindowRect is accurate (unlike left/right/bottom, which DWM inflates),
        // so the gap to the client origin is the real caption height.
        var captionHeight = origin.Y - window.Top;
        var hasCaption = (Native.GetWindowStyle(hWnd, Native.GWL_STYLE) & WS_CAPTION) != 0;
        var minCaption = (int)Math.Round(8 * scale);

        if (hasCaption && captionHeight >= minCaption)
        {
            strip = new Native.RECT
            {
                Left = origin.X,
                Top = window.Top,
                Right = origin.X + client.Width,
                Bottom = window.Top + captionHeight,
            };
            return true;
        }

        var height = (int)Math.Round(customTitleBarHeight * scale);
        if (height > client.Height) height = client.Height;

        strip = new Native.RECT
        {
            Left = origin.X,
            Top = origin.Y,
            Right = origin.X + client.Width,
            Bottom = origin.Y + height,
        };
        return true;
    }

    /// <summary>
    /// Screen-pixel rect of the bottom <paramref name="statusBarHeight"/> logical pixels of the
    /// client area — the strip an IDE draws its status bar in.
    ///
    /// Measured from the client rect at both ends, never the window rect. DWM inflates the window
    /// rect's bottom edge by the invisible resize border, which for a bottom-anchored strip is not
    /// the cosmetic few pixels it is at the top: it would push the pill below the bar it is meant
    /// to sit in and off the window entirely.
    ///
    /// There is no caption case to handle here. A status bar is always inside the client area, so
    /// unlike <see cref="TryGetTitleBarStrip"/> the frame style makes no difference.
    /// </summary>
    public static bool TryGetStatusBarStrip(
        IntPtr hWnd, double statusBarHeight, out Native.RECT strip, out double scale)
    {
        strip = default;
        scale = 1.0;

        if (!Native.GetClientRect(hWnd, out var client) || client.Width <= 0 || client.Height <= 0)
            return false;

        var origin = new Native.POINT { X = 0, Y = 0 };
        if (!Native.ClientToScreen(hWnd, ref origin)) return false;

        var dpi = Native.GetDpiForWindow(hWnd);
        scale = dpi <= 0 ? 1.0 : dpi / 96.0;

        var height = (int)Math.Round(statusBarHeight * scale);
        if (height > client.Height) height = client.Height;
        if (height <= 0) return false;

        strip = new Native.RECT
        {
            Left = origin.X,
            Top = origin.Y + client.Height - height,
            Right = origin.X + client.Width,
            Bottom = origin.Y + client.Height,
        };
        return true;
    }
}

/// <summary>Where a pill sits horizontally within the space it is allowed to use.</summary>
internal enum PillAlign
{
    /// <summary>Centred in what's left after the right reserve.</summary>
    Centre,

    /// <summary>
    /// Pushed as far right as the reserve allows, so the pill stops just short of the host's own
    /// window controls.
    ///
    /// This is what terminal windows want. A terminal's tabs occupy the middle of the title bar —
    /// exactly where a centred pill lands — so the two collide constantly, and the strip between
    /// the last tab and the minimise button is the one part of the bar that is reliably empty.
    /// </summary>
    Right,
}

/// <summary>
/// Where a pill of a given width sits inside an anchor strip, and whether it fits at all.
///
/// Split out from placement so it can be reasoned about (and tested) without a live window: the
/// two things that go wrong here are drawing over the host's own window controls, and squeezing a
/// pill into a strip too narrow to hold it.
/// </summary>
internal static class OverlayPlacement
{
    /// <summary>Minimum clear space either side of the pill before it is worth showing.</summary>
    public const int MinimumMargin = 24;

    /// <summary>
    /// Top-left position for the pill, or null when the strip is too narrow.
    ///
    /// <paramref name="rightReserve"/> keeps the pill clear of the minimise/maximise/close cluster
    /// (and whatever else the app draws up there). Both alignments work within the space that
    /// leaves rather than the whole strip, so the reserve is never encroached on either way.
    /// </summary>
    public static (int X, int Y)? Calculate(
        Native.RECT strip,
        Native.RECT accessibleBounds,
        int pillWidth,
        int pillHeight,
        int rightReserve,
        int offsetX,
        int offsetY,
        PillAlign align = PillAlign.Centre)
        => CalculateGroupMember(
            strip, accessibleBounds, pillWidth, pillWidth, memberOffset: 0, pillHeight,
            rightReserve, offsetX, offsetY, align);

    /// <summary>
    /// Positions one member of an anchored pill group. Fit, anchor, offset, and clamping are all
    /// evaluated against the complete group rectangle; <paramref name="memberOffset"/> is applied
    /// only after that rectangle has reached its final position.
    /// </summary>
    public static (int X, int Y)? CalculateGroupMember(
        Native.RECT strip,
        Native.RECT accessibleBounds,
        int groupWidth,
        int memberWidth,
        int memberOffset,
        int pillHeight,
        int rightReserve,
        int offsetX,
        int offsetY,
        PillAlign align = PillAlign.Centre)
    {
        if (groupWidth <= 0 || memberWidth <= 0 || pillHeight <= 0
            || memberOffset < 0 || memberOffset + memberWidth > groupWidth)
            return null;

        var usableWidth = strip.Width - Math.Max(0, rightReserve);

        // Centring needs breathing room on both sides. Right-alignment only needs it on the left:
        // the reserve is itself the clearance on the right, and butting up against it is the whole
        // point — that is what puts the pill "just short of the minimise button".
        var required = align == PillAlign.Right
            ? groupWidth + MinimumMargin
            : groupWidth + MinimumMargin * 2;
        if (usableWidth < required) return null;

        var groupX = align == PillAlign.Right
            ? strip.Left + usableWidth - groupWidth + offsetX
            : strip.Left + (usableWidth - groupWidth) / 2 + offsetX;
        var y = strip.Top + (strip.Height - pillHeight) / 2 + offsetY;

        var rightLimit = Math.Min(
            accessibleBounds.Right - groupWidth,
            strip.Right - Math.Max(0, rightReserve) - groupWidth);
        var leftLimit = accessibleBounds.Left;
        var topLimit = accessibleBounds.Top;
        var bottomLimit = accessibleBounds.Bottom - pillHeight;
        if (rightLimit < leftLimit || bottomLimit < topLimit) return null;

        groupX = Math.Clamp(groupX, leftLimit, rightLimit);
        y = Math.Clamp(y, topLimit, bottomLimit);

        return (groupX + memberOffset, y);
    }

    /// <summary>
    /// Legacy geometry entry point for callers that have only a strip. The rendered path always
    /// provides host bounds so target-specific offsets can be kept on-screen.
    /// </summary>
    public static (int X, int Y)? Calculate(
        Native.RECT strip,
        int pillWidth,
        int pillHeight,
        int rightReserve,
        int offsetX,
        int offsetY,
        PillAlign align = PillAlign.Centre) =>
        Calculate(strip, strip, pillWidth, pillHeight, rightReserve, offsetX, offsetY, align);
}
