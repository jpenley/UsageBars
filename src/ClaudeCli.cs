using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UsageBars;

/// <summary>Locates the Claude Code CLI executable across the ways it can be installed.</summary>
internal static class ClaudeCli
{
    /// <summary>Where to send someone who doesn't have Claude Code yet.</summary>
    public const string InstallUrl = "https://docs.claude.com/en/docs/claude-code/setup";

    /// <summary>
    /// Full path to a launchable claude.exe, or null if not installed. Prefers the versioned
    /// native build over the <c>~/.local/bin</c> shim: the shim re-launches the real binary in
    /// its own console, which a pseudo-console can't observe.
    /// </summary>
    public static string? Find()
    {
        var overridePath = Environment.GetEnvironmentVariable("USAGEBARS_CLAUDE");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;

        foreach (var candidate in Candidates())
        {
            try
            {
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch (Exception)
            {
                // Malformed path; keep looking.
            }
        }
        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        // 1. Versioned native builds, newest first — these are directly launchable.
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var exe in VersionedBuilds(Path.Combine(roaming, "Claude", "claude-code")))
            yield return exe;

        // 2. Same, under the MSIX package's redirected Roaming.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var packages = Path.Combine(localAppData, "Packages");
        if (Directory.Exists(packages))
        {
            IEnumerable<string> pkgDirs;
            try { pkgDirs = Directory.EnumerateDirectories(packages, "Claude_*"); }
            catch (Exception) { pkgDirs = Array.Empty<string>(); }

            foreach (var dir in pkgDirs)
                foreach (var exe in VersionedBuilds(
                    Path.Combine(dir, "LocalCache", "Roaming", "Claude", "claude-code")))
                    yield return exe;
        }

        // 3. The native-install shim, as a last resort.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
            yield return Path.Combine(home, ".local", "bin", "claude.exe");

        // 4. Anything on PATH.
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            yield return Path.Combine(dir.Trim(), "claude.exe");
        }
    }

    private static IEnumerable<string> VersionedBuilds(string root)
    {
        if (!Directory.Exists(root)) yield break;

        string[] dirs;
        try { dirs = Directory.GetDirectories(root); }
        catch (Exception) { yield break; }

        var ordered = dirs
            .Select(d => (Dir: d, Ver: ParseVersion(Path.GetFileName(d))))
            .OrderByDescending(x => x.Ver)
            .Select(x => x.Dir);

        foreach (var dir in ordered)
            yield return Path.Combine(dir, "claude.exe");
    }

    private static Version ParseVersion(string name) =>
        Version.TryParse(name, out var v) ? v : new Version(0, 0);
}
