# Per-Target Pill Position and Manager Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give every saved window target its own anchor and X/Y nudges, remove global tray positioning, and make `Manage targets...` reliably open or activate one visible manager window.

**Architecture:** A versioned target-position value extends `TargetDefinition` and is propagated through resolution into the existing pill geometry path. A shared position editor supplies the Add/Edit UI, while a small modeless-window coordinator owns the manager's hidden native owner, UI-thread dispatch, uniqueness, activation, diagnostics, and shutdown. Legacy global placement switches are removed from rendering and configuration output.

**Tech Stack:** C# 12, .NET 8 `net8.0-windows`, WPF application host, WinForms tray/dialogs, Win32 ownership, `System.Text.Json`, xUnit 2.9.

## Global Constraints

- Remain a Windows-only, self-contained, single-file-compatible application; add no NuGet dependencies.
- Persist `PillAnchor`, `OffsetX`, and `OffsetY` in the existing `AppPaths.TargetsFile` document.
- Supported anchors in this change are exactly `TopCenter`, `TopRight`, and `BottomCenter`.
- Context defaults are exactly Desktop -> Top center, Terminal -> Top right, and IDE -> Bottom center.
- Offsets are signed device-independent pixels in the inclusive range `-2000..2000`; positive X moves right and positive Y moves down.
- Directional nudge buttons change one axis by exactly four device-independent pixels per click.
- A user-selected anchor remains authoritative when `TargetPlacementKind` changes; context mapping is creation/migration behavior only.
- Legacy global placement values do not influence rendering and are not copied into target offsets.
- All pills selected from the same `TargetDefinition.Id` are positioned as one group; pills on the same HWND from different targets are independent.
- Target mutations publish only after the existing atomic save succeeds.
- Only one manager form may exist; repeated requests activate the existing form, and closing it must not exit UsageBars.
- The current folder has no `.git` metadata. Do not initialize a repository without authorization. At each commit step, record the named checkpoint and test output. If executing in a canonical Git checkout, run the listed `git add` and `git commit` commands instead.

---

## File Structure

- Create `src/TargetPosition.cs` — anchor enum, context default mapping, offset range, four-unit nudge math, and display formatting.
- Create `src/TargetPositionEditorControl.cs` — reusable WinForms anchor/X/Y/nudge/reset editor used by target add and edit flows.
- Create `src/TargetManagerCoordinator.cs` — hidden owner window, modeless manager abstraction, UI dispatch, single-instance activation, cleanup, and failure reporting.
- Create `tests/UsageBars.Tests/TargetPositionTests.cs` — default mapping, nudge, clamp, formatting, propagation, and placement tests.
- Create `tests/UsageBars.Tests/TargetManagerCoordinatorTests.cs` — single-instance, activation, dispatch, close, disposal, and failure tests.
- Modify `src/WindowTargets.cs` — persist anchor and offsets, validate them, and initialize explicit position defaults.
- Modify `src/TargetStore.cs` — increment the target document to version 2, migrate version 1 targets, normalize loaded offsets, and diagnose invalid anchors.
- Modify `src/HostWindow.cs` and `src/TargetResolver.cs` — carry the selected target position into `HostWindowInfo`.
- Modify `src/ProviderPill.cs` and `src/OverlayWindow.cs` — select top/bottom strips from the per-target anchor and apply per-target offsets and safe clamping.
- Modify `src/OverlayController.cs` — group only pills from the same target, use the manager coordinator, remove tray-global nudge handlers, and update diagnostics.
- Modify `src/TargetViewModels.cs`, `src/TargetEditorForm.cs`, and `src/TargetManagerForm.cs` — edit and display per-target positions.
- Modify `src/Tray.cs` — remove the Position submenu and its events.
- Modify `src/Config.cs` and `UsageBars.config.default.json` — remove retired global position properties from new configuration output while continuing to ignore unknown legacy JSON fields.
- Modify `README.md` — document per-target anchors/nudges, migration, the manager behavior, and the reduced tray menu.
- Modify existing tests in `TargetStoreTests.cs`, `TargetResolverTests.cs`, `TargetViewModelTests.cs`, and `ConfigAndPlacementTests.cs` for the versioned model and retired global behavior.

---

### Task 1: Persisted target-position domain and version-1 migration

**Files:**
- Create: `src/TargetPosition.cs`
- Create: `tests/UsageBars.Tests/TargetPositionTests.cs`
- Modify: `src/WindowTargets.cs:9-10,46-105,189`
- Modify: `src/TargetStore.cs:49-151`
- Modify: `tests/UsageBars.Tests/TargetStoreTests.cs`
- Modify: `tests/UsageBars.Tests/WindowTargetTests.cs`

**Interfaces:**
- Produces: `enum PillAnchor { TopCenter, TopRight, BottomCenter }`.
- Produces: `TargetPositionRules.DefaultFor(TargetPlacementKind)`, `ClampOffset(int)`, `Nudge(int,int)`, and `Format(PillAnchor,int,int)`.
- Produces: `TargetDefinition.Anchor`, `TargetDefinition.OffsetX`, and `TargetDefinition.OffsetY`.
- Produces: target document version 2; version 1 loads through the approved context migration.

- [ ] **Step 1: Add failing domain/default/nudge tests**

Create `tests/UsageBars.Tests/TargetPositionTests.cs`:

~~~csharp
using Xunit;

namespace UsageBars.Tests;

public class TargetPositionTests
{
    [Theory]
    [InlineData(TargetPlacementKind.Desktop, PillAnchor.TopCenter)]
    [InlineData(TargetPlacementKind.Terminal, PillAnchor.TopRight)]
    [InlineData(TargetPlacementKind.Ide, PillAnchor.BottomCenter)]
    public void ContextDefaultMapsPlacementToAnchor(
        TargetPlacementKind placement, PillAnchor expected) =>
        Assert.Equal(expected, TargetPositionRules.DefaultFor(placement));

