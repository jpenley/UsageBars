using System;
using System.Collections.Generic;
using System.Text.Json;

namespace UsageBars;

/// <summary>
/// One Codex rate-limit window. Percentages are <em>utilization</em> (0-100), matching the wire
/// format; the overlay converts to "remaining" when it renders, as it does for Claude.
/// </summary>
internal sealed record CodexWindow(double UsedPct, int? WindowMinutes, DateTimeOffset? ResetsAt)
{
    public double RemainingPct => Math.Clamp(100 - UsedPct, 0, 100);

    /// <summary>
    /// Short bar label derived from the window's own duration rather than its position in the
    /// response, so a plan with different windows still labels itself correctly.
    /// </summary>
    public string Label => WindowMinutes switch
    {
        // Generous bands: the server reports the nominal window, which drifts a little by plan.
        >= 270 and <= 330 => "5h",
        >= 9_000 and <= 11_000 => "7d",
        > 0 and var m when m % 1440 == 0 => $"{m / 1440}d",
        > 0 and var m when m % 60 == 0 => $"{m / 60}h",
        > 0 and var m => $"{m}m",
        _ => "usage",
    };
}

/// <summary>
/// A spend budget, on plans where money rather than a rolling window is the binding constraint.
/// The Codex analogue of Claude's credit meter, and it reports its own reset.
/// </summary>
internal sealed record CodexSpendLimit(
    double RemainingPct,
    DateTimeOffset? ResetsAt,
    string? Used,
    string? Limit);

/// <summary>
/// One named metering bucket. Accounts can be metered against several at once — an overall Codex
/// limit plus a per-model one — and the server reports them keyed by limit id.
/// </summary>
internal sealed record CodexBucket(string LimitId, string? LimitName, CodexWindow? Primary)
{
    /// <summary>Bucket name for display, falling back to the id when the server sends none.</summary>
    public string Display => string.IsNullOrWhiteSpace(LimitName) ? LimitId : LimitName!;
}

/// <summary>Rate-limit state as of one read.</summary>
internal sealed record CodexSnapshot(
    CodexWindow? Primary,
    CodexWindow? Secondary,
    DateTimeOffset ReadAt)
{
    /// <summary>Present on usage-based and enterprise seats instead of, or alongside, the windows.</summary>
    public CodexSpendLimit? SpendLimit { get; init; }

    /// <summary>Plan name from the server, for diagnostics only.</summary>
    public string? PlanType { get; init; }

    /// <summary>Set when the server says a limit has actually been hit.</summary>
    public string? LimitReached { get; init; }

    /// <summary>
    /// Reset credits the account can redeem. Surfaced for information only — redeeming one is an
    /// account action UsageBars will not take on someone's behalf.
    /// </summary>
    public int ResetCreditsAvailable { get; init; }

    /// <summary>
    /// Every metering bucket the server reported, including per-model ones. Shown in the tray
    /// tooltip and diagnostics rather than as bars: the pill draws the account-wide limit, and a
    /// bar per model would make it far too wide to sit in a title bar.
    /// </summary>
    public IReadOnlyList<CodexBucket> Buckets { get; init; } = Array.Empty<CodexBucket>();

    public bool HasValues => Primary is not null || Secondary is not null || SpendLimit is not null;

    /// <summary>Soonest reset across everything reported, for the "resets in" line.</summary>
    public DateTimeOffset? NextReset
    {
        get
        {
            DateTimeOffset? soonest = null;
            foreach (var candidate in new[] { Primary?.ResetsAt, Secondary?.ResetsAt, SpendLimit?.ResetsAt })
            {
                if (candidate is null) continue;
                if (soonest is null || candidate < soonest) soonest = candidate;
            }
            return soonest;
        }
    }
}

