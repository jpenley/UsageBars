using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UsageBars;

internal readonly record struct TriggerDecision(bool Fire, string Cycle, string Reason);

/// <summary>
/// Decides when the credit meter is close enough to empty to start the insights hand-off, and
/// remembers that it has, so one billing cycle produces at most one terminal.
/// </summary>
internal sealed class LowCreditTrigger
{
    /// <summary>The billing cycle (identified by its reset day) the trigger last fired for.</summary>
    [JsonPropertyName("cycle")]
    public string? FiredCycle { get; set; }

    private static string FilePath => AppPaths.LowCreditStateFile;

    public static LowCreditTrigger Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<LowCreditTrigger>(File.ReadAllText(FilePath))
                    ?? new LowCreditTrigger();
        }
        catch (Exception)
        {
            // An unreadable marker means "not fired yet"; firing once more beats never firing.
        }
        return new LowCreditTrigger();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception)
        {
            // Failing to persist only risks a second terminal after a restart in the same cycle.
        }
    }

    /// <summary>
    /// Fires when the credit/spend bar is projected to empty within
    /// <see cref="Config.InsightsTriggerHours"/> at the current rate AND before its reset, or is
    /// already empty. Never fires on a stale reading, with no projection, or twice in one cycle.
    /// </summary>
    public static TriggerDecision Evaluate(
        UsageModel model, Config config, DateTimeOffset now, string? firedCycle)
    {
        if (!config.InsightsOnLowCredit || config.InsightsTriggerHours <= 0)
            return new(false, "", "disabled");
        if (model.Stale) return new(false, "", "reading is stale");

        UsageBar? credit = null;
        foreach (var bar in model.Bars)
        {
            if (bar.Label is "C" or "spend" && bar.RemainingPct is not null) { credit = bar; break; }
        }
        if (credit is null) return new(false, "", "no credit or spend meter");

        var cycle = credit.ResetsAt is { } reset
            ? reset.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            : "no-reset";
        if (string.Equals(firedCycle, cycle, StringComparison.Ordinal))
            return new(false, cycle, "already fired this cycle");

        if (credit.RemainingPct <= 0)
            return new(true, cycle, "credit is used up");

        if (credit.EmptyAt is not { } empty)
            return new(false, cycle, "no spend rate yet");

        // Hours of USE left when the rate was measured over working time; otherwise the plain
        // wall-clock distance to the projected empty moment.
        var hoursLeft = credit.ActiveHoursLeft ?? (empty - now).TotalHours;
        if (hoursLeft > config.InsightsTriggerHours)
            return new(false, cycle, $"{hoursLeft:0.0}h projected, over the {config.InsightsTriggerHours:0.#}h trigger");

        if (credit.ResetsAt is { } resetsAt && empty >= resetsAt)
            return new(false, cycle, "resets before it empties");

        return new(true, cycle, $"{Math.Max(0, hoursLeft):0.0}h of use projected to remain");
    }
}