    [Theory]
    [InlineData(0, -1, -4)]
    [InlineData(0, 1, 4)]
    [InlineData(1999, 1, 2000)]
    [InlineData(-1999, -1, -2000)]
    public void NudgeMovesFourUnitsAndClamps(int value, int direction, int expected) =>
        Assert.Equal(expected, TargetPositionRules.Nudge(value, direction));

    [Fact]
    public void CapturedTargetGetsAnExplicitContextDefault()
    {
        var target = TargetDefinition.Captured(
            "IDE", "rider64", "SunAwtFrame", new[] { ProviderBinding.Claude },
            TargetPlacementKind.Ide, TargetActivationPolicy.AlwaysWhileOpen, false, 26);

        Assert.Equal(PillAnchor.BottomCenter, target.Anchor);
        Assert.Equal(0, target.OffsetX);
        Assert.Equal(0, target.OffsetY);
    }

    [Fact]
    public void PositionFormattingUsesFriendlyAnchorAndSignedOffsets()
    {
        Assert.Equal("Top right (+8, -4)",
            TargetPositionRules.Format(PillAnchor.TopRight, 8, -4));
        Assert.Equal("Bottom center (0, 0)",
            TargetPositionRules.Format(PillAnchor.BottomCenter, 0, 0));
    }
}
~~~

- [ ] **Step 2: Add failing store migration and validation tests**

Add to `TargetStoreTests.cs`:

~~~csharp
[Fact]
public void VersionOneTargetsMigrateToContextAnchorsAndSaveAsVersionTwo()
{
    var desktop = Guid.Parse("10101010-1010-1010-1010-101010101010");
    var terminal = Guid.Parse("20202020-2020-2020-2020-202020202020");
    var ide = Guid.Parse("30303030-3030-3030-3030-303030303030");
    var storage = new MemoryTargetStorage(
        "{\"Version\":1,\"Targets\":[" +
        ValidTargetJson(desktop, "Desktop") + "," +
        ValidTargetJson(terminal, "Terminal").Replace("\"Placement\":\"Desktop\"", "\"Placement\":\"Terminal\"") + "," +
        ValidTargetJson(ide, "IDE").Replace("\"Placement\":\"Desktop\"", "\"Placement\":\"Ide\"") + "]}");

    var store = TargetStore.Load(storage, new Config());

    Assert.Equal(PillAnchor.TopCenter, store.Targets.Single(x => x.Id == desktop).Anchor);
    Assert.Equal(PillAnchor.TopRight, store.Targets.Single(x => x.Id == terminal).Anchor);
    Assert.Equal(PillAnchor.BottomCenter, store.Targets.Single(x => x.Id == ide).Anchor);

    Assert.True(store.SetEnabled(desktop, false).Success);
    Assert.Contains("\"Version\": 2", storage.Text);
    Assert.Contains("\"Anchor\": \"TopRight\"", storage.Text);

    var reloaded = TargetStore.Load(storage, new Config());
    Assert.Equal(PillAnchor.TopCenter, reloaded.Targets.Single(x => x.Id == desktop).Anchor);
    Assert.Equal(PillAnchor.TopRight, reloaded.Targets.Single(x => x.Id == terminal).Anchor);
    Assert.Equal(PillAnchor.BottomCenter, reloaded.Targets.Single(x => x.Id == ide).Anchor);
}

[Fact]
public void VersionTwoRejectsAnExplicitUnknownAnchorButKeepsValidEntries()
{
    var good = Guid.Parse("40404040-4040-4040-4040-404040404040");
    var bad = Guid.Parse("50505050-5050-5050-5050-505050505050");
    var valid = VersionTwoTargetJson(good, "Good", "TopCenter", 0, 0);
    var invalid = VersionTwoTargetJson(bad, "Bad", "Sideways", 0, 0);
    var store = TargetStore.Load(
        new MemoryTargetStorage($"{{\"Version\":2,\"Targets\":[{valid},{invalid}]}}"),
        new Config());

    Assert.Equal(good, Assert.Single(store.Targets).Id);
    Assert.Contains(store.Issues,
        issue => issue.Contains("anchor", StringComparison.OrdinalIgnoreCase));
}

[Fact]
public void LoadClampsPersistedOffsetsAndReportsTheAdjustment()
{
    var id = Guid.Parse("60606060-6060-6060-6060-606060606060");
    var storage = new MemoryTargetStorage(
        $"{{\"Version\":2,\"Targets\":[{VersionTwoTargetJson(id, "Clamped", "TopCenter", 9999, -9999)}]}}");

    var store = TargetStore.Load(storage, new Config());
    var target = Assert.Single(store.Targets);

    Assert.Equal(2000, target.OffsetX);
    Assert.Equal(-2000, target.OffsetY);
    Assert.Contains(store.Issues, issue => issue.Contains("offset", StringComparison.OrdinalIgnoreCase));
}
~~~

Update the existing JSON helpers so version-2 test JSON explicitly includes `Anchor`, `OffsetX`, and `OffsetY`.

- [ ] **Step 3: Run the focused tests and verify they fail**

Run:

~~~powershell
dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter "FullyQualifiedName~TargetPositionTests|FullyQualifiedName~TargetStoreTests"
~~~

Expected: FAIL with compiler errors for `PillAnchor`, `TargetPositionRules`, and the new `TargetDefinition` fields.

- [ ] **Step 4: Implement the position domain**

Create `src/TargetPosition.cs` with this public-internal surface:

~~~csharp
namespace UsageBars;

internal enum PillAnchor
{
    TopCenter,
    TopRight,
    BottomCenter,
}

internal static class TargetPositionRules
{
    public const int MinimumOffset = -2000;
    public const int MaximumOffset = 2000;
    public const int NudgeSize = 4;

