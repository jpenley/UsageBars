using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace UsageBars;

internal enum TargetPlacementKind { Desktop, Terminal, Ide }
internal enum TargetActivationPolicy
{
    AlwaysWhileOpen,
    RequireActivityProcess,
}
internal enum TargetSource { Seeded, Captured }
internal enum TitleMatchKind { Literal, Prefix, Regex }

internal sealed record TitleRule(TitleMatchKind Kind, string Value)
{
    public bool Matches(string title)
    {
        if (string.IsNullOrEmpty(Value)) return false;
        return Kind switch
        {
            TitleMatchKind.Literal => string.Equals(title, Value, StringComparison.OrdinalIgnoreCase),
            TitleMatchKind.Prefix => title.StartsWith(Value, StringComparison.OrdinalIgnoreCase),
            TitleMatchKind.Regex => SafeRegex(title, Value),
            _ => false,
        };
    }

    private static bool SafeRegex(string input, string pattern)
    {
        try { return Regex.IsMatch(input, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100)); }
        catch (ArgumentException) { return false; }
        catch (RegexMatchTimeoutException) { return false; }
    }
}

internal sealed record ProviderBinding
{
    public ProviderId Provider { get; init; }

    [JsonConstructor]
    public ProviderBinding(ProviderId provider)
    {
        Provider = provider;
    }

    public static ProviderBinding Claude => new(ProviderId.Claude);
    public static ProviderBinding Codex => new(ProviderId.Codex);
}

internal sealed record WindowSnapshot(
    IntPtr Hwnd, uint Pid, string ProcessName, string ClassName, string Title, bool IsResizable);

