using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace UsageBars;

internal interface IWindowIntrospection
{
    IntPtr WindowFromPoint(Native.POINT point);
    IntPtr RootOf(IntPtr hwnd);
    WindowSnapshot? SnapshotOf(IntPtr hwnd);
    IReadOnlyList<WindowSnapshot> GetTopLevelWindows();
    bool IsVisibleTopLevel(IntPtr hwnd);
}

internal sealed class NativeWindowIntrospection : IWindowIntrospection
{
    private const uint GaRoot = 2;

    public IntPtr WindowFromPoint(Native.POINT point) => WindowFromPointNative(point);

    public IntPtr RootOf(IntPtr hwnd) => Native.GetAncestor(hwnd, GaRoot);

    public WindowSnapshot? SnapshotOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;

        var pid = Native.ProcessIdOf(hwnd);
        return new WindowSnapshot(
            hwnd, pid, ProcessTreeCache.Current().NameOf(pid), Native.ClassNameOf(hwnd),
            Native.WindowTextOf(hwnd), DesktopFrame.IsResizableFrame(hwnd));
    }

    public IReadOnlyList<WindowSnapshot> GetTopLevelWindows()
    {
        var windows = new List<WindowSnapshot>();
        _ = EnumWindows((hwnd, _) =>
        {
            if (!IsVisibleTopLevel(hwnd)) return true;
            var snapshot = SnapshotOf(hwnd);
            if (snapshot is not null) windows.Add(snapshot);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    public bool IsVisibleTopLevel(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && IsWindowVisible(hwnd) && RootOf(hwnd) == hwnd;

    [DllImport("user32.dll", EntryPoint = "WindowFromPoint")]
    private static extern IntPtr WindowFromPointNative(Native.POINT point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
}

internal sealed record WindowCaptureResult(
    bool Success, WindowSnapshot? Window, string? Error,
    IReadOnlyList<WindowSnapshot>? TopLevelWindows = null);

internal interface IWindowCaptureService
{
    WindowCaptureResult CaptureAt(Native.POINT point);
}

internal sealed class WindowCaptureService : IWindowCaptureService
{
    private static readonly HashSet<string> ForbiddenClasses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
        };

    private readonly IWindowIntrospection _native;
    private readonly uint _ownPid;

    public WindowCaptureService(IWindowIntrospection native, uint ownPid)
    {
        _native = native;
        _ownPid = ownPid;
    }

    public WindowCaptureResult CaptureAt(Native.POINT point)
    {
        var child = _native.WindowFromPoint(point);
        var root = child == IntPtr.Zero ? IntPtr.Zero : _native.RootOf(child);
        var window = root == IntPtr.Zero ? null : _native.SnapshotOf(root);

        if (window is null) return new(false, null, "No top-level window was found.");
        if (window.Pid == _ownPid) return new(false, null, "UsageBars cannot target itself.");
        if (ForbiddenClasses.Contains(window.ClassName))
            return new(false, null, "The Windows taskbar is not a target.");
        if (string.IsNullOrWhiteSpace(window.ProcessName)
            || string.IsNullOrWhiteSpace(window.ClassName))
            return new(false, null, "Executable name and top-level class are required.");

        IReadOnlyList<WindowSnapshot> topLevelWindows;
        try
        {
            topLevelWindows = _native.GetTopLevelWindows()
                .Where(candidate => _native.IsVisibleTopLevel(candidate.Hwnd))
                .ToList();
        }
        catch { topLevelWindows = new[] { window }; }
        return new(true, window, null, topLevelWindows);
    }
}

internal static class ProviderInference
{
    private sealed record ProviderDetection(
        ProviderId Provider, string ProcessName, string? SeedTitlePattern);

    public static TargetDraft CreateDraft(
        WindowSnapshot window, IReadOnlyList<TargetDefinition> existing, ProcessTree tree,
        Config config, uint ownPid, IReadOnlyList<WindowSnapshot>? topLevelWindows = null)
    {
        var matches = existing.Where(target => target.Enabled && target.Matches(window)).ToList();
        var identityMatches = existing.Where(target => target.Matches(window)).ToList();
        var notes = new List<string>();
        var titleMatcher = StableTitleDiscriminator(window, topLevelWindows)
            ?? HighestRanked(identityMatches)?.WindowTitle;
        var alwaysOpen = HighestSpecificityAlwaysOpen(matches);

        if (alwaysOpen.Count > 0)
        {
            var template = alwaysOpen.OrderByDescending(target => target.Revision).First();
            var alwaysProviders = alwaysOpen.SelectMany(target => target.Providers)
                .Select(binding => new ProviderBinding(binding.Provider))
                .DistinctBy(binding => binding.Provider).ToList();
            notes.Add("Matched an always-open target.");
            return Draft(window, alwaysProviders, template.Placement, template.Activation,
                template.StatusBarHeight, notes, titleMatcher);
        }

        if (TryDesktopProvider(window, out var desktopProvider))
        {
            notes.Add($"Detected {desktopProvider.Provider} desktop identity.");
            return Draft(window, new[] { new ProviderBinding(desktopProvider.Provider) }, TargetPlacementKind.Desktop,
                TargetActivationPolicy.AlwaysWhileOpen, 0, notes, titleMatcher);
        }

        var providers = new List<ProviderBinding>();
        var activityProcessNames = new List<string>();
        foreach (var detection in CanonicalCliDetections(config))
        {
            var evidence = EvidenceFor(window, detection, tree, ownPid);
            if (evidence is null) continue;
            providers.Add(new ProviderBinding(detection.Provider));
            activityProcessNames.Add(detection.ProcessName);
            notes.Add($"Detected {detection.Provider} from {evidence.Value} evidence.");
        }

        var activeTemplates = matches.Where(target =>
            target.Activation == TargetActivationPolicy.RequireActivityProcess
            && tree.HasAnyProcessUnder(window.Pid, target.ActivityProcessNames, ownPid)).ToList();
        foreach (var template in activeTemplates)
        {
            providers.AddRange(template.Providers.Select(binding => new ProviderBinding(binding.Provider)));
            activityProcessNames.AddRange(template.ActivityProcessNames);
        }

        if (providers.Count > 0)
        {
            var template = HighestRanked(activeTemplates) ?? HighestRanked(matches);
            if (template is not null)
                notes.Add("Matched a target whose placement was preserved.");
            return Draft(window, providers.DistinctBy(binding => binding.Provider), template?.Placement ?? TargetPlacementKind.Terminal,
                TargetActivationPolicy.RequireActivityProcess, template?.StatusBarHeight ?? 0,
                notes, titleMatcher, ActivityProcessName.NormalizeMany(activityProcessNames));
        }

        notes.Add("No provider detected; choose providers before saving this target.");
        return Draft(window, Array.Empty<ProviderBinding>(), TargetPlacementKind.Desktop,
            TargetActivationPolicy.AlwaysWhileOpen, 0, notes, titleMatcher);
    }

    private static IEnumerable<ProviderDetection> CanonicalCliDetections(Config config) =>
        new[]
        {
            new ProviderDetection(ProviderId.Claude, "claude", config.TerminalTitlePattern),
            new ProviderDetection(ProviderId.Codex, "codex", config.CodexTitlePattern),
        };

    private static List<TargetDefinition> HighestSpecificityAlwaysOpen(
        IReadOnlyList<TargetDefinition> matches)
    {
        if (matches.Count == 0) return new List<TargetDefinition>();
        var specificity = matches.Max(target => target.Specificity);
        return matches
            .Where(target => target.Specificity == specificity
                && target.Activation == TargetActivationPolicy.AlwaysWhileOpen)
            .ToList();
    }

    private static TargetDefinition? HighestRanked(IReadOnlyList<TargetDefinition> matches) =>
        matches.OrderByDescending(target => target.Specificity)
            .ThenByDescending(target => target.Revision)
            .FirstOrDefault();

    private static bool TryDesktopProvider(WindowSnapshot window, out ProviderDetection provider)
    {
        if (window.ClassName.Equals("Chrome_WidgetWin_1", StringComparison.OrdinalIgnoreCase)
            && window.ProcessName.Equals("claude", StringComparison.OrdinalIgnoreCase))
        {
            provider = new(ProviderId.Claude, "claude", null);
            return true;
        }

        if (window.ClassName.Equals("Chrome_WidgetWin_1", StringComparison.OrdinalIgnoreCase)
            && window.ProcessName.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase))
        {
            provider = new(ProviderId.Codex, "codex", null);
            return true;
        }

        provider = null!;
        return false;
    }

    private static HostEvidence? EvidenceFor(
        WindowSnapshot window, ProviderDetection detection, ProcessTree tree, uint ownPid)
    {
        if (!string.IsNullOrWhiteSpace(detection.SeedTitlePattern)
            && SafeTitleMatch(window.Title, detection.SeedTitlePattern!)
            && tree.AnyRunning(detection.ProcessName, ownPid))
            return HostEvidence.Title;

        return tree.HasProcessUnder(window.Pid, detection.ProcessName, ownPid)
            ? HostEvidence.Ancestry
            : null;
    }

    private static bool SafeTitleMatch(string title, string pattern)
    {
        if (string.IsNullOrEmpty(title)) return false;
        try { return Regex.IsMatch(title, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100)); }
        catch (ArgumentException) { return false; }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private static TargetDraft Draft(
        WindowSnapshot window, IEnumerable<ProviderBinding> providers,
        TargetPlacementKind placement, TargetActivationPolicy activation, double statusBarHeight,
        IReadOnlyList<string> notes, TitleRule? titleMatcher,
        IEnumerable<string>? activityProcessNames = null) =>
        new(TargetDefinition.Captured(
                window.ProcessName, window.ProcessName, window.ClassName,
                providers.Select(binding => new ProviderBinding(binding.Provider)),
                placement, activation, window.IsResizable, statusBarHeight, activityProcessNames) with
            {
                WindowTitle = titleMatcher,
            },
            window.Title, notes);

    private static TitleRule? StableTitleDiscriminator(
        WindowSnapshot window, IReadOnlyList<WindowSnapshot>? topLevelWindows)
    {
        if (string.IsNullOrWhiteSpace(window.Title) || topLevelWindows is null) return null;

        var siblings = topLevelWindows
            .Where(candidate => candidate.Hwnd != window.Hwnd
                && candidate.ProcessName.Equals(window.ProcessName, StringComparison.OrdinalIgnoreCase)
                && candidate.ClassName.Equals(window.ClassName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (siblings.Count == 0
            || siblings.Any(candidate => candidate.Title.Equals(window.Title, StringComparison.OrdinalIgnoreCase)))
            return null;

        foreach (var length in WordBoundaryLengths(window.Title))
        {
            var prefix = window.Title[..length].TrimEnd();
            if (prefix.Length == 0) continue;
            if (!siblings.Any(candidate => candidate.Title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                return new(TitleMatchKind.Prefix, prefix);
        }

        return EndsWithVolatileToken(window.Title)
            ? null
            : new(TitleMatchKind.Literal, window.Title);
    }

    private static IEnumerable<int> WordBoundaryLengths(string title)
    {
        for (var index = 1; index < title.Length; index++)
            if (char.IsWhiteSpace(title[index]) || char.IsPunctuation(title[index]))
                yield return index;
    }

    private static bool EndsWithVolatileToken(string title)
    {
        var trimmed = title.TrimEnd();
        var tokenStart = trimmed.Length;
        while (tokenStart > 0 && char.IsLetterOrDigit(trimmed[tokenStart - 1])) tokenStart--;
        return tokenStart < trimmed.Length
            && trimmed[tokenStart..].All(char.IsDigit);
    }
}