    public static PillAnchor DefaultFor(TargetPlacementKind placement) => placement switch
    {
        TargetPlacementKind.Desktop => PillAnchor.TopCenter,
        TargetPlacementKind.Terminal => PillAnchor.TopRight,
        TargetPlacementKind.Ide => PillAnchor.BottomCenter,
        _ => PillAnchor.TopCenter,
    };

    public static int ClampOffset(int value) =>
        Math.Clamp(value, MinimumOffset, MaximumOffset);

    public static int Nudge(int value, int direction) =>
        ClampOffset(value + Math.Sign(direction) * NudgeSize);

    public static string Format(PillAnchor anchor, int offsetX, int offsetY)
    {
        var label = anchor switch
        {
            PillAnchor.TopCenter => "Top center",
            PillAnchor.TopRight => "Top right",
            PillAnchor.BottomCenter => "Bottom center",
            _ => anchor.ToString(),
        };
        static string Signed(int value) => value > 0 ? $"+{value}" : value.ToString();
        return $"{label} ({Signed(offsetX)}, {Signed(offsetY)})";
    }
}
~~~

Add to `TargetDefinition`:

~~~csharp
public PillAnchor Anchor { get; init; }
public int OffsetX { get; init; }
public int OffsetY { get; init; }
~~~

Set `Anchor = TargetPositionRules.DefaultFor(placement)` inside `Captured`; `Seeded` inherits that value. Extend `Validate` with `Enum.IsDefined(Anchor)` and inclusive offset-range checks. Do not add position to `HasSameIdentity`: two definitions with the same matching rule remain duplicates even if their visual position differs.

- [ ] **Step 5: Implement target-document version 2 and migration**

In `TargetStore`:

~~~csharp
private const int CurrentVersion = 2;
private const int OldestSupportedVersion = 1;
~~~

Accept versions 1 and 2. After deserializing each version-1 target, apply:

~~~csharp
target = target with
{
    Anchor = TargetPositionRules.DefaultFor(target.Placement),
    OffsetX = 0,
    OffsetY = 0,
};
~~~

For version 2, require the raw element to contain the `Anchor` property so a missing property cannot silently deserialize as the first enum value. Let `JsonStringEnumConverter(allowIntegerValues: false)` reject unknown strings and numeric values. Before `ValidateLoadedTarget`, clamp out-of-range persisted offsets, append an issue containing the target ID and old/new values, and keep the clamped target. A later successful mutation writes the complete document as version 2 through the existing atomic `Save` path.

- [ ] **Step 6: Run domain/store tests and the full suite**

Run:

~~~powershell
dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter "FullyQualifiedName~TargetPositionTests|FullyQualifiedName~TargetStoreTests|FullyQualifiedName~WindowTargetTests"
dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj
~~~

Expected: both commands PASS; existing version-one fixtures continue loading through migration.

- [ ] **Step 7: Record checkpoint 1**

Checkpoint: `target position domain and v1 migration` with both test command summaries. In a Git checkout:

~~~bash
git add src/TargetPosition.cs src/WindowTargets.cs src/TargetStore.cs tests/UsageBars.Tests/TargetPositionTests.cs tests/UsageBars.Tests/TargetStoreTests.cs tests/UsageBars.Tests/WindowTargetTests.cs
git commit -m "feat: persist per-target pill positions"
~~~

---

### Task 2: Resolve and render per-target anchors and offsets

**Files:**
- Modify: `src/HostWindow.cs:68-70,285-420`
- Modify: `src/TargetResolver.cs:48-54`
- Modify: `src/ProviderPill.cs:100-120,274-377`
- Modify: `src/OverlayWindow.cs` (`TryPlaceIn` and `OverlayPlacement.Calculate` call)
- Modify: `src/OverlayController.cs:542-595`
- Modify: `tests/UsageBars.Tests/TargetResolverTests.cs`
- Modify: `tests/UsageBars.Tests/ConfigAndPlacementTests.cs:146-349`
- Modify: `tests/UsageBars.Tests/TargetPositionTests.cs`

**Interfaces:**
- Consumes: `TargetDefinition.Anchor`, `OffsetX`, and `OffsetY` from Task 1.
- Produces: `HostWindowInfo.Anchor`, `OffsetX`, and `OffsetY`.
- Produces: `ProviderPill.Alignment()` derived only from `Host.Anchor`.
- Produces: generic bottom-edge strip measurement for any target; known IDE status-bar height is preferred, otherwise `Config.TitleBarHeight` supplies the strip height.
- Produces: placement clamping that keeps the complete pill inside the target window and keeps top-edge pills left of `RightReservePx`.

- [ ] **Step 1: Add failing resolver propagation tests**

Add to `TargetResolverTests.cs`:

~~~csharp
[Fact]
public void WinningTargetCarriesItsAnchorAndOffsetsIntoTheHost()
{
    var target = TargetDefinition.Captured(
        "Terminal", "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS",
        new[] { ProviderBinding.Codex }, TargetPlacementKind.Terminal,
        TargetActivationPolicy.AlwaysWhileOpen, true, 0) with
    {
        Anchor = PillAnchor.BottomCenter,
        OffsetX = 12,
        OffsetY = -16,
    };
    var window = new WindowSnapshot(
        new IntPtr(42), 7, "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS", "Codex", true);

    var host = Assert.Single(TargetResolver.Resolve(
        new[] { window }, new[] { target }, ProcessTree.FromMap(Array.Empty<(uint,uint,string)>()),
        new Config(), ownPid: 999));

    Assert.Equal(target.Id, host.TargetId);
    Assert.Equal(PillAnchor.BottomCenter, host.Anchor);
    Assert.Equal(12, host.OffsetX);
    Assert.Equal(-16, host.OffsetY);
}
~~~

- [ ] **Step 2: Add failing geometry and grouping tests**

Add to `TargetPositionTests.cs` and update placement fixtures to pass accessible bounds:

