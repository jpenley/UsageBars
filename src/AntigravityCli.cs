using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace UsageBars;

/// <summary>
/// Locates the Antigravity CLI (<c>agy</c>) executable across standard Windows installs.
/// </summary>
internal static class AntigravityCli
{
    /// <summary>Where to send someone who doesn't have Antigravity CLI installed yet.</summary>
    public const string InstallUrl = "https://antigravity.google/docs/install";

    /// <summary>Pins discovery to one executable, for testing and support.</summary>
    public const string OverrideVariable = "USAGEBARS_ANTIGRAVITY";

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
            }
        }

        var overridePath = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            Add(overridePath);
            if (found.Count > 0) return found;
        }

        // 1. A running agy process — proves the exact installed path.
        foreach (var exe in RunningExecutables("agy"))
        {
            if (Path.GetFileName(exe).Equals("agy.exe", StringComparison.OrdinalIgnoreCase))
                Add(exe);
        }

        // 2. Standard per-user install location on Windows.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
            Add(Path.Combine(localAppData, "agy", "bin", "agy.exe"));

        // 3. PATH.
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var trimmed = dir.Trim().Trim('"');
            if (trimmed.Length == 0) continue;
            Add(Path.Combine(trimmed, "agy.exe"));
            Add(Path.Combine(trimmed, "agy.cmd"));
        }

        return found;
    }

    public static string? Find()
    {
        var candidates = Candidates();
        return candidates.Count > 0 ? candidates[0] : null;
    }

    private static IEnumerable<string> RunningExecutables(string processName)
    {
        Process[] processes;
        try { processes = Process.GetProcessesByName(processName); }
        catch (Exception) { yield break; }

        foreach (var process in processes)
        {
            using (process)
            {
                string? path = null;
                try { path = process.MainModule?.FileName; }
                catch (Exception ex)
                    when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                }

                if (!string.IsNullOrWhiteSpace(path)) yield return path;
            }
        }
    }
}