internal sealed record TargetDefinition
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public List<ProviderBinding> Providers { get; init; } = new();
    public List<string> ActivityProcessNames { get; init; } = new();
    public string? ProcessName { get; init; }
    public string? WindowClass { get; init; }
    public bool RequireResizableFrame { get; init; }
    public TitleRule? WindowTitle { get; init; }
    public TargetPlacementKind Placement { get; init; }
    public TargetActivationPolicy Activation { get; init; }
    public TargetSource Source { get; init; }
    public double StatusBarHeight { get; init; }
    public long Revision { get; init; }
    public PillAnchor Anchor { get; init; }
    public int OffsetX { get; init; }
    public int OffsetY { get; init; }

    public string? MigrationWarning { get; init; }

    [JsonIgnore]
    public int Specificity =>
        (ProcessName is not null ? 4 : 0)
        + (WindowClass is not null ? 2 : 0)
        + (WindowTitle is not null ? 8 : 0)
        + (RequireResizableFrame ? 1 : 0);

    public static TargetDefinition Captured(
        string name, string process, string windowClass, IEnumerable<ProviderBinding> providers,
        TargetPlacementKind placement, TargetActivationPolicy activation,
        bool requireResizableFrame, double statusBarHeight,
        IEnumerable<string>? activityProcessNames = null)
        => new()
        {
            Name = name, ProcessName = process, WindowClass = windowClass,
            Providers = providers.DistinctBy(x => x.Provider).ToList(),
            ActivityProcessNames = ActivityProcessName.NormalizeMany(
                activityProcessNames ?? Array.Empty<string>()),
            Placement = placement, Activation = activation, Source = TargetSource.Captured,
            RequireResizableFrame = requireResizableFrame, StatusBarHeight = statusBarHeight,
            Anchor = TargetPositionRules.DefaultFor(placement),
        };

    public static TargetDefinition Seeded(
        string name, string? process, string? windowClass, IEnumerable<ProviderBinding> providers,
        TargetPlacementKind placement, TargetActivationPolicy activation,
        bool requireResizableFrame, double statusBarHeight,
        IEnumerable<string>? activityProcessNames = null) =>
        Captured(name, process ?? "", windowClass ?? "", providers, placement, activation,
            requireResizableFrame, statusBarHeight, activityProcessNames) with
        {
            ProcessName = string.IsNullOrWhiteSpace(process) ? null : process,
            WindowClass = string.IsNullOrWhiteSpace(windowClass) ? null : windowClass,
            Source = TargetSource.Seeded,
        };

    public IReadOnlyList<string> Validate(bool captured)
    {
        var errors = new List<string>();
        var isCaptured = captured || Source == TargetSource.Captured;
        if (Id == Guid.Empty) errors.Add("id is empty");
        if (string.IsNullOrWhiteSpace(Name)) errors.Add("name is required");
        if (Providers.Count == 0) errors.Add("at least one provider is required");
        if (Providers.Select(x => x.Provider).Distinct().Count() != Providers.Count)
            errors.Add("providers must be unique");
        foreach (var binding in Providers)
        {
            if (binding.Provider is not (ProviderId.Claude or ProviderId.Codex))
                errors.Add($"unsupported provider: {binding.Provider}");
        }
        if (Activation == TargetActivationPolicy.AlwaysWhileOpen && ActivityProcessNames.Count != 0)
            errors.Add("always-open targets cannot have activity processes");
        if (Activation == TargetActivationPolicy.RequireActivityProcess && ActivityProcessNames.Count == 0)
            errors.Add("process-activated targets require at least one activity process");
        if (ActivityProcessName.NormalizeMany(ActivityProcessNames).Count != ActivityProcessNames.Count)
            errors.Add("activity processes must be valid and unique");
        if (isCaptured && string.IsNullOrWhiteSpace(ProcessName)) errors.Add("captured process is required");
        if (isCaptured && string.IsNullOrWhiteSpace(WindowClass)) errors.Add("captured window class is required");
        if (ProcessName is null && WindowClass is null) errors.Add("process or window class is required");
        if (ProcessName is not null && WindowClass is null && !RequireResizableFrame)
            errors.Add("process-only rules must require a resizable frame");
        if (StatusBarHeight < 0 || !double.IsFinite(StatusBarHeight))
            errors.Add("status-bar height must be finite and non-negative");
        if (!Enum.IsDefined(Anchor)) errors.Add("anchor is invalid");
        if (OffsetX < TargetPositionRules.MinimumOffset || OffsetX > TargetPositionRules.MaximumOffset)
            errors.Add("horizontal offset is out of range");
        if (OffsetY < TargetPositionRules.MinimumOffset || OffsetY > TargetPositionRules.MaximumOffset)
            errors.Add("vertical offset is out of range");
        if (WindowTitle is { Kind: TitleMatchKind.Regex } title && !ValidRegex(title.Value))
            errors.Add("window title regex is invalid");
        return errors;
    }

    private static bool ValidRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    public bool Matches(WindowSnapshot window) =>
        (ProcessName is null || ProcessName.Equals(window.ProcessName, StringComparison.OrdinalIgnoreCase))
        && (WindowClass is null || WindowClass.Equals(window.ClassName, StringComparison.OrdinalIgnoreCase))
        && (!RequireResizableFrame || window.IsResizable)
        && (WindowTitle is null || WindowTitle.Matches(window.Title));

    public bool HasSameIdentity(TargetDefinition other) =>
        string.Equals(ProcessName, other.ProcessName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(WindowClass, other.WindowClass, StringComparison.OrdinalIgnoreCase)
        && RequireResizableFrame == other.RequireResizableFrame
        && SameTitleRule(WindowTitle, other.WindowTitle)
        && Placement == other.Placement
        && Activation == other.Activation
        && Providers.OrderBy(x => x.Provider).SequenceEqual(other.Providers.OrderBy(x => x.Provider))
        && ActivityProcessName.NormalizeMany(ActivityProcessNames)
            .SequenceEqual(ActivityProcessName.NormalizeMany(other.ActivityProcessNames),
                StringComparer.OrdinalIgnoreCase);

    public bool Overlaps(TargetDefinition other)
    {
        var sameProcessSpace = ProcessName is null || other.ProcessName is null
            || ProcessName.Equals(other.ProcessName, StringComparison.OrdinalIgnoreCase);
        var sameClassSpace = WindowClass is null || other.WindowClass is null
            || WindowClass.Equals(other.WindowClass, StringComparison.OrdinalIgnoreCase);
        var sharedProvider = Providers.Select(x => x.Provider)
            .Intersect(other.Providers.Select(x => x.Provider)).Any();
        return Id != other.Id && sameProcessSpace && sameClassSpace && sharedProvider
            && TitleSpacesOverlap(WindowTitle, other.WindowTitle);
    }

    private static bool SameTitleRule(TitleRule? first, TitleRule? second)
    {
        if (first is null || second is null) return first is null && second is null;
        if (first.Kind != second.Kind) return false;
        var comparison = first.Kind == TitleMatchKind.Regex
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        return string.Equals(first.Value, second.Value, comparison);
    }

    private static bool TitleSpacesOverlap(TitleRule? first, TitleRule? second)
    {
        if (first is null || second is null) return true;
        if (first.Kind == TitleMatchKind.Regex || second.Kind == TitleMatchKind.Regex) return true;

        if (first.Kind == TitleMatchKind.Literal && second.Kind == TitleMatchKind.Literal)
            return string.Equals(first.Value, second.Value, StringComparison.OrdinalIgnoreCase);
        if (first.Kind == TitleMatchKind.Prefix && second.Kind == TitleMatchKind.Prefix)
            return first.Value.StartsWith(second.Value, StringComparison.OrdinalIgnoreCase)
                || second.Value.StartsWith(first.Value, StringComparison.OrdinalIgnoreCase);

        var literal = first.Kind == TitleMatchKind.Literal ? first.Value : second.Value;
        var prefix = first.Kind == TitleMatchKind.Prefix ? first.Value : second.Value;
        return literal.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record TargetDraft(
    TargetDefinition Definition, string CapturedTitle, IReadOnlyList<string> InferenceNotes);
internal sealed record TargetDocument(int Version, List<TargetDefinition> Targets);