~~~csharp
[Fact]
public void TopRightUsesTheRightReserveAndPerTargetOffsets()
{
    var strip = new Native.RECT { Left = 100, Top = 50, Right = 1100, Bottom = 90 };
    var bounds = new Native.RECT { Left = 100, Top = 50, Right = 1100, Bottom = 850 };

    var position = OverlayPlacement.Calculate(
        strip, bounds, pillWidth: 200, pillHeight: 24, rightReserve: 160,
        offsetX: -12, offsetY: 8, align: PillAlign.Right);

    Assert.Equal(728, position!.Value.X);
    Assert.Equal(66, position.Value.Y);
}

[Fact]
public void LargeOffsetsAreClampedInsideTheTargetBounds()
{
    var strip = new Native.RECT { Left = 0, Top = 0, Right = 1000, Bottom = 40 };
    var bounds = new Native.RECT { Left = 0, Top = 0, Right = 1000, Bottom = 700 };

    var position = OverlayPlacement.Calculate(
        strip, bounds, 200, 24, rightReserve: 160,
        offsetX: 2000, offsetY: -2000, align: PillAlign.Centre);

    Assert.Equal(640, position!.Value.X);
    Assert.Equal(0, position.Value.Y);
}

[Fact]
public void SharedLayoutRequiresTheSameTargetId()
{
    var sharedTarget = Guid.Parse("70707070-7070-7070-7070-707070707070");
    Assert.True(SharedPillLayout.SharesPosition(
        new HostWindowInfo(IntPtr.Zero, HostKind.Desktop, "A", ProviderId.Claude,
            HostEvidence.Open, TargetId: sharedTarget, Anchor: PillAnchor.TopCenter),
        new HostWindowInfo(IntPtr.Zero, HostKind.Desktop, "A", ProviderId.Codex,
            HostEvidence.Open, TargetId: sharedTarget, Anchor: PillAnchor.TopCenter)));
    Assert.False(SharedPillLayout.SharesPosition(
        new HostWindowInfo(IntPtr.Zero, HostKind.Desktop, "A", ProviderId.Claude,
            HostEvidence.Open, TargetId: Guid.NewGuid(), Anchor: PillAnchor.TopCenter),
        new HostWindowInfo(IntPtr.Zero, HostKind.Desktop, "A", ProviderId.Codex,
            HostEvidence.Open, TargetId: Guid.NewGuid(), Anchor: PillAnchor.TopCenter)));
}
~~~

- [ ] **Step 3: Run focused tests and verify failures**

Run:

~~~powershell
dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter "FullyQualifiedName~TargetPositionTests|FullyQualifiedName~TargetResolverTests|FullyQualifiedName~OverlayPlacementTests"
~~~

Expected: FAIL because `HostWindowInfo` lacks position fields, the geometry overload lacks accessible bounds, and `SharedPillLayout` does not exist.

- [ ] **Step 4: Propagate the winning target position**

Append optional position parameters to `HostWindowInfo` so existing call sites remain readable:

~~~csharp
internal sealed record HostWindowInfo(
    IntPtr Hwnd, HostKind Kind, string Description, ProviderId Provider, HostEvidence Evidence,
    double StatusBarHeight = 0, Guid TargetId = default,
    PillAnchor Anchor = PillAnchor.TopCenter, int OffsetX = 0, int OffsetY = 0);
~~~

Pass `match.Target.Anchor`, `OffsetX`, and `OffsetY` from `TargetResolver.Resolve` after `TargetId`.

- [ ] **Step 5: Make strip selection and alignment anchor-driven**

In `ProviderPill` replace the config-driven alignment/status checks with:

~~~csharp
public PillAlign Alignment() =>
    Host.Anchor == PillAnchor.TopRight ? PillAlign.Right : PillAlign.Centre;

private bool OnBottomEdge() => Host.Anchor == PillAnchor.BottomCenter;
~~~

For a bottom anchor, measure the bottom client strip with `host.StatusBarHeight` when it is positive; otherwise use `config.TitleBarHeight`. For top anchors, continue using `TryGetTitleBarStrip`. Set `rightReserve` to zero for bottom anchors and to `config.RightReservePx` for both top anchors. Use:

~~~csharp
var offsetX = host.OffsetX - SharedShiftX;
var offsetY = host.OffsetY;
~~~

Do not read `Config.OffsetX`, `OffsetY`, `TerminalOffsetY`, `RightAlignOnTerminals`, `PlaceOnIdeStatusBar`, or `StatusBarHeightOverride` in placement.

- [ ] **Step 6: Clamp geometry to the target's accessible rectangle**

Change `OverlayPlacement.Calculate` and `OverlayWindow.TryPlaceIn` to accept the target window bounds in addition to the anchor strip. Calculate the unnudged anchor as today, add offsets, then clamp:

~~~csharp
var rightLimit = Math.Min(
    accessibleBounds.Right - pillWidth,
    strip.Right - Math.Max(0, rightReserve) - pillWidth);
var leftLimit = accessibleBounds.Left;
var topLimit = accessibleBounds.Top;
var bottomLimit = accessibleBounds.Bottom - pillHeight;
if (rightLimit < leftLimit || bottomLimit < topLimit) return null;

x = Math.Clamp(x, leftLimit, rightLimit);
y = Math.Clamp(y, topLimit, bottomLimit);
~~~

`ProviderPill.Place` obtains the current host rectangle with `Native.GetWindowRect`; failure votes to hide with a diagnostic reason. This clamps only the rendered coordinate and never writes a changed offset back to the target.

- [ ] **Step 7: Group only pills resolved from the same target**

Extract a pure predicate:

~~~csharp
internal static class SharedPillLayout
{
    public static bool SharesPosition(HostWindowInfo first, HostWindowInfo second) =>
        first.Hwnd == second.Hwnd
        && first.TargetId != Guid.Empty
        && first.TargetId == second.TargetId
        && first.Anchor == second.Anchor
        && first.OffsetX == second.OffsetX
        && first.OffsetY == second.OffsetY;
}
~~~

Use it in `ApplySharedHostLayout`. Only a pair sharing position receives prefixes and shared shifts. Two provider pills attached to the same HWND through different target definitions keep zero shared shift and no shared prefix.

- [ ] **Step 8: Run focused and full tests**

Run:

~~~powershell
dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter "FullyQualifiedName~TargetPositionTests|FullyQualifiedName~TargetResolverTests|FullyQualifiedName~OverlayPlacementTests"
dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj
~~~

Expected: PASS. Existing centre/right/group tests are updated to use target anchors and accessible bounds rather than global terminal alignment.

- [ ] **Step 9: Record checkpoint 2**

Checkpoint: `resolve and render per-target pill positions`. In a Git checkout:

~~~bash
git add src/HostWindow.cs src/TargetResolver.cs src/ProviderPill.cs src/OverlayWindow.cs src/OverlayController.cs tests/UsageBars.Tests/TargetResolverTests.cs tests/UsageBars.Tests/ConfigAndPlacementTests.cs tests/UsageBars.Tests/TargetPositionTests.cs
git commit -m "feat: render target-specific pill anchors"
~~~

---

### Task 3: Add/Edit position controls and manager summaries

**Files:**
- Create: `src/TargetPositionEditorControl.cs`
- Modify: `src/TargetViewModels.cs:7-105`
- Modify: `src/TargetEditorForm.cs:8-173`
- Modify: `src/TargetManagerForm.cs:82-111`
- Modify: `tests/UsageBars.Tests/TargetViewModelTests.cs`
- Modify: `tests/UsageBars.Tests/TargetPositionTests.cs`

**Interfaces:**
- Consumes: `PillAnchor` and `TargetPositionRules` from Task 1.
- Produces: `TargetPositionEditorControl.Anchor`, `OffsetX`, `OffsetY`, and `ResetOffsets()`.
- Produces: `TargetEditorModel.Anchor`, `OffsetX`, `OffsetY`; `ToDefinition()` persists them.
- Produces: `TargetRow.Position` and position details text.

- [ ] **Step 1: Add failing editor-model tests**

Add to `TargetViewModelTests.cs`:

~~~csharp
[Fact]
public void EditorRoundTripsAnchorAndOffsetsWithoutChangingThemWithPlacement()
{
    var target = TargetDefinition.Captured(
        "Rider", "rider64", "SunAwtFrame", new[] { ProviderBinding.Claude },
        TargetPlacementKind.Ide, TargetActivationPolicy.AlwaysWhileOpen, false, 26) with
    {
        Anchor = PillAnchor.TopRight,
        OffsetX = 8,
        OffsetY = -12,
    };
    var model = TargetEditorModel.FromExisting(target);

    model.Placement = TargetPlacementKind.Desktop;
    var edited = model.ToDefinition();

    Assert.Equal(PillAnchor.TopRight, edited.Anchor);
    Assert.Equal(8, edited.OffsetX);
    Assert.Equal(-12, edited.OffsetY);
}

[Fact]
public void ManagerRowAndDetailsShowFriendlyPerTargetPosition()
{
    var target = TargetDefinition.Captured(
        "Terminal", "pwsh", "ConsoleWindowClass", new[] { ProviderBinding.Codex },
        TargetPlacementKind.Terminal, TargetActivationPolicy.AlwaysWhileOpen, true, 0) with
    {
        Anchor = PillAnchor.TopRight,
        OffsetX = 8,
        OffsetY = -4,
    };

    var row = TargetRow.From(target, new[] { target });
    var details = TargetDetailsFormatter.Format(target, new[] { target });

    Assert.Equal("Top right (+8, -4)", row.Position);
    Assert.Contains("Position: Top right (+8, -4)", details);
}
~~~

- [ ] **Step 2: Run view-model tests and verify failure**

Run:

~~~powershell
dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter FullyQualifiedName~TargetViewModelTests
~~~

Expected: FAIL because the editor model and target row do not expose position.

- [ ] **Step 3: Extend editor and manager view models**

Copy `Anchor`, `OffsetX`, and `OffsetY` from the original target in `TargetEditorModel` and write them in `ToDefinition()`. Validate `Enum.IsDefined(Anchor)` and the inclusive offset range. Add `Position` to `TargetRow`, populated with `TargetPositionRules.Format`, and add the same text as a separate `Position:` line in `TargetDetailsFormatter`.

- [ ] **Step 4: Implement the reusable WinForms position editor**

Create `TargetPositionEditorControl` as an `AutoSize` `UserControl` containing:

~~~csharp
private sealed record AnchorOption(PillAnchor Value, string Label)
{
    public override string ToString() => Label;
}

private readonly ComboBox _anchor = new() { DropDownStyle = ComboBoxStyle.DropDownList };
private readonly NumericUpDown _x = new()
{
    Minimum = TargetPositionRules.MinimumOffset,
    Maximum = TargetPositionRules.MaximumOffset,
    Increment = TargetPositionRules.NudgeSize,
};
private readonly NumericUpDown _y = new()
{
    Minimum = TargetPositionRules.MinimumOffset,
    Maximum = TargetPositionRules.MaximumOffset,
    Increment = TargetPositionRules.NudgeSize,
};

public PillAnchor Anchor
{
    get => ((AnchorOption)_anchor.SelectedItem!).Value;
    set => _anchor.SelectedItem = _anchor.Items.Cast<AnchorOption>()
        .Single(option => option.Value == value);
}
public int OffsetX { get => decimal.ToInt32(_x.Value); set => _x.Value = TargetPositionRules.ClampOffset(value); }
public int OffsetY { get => decimal.ToInt32(_y.Value); set => _y.Value = TargetPositionRules.ClampOffset(value); }
public void ResetOffsets() { OffsetX = 0; OffsetY = 0; }
~~~

