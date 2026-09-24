using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace UsageBars;

internal sealed class TargetEditorModel
{
    private TargetDefinition _original;
    private IReadOnlyList<string> _activityProcessNames = Array.Empty<string>();
    private bool _hasInvalidActivityProcessName;

    private TargetEditorModel(TargetDraft draft)
    {
        _original = draft.Definition;

        Name = draft.Definition.Name;
        Application = draft.Definition.ProcessName ?? "";
        WindowClass = draft.Definition.WindowClass ?? "";
        CapturedTitle = draft.CapturedTitle;
        Claude = draft.Definition.Providers.Any(binding => binding.Provider == ProviderId.Claude);
        Codex = draft.Definition.Providers.Any(binding => binding.Provider == ProviderId.Codex);
        TitleMatcherEnabled = draft.Definition.WindowTitle is not null;
        TitleKind = draft.Definition.WindowTitle?.Kind ?? TitleMatchKind.Literal;
        TitleValue = draft.Definition.WindowTitle?.Value ?? "";
        SetActivityProcesses(draft.Definition.ActivityProcessNames);
        Placement = draft.Definition.Placement;
        Anchor = draft.Definition.Anchor;
        OffsetX = draft.Definition.OffsetX;
        OffsetY = draft.Definition.OffsetY;
        Enabled = draft.Definition.Enabled;
        RequireResizableFrame = draft.Definition.RequireResizableFrame;
        StatusBarHeight = draft.Definition.StatusBarHeight;
        InferenceNotes = draft.InferenceNotes;
    }

    public string Name { get; set; }
    public string Application { get; }
    public string WindowClass { get; }
    public string CapturedTitle { get; }
    public bool TitleMatcherEnabled { get; set; }
    public TitleMatchKind TitleKind { get; set; }
    public string TitleValue { get; set; } = "";
    public IReadOnlyList<string> ActivityProcessNames => _activityProcessNames;
    public bool Claude { get; set; }
    public bool Codex { get; set; }
    public TargetPlacementKind Placement { get; set; }
    public TargetActivationPolicy Activation => _activityProcessNames.Count == 0
        ? TargetActivationPolicy.AlwaysWhileOpen
        : TargetActivationPolicy.RequireActivityProcess;
    public PillAnchor Anchor { get; set; }
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
    public bool Enabled { get; set; }
    public bool RequireResizableFrame { get; set; }
    public double StatusBarHeight { get; set; }
    public IReadOnlyList<string> InferenceNotes { get; }

    public static TargetEditorModel From(TargetDraft draft) => new(draft);

    public static TargetEditorModel FromExisting(TargetDefinition target) =>
        new(new TargetDraft(target, "", Array.Empty<string>()));

    public void ApplyDefinition(TargetDefinition definition)
    {
        _original = definition;
        Name = definition.Name;
        Claude = definition.Providers.Any(binding => binding.Provider == ProviderId.Claude);
        Codex = definition.Providers.Any(binding => binding.Provider == ProviderId.Codex);
        TitleMatcherEnabled = definition.WindowTitle is not null;
        TitleKind = definition.WindowTitle?.Kind ?? TitleMatchKind.Literal;
        TitleValue = definition.WindowTitle?.Value ?? "";
        SetActivityProcesses(definition.ActivityProcessNames);
        Placement = definition.Placement;
        Anchor = definition.Anchor;
        OffsetX = definition.OffsetX;
        OffsetY = definition.OffsetY;
        Enabled = definition.Enabled;
        RequireResizableFrame = definition.RequireResizableFrame;
        StatusBarHeight = definition.StatusBarHeight;
    }

