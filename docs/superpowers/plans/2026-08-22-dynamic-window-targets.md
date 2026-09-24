# Dynamic Window Targets Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace hard-coded host-window discovery with a persisted, user-manageable target list and a tray-driven click-to-target workflow for Claude and Codex.

**Architecture:** A versioned `TargetStore` owns immutable target snapshots and atomic JSON persistence. Pure matching, provider-activity, capture, and inference services sit between Win32 snapshots and the existing pill lifecycle; WinForms dialogs and tray commands only edit store models. The overlay controller subscribes to store changes and feeds a single dynamic resolver while retaining existing provider models, pill placement, and ownership behavior.

**Tech Stack:** C# 12, .NET 8 `net8.0-windows`, WPF application host, WinForms tray/dialogs, Win32 P/Invoke, `System.Text.Json`, xUnit 2.9.

## Global Constraints

- Remain a Windows-only, self-contained, single-file-compatible application; add no NuGet dependencies.
- Persist targets at `AppPaths.TargetsFile` under the existing portable-first `UsageBars.data` root.
- Seed current Claude Desktop, Codex/ChatGPT, terminal, Rider, VS Code, and supported-editor behavior exactly once when no target document exists.
- Keep `EnableClaude` and `EnableCodex` as global master switches; targeting-specific legacy config values affect seeding only.
- Captured rules always persist executable name plus top-level window class; never persist HWND or PID.
- Allow only Claude, Codex, or both; arbitrary providers and per-target pill styling remain out of scope.
- Publish store changes only after an atomic save succeeds, and preserve malformed/partially invalid source documents before recovery writes.
- Produce at most one pill per provider/window pair and resolve overlaps by specificity, then revision.
- Remove picker hooks, banner, and cursor state after success, cancellation, error, or disposal.
- The current folder has no `.git` metadata. Do not initialize a repository without authorization. Run each listed commit once execution occurs in the canonical Git checkout; otherwise record the checkpoint and continue only with explicit user approval.

---

## File Structure

- Create `src/WindowTargets.cs` — serialized target domain types, validation, matching, specificity, and target draft types.
- Create `src/TargetDefaults.cs` — one-time conversion of legacy targeting config into seeded target definitions.
- Create `src/TargetStore.cs` — versioned JSON document, storage abstraction, recovery, atomic writes, mutations, and change notification.
- Create `src/TargetResolver.cs` — evaluate target definitions and provider activity against window snapshots and select one host per provider/window.
- Create `src/TargetCapture.cs` — top-level window capture, provider inference, and editor draft creation.
- Create `src/WindowPicker.cs` — low-level mouse/keyboard hook lifecycle and picker orchestration.
- Create `src/PickerBanner.cs` — non-activating picker instructions.
- Create `src/TargetEditorForm.cs` — confirm/edit target name, providers, placement, activation, and enabled state.
- Create `src/TargetManagerForm.cs` — list, details, enable/disable, edit, and confirmed deletion.
- Create `src/TargetViewModels.cs` — UI-only formatting and validation, testable without desktop automation.
- Modify `src/AppPaths.cs` — add `TargetsFile`.
- Modify `src/Native.cs` — add hook, point lookup, cursor, and root-window declarations.
- Modify `src/HostWindow.cs` — retain process-tree/candidate/geometry types, remove static host catalogs and old `HostResolver`.
- Modify `src/Tray.cs` — add Targets submenu, summary, and events.
- Modify `src/OverlayController.cs` — own store/resolver/picker/forms, react to target changes, and extend diagnostics.
- Modify `README.md` and `UsageBars.config.default.json` — document target workflow and seed-only legacy fields.
- Create tests: `WindowTargetTests.cs`, `TargetStoreTests.cs`, `TargetResolverTests.cs`, `TargetCaptureTests.cs`, `WindowPickerTests.cs`, and `TargetViewModelTests.cs`.
- Modify `HostCreditingTests.cs`, `ConfigAndPlacementTests.cs`, and `UsageModelTests.cs` to assert seeded dynamic behavior instead of removed static catalogs.

---

### Task 1: Target domain, validation, and deterministic matching

**Files:**
- Create: `src/WindowTargets.cs`
- Create: `tests/UsageBars.Tests/WindowTargetTests.cs`

**Interfaces:**
- Produces: `ProviderBinding(ProviderId Provider, string CliProcessName, string? ActivityTitlePattern)`.
- Produces: `TargetDefinition` with `Validate(bool captured)`, `Matches(WindowSnapshot)`, `Specificity`, `HasSameIdentity(TargetDefinition)`, and `Overlaps(TargetDefinition)`.
- Produces: `WindowSnapshot(IntPtr Hwnd, uint Pid, string ProcessName, string ClassName, string Title, bool IsResizable)`.
- Produces: `TargetDraft` for Task 4 and `TargetDocument` for Task 2.

- [ ] **Step 1: Write failing domain and matcher tests**

~~~csharp
using System;
using System.Collections.Generic;
using Xunit;

namespace UsageBars.Tests;

public class WindowTargetTests
{
    private static WindowSnapshot Window(
        string process = "rider64", string @class = "SunAwtFrame",
        string title = "UsageBars", bool resizable = true) =>
        new(new IntPtr(42), 7, process, @class, title, resizable);