Populate options with the labels `Top center`, `Top right`, and `Bottom center`. Add Left/Right/Up/Down buttons whose click handlers assign `TargetPositionRules.Nudge(current, -1 or +1)` to one numeric field. Add a `Reset offset` button calling `ResetOffsets`. Label numeric fields `X` and `Y`; set accessible names on every directional button.

- [ ] **Step 5: Integrate the control into Add and Edit dialogs**

`TargetEditorForm` is already used for captured targets and manager edits, so one control covers both workflows. Initialize it from the model, add it as the `Position` row immediately after semantic `Placement`, and copy its values back before validation/save:

~~~csharp
_model.Anchor = _position.Anchor;
_model.OffsetX = _position.OffsetX;
_model.OffsetY = _position.OffsetY;
~~~

Increase the fixed dialog height enough to show the position row, notes, validation, and buttons without clipping at 100% and 150% display scaling. Do not recompute the anchor when `_placement.SelectedIndexChanged` fires.

- [ ] **Step 6: Add Position to the manager list**

Keep the semantic `Placement` column and add a read-only `Position` column. Resize the application/matcher columns or widen the form so both remain readable. The existing Edit command opens `TargetEditorForm`, so position updates continue through `TargetStore.Update` and its immediate `Changed` notification.

- [ ] **Step 7: Run focused tests and build the UI project**

Run:

~~~powershell
dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter "FullyQualifiedName~TargetViewModelTests|FullyQualifiedName~TargetPositionTests"
dotnet build src/UsageBars.csproj -c Debug --nologo
~~~

Expected: tests PASS and build succeeds with no new warnings.

- [ ] **Step 8: Record checkpoint 3**

Checkpoint: `edit and display per-target positions`. In a Git checkout:

~~~bash
git add src/TargetPositionEditorControl.cs src/TargetViewModels.cs src/TargetEditorForm.cs src/TargetManagerForm.cs tests/UsageBars.Tests/TargetViewModelTests.cs tests/UsageBars.Tests/TargetPositionTests.cs
git commit -m "feat: add per-target position controls"
~~~

---

### Task 4: Reliable single-instance target manager lifecycle

**Files:**
- Create: `src/TargetManagerCoordinator.cs`
- Create: `tests/UsageBars.Tests/TargetManagerCoordinatorTests.cs`
- Modify: `src/TargetManagerForm.cs:9`
- Modify: `src/OverlayController.cs:58-63,117-175,389-406,1086-1106`

**Interfaces:**
- Produces: `IModelessTargetManager` implemented by `TargetManagerForm`.
- Produces: `ITargetManagerOwner : IWin32Window, IDisposable` and its native `TargetManagerOwnerWindow` implementation.
- Produces: `TargetManagerCoordinator.Open()` and `Dispose()`.
- Consumes: controller callbacks for manager construction and diagnostic/failure reporting.

- [ ] **Step 1: Add failing coordinator tests with a fake manager**

Create `TargetManagerCoordinatorTests.cs` with a fake implementing the interface and these cases:

~~~csharp
[Fact]
public void FirstOpenIsDispatchedAndShowsOneOwnedManager()
{
    Action? posted = null;
    var manager = new FakeManager();
    using var owner = new FakeOwner();
    using var coordinator = new TargetManagerCoordinator(
        action => posted = action, () => manager, owner, (_, _) => { });

    coordinator.Open();

    Assert.False(manager.ShowCalled);
    posted!();
    Assert.True(manager.ShowCalled);
    Assert.Same(owner, manager.Owner);
}

[Fact]
public void RepeatedOpenRestoresAndActivatesTheExistingManager()
{
    var manager = new FakeManager { WindowState = FormWindowState.Minimized };
    using var coordinator = CoordinatorWithImmediateDispatch(manager);

    coordinator.Open();
    coordinator.Open();

    Assert.Equal(1, manager.ShowCount);
    Assert.Equal(FormWindowState.Normal, manager.WindowState);
    Assert.Equal(2, manager.ActivateCount);
    Assert.Equal(2, manager.BringToFrontCount);
}

[Fact]
public void ClosingClearsTheSlotSoTheNextRequestCreatesANewManager()
{
    var created = new List<FakeManager>();
    using var coordinator = CoordinatorWithFactory(() =>
    {
        var manager = new FakeManager();
        created.Add(manager);
        return manager;
    });

    coordinator.Open();
    created[0].RaiseFormClosed();
    coordinator.Open();

    Assert.Equal(2, created.Count);
}

[Fact]
public void ShowFailureIsReportedAndDoesNotOccupyTheSlot()
{
    var reports = new List<(string Event, Exception? Error)>();
    var first = new FakeManager { ShowError = new InvalidOperationException("show failed") };
    var second = new FakeManager();
    var queue = new Queue<FakeManager>(new[] { first, second });
    using var coordinator = CoordinatorWithFactory(
        () => queue.Dequeue(), (name, error) => reports.Add((name, error)));

    coordinator.Open();
    coordinator.Open();

    Assert.Contains(reports, report => report.Event == "display-failed");
    Assert.True(second.ShowCalled);
}
~~~

Use these complete test doubles and helpers inside the test class; the fake must also be used by a test asserting that `Dispose()` closes and disposes an active manager exactly once:

~~~csharp
private sealed class FakeOwner : ITargetManagerOwner
{
    public IntPtr Handle { get; } = new(123);
    public bool Disposed { get; private set; }
    public void Dispose() => Disposed = true;
}