    public void SetActivityProcesses(IEnumerable<string> values)
    {
        var supplied = values.ToList();
        _hasInvalidActivityProcessName = supplied.Any(value =>
            !ActivityProcessName.TryNormalize(value, out _));
        _activityProcessNames = ActivityProcessName.NormalizeMany(supplied);
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name)) errors.Add("Name is required.");
        if (!Claude && !Codex) errors.Add("Choose at least one provider.");
        if (TitleMatcherEnabled && string.IsNullOrWhiteSpace(TitleValue))
            errors.Add("Title matcher value is required.");
        if (TitleMatcherEnabled && !Enum.IsDefined(TitleKind))
            errors.Add("Title matcher kind is invalid.");
        if (TitleMatcherEnabled && TitleKind == TitleMatchKind.Regex && !ValidRegex(TitleValue.Trim()))
            errors.Add("Title matcher regex is invalid.");
        if (_hasInvalidActivityProcessName)
            errors.Add("Activity process names must be valid file names.");
        if (!Enum.IsDefined(Anchor)) errors.Add("anchor is invalid");
        if (OffsetX < TargetPositionRules.MinimumOffset || OffsetX > TargetPositionRules.MaximumOffset)
            errors.Add("horizontal offset is out of range");
        if (OffsetY < TargetPositionRules.MinimumOffset || OffsetY > TargetPositionRules.MaximumOffset)
            errors.Add("vertical offset is out of range");
        return errors;
    }

    public TargetDefinition ToDefinition()
    {
        var providers = new List<ProviderBinding>();
        if (Claude) providers.Add(new ProviderBinding(ProviderId.Claude));
        if (Codex) providers.Add(new ProviderBinding(ProviderId.Codex));

        return _original with
        {
            Name = Name.Trim(),
            Providers = providers,
            WindowTitle = TitleMatcherEnabled
                ? new TitleRule(TitleKind, TitleValue.Trim())
                : null,
            ActivityProcessNames = _activityProcessNames.ToList(),
            Enabled = Enabled,
            Placement = Placement,
            Activation = _activityProcessNames.Count == 0
                ? TargetActivationPolicy.AlwaysWhileOpen
                : TargetActivationPolicy.RequireActivityProcess,
            Anchor = Anchor,
            OffsetX = OffsetX,
            OffsetY = OffsetY,
            RequireResizableFrame = RequireResizableFrame,
            StatusBarHeight = StatusBarHeight,
        };
    }

    private static bool ValidRegex(string value)
    {
        try
        {
            _ = new Regex(value, RegexOptions.None, TimeSpan.FromMilliseconds(100));
            return true;
        }
        catch (ArgumentException) { return false; }
    }

}

internal sealed record TargetRow(
    TargetDefinition Target,
    bool Enabled,
    string Name,
    string Providers,
    string Application,
    string Match,
    string Placement,
    string Position,
    bool HasOverlap)
{
    public string Warning => HasOverlap ? "Overlapping rule" : "";

    public static TargetRow From(TargetDefinition target, IReadOnlyList<TargetDefinition> targets)
    {
        var providers = target.Providers.Select(x => x.Provider).Distinct().Order() switch
        {
            var values when values.SequenceEqual(new[] { ProviderId.Claude, ProviderId.Codex })
                => "Claude + Codex",
            var values when values.SequenceEqual(new[] { ProviderId.Claude }) => "Claude",
            _ => "Codex",
        };
        var match = string.Join(" · ", new[]
        {
            target.ProcessName is null ? null : $"process={target.ProcessName}",
            target.WindowClass is null ? null : $"class={target.WindowClass}",
            target.WindowTitle is null ? null : $"title={target.WindowTitle.Kind}:{target.WindowTitle.Value}",
            $"activity={TargetRuleText.FormatActivity(target)}",
        }.Where(x => x is not null));

        return new TargetRow(
            target,
            target.Enabled,
            target.Name,
            providers,
            target.ProcessName ?? "(any application)",
            match,
            target.Placement.ToString(),
            TargetPositionRules.Format(target.Anchor, target.OffsetX, target.OffsetY),
            target.Enabled && targets.Any(other => other.Enabled && target.Overlaps(other)));
    }
}

internal static class TargetDetailsFormatter
{
    public static string Format(TargetDefinition target, IReadOnlyList<TargetDefinition> targets)
    {
        var matcher = string.Join(" · ", new[]
        {
            target.ProcessName is null ? "process=(any)" : $"process={target.ProcessName}",
            target.WindowClass is null ? "class=(any)" : $"class={target.WindowClass}",
            target.RequireResizableFrame ? "resizable=yes" : "resizable=no",
        });
        var providers = string.Join(" + ", target.Providers
            .Select(binding => binding.Provider).Distinct().Order());
        var lines = new List<string>
        {
            $"Rule: {matcher}",
            $"Title: {(target.WindowTitle is null ? "(any)" : $"{target.WindowTitle.Kind}:{target.WindowTitle.Value}")}",
            $"Activity: {TargetRuleText.FormatActivity(target).Replace("|", " OR ")}",
            $"Providers: {providers}",
            $"Placement: {target.Placement} · Activation: {target.Activation}",
            $"Position: {TargetPositionRules.Format(target.Anchor, target.OffsetX, target.OffsetY)}",
            $"Source: {target.Source} · Revision: {target.Revision}",
        };

        var overlaps = targets
            .Where(other => target.Enabled && other.Enabled && target.Overlaps(other))
            .Select(other => other.Name)
            .ToArray();
        lines.Add(overlaps.Length == 0
            ? "No overlapping enabled rules."
            : $"Overlaps with: {string.Join(", ", overlaps)}{Environment.NewLine}"
                + "The more specific rule wins; if equally specific, the most recently edited rule wins.");
        if (targets.Any(other => target.Enabled && other.Enabled && target.Overlaps(other)
            && TargetRuleText.ActivityFiltersDiffer(target, other)))
        {
            lines.Add("activity filters differ");
        }
        if (!string.IsNullOrWhiteSpace(target.MigrationWarning))
            lines.Add($"Migration warning: {target.MigrationWarning}");
        return string.Join(Environment.NewLine, lines);
    }
}