/// <summary>
/// Parses the two shapes the Codex app-server sends rate limits in: the reply to
/// <c>account/rateLimits/read</c>, and the unsolicited <c>account/rateLimits/updated</c>
/// notification it pushes when a request changes them.
///
/// Deliberately strict about types but tolerant about absence: a missing window means "this plan
/// has no such window", which is different from a malformed one and must not blank the other bar.
/// </summary>
internal static class CodexRateLimitParser
{
    public const string UpdatedMethod = "account/rateLimits/updated";

    public static CodexSnapshot ParseReadResponse(string json, DateTimeOffset readAt)
    {
        using var doc = Parse(json);
        var result = RequireObject(doc.RootElement, "response");
        var payload = RequireObjectProperty(result, "result", "result");
        var limits = RequireObjectProperty(payload, "rateLimits", "result.rateLimits");

        var snapshot = ParseSnapshot(limits, "result.rateLimits", readAt);

        // Reset credits sit beside rateLimits rather than inside it.
        var credits = 0;
        if (payload.TryGetProperty("rateLimitResetCredits", out var resetCredits)
            && resetCredits.ValueKind == JsonValueKind.Object
            && resetCredits.TryGetProperty("availableCount", out var available)
            && available.ValueKind == JsonValueKind.Number
            && available.TryGetInt32(out var count))
        {
            credits = Math.Max(0, count);
        }

        return snapshot with
        {
            ResetCreditsAvailable = credits,
            Buckets = ParseBuckets(payload),
        };
    }

    /// <summary>
    /// The per-limit-id view. Best-effort: a bucket that doesn't parse is skipped rather than
    /// failing the read, because the account-wide numbers in <c>rateLimits</c> are what the pill
    /// actually draws.
    /// </summary>
    private static IReadOnlyList<CodexBucket> ParseBuckets(JsonElement payload)
    {
        if (!payload.TryGetProperty("rateLimitsByLimitId", out var byId)
            || byId.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<CodexBucket>();
        }

        var buckets = new List<CodexBucket>();
        foreach (var entry in byId.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;

            try
            {
                buckets.Add(new CodexBucket(
                    entry.Name,
                    OptionalString(entry.Value, "limitName"),
                    ParseWindow(entry.Value, "primary", $"rateLimitsByLimitId.{entry.Name}.primary")));
            }
            catch (FormatException)
            {
            }
        }

        return buckets;
    }

    /// <summary>
    /// Applies a pushed update on top of what we already have. A notification may carry only one
    /// window; the other keeps its previous value rather than disappearing.
    /// </summary>
    public static CodexSnapshot MergeNotification(
        CodexSnapshot? current, string json, DateTimeOffset readAt)
    {
        using var doc = Parse(json);
        var root = RequireObject(doc.RootElement, "notification");

        if (!root.TryGetProperty("method", out var method)
            || method.ValueKind != JsonValueKind.String
            || method.GetString() != UpdatedMethod)
        {
            throw new FormatException($"notification.method must be '{UpdatedMethod}'");
        }

        var parameters = RequireObjectProperty(root, "params", "params");
        var limits = RequireObjectProperty(parameters, "rateLimits", "params.rateLimits");

        var update = ParseSnapshot(limits, "params.rateLimits", readAt);

        // The update is documented as sparse: an absent field means "unchanged", not "cleared".
        // Merging rather than replacing is what keeps the other bar on screen.
        return update with
        {
            Primary = limits.TryGetProperty("primary", out _) ? update.Primary : current?.Primary,
            Secondary = limits.TryGetProperty("secondary", out _) ? update.Secondary : current?.Secondary,
            SpendLimit = limits.TryGetProperty("individualLimit", out _)
                ? update.SpendLimit
                : current?.SpendLimit,
            PlanType = update.PlanType ?? current?.PlanType,
            LimitReached = limits.TryGetProperty("rateLimitReachedType", out _)
                ? update.LimitReached
                : current?.LimitReached,
            ResetCreditsAvailable = current?.ResetCreditsAvailable ?? 0,
            Buckets = current?.Buckets ?? Array.Empty<CodexBucket>(),
        };
    }