private sealed class FakeManager : IModelessTargetManager
{
    public bool IsDisposed { get; private set; }
    public FormWindowState WindowState { get; set; }
    public event FormClosedEventHandler? FormClosed;
    public int ShowCount { get; private set; }
    public int ActivateCount { get; private set; }
    public int BringToFrontCount { get; private set; }
    public int CloseCount { get; private set; }
    public bool ShowCalled => ShowCount > 0;
    public IWin32Window? Owner { get; private set; }
    public Exception? ShowError { get; init; }

    public void Show(IWin32Window owner)
    {
        ShowCount++;
        Owner = owner;
        if (ShowError is not null) throw ShowError;
    }

    public void Activate() => ActivateCount++;
    public void BringToFront() => BringToFrontCount++;
    public void Close() => CloseCount++;
    public void Dispose() => IsDisposed = true;
    public void RaiseFormClosed() =>
        FormClosed?.Invoke(this, new FormClosedEventArgs(CloseReason.UserClosing));
}

private static TargetManagerCoordinator CoordinatorWithImmediateDispatch(FakeManager manager) =>
    new(action => action(), () => manager, new FakeOwner(), (_, _) => { });

private static TargetManagerCoordinator CoordinatorWithFactory(
    Func<FakeManager> factory, Action<string, Exception?>? report = null) =>
    new(action => action(), () => factory(), new FakeOwner(), report ?? ((_, _) => { }));
~~~

- [ ] **Step 2: Run coordinator tests and verify failure**

Run:

~~~powershell
dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter FullyQualifiedName~TargetManagerCoordinatorTests
~~~

Expected: FAIL because coordinator and interface types do not exist.

- [ ] **Step 3: Define the modeless-manager boundary and hidden owner**

Define:

~~~csharp
internal interface ITargetManagerOwner : IWin32Window, IDisposable { }

internal interface IModelessTargetManager : IDisposable
{
    bool IsDisposed { get; }
    FormWindowState WindowState { get; set; }
    event FormClosedEventHandler? FormClosed;
    void Show(IWin32Window owner);
    void Activate();
    void BringToFront();
    void Close();
}
~~~

Have `TargetManagerForm` implement it through its inherited members and change only that form's `StartPosition` to `CenterScreen`; the editor remains `CenterParent`. `TargetManagerOwnerWindow` implements `ITargetManagerOwner`, creates one hidden `NativeWindow` handle with `WS_EX_TOOLWINDOW`, never calls `ShowWindow`, returns that handle through `IWin32Window`, and destroys it once during disposal.

- [ ] **Step 4: Implement the coordinator state machine**

`TargetManagerCoordinator` stores `Action<Action> dispatch`, `Func<IModelessTargetManager> factory`, `ITargetManagerOwner owner`, and `Action<string,Exception?> report`. `Open()` always dispatches `OpenOnUiThread`.

`OpenOnUiThread` behavior:

~~~csharp
if (_manager is { IsDisposed: false } current)
{
    if (current.WindowState == FormWindowState.Minimized)
        current.WindowState = FormWindowState.Normal;
    current.Activate();
    current.BringToFront();
    _report("activated", null);
    return;
}

var manager = _factory();
manager.FormClosed += OnManagerClosed;
try
{
    manager.Show(_owner);
    _manager = manager;
    manager.Activate();
    manager.BringToFront();
    _report("created", null);
}
catch (Exception ex)
{
    if (ReferenceEquals(_manager, manager)) _manager = null;
    manager.FormClosed -= OnManagerClosed;
    manager.Dispose();
    _report("display-failed", ex);
}
~~~

The close handler clears only the matching active instance and reports `closed`. `Dispose` marks the coordinator disposed, unsubscribes, closes/disposes the current manager, clears it, and disposes the hidden owner when the coordinator owns it. Calls after disposal do nothing.

- [ ] **Step 5: Replace controller-owned manager logic**

Construct the coordinator after `_tray`, `_log`, and `_targets` exist:

~~~csharp
_targetManagerCoordinator = new TargetManagerCoordinator(
    action => Application.Current.Dispatcher.BeginInvoke(action),
    () => new TargetManagerForm(_targets, EditTargetFromManager),
    new TargetManagerOwnerWindow(),
    ReportTargetManagerEvent);
~~~

`ShowTargetManager` becomes `_targetManagerCoordinator.Open()`. `ReportTargetManagerEvent` writes the event to `_log`; for `display-failed`, it additionally calls `_tray.ShowWarning("Could not open Manage targets. See diagnostics for details.")`. Remove `_targetManager` and its direct close/dispose code. Dispose the coordinator before the log and tray.

- [ ] **Step 6: Run coordinator, view-model, and full tests**

Run:

~~~powershell
dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter "FullyQualifiedName~TargetManagerCoordinatorTests|FullyQualifiedName~TargetViewModelTests"
dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj
~~~

Expected: PASS, including the existing target-manager reload scheduler tests.

- [ ] **Step 7: Record checkpoint 4**

Checkpoint: `reliable owned target manager lifecycle`. In a Git checkout:

~~~bash
git add src/TargetManagerCoordinator.cs src/TargetManagerForm.cs src/OverlayController.cs tests/UsageBars.Tests/TargetManagerCoordinatorTests.cs
git commit -m "fix: reliably open the target manager"
~~~

---

### Task 5: Remove global positioning, update diagnostics/docs, and verify the release

**Files:**
- Modify: `src/Tray.cs:31-35,67-79,115-120`
- Modify: `src/OverlayController.cs:154-169,570-586,990-1027`
- Modify: `src/Config.cs:17-65,228-246,311-353`
- Modify: `UsageBars.config.default.json:142-151,178-224`
- Modify: `README.md:218,430-505,685-695,760-785,845-855`
- Modify: `tests/UsageBars.Tests/ConfigAndPlacementTests.cs`
- Modify: `tests/UsageBars.Tests/TargetViewModelTests.cs`

