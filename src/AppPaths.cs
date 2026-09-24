using System;
using System.IO;

namespace UsageBars;

/// <summary>
/// Where UsageBars keeps its working files.
///
/// Portable first: everything lives in a <c>UsageBars.data</c> folder beside the exe, so the whole
/// thing can be copied to a USB stick or a network share and leave nothing behind. Only when that
/// location isn't writable (Program Files, a read-only share) does it fall back to
/// <c>%LOCALAPPDATA%\UsageBars</c>.
/// </summary>
internal static class AppPaths
{
    private const string PortableFolderName = "UsageBars.data";

    /// <summary>Root for all working files. Created on first use.</summary>
    public static string DataRoot { get; }

    /// <summary>True when working files sit beside the exe rather than in the user profile.</summary>
    public static bool IsPortable { get; }

    /// <summary>Why the portable location was rejected, when it was.</summary>
    public static string? PortableRejectedBecause { get; }

    static AppPaths()
    {
        // An explicit override wins, for testing and for pinning state somewhere specific.
        var overridden = Environment.GetEnvironmentVariable("USAGEBARS_DATA");
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            try
            {
                Directory.CreateDirectory(overridden);
                DataRoot = Path.GetFullPath(overridden);
                IsPortable = true;
                return;
            }
            catch (Exception ex)
            {
                PortableRejectedBecause = $"USAGEBARS_DATA unusable: {ex.Message}";
            }
        }

        var exeDir = ExeDirectory();
        if (exeDir is not null)
        {
            var candidate = Path.Combine(exeDir, PortableFolderName);
            if (TryPrepare(candidate, out var why))
            {
                DataRoot = candidate;
                IsPortable = true;
                return;
            }
            PortableRejectedBecause ??= why;
        }
        else
        {
            PortableRejectedBecause ??= "could not determine the exe directory";
        }

        DataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsageBars");
        IsPortable = false;
        try { Directory.CreateDirectory(DataRoot); } catch (Exception) { }
    }

    public static string? ExeDirectory()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return null;
            var dir = Path.GetDirectoryName(exe);
            return string.IsNullOrEmpty(dir) ? null : dir;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Creates the folder and proves it's actually writable, not merely creatable.</summary>
    private static bool TryPrepare(string path, out string? why)
    {
        why = null;
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, ".writable");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            why = $"{path} not writable: {ex.Message}";
            return false;
        }
    }

    /// <summary>Native winpty binaries, extracted here at runtime.</summary>
    public static string WinptyDir => Path.Combine(DataRoot, "winpty");

    /// <summary>Rolling meter readings that feed the burn-rate projection.</summary>
    public static string HistoryFile => Path.Combine(DataRoot, "usage-history.json");

    /// <summary>Persisted window targets and their configured activation rules.</summary>
    public static string TargetsFile => Path.Combine(DataRoot, "targets.json");

    /// <summary>
    /// Per-bar readings for windows that report no history of their own — the Codex app-server's,
    /// principally. See <see cref="BarHistory"/>.
    /// </summary>
    public static string BarHistoryFile => Path.Combine(DataRoot, "bar-history.json");

    /// <summary>
    /// Stable working directory for the hidden Claude Code session, so its folder-trust prompt is
    /// only answered once.
    /// </summary>
    public static string ProbeDir => Path.Combine(DataRoot, "probe");

    /// <summary>Fallback config location, used when the exe directory isn't writable.</summary>
    public static string FallbackConfigFile => Path.Combine(DataRoot, "config.json");

    /// <summary>Rotating JSONL diagnostics. Bounded, so it stays inside the portable folder.</summary>
    public static string DiagnosticsDir => Path.Combine(DataRoot, "diagnostics");
}
