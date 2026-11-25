using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace UsageBars;

internal sealed record AntigravityBucket(
    string Id,
    double? RemainingFraction,
    double? RemainingAmount,
    DateTimeOffset? ResetTime,
    double? ResetInSeconds,
    bool Disabled)
{
    public double? RemainingPct =>
        RemainingFraction is { } f && double.IsFinite(f)
            ? Math.Clamp(f * 100.0, 0.0, 100.0)
            : null;

    /// <summary>
    /// Active reset timestamp, ignoring the Unix-epoch placeholder Antigravity emits for untouched
    /// full quota pools (reset_time = 1970-01-01T00:00:00Z, reset_in_seconds = 0).
    /// </summary>
    public DateTimeOffset? EffectiveResetsAt
    {
        get
        {
            if (ResetTime is not { } rt || rt <= DateTimeOffset.UnixEpoch) return null;
            if (ResetInSeconds is <= 0) return null;
            return rt;
        }
    }
}

internal sealed record AntigravitySnapshot(
    IReadOnlyDictionary<string, AntigravityBucket> Buckets,
    string? PlanTier,
    string? ModelName,
    DateTimeOffset ReadAt,
    bool Installed = true,
    string? Error = null)
{
    public bool HasValues => Buckets.Values.Any(b => !b.Disabled && b.RemainingPct is not null);

    public AntigravityBucket? Bucket(string id) =>
        Buckets.TryGetValue(id, out var bucket) && !bucket.Disabled ? bucket : null;

    public static AntigravitySnapshot NotInstalled() => new(
        new Dictionary<string, AntigravityBucket>(StringComparer.OrdinalIgnoreCase),
        null, null, DateTimeOffset.Now, Installed: false, Error: "Antigravity CLI not found");

    public static AntigravitySnapshot Failed(string error) => new(
        new Dictionary<string, AntigravityBucket>(StringComparer.OrdinalIgnoreCase),
        null, null, DateTimeOffset.Now, Installed: true, Error: error);
}

internal static class AntigravityUsageParser
{
    /// <summary>
    /// Parses the documented JSON payload piped by <c>agy</c> to a <c>statusLine</c> command.
    /// </summary>
    public static AntigravitySnapshot? Parse(string json, DateTimeOffset readAt)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var buckets = new Dictionary<string, AntigravityBucket>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("quota", out var quota) && quota.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in quota.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                    var obj = prop.Value;

                    double? remFraction = TryDouble(obj, "remaining_fraction");
                    double? remAmount = TryDouble(obj, "remaining_amount");
                    double? resetInSec = TryDouble(obj, "reset_in_seconds");
                    DateTimeOffset? resetTime = TryTimestamp(obj, "reset_time");
                    var disabled = obj.TryGetProperty("disabled", out var dis)
                        && dis.ValueKind == JsonValueKind.True;