    [Fact]
    public void CapturedTargetsRequireProcessClassAndProvider()
    {
        var target = TargetDefinition.Captured(
            "Rider", "", "SunAwtFrame", new[] { ProviderBinding.Claude },
            TargetPlacementKind.Ide, TargetActivationPolicy.AlwaysWhileOpen, true, 26);

        Assert.Contains(target.Validate(captured: true), x => x.Contains("process", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProcessAndClassMatchingIsCaseInsensitive()
    {
        var target = TargetDefinition.Captured(
            "Rider", "RIDER64", "sunawtframe", new[] { ProviderBinding.Claude },
            TargetPlacementKind.Ide, TargetActivationPolicy.AlwaysWhileOpen, true, 26);

        Assert.True(target.Matches(Window()));
    }

    [Fact]
    public void ATitleRuleOutranksProcessAndClass()
    {
        var general = TargetDefinition.Captured(
            "Terminal", "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS",
            new[] { ProviderBinding.Codex }, TargetPlacementKind.Terminal,
            TargetActivationPolicy.RequireProviderActivity, true, 0);
        var specific = general with
        {
            Id = Guid.NewGuid(),
            Name = "Codex tab",
            WindowTitle = new TitleRule(TitleMatchKind.Prefix, "codex"),
        };

        Assert.True(specific.Specificity > general.Specificity);
        Assert.True(specific.Matches(Window(
            "windowsterminal", "CASCADIA_HOSTING_WINDOW_CLASS", "Codex session")));
    }

    [Fact]
    public void SeededProcessOnlyRulesMustRequireAResizableFrame()
    {
        var target = TargetDefinition.Seeded(
            "Cursor", "cursor", null, new[] { ProviderBinding.Claude },
            TargetPlacementKind.Terminal, TargetActivationPolicy.RequireProviderActivity,
            requireResizableFrame: true, statusBarHeight: 0);

        Assert.False(target.Matches(Window("cursor", "#32768", resizable: false)));
        Assert.True(target.Matches(Window("cursor", "Chrome_WidgetWin_1", resizable: true)));
    }

    [Fact]
    public void IdenticalIdentityIgnoresNameEnabledAndRevision()
    {
        var first = TargetDefinition.Captured(
            "A", "code", "Chrome_WidgetWin_1", new[] { ProviderBinding.Claude },
            TargetPlacementKind.Ide, TargetActivationPolicy.AlwaysWhileOpen, true, 22);
        var second = first with { Id = Guid.NewGuid(), Name = "B", Enabled = false, Revision = 99 };

        Assert.True(first.HasSameIdentity(second));
    }

    [Fact]
    public void AClassOnlyProviderRuleOverlapsAMoreSpecificRuleForTheSameProvider()
    {
        var broad = TargetDefinition.Seeded(
            "All Cascadia", null, "CASCADIA_HOSTING_WINDOW_CLASS",
            new[] { ProviderBinding.Claude }, TargetPlacementKind.Terminal,
            TargetActivationPolicy.RequireProviderActivity, false, 0);
        var narrow = TargetDefinition.Captured(
            "Windows Terminal", "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS",
            new[] { ProviderBinding.Claude }, TargetPlacementKind.Terminal,
            TargetActivationPolicy.RequireProviderActivity, true, 0);

        Assert.True(broad.Overlaps(narrow));
    }

    [Fact]
    public void InvalidActivityRegexIsAValidationError()
    {
        var target = TargetDefinition.Captured(
            "Terminal", "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS",
            new[] { new ProviderBinding(ProviderId.Claude, "claude", "([broken") },
            TargetPlacementKind.Terminal, TargetActivationPolicy.RequireProviderActivity, true, 0);

        Assert.Contains(target.Validate(captured: true), x => x.Contains("regex"));
    }
}
~~~

- [ ] **Step 2: Run the focused tests and verify the new types are absent**

Run: `dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter FullyQualifiedName~WindowTargetTests`

Expected: FAIL with compiler errors for `WindowSnapshot` and `TargetDefinition`.

- [ ] **Step 3: Implement the serialized types and pure matcher**

~~~csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace UsageBars;

internal enum TargetPlacementKind { Desktop, Terminal, Ide }
internal enum TargetActivationPolicy { AlwaysWhileOpen, RequireProviderActivity }
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

internal sealed record ProviderBinding(
    ProviderId Provider, string CliProcessName, string? ActivityTitlePattern)
{
    public static ProviderBinding Claude => new(ProviderId.Claude, "claude", null);
    public static ProviderBinding Codex => new(ProviderId.Codex, "codex", null);
}

internal sealed record WindowSnapshot(
    IntPtr Hwnd, uint Pid, string ProcessName, string ClassName, string Title, bool IsResizable);

internal sealed record TargetDefinition
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public List<ProviderBinding> Providers { get; init; } = new();
    public string? ProcessName { get; init; }
    public string? WindowClass { get; init; }
    public bool RequireResizableFrame { get; init; }
    public TitleRule? WindowTitle { get; init; }
    public TargetPlacementKind Placement { get; init; }
    public TargetActivationPolicy Activation { get; init; }
    public TargetSource Source { get; init; }
    public double StatusBarHeight { get; init; }
    public long Revision { get; init; }

    [JsonIgnore]
    public int Specificity =>
        (ProcessName is not null ? 4 : 0)
        + (WindowClass is not null ? 2 : 0)
        + (WindowTitle is not null ? 8 : 0)
        + (RequireResizableFrame ? 1 : 0);

    public static TargetDefinition Captured(
        string name, string process, string windowClass, IEnumerable<ProviderBinding> providers,
        TargetPlacementKind placement, TargetActivationPolicy activation,
        bool requireResizableFrame, double statusBarHeight) =>
        new()
        {
            Name = name, ProcessName = process, WindowClass = windowClass,
            Providers = providers.DistinctBy(x => x.Provider).ToList(),
            Placement = placement, Activation = activation, Source = TargetSource.Captured,
            RequireResizableFrame = requireResizableFrame, StatusBarHeight = statusBarHeight,
        };

    public static TargetDefinition Seeded(
        string name, string? process, string? windowClass, IEnumerable<ProviderBinding> providers,
        TargetPlacementKind placement, TargetActivationPolicy activation,
        bool requireResizableFrame, double statusBarHeight) =>
        Captured(name, process ?? "", windowClass ?? "", providers, placement, activation,
            requireResizableFrame, statusBarHeight) with
        {
            ProcessName = string.IsNullOrWhiteSpace(process) ? null : process,
            WindowClass = string.IsNullOrWhiteSpace(windowClass) ? null : windowClass,
            Source = TargetSource.Seeded,
        };

    public IReadOnlyList<string> Validate(bool captured)
    {
        var errors = new List<string>();
        if (Id == Guid.Empty) errors.Add("id is empty");
        if (string.IsNullOrWhiteSpace(Name)) errors.Add("name is required");
        if (Providers.Count == 0) errors.Add("at least one provider is required");
        if (Providers.Select(x => x.Provider).Distinct().Count() != Providers.Count)
            errors.Add("providers must be unique");
        foreach (var binding in Providers)
        {
            if (string.IsNullOrWhiteSpace(binding.CliProcessName))
                errors.Add($"{binding.Provider} CLI process is required");
            if (binding.ActivityTitlePattern is { } pattern && !ValidRegex(pattern))
                errors.Add($"{binding.Provider} activity title regex is invalid");
        }
        if (captured && string.IsNullOrWhiteSpace(ProcessName)) errors.Add("captured process is required");
        if (captured && string.IsNullOrWhiteSpace(WindowClass)) errors.Add("captured window class is required");
        if (ProcessName is null && WindowClass is null) errors.Add("process or window class is required");
        if (ProcessName is not null && WindowClass is null && !RequireResizableFrame)
            errors.Add("process-only rules must require a resizable frame");
        if (StatusBarHeight < 0 || !double.IsFinite(StatusBarHeight))
            errors.Add("status-bar height must be finite and non-negative");
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
        && WindowTitle == other.WindowTitle
        && Placement == other.Placement
        && Activation == other.Activation
        && Providers.OrderBy(x => x.Provider).SequenceEqual(
            other.Providers.OrderBy(x => x.Provider));

    public bool Overlaps(TargetDefinition other)
    {
        var sameProcessSpace = ProcessName is null || other.ProcessName is null
            || ProcessName.Equals(other.ProcessName, StringComparison.OrdinalIgnoreCase);
        var sameClassSpace = WindowClass is null || other.WindowClass is null
            || WindowClass.Equals(other.WindowClass, StringComparison.OrdinalIgnoreCase);
        var sharedProvider = Providers.Select(x => x.Provider)
            .Intersect(other.Providers.Select(x => x.Provider)).Any();
        return Id != other.Id && sameProcessSpace && sameClassSpace && sharedProvider;
    }
}

internal sealed record TargetDraft(
    TargetDefinition Definition, string CapturedTitle, IReadOnlyList<string> InferenceNotes);
internal sealed record TargetDocument(int Version, List<TargetDefinition> Targets);
~~~

- [ ] **Step 4: Run matcher tests**

Run: `dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter FullyQualifiedName~WindowTargetTests`

Expected: PASS.

- [ ] **Step 5: Commit the domain checkpoint**

~~~powershell
git add src/WindowTargets.cs tests/UsageBars.Tests/WindowTargetTests.cs
git commit -m "feat: add dynamic window target model"
~~~

---

### Task 2: Seed defaults and persist a recoverable target store

**Files:**
- Create: `src/TargetDefaults.cs`
- Create: `src/TargetStore.cs`
- Modify: `src/AppPaths.cs:104-126`
- Create: `tests/UsageBars.Tests/TargetStoreTests.cs`
- Modify: `tests/UsageBars.Tests/ConfigAndPlacementTests.cs:403-527`
- Modify: `tests/UsageBars.Tests/UsageModelTests.cs:200-270`

**Interfaces:**
- Consumes: Task 1 target records.
- Produces: `TargetDefaults.Create(Config)`.
- Produces: `ITargetDocumentStorage` and `JsonTargetFileStorage`.
- Produces: `TargetStore.Load(ITargetDocumentStorage, Config)`, `Targets`, `Issues`, `Changed`, `Add`, `Update`, `SetEnabled`, and `Delete`.
- Produces: `TargetMutationResult(bool Success, string? Error, Guid? ExistingId)`.

- [ ] **Step 1: Write failing seed, persistence, duplicate, and recovery tests**

~~~csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace UsageBars.Tests;

public class TargetStoreTests
{
    [Fact]
    public void MissingDocumentSeedsLegacyBehaviorAndPersistsIt()
    {
        var storage = new MemoryTargetStorage();
        var store = TargetStore.Load(storage, new Config
        {
            AttachToClaudeCodeTerminals = false,
            AttachToCodexTerminals = true,
        });

        Assert.Contains(store.Targets, x => x.ProcessName == "claude");
        Assert.Contains(store.Targets, x => x.ProcessName == "ChatGPT");
        Assert.Contains(store.Targets, x => x.ProcessName == "rider64" && x.StatusBarHeight == 26);
        Assert.Contains(store.Targets, x => x.ProcessName == "code" && x.StatusBarHeight == 22);
        Assert.Contains(store.Targets, x =>
            x.WindowClass == "CASCADIA_HOSTING_WINDOW_CLASS"
            && x.Providers.Any(p => p.Provider == ProviderId.Claude)
            && !x.Enabled);
        Assert.True(storage.Exists);
    }

    [Fact]
    public void FailedSaveDoesNotPublishTheMutation()
    {
        var storage = new MemoryTargetStorage();
        var store = TargetStore.Load(storage, new Config());
        var count = store.Targets.Count;
        storage.WriteError = new UnauthorizedAccessException("locked");

        var result = store.Add(TargetDefinition.Captured(
            "Notepad", "notepad", "Notepad", new[] { ProviderBinding.Claude },
            TargetPlacementKind.Desktop, TargetActivationPolicy.AlwaysWhileOpen, true, 0));

        Assert.False(result.Success);
        Assert.Equal(count, store.Targets.Count);
    }

    [Fact]
    public void DuplicateAddReturnsTheExistingId()
    {
        var storage = new MemoryTargetStorage();
        var store = TargetStore.Load(storage, new Config());
        var existing = store.Targets.First();

        var result = store.Add(existing with { Id = Guid.NewGuid(), Name = "Duplicate" });

        Assert.False(result.Success);
        Assert.Equal(existing.Id, result.ExistingId);
    }

    [Fact]
    public void CorruptInputIsPreservedBeforeFirstRecoveryWrite()
    {
        var storage = new MemoryTargetStorage("{not-json");
        var store = TargetStore.Load(storage, new Config());

        var result = store.SetEnabled(store.Targets[0].Id, false);

        Assert.True(result.Success);
        Assert.Contains("corrupt", storage.PreservedLabels);
    }
}
~~~

Add `MemoryTargetStorage` in the same test file implementing the exact `ITargetDocumentStorage` interface from Step 3; it stores `Text`, throws `WriteError` from `WriteAtomically`, and records labels passed to `Preserve`.

- [ ] **Step 2: Run store tests and verify they fail**

Run: `dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter FullyQualifiedName~TargetStoreTests`

Expected: FAIL because `TargetStore` and `TargetDefaults` do not exist.

- [ ] **Step 3: Implement complete default seeding**

~~~csharp
internal static class TargetDefaults
{
    private static readonly string[] TerminalClasses =
    {
        "CASCADIA_HOSTING_WINDOW_CLASS", "ConsoleWindowClass", "VirtualConsoleClass",
        "mintty", "Alacritty", "org.wezfurlong.wezterm",
    };

    private static readonly string[] GenericEditors =
    {
        "codium", "cursor", "windsurf", "devenv", "idea64", "idea", "pycharm64",
        "webstorm64", "clion64", "goland64", "phpstorm64", "rubymine64", "datagrip64",
        "sublime_text", "atom", "zed", "nvim-qt", "neovide", "emacs",
    };

    public static IReadOnlyList<TargetDefinition> Create(Config config)
    {
        var revision = 0L;
        TargetDefinition Stamp(TargetDefinition value) => value with { Revision = ++revision };
        var result = new List<TargetDefinition>
        {
            Stamp(TargetDefinition.Seeded(
                "Claude Desktop", "claude", "Chrome_WidgetWin_1",
                new[] { ProviderBinding.Claude }, TargetPlacementKind.Desktop,
                TargetActivationPolicy.AlwaysWhileOpen, true, 0)),
            Stamp(TargetDefinition.Seeded(
                "Codex / ChatGPT", "ChatGPT", "Chrome_WidgetWin_1",
                new[] { ProviderBinding.Codex }, TargetPlacementKind.Desktop,
                TargetActivationPolicy.AlwaysWhileOpen, true, 0)),
        };

        foreach (var className in TerminalClasses)
        {
            result.Add(Stamp(TargetDefinition.Seeded(
                $"Claude terminal ({className})", null, className,
                new[] { new ProviderBinding(ProviderId.Claude, "claude", config.TerminalTitlePattern) },
                TargetPlacementKind.Terminal, TargetActivationPolicy.RequireProviderActivity,
                false, 0) with { Enabled = config.AttachToClaudeCodeTerminals }));
            result.Add(Stamp(TargetDefinition.Seeded(
                $"Codex terminal ({className})", null, className,
                new[] { new ProviderBinding(ProviderId.Codex, "codex", config.CodexTitlePattern) },
                TargetPlacementKind.Terminal, TargetActivationPolicy.RequireProviderActivity,
                false, 0) with { Enabled = config.AttachToCodexTerminals }));
        }

        foreach (var (process, frame, height) in new[]
        {
            ("rider64", "SunAwtFrame", 26d), ("rider", "SunAwtFrame", 26d),
            ("code", "Chrome_WidgetWin_1", 22d),
            ("code - insiders", "Chrome_WidgetWin_1", 22d),
        })
        {
            result.Add(Stamp(TargetDefinition.Seeded(
                $"{process} IDE", process, frame,
                new[] { ProviderBinding.Claude, ProviderBinding.Codex },
                TargetPlacementKind.Ide,
                config.AlwaysShowOnIdes
                    ? TargetActivationPolicy.AlwaysWhileOpen
                    : TargetActivationPolicy.RequireProviderActivity,
                false, height)));
        }

        foreach (var process in GenericEditors)
            result.Add(Stamp(TargetDefinition.Seeded(
                $"{process} editor", process, null,
                new[] { ProviderBinding.Claude, ProviderBinding.Codex },
                TargetPlacementKind.Terminal, TargetActivationPolicy.RequireProviderActivity,
                true, 0)));

        return result;
    }
}
~~~

- [ ] **Step 4: Implement versioned storage and save-before-publish mutations**

~~~csharp
internal interface ITargetDocumentStorage
{
    bool Exists { get; }
    string DisplayPath { get; }
    string Read();
    void WriteAtomically(string text);
    void Preserve(string label);
}

internal sealed record TargetMutationResult(bool Success, string? Error = null, Guid? ExistingId = null);

internal sealed class TargetStore
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
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
    public IReadOnlyList<string> Issues { get; private init; } = Array.Empty<string>();
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
            var document = JsonSerializer.Deserialize<TargetDocument>(storage.Read(), ReadOptions)
                ?? throw new JsonException("target document is empty");
            if (document.Version != CurrentVersion)
                throw new JsonException($"unsupported target document version {document.Version}");
            var issues = new List<string>();
            var valid = document.Targets.Where(target =>
            {
                var errors = target.Validate(target.Source == TargetSource.Captured);
                if (errors.Count == 0) return true;
                issues.Add($"{target.Id}: {string.Join("; ", errors)}");
                return false;
            }).ToList();
            return new TargetStore(storage, valid, issues, issues.Count > 0 ? "invalid" : null);
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
        var existing = _targets.FirstOrDefault(x => x.HasSameIdentity(target));
        if (existing is not null)
            return new(false, "An identical target already exists.", existing.Id);
        return Publish(_targets.Append(target with { Revision = ++_revision }).ToList());
    }

    public TargetMutationResult Update(TargetDefinition target)
    {
        var errors = target.Validate(target.Source == TargetSource.Captured);
        if (errors.Count > 0) return new(false, string.Join("; ", errors));
        var duplicate = _targets.FirstOrDefault(
            x => x.Id != target.Id && x.HasSameIdentity(target));
        if (duplicate is not null)
            return new(false, "An identical target already exists.", duplicate.Id);
        return Publish(_targets.Select(x => x.Id == target.Id
            ? target with { Revision = ++_revision }
            : x).ToList());
    }

    public TargetMutationResult SetEnabled(Guid id, bool enabled) =>
        Update(_targets.Single(x => x.Id == id) with { Enabled = enabled });

    public TargetMutationResult Delete(Guid id) =>
        Publish(_targets.Where(x => x.Id != id).ToList());

    private TargetMutationResult Publish(List<TargetDefinition> next)
    {
        try
        {
            if (_preserveBeforeWrite is { } label)
            {
                _storage.Preserve(label);
                _preserveBeforeWrite = null;
            }
            Save(next);
            _targets = next;
            Changed?.Invoke();
            return new(true);
        }
        catch (Exception ex) { return new(false, ex.Message); }
    }

    private void Save(List<TargetDefinition> values) =>
        _storage.WriteAtomically(JsonSerializer.Serialize(
            new TargetDocument(CurrentVersion, values), WriteOptions));
}
~~~

`JsonTargetFileStorage.DisplayPath` returns its full path. `JsonTargetFileStorage.WriteAtomically` must write `path + ".tmp"` with `FileOptions.WriteThrough`, flush with `Flush(true)`, then use `File.Move(temp, path, true)`. `Preserve(label)` copies the current file to `targets.{label}.{yyyyMMddHHmmssfff}.json` in the same directory before the recovery write. `MemoryTargetStorage.DisplayPath` returns `(memory)`.

- [ ] **Step 5: Add the target path and convert legacy static-catalog tests**

Add to `AppPaths.cs`:

~~~csharp
public static string TargetsFile => Path.Combine(DataRoot, "targets.json");
~~~

Replace `IdeFrameTests` assertions against `HostResolver.IsIdeFrame` with assertions against `TargetDefaults.Create(new Config())` and `TargetDefinition.Matches`. Keep process-tree depth, terminal-title specificity, and desktop exact-name regressions; point their expected identities at seeded definitions.

- [ ] **Step 6: Run store, matching, and migrated compatibility tests**

Run: `dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter "FullyQualifiedName~TargetStoreTests|FullyQualifiedName~WindowTargetTests|FullyQualifiedName~IdeFrameTests|FullyQualifiedName~ProviderTarget"`

Expected: PASS.

- [ ] **Step 7: Commit persistence and seeding**

~~~powershell
git add src/AppPaths.cs src/TargetDefaults.cs src/TargetStore.cs tests/UsageBars.Tests/TargetStoreTests.cs tests/UsageBars.Tests/ConfigAndPlacementTests.cs tests/UsageBars.Tests/UsageModelTests.cs
git commit -m "feat: persist and seed window targets"
~~~

---

### Task 3: Resolve dynamic targets into existing host pills

**Files:**
- Create: `src/TargetResolver.cs`
- Modify: `src/HostWindow.cs:1-648`
- Create: `tests/UsageBars.Tests/TargetResolverTests.cs`
- Modify: `tests/UsageBars.Tests/HostCreditingTests.cs`

**Interfaces:**
- Consumes: `TargetDefinition`, `WindowSnapshot`, `ProcessTree`, and `HostWindowInfo`.
- Produces: `TargetResolver.Resolve(IReadOnlyList<WindowSnapshot>, IReadOnlyList<TargetDefinition>, ProcessTree, Config, uint)`.
- Produces: `TargetResolver.FindAll(IReadOnlyList<HostCandidate>, IReadOnlyList<TargetDefinition>, Config)` and `LastRejections`.

- [ ] **Step 1: Write failing activation, overlap, and master-switch tests**

~~~csharp
[Fact]
public void AlwaysOpenTargetNeedsNoCliEvidence()
{
    var target = TargetDefinition.Captured(
        "Rider", "rider64", "SunAwtFrame",
        new[] { ProviderBinding.Claude, ProviderBinding.Codex },
        TargetPlacementKind.Ide, TargetActivationPolicy.AlwaysWhileOpen, false, 26);

    var hosts = TargetResolver.Resolve(
        new[] { Window("rider64", "SunAwtFrame") }, new[] { target },
        ProcessTree.FromMap(Array.Empty<(uint, uint, string)>()), new Config(), 999);

    Assert.Equal(2, hosts.Count);
    Assert.All(hosts, host => Assert.Equal(HostKind.Ide, host.Kind));
}

[Fact]
public void ActivityTargetRequiresTitleOrAncestry()
{
    var target = TerminalTarget(ProviderId.Claude, @"(?i)claude code");
    var empty = ProcessTree.FromMap(Array.Empty<(uint, uint, string)>());

    Assert.Empty(TargetResolver.Resolve(
        new[] { Window("WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS", "pwsh") },
        new[] { target }, empty, new Config(), 999));
}

[Fact]
public void SpecificThenNewestRuleWinsWithoutDuplicateHosts()
{
    var general = TargetDefinition.Seeded(
        "General", null, "CASCADIA_HOSTING_WINDOW_CLASS",
        new[] { ProviderBinding.Claude }, TargetPlacementKind.Terminal,
        TargetActivationPolicy.AlwaysWhileOpen, false, 0) with { Revision = 10 };
    var specific = TargetDefinition.Captured(
        "Specific", "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS",
        new[] { ProviderBinding.Claude }, TargetPlacementKind.Desktop,
        TargetActivationPolicy.AlwaysWhileOpen, true, 0) with { Revision = 1 };

    var hosts = TargetResolver.Resolve(
        new[] { Window("WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS") },
        new[] { general, specific }, EmptyTree(), new Config(), 999);

    var host = Assert.Single(hosts);
    Assert.Equal(HostKind.Desktop, host.Kind);
    Assert.Equal(specific.Id, host.TargetId);
}

[Fact]
public void GlobalProviderSwitchStillWins()
{
    var target = AlwaysTarget(ProviderId.Claude);
    var config = new Config { EnableClaude = false };

    Assert.Empty(TargetResolver.Resolve(
        new[] { Window("app", "frame") }, new[] { target }, EmptyTree(), config, 999));
}
~~~

Define local helpers in the test file with fixed HWND `42` and PID `7` so assertions are deterministic.

- [ ] **Step 2: Run resolver tests and verify failure**

Run: `dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter FullyQualifiedName~TargetResolverTests`

Expected: FAIL because `TargetResolver` and `HostWindowInfo.TargetId` do not exist.

- [ ] **Step 3: Add the target ID and implement pure resolution**

~~~csharp
internal sealed record HostWindowInfo(
    IntPtr Hwnd, HostKind Kind, string Description, ProviderId Provider, HostEvidence Evidence,
    double StatusBarHeight = 0, Guid TargetId = default);

internal sealed class TargetResolver
{
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    public IReadOnlyDictionary<ProviderId, string> LastRejections { get; private set; } =
        new Dictionary<ProviderId, string>();

