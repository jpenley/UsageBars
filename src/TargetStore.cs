using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UsageBars;

internal interface ITargetDocumentStorage
{
    bool Exists { get; }
    string DisplayPath { get; }
    string Read();
    void WriteAtomically(string text);
    void Preserve(string label);
}

internal sealed class JsonTargetFileStorage : ITargetDocumentStorage
{
    private readonly string _path;

    public JsonTargetFileStorage(string path)
    {
        _path = Path.GetFullPath(path);
    }

    public bool Exists => File.Exists(_path);
    public string DisplayPath => _path;

    public string Read() => File.ReadAllText(_path);

    public void WriteAtomically(string text)
    {
        var temp = _path + ".tmp";
        using (var stream = new FileStream(
            temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true))
        {
            writer.Write(text);
            writer.Flush();
            stream.Flush(true);
        }
        File.Move(temp, _path, true);
    }

    public void Preserve(string label)
    {
        var directory = Path.GetDirectoryName(_path)!;
        var recovery = Path.Combine(
            directory, $"targets.{label}.{DateTime.Now:yyyyMMddHHmmssfff}.json");
        File.Copy(_path, recovery);
    }
}

internal sealed record TargetMutationResult(bool Success, string? Error = null, Guid? ExistingId = null);

internal interface ITargetRuleStore
{
    IReadOnlyList<TargetDefinition> Targets { get; }
    event Action? Changed;
    TargetMutationResult Add(TargetDefinition target);
    TargetMutationResult Update(TargetDefinition target);
    TargetMutationResult SetEnabled(Guid id, bool enabled);
    TargetMutationResult Delete(Guid id);
}

internal sealed class TargetStore : ITargetRuleStore
{
    private const int CurrentVersion = 3;
    private const int OldestSupportedVersion = 1;
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly ITargetDocumentStorage _storage;
    private List<TargetDefinition> _targets;
    private string? _preserveBeforeWrite;
    private long _revision;

    public IReadOnlyList<TargetDefinition> Targets => _targets;
    public IReadOnlyList<string> Issues { get; private set; } = Array.Empty<string>();
    public string Location => _storage.DisplayPath;
    public event Action? Changed;

    private TargetStore(
        ITargetDocumentStorage storage, List<TargetDefinition> targets,
        IReadOnlyList<string> issues, string? preserveBeforeWrite)
    {
        _storage = storage;
        _targets = targets;
        Issues = issues;
        _preserveBeforeWrite = preserveBeforeWrite;
        _revision = targets.Count == 0 ? 0 : targets.Max(x => x.Revision);
    }