                    buckets[prop.Name] = new AntigravityBucket(
                        prop.Name, remFraction, remAmount, resetTime, resetInSec, disabled);
                }
            }

            if (buckets.Count == 0) return null;

            var planTier = TryString(root, "plan_tier");
            string? modelName = null;
            if (root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object)
                modelName = TryString(model, "display_name") ?? TryString(model, "id");

            return new AntigravitySnapshot(buckets, planTier, modelName, readAt);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static double? TryDouble(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number
            && prop.TryGetDouble(out var v) && double.IsFinite(v)
            ? v
            : null;

    private static DateTimeOffset? TryTimestamp(JsonElement obj, string name)
    {
        var text = TryString(obj, name);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return DateTimeOffset.TryParse(
            text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }
}

/// <summary>
/// Reads Antigravity quotas by launching <c>agy</c> in a hidden winpty pseudo-console with a
/// session-only <c>--settings</c> file that attaches a temporary <c>statusLine</c> capture script.
/// Never modifies the user's <c>settings.json</c> and never creates a conversation.
/// </summary>
internal static class AntigravityUsageReader
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    public static AntigravitySnapshot Read()
    {
        var agy = AntigravityCli.Find();
        if (agy is null) return AntigravitySnapshot.NotInstalled();

        try
        {
            var probeRoot = AppPaths.AntigravityProbeDir;
            var workDir = Path.Combine(probeRoot, "work");
            Directory.CreateDirectory(workDir);

            var outFile = Path.Combine(probeRoot, "statusline.json");
            var tmpFile = outFile + ".tmp";
            var capCmd = Path.Combine(probeRoot, "capture-statusline.cmd");
            var settingsFile = Path.Combine(probeRoot, "settings.json");

            try { if (File.Exists(outFile)) File.Delete(outFile); } catch (Exception) { }
            try { if (File.Exists(tmpFile)) File.Delete(tmpFile); } catch (Exception) { }

            // `more` drains stdin to the temp file quickly, then atomic-moves it into place.
            var cmdBody =
                "@echo off\r\n" +
                $"more > \"{tmpFile}\"\r\n" +
                $"move /y \"{tmpFile}\" \"{outFile}\" >nul 2>nul\r\n" +
                "echo ok\r\n";
            File.WriteAllText(capCmd, cmdBody, Encoding.ASCII);

            var settingsJson = JsonSerializer.Serialize(new
            {
                altScreenMode = "never",
                trustedWorkspaces = new[] { workDir },
                statusLine = new
                {
                    type = "command",
                    command = capCmd,
                    enabled = true,
                },
            });
            File.WriteAllText(settingsFile, settingsJson, new UTF8Encoding(false));

            var commandLine = $"\"{agy}\" --settings \"{settingsFile}\"";
            using var session = new WinptySession(commandLine, workDir, cols: 120, rows: 45);
            return Drive(session, outFile);
        }
        catch (Exception ex)
        {
            return AntigravitySnapshot.Failed(ex.Message);
        }
    }

    private static AntigravitySnapshot Drive(WinptySession session, string outFile)
    {
        var deadline = DateTime.UtcNow + Timeout;
        var trustHandled = false;
        var usageSent = false;
        DateTime? usageSentAtUtc = null;
        DateTime lastWriteSeenUtc = DateTime.MinValue;
        AntigravitySnapshot? latest = null;

        while (DateTime.UtcNow < deadline)
        {
            var text = session.Snapshot();

            if (!trustHandled && text.Contains("Do you trust", StringComparison.OrdinalIgnoreCase))
            {
                session.Send("\r");
                trustHandled = true;
                Thread.Sleep(400);
                continue;
            }

            if (TryReadSnapshot(outFile, ref lastWriteSeenUtc, out var parsed) && parsed is not null)
            {
                latest = parsed;
                // Once we have a post-/usage refresh (or at least 2.5s after sending /usage),
                // we have fresh backend quota data and can exit cleanly.
                if (usageSent && usageSentAtUtc is { } sentAt &&
                    (lastWriteSeenUtc > sentAt || DateTime.UtcNow - sentAt >= TimeSpan.FromSeconds(2.5)))
                {
                    return parsed;
                }
            }

            if (!usageSent)
            {
                var ready = latest is not null
                    || text.Contains("shift+tab", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("Antigravity CLI", StringComparison.OrdinalIgnoreCase);

                if (ready && session.QuietFor(TimeSpan.FromMilliseconds(600)))
                {
                    session.Send("/usage\r");
                    usageSent = true;
                    usageSentAtUtc = DateTime.UtcNow;
                }
            }
            else if (latest is not null && usageSentAtUtc is { } sentAt &&
                     DateTime.UtcNow - sentAt >= TimeSpan.FromSeconds(4))
            {
                return latest;
            }

            Thread.Sleep(200);
        }

        if (TryReadSnapshot(outFile, ref lastWriteSeenUtc, out var finalParsed))
            latest = finalParsed;

        return latest ?? AntigravitySnapshot.Failed(
            $"timed out waiting for Antigravity quota (trust={trustHandled} usageSent={usageSent})");
    }

    private static bool TryReadSnapshot(
        string outFile, ref DateTime lastWriteSeenUtc, out AntigravitySnapshot? parsed)
    {
        parsed = null;
        try
        {
            if (!File.Exists(outFile)) return false;
            var writeUtc = File.GetLastWriteTimeUtc(outFile);
            if (writeUtc == lastWriteSeenUtc) return false;
            var raw = File.ReadAllText(outFile, Encoding.UTF8);
            var candidate = AntigravityUsageParser.Parse(raw, DateTimeOffset.Now);
            if (candidate is null) return false;
            lastWriteSeenUtc = writeUtc;
            parsed = candidate;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