    internal static List<HostWindowInfo> Resolve(
        IReadOnlyList<WindowSnapshot> windows, IReadOnlyList<TargetDefinition> targets,
        ProcessTree tree, Config config, uint ownPid)
    {
        var matches = new List<(WindowSnapshot Window, TargetDefinition Target,
            ProviderBinding Binding, HostEvidence Evidence)>();

        foreach (var window in windows)
        foreach (var target in targets.Where(x => x.Enabled && x.Matches(window)))
        foreach (var binding in target.Providers)
        {
            if (!ProviderEnabled(binding.Provider, config)) continue;
            var evidence = EvidenceFor(target, binding, window, tree, ownPid);
            if (evidence is not null) matches.Add((window, target, binding, evidence.Value));
        }

        return matches
            .GroupBy(x => (x.Binding.Provider, x.Window.Hwnd))
            .Select(group => group
                .OrderByDescending(x => x.Target.Specificity)
                .ThenByDescending(x => x.Target.Revision)
                .First())
            .Select(x => new HostWindowInfo(
                x.Window.Hwnd, KindFor(x.Target.Placement), x.Target.Name,
                x.Binding.Provider, x.Evidence, x.Target.StatusBarHeight, x.Target.Id))
            .ToList();
    }

    private static HostEvidence? EvidenceFor(
        TargetDefinition target, ProviderBinding binding, WindowSnapshot window,
        ProcessTree tree, uint ownPid)
    {
        if (target.Activation == TargetActivationPolicy.AlwaysWhileOpen)
            return target.Placement == TargetPlacementKind.Desktop
                ? HostEvidence.Desktop : HostEvidence.Open;

        if (!string.IsNullOrWhiteSpace(binding.ActivityTitlePattern)
            && SafeTitleMatch(window.Title, binding.ActivityTitlePattern!)
            && tree.AnyRunning(binding.CliProcessName, ownPid))
            return HostEvidence.Title;

        return tree.HasProcessUnder(window.Pid, binding.CliProcessName, ownPid)
            ? HostEvidence.Ancestry
            : null;
    }