    /// <summary>Shared shape of the read result and the pushed notification.</summary>
    private static CodexSnapshot ParseSnapshot(
        JsonElement limits, string path, DateTimeOffset readAt) =>
        new(ParseWindow(limits, "primary", $"{path}.primary"),
            ParseWindow(limits, "secondary", $"{path}.secondary"),
            readAt)
        {
            SpendLimit = ParseSpendLimit(limits, $"{path}.individualLimit"),
            PlanType = OptionalString(limits, "planType"),
            LimitReached = OptionalString(limits, "rateLimitReachedType"),
        };

    private static string? OptionalString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static CodexSpendLimit? ParseSpendLimit(JsonElement parent, string path)
    {
        if (!parent.TryGetProperty("individualLimit", out var element)) return null;
        if (element.ValueKind == JsonValueKind.Null) return null;

        var limit = RequireObject(element, path);

        if (!limit.TryGetProperty("remainingPercent", out var remaining)
            || remaining.ValueKind != JsonValueKind.Number
            || !remaining.TryGetDouble(out var percent)
            || !double.IsFinite(percent))
        {
            throw new FormatException($"{path}.remainingPercent must be a finite number");
        }

        DateTimeOffset? resetsAt = null;
        if (limit.TryGetProperty("resetsAt", out var reset) && reset.ValueKind != JsonValueKind.Null)
            resetsAt = ParseUnixSeconds(reset, $"{path}.resetsAt");

        return new CodexSpendLimit(
            Math.Clamp(percent, 0, 100),
            resetsAt,
            OptionalString(limit, "used"),
            OptionalString(limit, "limit"));
    }

    private static JsonDocument Parse(string json)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new FormatException("rate-limit JSON is invalid", ex); }
    }

    private static CodexWindow? ParseWindow(JsonElement parent, string name, string path)
    {
        if (!parent.TryGetProperty(name, out var element)) return null;
        if (element.ValueKind == JsonValueKind.Null) return null;

        var window = RequireObject(element, path);

        // Clamped rather than rejected: an overage plan can legitimately report past 100, and a
        // full bar is the right answer there. Only a non-numeric value is a parse failure.
        var used = Math.Clamp(RequireDouble(window, "usedPercent", $"{path}.usedPercent"), 0, 100);

        int? minutes = null;
        if (window.TryGetProperty("windowDurationMins", out var duration)
            && duration.ValueKind != JsonValueKind.Null)
        {
            if (duration.ValueKind != JsonValueKind.Number
                || !duration.TryGetInt32(out var value)
                || value <= 0)
            {
                throw new FormatException($"{path}.windowDurationMins must be a positive integer");
            }
            minutes = value;
        }

        DateTimeOffset? resetsAt = null;
        if (window.TryGetProperty("resetsAt", out var reset) && reset.ValueKind != JsonValueKind.Null)
        {
            resetsAt = ParseUnixSeconds(reset, $"{path}.resetsAt");
        }

        return new CodexWindow(used, minutes, resetsAt);
    }

    private static DateTimeOffset? ParseUnixSeconds(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var seconds))
            throw new FormatException($"{path} must be Unix seconds");

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new FormatException($"{path} is not a representable time", ex);
        }
    }

    private static double RequireDouble(JsonElement parent, string name, string path)
    {
        if (!parent.TryGetProperty(name, out var element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetDouble(out var value)
            || !double.IsFinite(value))
        {
            throw new FormatException($"{path} must be a finite number");
        }
        return value;
    }

    private static JsonElement RequireObjectProperty(JsonElement parent, string name, string path)
    {
        if (!parent.TryGetProperty(name, out var element))
            throw new FormatException($"{path} is required");
        return RequireObject(element, path);
    }

    private static JsonElement RequireObject(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException($"{path} must be an object");
        return element;
    }

}
