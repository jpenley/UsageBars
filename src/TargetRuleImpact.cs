using System;
using System.Collections.Generic;
using System.Linq;

namespace UsageBars;

internal enum TargetRuleRelationship
{
    ExactIdentity,
    AppliesNow,
    OverlapsDraft,
    SearchOnly,
}

internal sealed record TargetRuleImpact(
    TargetDefinition Target,
    TargetRuleRelationship Relationship,
    bool ExactIdentity,
    bool AppliesNow,
    bool OverlapsDraft,
    bool SearchMatch,
    string? ImpactText);

internal static class TargetRuleImpactAnalyzer
{
    public static IReadOnlyList<TargetRuleImpact> Analyze(
        WindowSnapshot window,
        TargetDefinition draft,
        IReadOnlyList<TargetDefinition> saved,
        string? search)
    {
        var query = search?.Trim();
        return saved
            .Select(target => Row(window, draft, target, query))
            .Where(row => row is not null)
            .Select(row => row!)
            .OrderBy(row => row.Relationship)
            .ThenByDescending(row => row.Target.Enabled)
            .ThenByDescending(row => row.Target.Specificity)
            .ThenByDescending(row => row.Target.Revision)
            .ThenBy(row => row.Target.Id)
            .ToList();
    }

    public static TargetRuleImpact? StrongestApplicable(IReadOnlyList<TargetRuleImpact> rows) =>
        rows
            .Where(row => row.ExactIdentity || row.AppliesNow)
            .OrderBy(row => row.ExactIdentity ? 0 : 1)
            .ThenByDescending(row => row.Target.Enabled)
            .ThenByDescending(row => row.Target.Specificity)
            .ThenByDescending(row => row.Target.Revision)
            .ThenBy(row => row.Target.Id)
            .FirstOrDefault();

    private static TargetRuleImpact? Row(
        WindowSnapshot window,
        TargetDefinition draft,
        TargetDefinition target,
        string? query)
    {
        var exact = target.HasSameIdentity(draft);
        var applies = target.Matches(window);
        var overlaps = target.Overlaps(draft);
        var searchMatch = TargetRuleSearch.Matches(target, query);

        if (!string.IsNullOrWhiteSpace(query) && !searchMatch) return null;

        TargetRuleRelationship relationship;
        if (exact) relationship = TargetRuleRelationship.ExactIdentity;
        else if (applies) relationship = TargetRuleRelationship.AppliesNow;
        else if (overlaps) relationship = TargetRuleRelationship.OverlapsDraft;
        else if (searchMatch) relationship = TargetRuleRelationship.SearchOnly;
        else return null;

        return new TargetRuleImpact(
            target, relationship, exact, applies, overlaps, searchMatch,
            overlaps && TargetRuleText.ActivityFiltersDiffer(target, draft)
                ? "activity filters differ"
                : null);
    }

}

internal static class TargetRuleSearch
{
    public static bool Matches(TargetDefinition target, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;
        query = query.Trim();

        return Contains(target.Name, query)
            || Contains(target.Id.ToString("D"), query)
            || Contains(target.Id.ToString("N"), query)
            || Contains(target.ProcessName, query)
            || Contains(target.WindowClass, query)
            || Contains(target.WindowTitle?.Kind.ToString(), query)
            || Contains(target.WindowTitle?.Value, query)
            || target.ActivityProcessNames.Any(value => ActivityMatches(value, query))
            || target.Providers.Any(binding => Contains(binding.Provider.ToString(), query));
    }

    private static bool Contains(string? value, string query) =>
        value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

    private static bool ActivityMatches(string value, string query) =>
        Contains(value, query)
        || (ActivityProcessName.TryNormalize(query, out var normalized) && Contains(value, normalized));
}

internal static class TargetRuleText
{
    internal static string FormatActivity(TargetDefinition target) =>
        target.ActivityProcessNames.Count == 0
            ? "always"
            : string.Join("|", ActivityProcessName.NormalizeMany(target.ActivityProcessNames));

    internal static bool ActivityFiltersDiffer(TargetDefinition first, TargetDefinition second) =>
        !ActivityProcessName.NormalizeMany(first.ActivityProcessNames).SequenceEqual(
            ActivityProcessName.NormalizeMany(second.ActivityProcessNames),
            StringComparer.OrdinalIgnoreCase);
}
