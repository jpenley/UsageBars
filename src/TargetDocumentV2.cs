using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace UsageBars;

internal sealed record ProviderBindingV2(
    ProviderId Provider, string CliProcessName, string? ActivityTitlePattern);

internal sealed record TargetDefinitionV2
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public List<ProviderBindingV2> Providers { get; init; } = new();
    public string? ProcessName { get; init; }
    public string? WindowClass { get; init; }
    public bool RequireResizableFrame { get; init; }
    public TitleRule? WindowTitle { get; init; }
    public TargetPlacementKind Placement { get; init; }
    public string Activation { get; init; } = "AlwaysWhileOpen";
    public TargetSource Source { get; init; }
    public double StatusBarHeight { get; init; }
    public long Revision { get; init; }
    public PillAnchor Anchor { get; init; }
    public int OffsetX { get; init; }
    public int OffsetY { get; init; }
}

internal sealed record MigratedTarget(TargetDefinition Definition, string? Warning);

internal static class TargetDocumentV2Migration
{
    private const string ActivityTitleWarning =
        "Migrated provider-specific title activity to process-only activation; review this target.";

    public static MigratedTarget Read(
        JsonElement element, int documentVersion, JsonSerializerOptions options)
    {
        var legacy = element.Deserialize<TargetDefinitionV2>(options)
            ?? throw new JsonException("target is null");
        var providers = (legacy.Providers ?? throw new JsonException("providers are required"))
            .Select(binding => new ProviderBinding(binding.Provider))
            .DistinctBy(binding => binding.Provider)
            .ToList();
        var activation = legacy.Activation switch
        {
            "AlwaysWhileOpen" => TargetActivationPolicy.AlwaysWhileOpen,
            "RequireProviderActivity" => TargetActivationPolicy.RequireActivityProcess,
            _ => throw new JsonException($"unknown legacy activation {legacy.Activation}"),
        };
        var activityProcessNames = activation == TargetActivationPolicy.RequireActivityProcess
            ? ActivityProcessName.NormalizeMany(legacy.Providers.Select(binding => binding.CliProcessName))
            : new List<string>();
        var warning = legacy.Providers.Any(binding =>
            !string.IsNullOrWhiteSpace(binding.ActivityTitlePattern))
            ? ActivityTitleWarning
            : null;
        var definition = new TargetDefinition
        {
            Id = legacy.Id,
            Name = legacy.Name,
            Enabled = legacy.Enabled,
            Providers = providers,
            ActivityProcessNames = activityProcessNames,
            ProcessName = legacy.ProcessName,
            WindowClass = legacy.WindowClass,
            RequireResizableFrame = legacy.RequireResizableFrame,
            WindowTitle = legacy.WindowTitle,
            Placement = legacy.Placement,
            Activation = activation,
            Source = legacy.Source,
            StatusBarHeight = legacy.StatusBarHeight,
            Revision = legacy.Revision,
            Anchor = documentVersion == 1
                ? TargetPositionRules.DefaultFor(legacy.Placement)
                : legacy.Anchor,
            OffsetX = documentVersion == 1 ? 0 : legacy.OffsetX,
            OffsetY = documentVersion == 1 ? 0 : legacy.OffsetY,
            MigrationWarning = warning,
        };
        return new MigratedTarget(definition, warning);
    }
}