    public static TargetStore Load(ITargetDocumentStorage storage, Config config)
    {
        if (!storage.Exists)
        {
            var seeded = TargetDefaults.Create(config).ToList();
            var store = new TargetStore(storage, seeded, Array.Empty<string>(), null);
            try { store.Save(seeded); }
            catch (Exception ex)
            {
                store = new TargetStore(
                    storage, seeded, new[] { $"initial target save failed: {ex.Message}" }, null);
            }
            return store;
        }

        try
        {
            using var document = JsonDocument.Parse(storage.Read(), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("target document must be an object");
            if (!document.RootElement.TryGetProperty(nameof(TargetDocument.Version), out var version))
                throw new JsonException("target document version is missing");
            var documentVersion = version.GetInt32();
            if (documentVersion < OldestSupportedVersion || documentVersion > CurrentVersion)
                throw new JsonException($"unsupported target document version {documentVersion}");

            if (!document.RootElement.TryGetProperty(nameof(TargetDocument.Targets), out var targets)
                || targets.ValueKind != JsonValueKind.Array)
                throw new JsonException("target document targets must be an array");

            var issues = new List<string>();
            var valid = new List<TargetDefinition>();
            var ids = new HashSet<Guid>();
            var invalidEntries = false;
            var index = 0;
            foreach (var element in targets.EnumerateArray())
            {
                try
                {
                    if (documentVersion == CurrentVersion)
                    {
                        if (!element.TryGetProperty(nameof(TargetDefinition.Anchor), out _))
                            throw new JsonException("target anchor is missing");
                        if (!element.TryGetProperty(nameof(TargetDefinition.ActivityProcessNames), out var activity)
                            || activity.ValueKind == JsonValueKind.Null)
                            throw new JsonException("target ActivityProcessNames is missing");
                        if (!element.TryGetProperty(nameof(TargetDefinition.Providers), out var providers)
                            || providers.ValueKind != JsonValueKind.Array
                            || providers.EnumerateArray().Any(provider =>
                                !provider.TryGetProperty(nameof(ProviderBinding.Provider), out _)))
                            throw new JsonException("target provider is missing");
                    }
                    if (documentVersion == 2
                        && !element.TryGetProperty(nameof(TargetDefinition.Anchor), out _))
                        throw new JsonException("target anchor is missing");
                    var migrated = documentVersion < CurrentVersion
                        ? TargetDocumentV2Migration.Read(element, documentVersion, ReadOptions)
                        : new MigratedTarget(
                            element.Deserialize<TargetDefinition>(ReadOptions)
                                ?? throw new JsonException("target is null"),
                            null);
                    var target = migrated.Definition;
                    var warning = migrated.Warning ?? target.MigrationWarning;
                    if (warning is not null)
                        issues.Add(FormatMigrationWarningIssue(target.Id, warning));

                    var originalOffsetX = target.OffsetX;
                    var originalOffsetY = target.OffsetY;
                    target = target with
                    {
                        OffsetX = TargetPositionRules.ClampOffset(target.OffsetX),
                        OffsetY = TargetPositionRules.ClampOffset(target.OffsetY),
                    };
                    if (target.OffsetX != originalOffsetX || target.OffsetY != originalOffsetY)
                    {
                        issues.Add(
                            $"target {target.Id}: offsets clamped from ({originalOffsetX}, {originalOffsetY}) "
                            + $"to ({target.OffsetX}, {target.OffsetY})");
                    }
                    var errors = ValidateLoadedTarget(target);
                    if (errors.Count > 0)
                    {
                        issues.Add($"target {index}: {string.Join("; ", errors)}");
                        invalidEntries = true;
                    }
                    else if (!ids.Add(target.Id))
                    {
                        issues.Add($"target {index}: duplicate target id {target.Id}");
                        invalidEntries = true;
                    }
                    else
                    {
                        valid.Add(CanonicalizeActivityProcesses(target));
                    }
                }
                catch (Exception ex)
                {
                    issues.Add($"target {index}: {ex.Message}");
                    invalidEntries = true;
                }
                index++;
            }
            return new TargetStore(storage, valid, issues, invalidEntries ? "invalid" : null);
        }
        catch (Exception ex)
        {
            return new TargetStore(
                storage, TargetDefaults.Create(config).ToList(),
                new[] { ex.Message }, "corrupt");
        }
    }

    public TargetMutationResult Add(TargetDefinition target)
    {
        var errors = target.Validate(target.Source == TargetSource.Captured);
        if (errors.Count > 0) return new(false, string.Join("; ", errors));
        target = CanonicalizeActivityProcesses(target);
        if (_targets.Any(x => x.Id == target.Id))
            return new(false, "A target with this id already exists.", target.Id);
        var existing = _targets.FirstOrDefault(x => x.HasSameIdentity(target));
        if (existing is not null)
            return new(false, "An identical target already exists.", existing.Id);
        return Publish(_targets.Append(target with
        {
            Revision = _revision + 1,
            MigrationWarning = null,
        }).ToList());
    }

    public TargetMutationResult Update(TargetDefinition target)
    {
        if (FindTarget(target.Id, out _) is { } failure) return failure;
        var errors = target.Validate(target.Source == TargetSource.Captured);
        if (errors.Count > 0) return new(false, string.Join("; ", errors));
        target = CanonicalizeActivityProcesses(target);
        var duplicate = _targets.FirstOrDefault(
            x => x.Id != target.Id && x.HasSameIdentity(target));
        if (duplicate is not null)
            return new(false, "An identical target already exists.", duplicate.Id);
        return Publish(_targets.Select(x => x.Id == target.Id
            ? target with { Revision = _revision + 1, MigrationWarning = null }
            : x).ToList());
    }

    public TargetMutationResult SetEnabled(Guid id, bool enabled)
    {
        if (FindTarget(id, out var target) is { } failure) return failure;
        return Update(target with { Enabled = enabled });
    }

    public TargetMutationResult Delete(Guid id)
    {
        if (FindTarget(id, out _) is { } failure) return failure;
        return Publish(_targets.Where(x => x.Id != id).ToList());
    }

    private static IReadOnlyList<string> ValidateLoadedTarget(TargetDefinition target)
    {
        if (target.Providers is null) return new[] { "providers are required" };
        if (target.Providers.Any(binding => binding is null))
            return new[] { "providers cannot contain null values" };
        var errors = target.Validate(target.Source == TargetSource.Captured).ToList();
        if (target.WindowTitle is { } title && string.IsNullOrWhiteSpace(title.Value))
            errors.Add("window title value is required");
        return errors;
    }

    private TargetMutationResult? FindTarget(Guid id, out TargetDefinition target)
    {
        var matches = _targets.Where(candidate => candidate.Id == id).ToList();
        if (matches.Count == 1)
        {
            target = matches[0];
            return null;
        }

        target = null!;
        return matches.Count == 0
            ? new(false, "Target was not found.")
            : new(false, "Target id is ambiguous.");
    }

    private TargetMutationResult Publish(List<TargetDefinition> next)
    {
        try
        {
            next = next.Select(CanonicalizeActivityProcesses).ToList();
            if (_preserveBeforeWrite is { } label)
            {
                _storage.Preserve(label);
                _preserveBeforeWrite = null;
            }
            Save(next);
            ReconcileMigrationWarningIssues(_targets, next);
            _targets = next;
            _revision = next.Count == 0 ? 0 : next.Max(x => x.Revision);
            NotifyChanged();
            return new(true);
        }
        catch (Exception ex) { return new(false, ex.Message); }
    }

    /// <summary>
    /// A successful write is authoritative even if a UI observer has already gone away. Observers
    /// are notified independently so they cannot make a persisted mutation appear to have failed.
    /// </summary>
    private void NotifyChanged()
    {
        var subscribers = Changed;
        if (subscribers is null) return;

        foreach (Action subscriber in subscribers.GetInvocationList())
        {
            try { subscriber(); }
            catch (Exception) { }
        }
    }

    private void Save(List<TargetDefinition> values) =>
        _storage.WriteAtomically(JsonSerializer.Serialize(
            new TargetDocument(CurrentVersion, values), WriteOptions));

    private static TargetDefinition CanonicalizeActivityProcesses(TargetDefinition target) =>
        target with { ActivityProcessNames = ActivityProcessName.NormalizeMany(target.ActivityProcessNames) };

    private static string FormatMigrationWarningIssue(Guid targetId, string warning) =>
        $"target {targetId}: {warning}";

    private void ReconcileMigrationWarningIssues(
        IEnumerable<TargetDefinition> previous, IEnumerable<TargetDefinition> next)
    {
        var previousWarningIssues = previous
            .Where(target => target.MigrationWarning is not null)
            .Select(target => FormatMigrationWarningIssue(target.Id, target.MigrationWarning!))
            .ToHashSet(StringComparer.Ordinal);
        var nextWarningIssues = next
            .Where(target => target.MigrationWarning is not null)
            .Select(target => FormatMigrationWarningIssue(target.Id, target.MigrationWarning!))
            .ToArray();
        var reconciled = Issues
            .Where(issue => !previousWarningIssues.Contains(issue))
            .ToList();
        foreach (var issue in nextWarningIssues)
            if (!reconciled.Contains(issue, StringComparer.Ordinal)) reconciled.Add(issue);
        Issues = reconciled;
    }
}

internal static class TargetDiagnostics
{
    public static void Append(
        StringBuilder text, TargetStore store, IReadOnlyDictionary<ProviderId, string> rejections)
    {
        var enabled = store.Targets.Count(target => target.Enabled);
        var disabled = store.Targets.Count - enabled;

        text.AppendLine("== dynamic targets ==");
        text.AppendLine($"location       : {store.Location}");
        text.AppendLine($"enabled        : {enabled}");
        text.AppendLine($"disabled       : {disabled}");
        foreach (var target in store.Targets)
        {
            var matcher = string.Join(", ", new[]
            {
                target.ProcessName is null ? null : $"process={target.ProcessName}",
                target.WindowClass is null ? null : $"class={target.WindowClass}",
                target.WindowTitle is null ? null : $"title={target.WindowTitle.Kind}:{target.WindowTitle.Value}",
                $"activity={TargetRuleText.FormatActivity(target)}",
                target.RequireResizableFrame ? "resizable" : null,
            }.Where(part => part is not null));
            var providers = string.Join(", ", target.Providers.Select(binding => binding.Provider));
            text.AppendLine($"target {target.Id} · {target.Name}");
            text.AppendLine($"  source={target.Source} · enabled={target.Enabled} · providers={providers}");
            text.AppendLine($"  matcher={matcher} · placement={target.Placement} · activation={target.Activation} · revision={target.Revision}");
        }

        foreach (var issue in store.Issues)
            text.AppendLine($"recovery issue : {issue}");
        foreach (var (provider, reason) in rejections)
            text.AppendLine($"{provider} rejection: {reason}");
        text.AppendLine();
    }
}