**Interfaces:**
- Removes: `Tray.NudgeRequested`, `Tray.ResetPositionRequested`, and `Tray.AddNudge`.
- Removes from active configuration: `OffsetX`, `OffsetY`, `TerminalOffsetY`, `RightAlignOnTerminals`, `PlaceOnIdeStatusBar`, and `StatusBarHeightOverride`.
- Retains geometry configuration: `TitleBarHeight` and `RightReservePx`.
- Produces: diagnostics that print each target's semantic placement, anchor, and offsets.

- [ ] **Step 1: Add a testable tray-menu description and failing menu test**

Extract the stable menu labels into an internal read-only list used when constructing the tray menu:

~~~csharp
internal static IReadOnlyList<string> CommandLabels { get; } = new[]
{
    "Refresh now",
    "Targets",
    "Start with Windows",
    "Copy diagnostics",
    "Open diagnostics folder",
    "Exit",
};
~~~

Add a test asserting `Targets` remains and `Position` is absent. The test must not construct `NotifyIcon`, so it remains headless.

- [ ] **Step 2: Run the tray/config tests and verify failure**

Run:

~~~powershell
dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj --filter "FullyQualifiedName~ConfigTests|FullyQualifiedName~TargetViewModelTests|FullyQualifiedName~OverlayPlacementTests"
~~~

Expected: FAIL until the menu description exists and tests referring to retired global values are updated.

- [ ] **Step 3: Remove the global Position tray path**

Delete the Position submenu construction, the nudge/reset events, and `AddNudge` from `Tray`. Delete the matching subscriptions from `OverlayController`. Target editing remains under `Targets -> Add by clicking a window...` and `Targets -> Manage targets...`.

- [ ] **Step 4: Retire global placement configuration**

Remove active properties and normalization for:

~~~text
OffsetX
OffsetY
TerminalOffsetY
RightAlignOnTerminals
PlaceOnIdeStatusBar
StatusBarHeightOverride
~~~

`System.Text.Json` ignores these unknown properties when reading older config files. The next unrelated `Config.Save()` serializes the current model without them, which discards the old global values. Keep `TitleBarHeight` as the fallback height for top and generic bottom strips and `RightReservePx` as window-control clearance.

Delete or rewrite tests that assert the retired settings change placement. Retain tests for context-default anchors, per-target offsets, title-bar measurement, right-reserve safety, and known per-target IDE status heights.

- [ ] **Step 5: Update diagnostics**

For every target, append:

~~~csharp
$"placement={target.Placement} · position={TargetPositionRules.Format(target.Anchor, target.OffsetX, target.OffsetY)}"
~~~

For every live pill, print its anchor and offsets from `pill.Host`. Remove global `offsetX/offsetY`, `rightAlignOnTerminals`, `placeOnIdeStatusBar`, and status-bar override lines. Include manager lifecycle events from Task 4 in the copied log.

- [ ] **Step 6: Update shipped configuration and README**

Remove the six retired fields and their comments from `UsageBars.config.default.json`. Update README sections to state:

- targets persist their own Top center, Top right, or Bottom center anchor;
- Desktop/Terminal/IDE defaults use the approved mapping;
- Add and Edit provide four-pixel arrows, exact X/Y values, and Reset offset;
- target offsets survive restarts;
- the tray has no Position submenu;
- `Manage targets...` opens one reusable modeless manager;
- legacy global placement keys are ignored when present in older configuration files;
- `targets.json` is version 2 and version 1 migrates automatically.

- [ ] **Step 7: Run all automated verification**

Run:

~~~powershell
dotnet test tests/UsageBars.Tests/UsageBars.Tests.csproj
dotnet build src/UsageBars.csproj -c Release --nologo
~~~

Expected: every test passes and Release build succeeds with no new warnings.

- [ ] **Step 8: Publish and perform the Windows smoke test**

Publish and relaunch the canonical app:

~~~powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Run
~~~

Verify in order:

1. Open the tray and confirm there is no Position submenu.
2. Select `Manage targets...`; confirm one visible manager opens.
3. Select it again; confirm the same manager is restored/activated and no duplicate appears.
4. Close the manager; confirm UsageBars stays running and the manager opens again.
5. Capture a desktop target and confirm Top center with `(0, 0)`.
6. Capture a terminal target and confirm Top right with `(0, 0)`.
7. Capture an IDE target and confirm Bottom center with `(0, 0)`.
8. In both capture and manager edit flows, use every direction button and exact positive/negative X/Y entry; confirm each click changes four units and Reset returns `(0, 0)`.
9. Change a position and cancel the editor; confirm the saved target and live pill do not move.
10. Save a nonzero position, restart UsageBars, and confirm the anchor and offsets remain.
11. Resize the target and move it between different-DPI displays; confirm the pill remains relative to the target and inside its accessible bounds.
12. Open Copy diagnostics and confirm target position and manager lifecycle lines are present while retired global position lines are absent.

- [ ] **Step 9: Record checkpoint 5**

Checkpoint: `remove global positioning and verify per-target release`, including full test count, Release build result, publish result, and smoke-test observations. In a Git checkout:

~~~bash
git add src/Tray.cs src/OverlayController.cs src/Config.cs UsageBars.config.default.json README.md tests/UsageBars.Tests/ConfigAndPlacementTests.cs tests/UsageBars.Tests/TargetViewModelTests.cs
git commit -m "feat: finish per-target position workflow"
~~~

---

## Completion Gate

Do not claim completion until all of these are true:

- Target document version 1 migrates and version 2 round-trips all three anchors and signed offsets.
- Add and manager-edit flows expose the same anchor/nudge/reset behavior.
- Rendering uses only the winning target's anchor/offsets, with safe bounds and same-target grouping.
- The tray Position submenu and its global events are absent.
- Retired global placement configuration has no runtime references.
- `Manage targets...` creates or activates exactly one owned manager on the UI dispatcher.
- Full tests, Release build, publish, restart persistence, and the Windows smoke test all pass.