    private static bool ProviderEnabled(ProviderId provider, Config config) =>
        provider == ProviderId.Claude ? config.EnableClaude : config.EnableCodex;

    private static HostKind KindFor(TargetPlacementKind placement) => placement switch
    {
        TargetPlacementKind.Desktop => HostKind.Desktop,
        TargetPlacementKind.Ide => HostKind.Ide,
        _ => HostKind.Terminal,
    };
}
~~~

Use the same 100 ms regex timeout and exception handling as `TitleRule` for `SafeTitleMatch`.

- [ ] **Step 4: Implement native candidate snapshots and remove the old resolver catalog**

`TargetResolver.FindAll` captures `ProcessTreeCache.Current()` once, maps candidates to `WindowSnapshot` using `ProcessTree.NameOf`, `Native.ClassNameOf`, `Native.WindowTextOf`, and `DesktopFrame.IsResizableFrame`, then calls `Resolve`. Populate `LastRejections` for both providers when no host survives.

Delete `TerminalClasses`, `EditorProcesses`, `IdeFrames`, and the old per-provider `HostResolver.Resolve` path from `HostWindow.cs`. Keep `ProviderTarget` as provider display/config metadata used by `ProviderPill` and existing data-source tests.

- [ ] **Step 5: Run resolver and process-tree regressions**

Run: `dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter "FullyQualifiedName~TargetResolverTests|FullyQualifiedName~HostCreditingTests|FullyQualifiedName~TitlePatternTests"`

Expected: PASS, including the browser-depth, shell-ancestor, own-child, and title-specificity regressions.

- [ ] **Step 6: Commit dynamic resolution**

~~~powershell
git add src/TargetResolver.cs src/HostWindow.cs tests/UsageBars.Tests/TargetResolverTests.cs tests/UsageBars.Tests/HostCreditingTests.cs
git commit -m "refactor: resolve hosts from dynamic targets"
~~~

---

### Task 4: Capture a clicked top-level window and infer providers

**Files:**
- Create: `src/TargetCapture.cs`
- Create: `tests/UsageBars.Tests/TargetCaptureTests.cs`

**Interfaces:**
- Consumes: target snapshots/store records and `ProcessTree`.
- Produces: `IWindowIntrospection`, `IWindowCaptureService.CaptureAt(Native.POINT)`, `WindowCaptureService`, and `ProviderInference.CreateDraft(...)`.
- Produces: `WindowCaptureResult(bool Success, WindowSnapshot? Window, string? Error)`.

- [ ] **Step 1: Write failing root-resolution, self-filter, inference, and no-evidence tests**

~~~csharp
[Fact]
public void AClickedChildIsCapturedAsItsTopLevelWindow()
{
    var native = new FakeIntrospection
    {
        PointWindow = new IntPtr(100),
        RootWindow = new IntPtr(200),
        Snapshot = Window("rider64", "SunAwtFrame"),
    };

    var result = new WindowCaptureService(native, ownPid: 999).CaptureAt(new Native.POINT());

    Assert.True(result.Success);
    Assert.Equal(new IntPtr(200), result.Window!.Hwnd);
}

[Fact]
public void OwnWindowsAndTheTaskbarAreRejected()
{
    var own = FakeIntrospection.For(Window("UsageBars", "HwndWrapper", pid: 999));
    var shell = FakeIntrospection.For(Window("explorer", "Shell_TrayWnd", pid: 20));

    Assert.False(new WindowCaptureService(own, 999).CaptureAt(new Native.POINT()).Success);
    Assert.False(new WindowCaptureService(shell, 999).CaptureAt(new Native.POINT()).Success);
}

[Fact]
public void DesktopIdentityPreselectsItsProviderAndAlwaysOpen()
{
    var window = Window("ChatGPT", "Chrome_WidgetWin_1");

    var draft = ProviderInference.CreateDraft(
        window, TargetDefaults.Create(new Config()), EmptyTree(), new Config(), 999);

    Assert.Contains(draft.Definition.Providers, p => p.Provider == ProviderId.Codex);
    Assert.Equal(TargetActivationPolicy.AlwaysWhileOpen, draft.Definition.Activation);
    Assert.Equal(TargetPlacementKind.Desktop, draft.Definition.Placement);
}

[Fact]
public void NoEvidenceStillProducesAManuallyAssignableDraft()
{
    var draft = ProviderInference.CreateDraft(
        Window("newide", "MainFrame"), Array.Empty<TargetDefinition>(),
        EmptyTree(), new Config(), 999);

    Assert.Empty(draft.Definition.Providers);
    Assert.Contains(draft.InferenceNotes, x => x.Contains("No provider detected"));
    Assert.Equal("newide", draft.Definition.ProcessName);
    Assert.Equal("MainFrame", draft.Definition.WindowClass);
}
~~~

- [ ] **Step 2: Run capture tests and verify failure**

Run: `dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter FullyQualifiedName~TargetCaptureTests`

Expected: FAIL because capture and inference services are absent.

- [ ] **Step 3: Implement root-window capture behind an injectable boundary**

~~~csharp
internal interface IWindowIntrospection
{
    IntPtr WindowFromPoint(Native.POINT point);
    IntPtr RootOf(IntPtr hwnd);
    WindowSnapshot? SnapshotOf(IntPtr hwnd);
}

internal sealed record WindowCaptureResult(bool Success, WindowSnapshot? Window, string? Error);

internal interface IWindowCaptureService
{
    WindowCaptureResult CaptureAt(Native.POINT point);
}

internal sealed class WindowCaptureService : IWindowCaptureService
{
    private static readonly HashSet<string> ForbiddenClasses =
        new(StringComparer.OrdinalIgnoreCase)
        { "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow" };

