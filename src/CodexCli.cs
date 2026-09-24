using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace UsageBars;

/// <summary>
/// Locates the Codex CLI, which is what exposes the app-server UsageBars talks to.
///
/// Codex ships two ways and both are checked: bundled inside the ChatGPT desktop app, and as a
/// standalone npm-installed CLI. Discovery prefers whatever is already running, since a live
/// process is proof of a working install and gives the exact path without guessing.
/// </summary>
internal static class CodexCli
{
    /// <summary>Where to send someone who doesn't have Codex yet.</summary>
    public const string InstallUrl = "https://developers.openai.com/codex/cli/";

    /// <summary>Pins discovery to one executable, for testing and support.</summary>
    public const string OverrideVariable = "USAGEBARS_CODEX";

    private static string? _appxCache;
    private static bool _appxQueried;
    private static readonly object AppxLock = new();

    /// <summary>
    /// Candidate executables in priority order. The caller tries each until one completes an
    /// app-server handshake, because a file existing is not proof it's the right build.
    /// </summary>
    public static IReadOnlyList<string> Candidates()
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return;
            try
            {
                var full = Path.GetFullPath(candidate);
                if (seen.Add(full) && File.Exists(full)) found.Add(full);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Malformed path; keep looking.
            }
        }

        var overridePath = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            Add(overridePath);
            if (found.Count > 0) return found;
        }

        // 1. A running ChatGPT desktop app: codex sits beside it under app\resources.
        foreach (var exe in RunningExecutables("ChatGPT", requireWindow: true))
        {
            try
            {
                var dir = Path.GetDirectoryName(exe);
                if (dir is null) continue;
                if (!Path.GetFileName(exe).Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (!Path.GetFileName(dir).Equals("app", StringComparison.OrdinalIgnoreCase)) continue;
                Add(Path.Combine(dir, "resources", "codex.exe"));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }

        // 2. A running codex process — its own path is exactly what we want.
        foreach (var exe in RunningExecutables("codex", requireWindow: false))
        {
            if (Path.GetFileName(exe).Equals("codex.exe", StringComparison.OrdinalIgnoreCase))
                Add(exe);
        }

        // 3. PATH. codex.cmd is the npm/nvm launcher shim and is handled by the launcher.
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var trimmed = dir.Trim().Trim('"');
            if (trimmed.Length == 0) continue;
            Add(Path.Combine(trimmed, "codex.exe"));
            Add(Path.Combine(trimmed, "codex.cmd"));
        }

        // 4. The Store package. Costs a PowerShell launch, so it's last and cached.
        if (found.Count == 0)
        {
            var install = AppxInstallLocation();
            if (install is not null) Add(Path.Combine(install, "app", "resources", "codex.exe"));
        }

        return found;
    }

    /// <summary>First candidate, or null when Codex isn't installed. For diagnostics.</summary>
    public static string? Find()
    {
        var candidates = Candidates();
        return candidates.Count > 0 ? candidates[0] : null;
    }

    private static IEnumerable<string> RunningExecutables(string processName, bool requireWindow)
    {
        Process[] processes;
        try { processes = Process.GetProcessesByName(processName); }
        catch (Exception) { yield break; }

        foreach (var process in processes)
        {
            using (process)
            {
                string? path = null;
                try
                {
                    // MainModule throws for processes owned by another user or of another
                    // bitness; those simply aren't usable candidates.
                    if (!requireWindow || process.MainWindowHandle != IntPtr.Zero)
                        path = process.MainModule?.FileName;
                }
                catch (Exception ex)
                    when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                }

                if (!string.IsNullOrWhiteSpace(path)) yield return path;
            }
        }
    }

    /// <summary>
    /// Install location of the Store-packaged Codex, via PowerShell because there's no
    /// non-COM API for it. Queried at most once per run: it costs about a second.
    /// </summary>
    private static string? AppxInstallLocation()
    {
        lock (AppxLock)
        {
            if (_appxQueried) return _appxCache;
            _appxQueried = true;

            try
            {
                var info = new ProcessStartInfo("powershell.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                info.ArgumentList.Add("-NoProfile");
                info.ArgumentList.Add("-NonInteractive");
                info.ArgumentList.Add("-Command");
                info.ArgumentList.Add(
                    "(Get-AppxPackage -Name OpenAI.Codex | Sort-Object Version -Descending | " +
                    "Select-Object -First 1 -ExpandProperty InstallLocation)");

                using var process = Process.Start(info);
                if (process is null) return _appxCache = null;

                var stdout = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(5000))
                {
                    try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                    return _appxCache = null;
                }

                if (process.ExitCode != 0) return _appxCache = null;

                var lines = stdout.GetAwaiter().GetResult()
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries |
                                                 StringSplitOptions.TrimEntries);
                return _appxCache = lines.Length == 1 ? lines[0] : null;
            }
            catch (Exception)
            {
                return _appxCache = null;
            }
        }
    }

    /// <summary>Drops the cached Appx answer so a fresh install is picked up without a restart.</summary>
    public static void InvalidateCache()
    {
        lock (AppxLock)
        {
            _appxQueried = false;
            _appxCache = null;
        }
    }

    /// <summary>
    /// Argument sets to try for one candidate, in order. Older Codex builds want an explicit
    /// <c>--stdio</c>; newer ones default to it and reject the flag. Trying both costs one failed
    /// spawn at worst and avoids pinning UsageBars to a single CLI generation.
    /// </summary>
    public static readonly IReadOnlyList<string[]> ArgumentSets = new[]
    {
        new[] { "app-server", "--stdio" },
        new[] { "app-server" },
    };

    /// <summary>
    /// Start info for <c>codex app-server</c> on a given candidate.
    ///
    /// A <c>.cmd</c> shim has to go through cmd.exe: CreateProcess can launch batch files, but
    /// the child then owns the pipes rather than the shim, which breaks the clean shutdown this
    /// needs. Routing through cmd.exe explicitly keeps one process to wait on and kill.
    /// </summary>
    public static ProcessStartInfo StartInfoFor(string executable, IReadOnlyList<string> arguments)
    {
        var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var isCmd = Path.GetExtension(executable).Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                 || Path.GetExtension(executable).Equals(".bat", StringComparison.OrdinalIgnoreCase);

        var info = new ProcessStartInfo(isCmd ? "cmd.exe" : executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
        };

        if (isCmd)
        {
            info.ArgumentList.Add("/c");
            info.ArgumentList.Add(executable);
        }
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        return info;
    }
}