    private readonly IWindowIntrospection _native;
    private readonly uint _ownPid;

    public WindowCaptureService(IWindowIntrospection native, uint ownPid)
    {
        _native = native;
        _ownPid = ownPid;
    }

    public WindowCaptureResult CaptureAt(Native.POINT point)
    {
        var child = _native.WindowFromPoint(point);
        var root = child == IntPtr.Zero ? IntPtr.Zero : _native.RootOf(child);
        var window = root == IntPtr.Zero ? null : _native.SnapshotOf(root);
        if (window is null) return new(false, null, "No top-level window was found.");
        if (window.Pid == _ownPid) return new(false, null, "UsageBars cannot target itself.");
        if (ForbiddenClasses.Contains(window.ClassName))
            return new(false, null, "The Windows taskbar is not a target.");
        if (string.IsNullOrWhiteSpace(window.ProcessName)
            || string.IsNullOrWhiteSpace(window.ClassName))
            return new(false, null, "Executable name and top-level class are required.");
        return new(true, window, null);
    }
}
~~~

The production `NativeWindowIntrospection` uses `WindowFromPoint`, `GetAncestor(hwnd, GA_ROOT)`, `ProcessTreeCache.Current().NameOf(pid)`, and the same snapshot fields as `TargetResolver`.

- [ ] **Step 4: Implement provider inference and stable captured drafts**

`ProviderInference.CreateDraft` must:

1. Collect all enabled existing definitions matching the window. If the highest-specificity matches use `AlwaysWhileOpen`, union their provider bindings and copy their placement, activation, and status height; this preserves both-provider IDE inference.
2. Recognize exact `claude/Chrome_WidgetWin_1` and `ChatGPT/Chrome_WidgetWin_1` desktop identities.
3. Independently test both CLI processes for title-plus-live-process and ancestry evidence, adding only the providers whose evidence succeeds; class-only terminal defaults do not preselect a provider merely because the class matches.
4. Suggest `RequireProviderActivity` when activity was found.
5. Suggest `AlwaysWhileOpen` and desktop placement when nothing was detected, leaving providers empty for manual confirmation.
6. Build the draft with the selected window's exact process and class, `RequireResizableFrame = window.IsResizable`, and no persisted HWND/PID.
7. Copy `window.Title` into transient `TargetDraft.CapturedTitle` for confirmation preview; it is not persisted unless a deliberate `WindowTitle` matcher is created.

- [ ] **Step 5: Run capture/inference tests**

Run: `dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter FullyQualifiedName~TargetCaptureTests`

Expected: PASS.

- [ ] **Step 6: Commit capture and inference**

~~~powershell
git add src/TargetCapture.cs tests/UsageBars.Tests/TargetCaptureTests.cs
git commit -m "feat: capture and infer window targets"
~~~

---

### Task 5: Implement a cancellable global window picker

**Files:**
- Modify: `src/Native.cs:1-191`
- Create: `src/WindowPicker.cs`
- Create: `src/PickerBanner.cs`
- Create: `tests/UsageBars.Tests/WindowPickerTests.cs`

**Interfaces:**
- Consumes: `WindowCaptureService`.
- Produces: `IWindowPickerNative`, `IPickerBanner`, and `WindowPicker.PickAsync()` returning `Task<WindowCaptureResult>`.

- [ ] **Step 1: Write failing hook-cleanup and click-consumption tests**

~~~csharp
[Fact]
public async Task EscapeCancelsAndAlwaysCleansUp()
{
    var native = new FakePickerNative();
    var banner = new FakeBanner();
    using var picker = new WindowPicker(native, banner, FakeCapture.Success());

    var pending = picker.PickAsync();
    native.RaiseKeyDown(WindowPicker.VirtualKeyEscape);
    var result = await pending;

    Assert.False(result.Success);
    Assert.Equal(2, native.UnhookCount);
    Assert.False(banner.Visible);
    Assert.True(native.CursorRestored);
}

[Fact]
public async Task LeftClickIsConsumedAndReturnsTheCapturedRoot()
{
    var native = new FakePickerNative { CursorPoint = new Native.POINT { X = 10, Y = 20 } };
    using var picker = new WindowPicker(native, new FakeBanner(), FakeCapture.Success());

    var pending = picker.PickAsync();
    var consumed = native.RaiseMouseDown(WindowPicker.LeftButtonDown);
    var result = await pending;

    Assert.True(consumed);
    Assert.True(result.Success);
}

[Fact]
public void ASecondPickerCannotStartWhileOneIsActive()
{
    using var picker = new WindowPicker(
        new FakePickerNative(), new FakeBanner(), FakeCapture.Success());

    _ = picker.PickAsync();

    Assert.Throws<InvalidOperationException>(() => picker.PickAsync());
}

[Fact]
public void ClickingAnIgnoredWindowKeepsPickerModeActive()
{
    var native = new FakePickerNative();
    var banner = new FakeBanner();
    using var picker = new WindowPicker(native, banner, FakeCapture.Failure("UsageBars cannot target itself."));

    _ = picker.PickAsync();
    var consumed = native.RaiseMouseDown(WindowPicker.LeftButtonDown);

    Assert.True(consumed);
    Assert.True(picker.IsActive);
    Assert.Contains("cannot target itself", banner.Message);
}
~~~

- [ ] **Step 2: Run picker tests and verify failure**

Run: `dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter FullyQualifiedName~WindowPickerTests`

Expected: FAIL because `WindowPicker` is absent.

- [ ] **Step 3: Add the required Win32 declarations**

Add `WH_MOUSE_LL = 14`, `WH_KEYBOARD_LL = 13`, `WM_LBUTTONDOWN`, `WM_RBUTTONDOWN`, `WM_KEYDOWN`, `VK_ESCAPE`, `GA_ROOT = 2`, `IDC_CROSS = 32515`, `HookProc`, `SetWindowsHookExW`, `CallNextHookEx`, `UnhookWindowsHookEx`, `WindowFromPoint`, and `GetCursorPos` to `Native.cs`. Keep delegate instances rooted in `WindowPicker` fields for the complete hook lifetime.

- [ ] **Step 4: Implement one-shot picker lifecycle**

~~~csharp
internal interface IWindowPickerNative
{
    Native.POINT CursorPoint { get; }
    IntPtr InstallMouseHook(Func<int, bool> callback);
    IntPtr InstallKeyboardHook(Func<int, bool> callback);
    void Unhook(IntPtr hook);
    void SetCrosshair();
    void RestoreCursor();
}

internal interface IPickerBanner : IDisposable
{
    void Show();
    void ShowMessage(string message);
    void Hide();
}

internal sealed class WindowPicker : IDisposable
{
    internal const int LeftButtonDown = 0x0201;
    internal const int RightButtonDown = 0x0204;
    internal const int VirtualKeyEscape = 0x1B;

    private readonly IWindowPickerNative _native;
    private readonly IPickerBanner _banner;
    private readonly IWindowCaptureService _capture;
    private TaskCompletionSource<WindowCaptureResult>? _pending;
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;
    internal bool IsActive => _pending is not null;

    public Task<WindowCaptureResult> PickAsync()
    {
        if (_pending is not null) throw new InvalidOperationException("A window pick is already active.");
        var completion = new TaskCompletionSource<WindowCaptureResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = completion;
        try
        {
            _banner.Show();
            _native.SetCrosshair();
            _mouseHook = _native.InstallMouseHook(OnMouse);
            _keyboardHook = _native.InstallKeyboardHook(OnKeyboard);
            if (_mouseHook == IntPtr.Zero || _keyboardHook == IntPtr.Zero)
                Finish(new(false, null, "Could not install the window-picker hooks."));
        }
        catch (Exception ex) { Finish(new(false, null, ex.Message)); }
        return completion.Task;
    }

    private bool OnMouse(int message)
    {
        if (message == RightButtonDown)
        {
            Finish(new(false, null, "Cancelled."));
            return true;
        }
        if (message != LeftButtonDown) return false;
        var capture = _capture.CaptureAt(_native.CursorPoint);
        if (!capture.Success)
        {
            _banner.ShowMessage(capture.Error ?? "Choose a top-level application window.");
            return true;
        }
        Finish(capture);
        return true;
    }

    private bool OnKeyboard(int virtualKey)
    {
        if (virtualKey != VirtualKeyEscape) return false;
        Finish(new(false, null, "Cancelled."));
        return true;
    }

    private void Finish(WindowCaptureResult result)
    {
        var completion = _pending;
        if (completion is null) return;
        try
        {
            if (_mouseHook != IntPtr.Zero) _native.Unhook(_mouseHook);
            if (_keyboardHook != IntPtr.Zero) _native.Unhook(_keyboardHook);
        }
        finally
        {
            _mouseHook = _keyboardHook = IntPtr.Zero;
            _native.RestoreCursor();
            _banner.Hide();
            _pending = null;
            completion.TrySetResult(result);
        }
    }

    public void Dispose()
    {
        if (_pending is not null) Finish(new(false, null, "UsageBars is closing."));
        _banner.Dispose();
    }
}
~~~

`PickerBanner` is a borderless WinForms `Form` with `ShowWithoutActivation => true`, `TopMost = true`, `FormBorderStyle.None`, and `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`. Its single label initially reads `Click a window to target · Esc or right-click to cancel` and it appears centered near the top of the primary working area. `ShowMessage` replaces the label with the capture rejection plus `Choose another window · Esc to cancel`; the next `Show` restores the initial text.

In `WindowPickerTests.cs`, `FakePickerNative` stores the two installed callbacks, returns nonzero handles 1 and 2, increments `UnhookCount`, exposes `CursorPoint`, invokes the stored callbacks from `RaiseMouseDown`/`RaiseKeyDown`, and sets `CursorRestored`. `FakeBanner` toggles `Visible` and records `Message` from `ShowMessage`. `FakeCapture.Success()` implements `IWindowCaptureService` and returns a successful fixed `WindowSnapshot` from `CaptureAt`; `Failure(string)` returns an unsuccessful result. Add a hook-install-failure test whose fake returns zero for the keyboard handle and assert both the already-installed mouse hook and banner are cleaned up.

- [ ] **Step 5: Run picker tests**

Run: `dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter FullyQualifiedName~WindowPickerTests`

Expected: PASS, including cleanup on hook installation failure.

- [ ] **Step 6: Commit picker infrastructure**

~~~powershell
git add src/Native.cs src/WindowPicker.cs src/PickerBanner.cs tests/UsageBars.Tests/WindowPickerTests.cs
git commit -m "feat: add tray window picker"
~~~

---

### Task 6: Add target editor and manager UI

**Files:**
- Create: `src/TargetViewModels.cs`
- Create: `src/TargetEditorForm.cs`
- Create: `src/TargetManagerForm.cs`
- Modify: `src/Tray.cs:1-236`
- Create: `tests/UsageBars.Tests/TargetViewModelTests.cs`

**Interfaces:**
- Consumes: `TargetDraft` and `TargetStore`.
- Produces: `TargetEditorModel`, `TargetRow`, `TargetEditorForm.EditedTarget`, and manager events.
- Adds tray events `AddTargetRequested` and `ManageTargetsRequested` plus `SetTargetSummary(int enabled, int disabled)`.

- [ ] **Step 1: Write failing editor validation and row-formatting tests**

~~~csharp
[Fact]
public void ManualAssignmentRequiresAtLeastOneProvider()
{
    var definition = TargetDefinition.Captured(
        "New IDE", "newide", "MainFrame", Array.Empty<ProviderBinding>(),
        TargetPlacementKind.Desktop, TargetActivationPolicy.AlwaysWhileOpen, true, 0);
    var model = TargetEditorModel.From(
        new TargetDraft(definition, "Project - New IDE", new[] { "No provider detected." }));

    Assert.Contains(model.Validate(), x => x.Contains("provider", StringComparison.OrdinalIgnoreCase));

    model.Claude = true;
    Assert.DoesNotContain(model.Validate(), x => x.Contains("provider", StringComparison.OrdinalIgnoreCase));
}

[Fact]
public void RowShowsBothProvidersAndMatcher()
{
    var target = TargetDefinition.Captured(
        "Rider", "rider64", "SunAwtFrame",
        new[] { ProviderBinding.Claude, ProviderBinding.Codex },
        TargetPlacementKind.Ide, TargetActivationPolicy.AlwaysWhileOpen, false, 26);

    var row = TargetRow.From(target, new[] { target });

    Assert.Equal("Claude + Codex", row.Providers);
    Assert.Contains("rider64", row.Match);
    Assert.Contains("SunAwtFrame", row.Match);
}
~~~

- [ ] **Step 2: Run view-model tests and verify failure**

Run: `dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter FullyQualifiedName~TargetViewModelTests`

Expected: FAIL because target UI models do not exist.

- [ ] **Step 3: Implement UI models with exact conversion rules**

`TargetEditorModel.From(TargetDraft)` copies name, process/class display, captured-title preview, provider checkboxes, placement, activation, enabled, resizable, and status height. `Validate()` requires a nonblank name and at least one provider. `ToDefinition()` preserves the original ID/source/revision and creates bindings with `claude`/`codex` plus the draft's inferred activity patterns.

`TargetRow.From(TargetDefinition, IReadOnlyList<TargetDefinition>)` accepts the full target snapshot, sets `HasOverlap` when any other enabled definition satisfies `target.Overlaps(other)`, and formats:

~~~csharp
Providers = target.Providers.Select(x => x.Provider).Distinct().Order() switch
{
    var values when values.SequenceEqual(new[] { ProviderId.Claude, ProviderId.Codex })
        => "Claude + Codex",
    var values when values.SequenceEqual(new[] { ProviderId.Claude }) => "Claude",
    _ => "Codex",
};
Match = string.Join(" · ", new[]
{
    target.ProcessName is null ? null : $"process={target.ProcessName}",
    target.WindowClass is null ? null : $"class={target.WindowClass}",
    target.WindowTitle is null ? null : $"title={target.WindowTitle.Kind}:{target.WindowTitle.Value}",
}.Where(x => x is not null));
~~~

- [ ] **Step 4: Implement the compact target editor**

Build `TargetEditorForm` with labeled controls for Name, captured Application, Window class, current title/matcher preview, Claude, Codex, Placement, Activation, and Enabled. Application/class/title preview are read-only. Disable status-bar height unless placement is IDE. Show inference notes above the buttons. Save calls `TargetEditorModel.Validate()`, displays validation text inline, assigns `EditedTarget = model.ToDefinition()`, and returns `DialogResult.OK`.

- [ ] **Step 5: Implement the target manager**

Build `TargetManagerForm` with a `DataGridView` containing Enabled, Name, Providers, Application, Match, Placement, and a warning column that displays `Overlapping rule` when `TargetRow.HasOverlap` is true; a read-only details box; and Edit/Delete/Close buttons. The details box names the overlapping target(s) and explains that the more specific, then most recently edited, rule wins. On enable toggles call `store.SetEnabled` and roll the checkbox back when the result fails. Delete uses:

~~~csharp
if (MessageBox.Show(
        this, $"Delete target “{target.Name}”?", "UsageBars",
        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
    return;
var result = _store.Delete(target.Id);
if (!result.Success) MessageBox.Show(this, result.Error, "UsageBars");
~~~

Subscribe to `TargetStore.Changed` while open and unsubscribe in `Dispose(bool)`. Double-click and Edit open `TargetEditorForm` and call `store.Update` only on OK.

- [ ] **Step 6: Add the tray submenu and events**

Insert `Targets` after `Refresh now`:

~~~csharp
private readonly ToolStripMenuItem _targetSummary = new() { Enabled = false };
public event Action? AddTargetRequested;
public event Action? ManageTargetsRequested;

var targets = new ToolStripMenuItem("Targets");
var addTarget = new ToolStripMenuItem("Add by clicking a window...");
addTarget.Click += (_, _) => AddTargetRequested?.Invoke();
var manageTargets = new ToolStripMenuItem("Manage targets...");
manageTargets.Click += (_, _) => ManageTargetsRequested?.Invoke();
targets.DropDownItems.Add(addTarget);
targets.DropDownItems.Add(manageTargets);
targets.DropDownItems.Add(new ToolStripSeparator());
targets.DropDownItems.Add(_targetSummary);
menu.Items.Add(targets);
~~~

Implement `SetTargetSummary` as `_targetSummary.Text = $"{enabled} enabled · {disabled} disabled";`.

- [ ] **Step 7: Run UI-model tests and the full compile**

Run: `dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter FullyQualifiedName~TargetViewModelTests`

Expected: PASS.

Run: `dotnet build UsageBars.sln --no-restore`

Expected: PASS with no compiler errors.

- [ ] **Step 8: Commit the target-management UI**

~~~powershell
git add src/TargetViewModels.cs src/TargetEditorForm.cs src/TargetManagerForm.cs src/Tray.cs tests/UsageBars.Tests/TargetViewModelTests.cs
git commit -m "feat: manage window targets from the tray"
~~~

---

### Task 7: Wire store, picker, resolver, and diagnostics into the controller

**Files:**
- Modify: `src/OverlayController.cs:20-982`
- Modify: `src/Program.cs:10-24` only if constructor dependencies require explicit disposal ordering.
- Modify: `tests/UsageBars.Tests/DiagnosticsAndStartupTests.cs`

**Interfaces:**
- Consumes: all Tasks 1-6.
- Replaces: two static `HostResolver` instances and `_resolvers` with one `TargetResolver` and `TargetStore`.
- Produces: immediate retargeting on `TargetStore.Changed` and complete target diagnostics.

- [ ] **Step 1: Add a failing diagnostics regression**

Add a pure formatter entry point `TargetDiagnostics.Append(StringBuilder, TargetStore, IReadOnlyDictionary<ProviderId, string>)` and test:

~~~csharp
[Fact]
public void TargetDiagnosticsIncludeEnabledDisabledAndRecoveryIssues()
{
    var storage = new MemoryTargetStorage("{bad");
    var store = TargetStore.Load(storage, new Config());
    var text = new StringBuilder();

    TargetDiagnostics.Append(text, store, new Dictionary<ProviderId, string>
    {
        [ProviderId.Claude] = "no matching Claude target",
    });

    Assert.Contains("dynamic targets", text.ToString());
    Assert.Contains("recovery issue", text.ToString());
    Assert.Contains("Claude Desktop", text.ToString());
    Assert.Contains("(memory)", text.ToString());
    Assert.Contains("no matching Claude target", text.ToString());
}
~~~

- [ ] **Step 2: Run the diagnostics test and verify failure**

Run: `dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter FullyQualifiedName~TargetDiagnostics`

Expected: FAIL because `TargetDiagnostics` is absent.

- [ ] **Step 3: Construct and subscribe to target services**

Add controller fields:

~~~csharp
private readonly TargetStore _targets;
private readonly TargetResolver _targetResolver = new();
private readonly WindowPicker _windowPicker;
private TargetManagerForm? _targetManager;
private TargetEditorForm? _targetEditor;
~~~

In the constructor, load `new JsonTargetFileStorage(AppPaths.TargetsFile)`, create production capture/picker services, record `_targets.Issues`, and subscribe:

~~~csharp
_targets.Changed += OnTargetsChanged;
_tray.AddTargetRequested += BeginAddTarget;
_tray.ManageTargetsRequested += ShowTargetManager;
UpdateTargetSummary();
~~~

`OnTargetsChanged` updates the summary and calls `Track()` on the WPF dispatcher. Failed store operations are shown through `_tray.ShowWarning` by the initiating UI.

- [ ] **Step 4: Replace static tracking with the dynamic resolver**

Replace the resolver loop in `Track()` with:

~~~csharp
var wanted = _targetResolver.FindAll(candidates, _targets.Targets, _config)
    .ToDictionary(host => (host.Provider, host.Hwnd));
~~~

Keep `RetirePillsMissingFrom`, `CreatePillsFor`, shared-host layout, repositioning, and hooks unchanged. `CreatePillsFor` continues selecting `ProviderTarget.Claude` or `ProviderTarget.Codex` only for pill display/model behavior.

- [ ] **Step 5: Implement asynchronous add and singleton manager flows**

~~~csharp
private async void BeginAddTarget()
{
    var capture = await _windowPicker.PickAsync();
    if (!capture.Success)
    {
        if (capture.Error is not "Cancelled.") _tray.ShowWarning(capture.Error ?? "Window capture failed.");
        return;
    }

    var draft = ProviderInference.CreateDraft(
        capture.Window!, _targets.Targets, ProcessTreeCache.Current(), _config, _ownProcessId);
    using var editor = new TargetEditorForm(TargetEditorModel.From(draft));
    _targetEditor = editor;
    try
    {
        if (editor.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        var result = _targets.Add(editor.EditedTarget!);
        if (result.ExistingId is { } existing)
        {
            var current = _targets.Targets.Single(x => x.Id == existing);
            if (System.Windows.Forms.MessageBox.Show(
                    $"That window is already targeted as “{current.Name}”. Edit it?",
                    "UsageBars", System.Windows.Forms.MessageBoxButtons.YesNo,
                    System.Windows.Forms.MessageBoxIcon.Information)
                == System.Windows.Forms.DialogResult.Yes)
            {
                using var existingEditor = new TargetEditorForm(
                    TargetEditorModel.From(new TargetDraft(current, "", Array.Empty<string>())));
                if (existingEditor.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    var update = _targets.Update(existingEditor.EditedTarget!);
                    if (!update.Success) _tray.ShowWarning(update.Error ?? "Could not update the target.");
                }
            }
        }
        else if (!result.Success)
            _tray.ShowWarning(result.Error ?? "Could not save the target.");
    }
    finally { _targetEditor = null; }
}
~~~

`ShowTargetManager` activates the existing manager when open; otherwise constructs one, clears the field on `FormClosed`, and calls `Show()`.

- [ ] **Step 6: Add diagnostics and disposal cleanup**

`TargetDiagnostics.Append` prints `store.Location`, enabled/disabled totals, every target's ID/name/source/providers/matcher/placement/activation/revision, store recovery issues, and the supplied resolver rejection text. Call it from `CopyDiagnostics` before the live-pill section with `_targetResolver.LastRejections`.

In `Dispose` unsubscribe `_targets.Changed`, close/dispose editor and manager, dispose `_windowPicker`, then dispose tray and pills. This ordering cancels hooks before the tray disappears.

- [ ] **Step 7: Run the complete automated suite**

Run: `dotnet test UsageBars.sln --no-restore`

Expected: PASS; no old tests reference removed `HostResolver` catalogs.

- [ ] **Step 8: Commit controller integration**

~~~powershell
git add src/OverlayController.cs src/Program.cs src/TargetResolver.cs tests/UsageBars.Tests/DiagnosticsAndStartupTests.cs
git commit -m "feat: activate dynamic window targeting"
~~~

---

### Task 8: Document migration, build, and perform Windows smoke verification

**Files:**
- Modify: `README.md:35-48`, `README.md:214-485`, `README.md:630-720`, `README.md:780-800`
- Modify: `UsageBars.config.default.json:116-138` and `UsageBars.config.default.json:196-200`
- Modify: `tests/UsageBars.Tests/DefaultConfigTests.cs`

**Interfaces:**
- Consumes: completed feature.
- Produces: user-facing workflow, migration semantics, target-file documentation, and final verified executable.

- [ ] **Step 1: Add a failing default-config documentation assertion**

Add a source-text test that requires the comments for `AttachToClaudeCodeTerminals`, `AttachToCodexTerminals`, `TerminalTitlePattern`, `CodexTitlePattern`, and `AlwaysShowOnIdes` to contain `first-run` or `seed`. Run:

`dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter FullyQualifiedName~DefaultConfigTests`

Expected: FAIL because current comments describe those fields as live targeting controls.

- [ ] **Step 2: Update config comments and README**

Document:

- `UsageBars.data\targets.json` and corrupt/invalid recovery backups.
- Tray path `Targets → Add by clicking a window...`.
- Crosshair, intercepted click, Escape/right-click cancellation, and confirmation choices.
- Manager columns, enable/disable, edit, and confirmed deletion.
- First-run seeding and the rule that deleted seeded targets do not reappear.
- Global provider switches versus seed-only legacy targeting fields.
- Specificity order and one-pill-per-provider/window behavior.
- Diagnostics entries and the manual recovery path.
- New source-file responsibilities.

Do not describe arbitrary provider support, target styling, or raw regex editing.

- [ ] **Step 3: Run documentation/config tests**

Run: `dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter FullyQualifiedName~DefaultConfigTests`

Expected: PASS.

- [ ] **Step 4: Run complete tests and release build**

Run: `dotnet test UsageBars.sln --no-restore`

Expected: PASS.

Run: `powershell -ExecutionPolicy Bypass -File .\build.ps1`

Expected: exit code 0 and `dist\UsageBars.exe` plus the default config.

- [ ] **Step 5: Execute the Windows smoke checklist under an isolated data root**

Set `USAGEBARS_DATA` to a new temporary directory and verify in order:

1. First launch writes `targets.json` and existing Claude/Codex/terminal/IDE pills behave as before.
2. Tray Add shows the crosshair and non-activating instruction banner.
3. Escape and right-click each cancel and leave no banner, crosshair, or active hook.
4. Clicking a child control does not activate it and confirms the top-level process/class.
5. Claude Desktop and Codex/ChatGPT preselect the correct provider.
6. Rider or VS Code can confirm both providers.
7. An undetected application can be assigned Claude, Codex, or both manually.
8. Saving creates a pill immediately; reopening the application matches its new HWND.
9. Disable removes pills immediately; re-enable restores them.
10. Delete asks for confirmation, removes the pill, and stays deleted after restart.
11. Overlapping rules produce one pill per provider/window.
12. A deliberately malformed isolated `targets.json` is preserved as `targets.corrupt.*.json` on the first mutation while a fresh valid file is written.

- [ ] **Step 6: Commit docs and verification checkpoint**

~~~powershell
git add README.md UsageBars.config.default.json tests/UsageBars.Tests/DefaultConfigTests.cs
git commit -m "docs: explain dynamic window targets"
~~~

---

## Final Verification

- [ ] Run `dotnet test UsageBars.sln --no-restore` and retain the passed/failed/skipped totals.
- [ ] Run `powershell -ExecutionPolicy Bypass -File .\build.ps1` and verify `dist\UsageBars.exe` exists.
- [ ] Confirm no process/class/title catalog remains in the live resolver; static lists exist only in `TargetDefaults` for first-run seeding.
- [ ] Confirm `targets.json` contains no HWND, PID, or transient process data.
- [ ] Confirm all target dialogs and picker hooks are disposed during normal exit.
- [ ] Confirm the README and default config describe targeting fields as seed-only after migration.
- [ ] If Git metadata is available, run `git status --short` and verify only intended changes remain.
