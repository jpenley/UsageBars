using System;
using System.Linq;
using Xunit;

namespace UsageBars.Tests;

public class ProcessInventoryTests
{
    [Fact]
    public void AnyConfiguredActivityProcessCreditsADeepDescendant()
    {
        var tree = ProcessTree.FromMap(new[]
        {
            (10u, 1u, "WindowsTerminal"),
            (11u, 10u, "pwsh"),
            (12u, 11u, "node"),
            (13u, 12u, "node"),
            (14u, 13u, "codex.exe"),
        });

        Assert.True(tree.HasAnyProcessUnder(10u, new[] { "omp", "CODEX" }, excludePid: 999u));
    }

    [Fact]
    public void InventoryContainsOwnerAndAllDescendantsWithStablePaths()
    {
        var tree = ProcessTree.FromMap(new[]
        {
            (10u, 1u, "WindowsTerminal"),
            (20u, 10u, "OpenConsole"),
            (30u, 20u, "pwsh"),
            (40u, 30u, "codex"),
            (50u, 1u, "unrelated"),
        });

        var inventory = ProcessInventoryBuilder.Build(
            Window(10u, new IntPtr(100)),
            new[] { Window(10u, new IntPtr(100)) }, tree);

        Assert.Equal(new[] { "WindowsTerminal", "OpenConsole", "pwsh", "codex" },
            inventory.Entries.OrderBy(entry => entry.Depth).Select(entry => entry.Name));
        Assert.Equal("WindowsTerminal > OpenConsole > pwsh > codex",
            inventory.Entries.Single(entry => entry.Pid == 40).AncestryPath);
        Assert.DoesNotContain(inventory.Entries, entry => entry.Pid == 50);
    }

    [Fact]
    public void NormalChoicesDeduplicateNamesAndExcludeKnownHelpers()
    {
        var inventory = new ProcessInventory(new[]
        {
            Entry(10, "OpenConsole", helper: true),
            Entry(11, "conhost", helper: true),
            Entry(12, "pwsh"),
            Entry(13, "codex"),
            Entry(14, "CODEX"),
        }, 1);

        Assert.Equal(new[] { "codex", "pwsh" }, inventory.SelectableNames(showSystemHelpers: false));
        Assert.Contains("OpenConsole", inventory.SelectableNames(showSystemHelpers: true));
        Assert.Contains("conhost", inventory.SelectableNames(showSystemHelpers: true));
    }

    [Fact]
    public void InventoryKeepsDuplicateProcessNamesForPidSpecificTreeRendering()
    {
        var inventory = new ProcessInventory(new[]
        {
            Entry(10, "terminal", parentPid: 0, depth: 0),
            Entry(20, "omp", parentPid: 10, depth: 1),
            Entry(30, "OMP.exe", parentPid: 10, depth: 1),
        }, 1);

        Assert.Equal(new uint[] { 20, 30 }, inventory.Entries
            .Where(entry => entry.Name.StartsWith("omp", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Pid));
    }

    [Fact]
    public void InventoryFingerprintIsStableAndIncludesHelperVisibility()
    {
        var inventory = new ProcessInventory(new[]
        {
            Entry(7, "WindowsTerminal", parentPid: 0, depth: 0),
            Entry(8, "pwsh", parentPid: 7, depth: 1),
            Entry(9, "OpenConsole", helper: true, parentPid: 7, depth: 1),
        }, 1);

        Assert.Equal(inventory.Fingerprint(showSystemHelpers: false), inventory.Fingerprint(showSystemHelpers: false));
        Assert.NotEqual(inventory.Fingerprint(showSystemHelpers: false), inventory.Fingerprint(showSystemHelpers: true));
    }

    [Fact]
    public void SharedOwnerPidProducesSiblingWindowWarning()
    {
        var selected = Window(10u, new IntPtr(100));
        var sibling = Window(10u, new IntPtr(200));
        var differentOwner = Window(11u, new IntPtr(300));

        var inventory = ProcessInventoryBuilder.Build(
            selected, new[] { selected, sibling, differentOwner }, TreeFor(10u));

        Assert.True(inventory.MayIncludeSiblingWindows);
        Assert.Equal(2, inventory.VisibleWindowsWithOwner);
    }

    [Fact]
    public void SingleVisibleOwnerWindowDoesNotWarn()
    {
        var selected = Window(10u, new IntPtr(100));

        var inventory = ProcessInventoryBuilder.Build(selected, new[] { selected }, TreeFor(10u));

        Assert.False(inventory.MayIncludeSiblingWindows);
        Assert.Equal(1, inventory.VisibleWindowsWithOwner);
    }

    [Fact]
    public void OwnerAndKnownConsoleProcessesAreMarkedAsHelpersAccurately()
    {
        var tree = ProcessTree.FromMap(new[]
        {
            (10u, 1u, "WindowsTerminal"),
            (20u, 10u, "OpenConsole.exe"),
            (30u, 20u, "conhost"),
            (40u, 30u, "pwsh"),
        });

        var inventory = ProcessInventoryBuilder.Build(
            Window(10u, new IntPtr(100)), Array.Empty<WindowSnapshot>(), tree);

        Assert.True(inventory.Entries.Single(entry => entry.Pid == 20).IsSystemHelper);
        Assert.True(inventory.Entries.Single(entry => entry.Pid == 30).IsSystemHelper);
        Assert.False(inventory.Entries.Single(entry => entry.Pid == 40).IsSystemHelper);
    }

    [Fact]
    public void CyclicParentDataDoesNotInventOwnerDescendants()
    {
        var tree = ProcessTree.FromMap(new[]
        {
            (10u, 1u, "owner"),
            (20u, 30u, "cycle-a"),
            (30u, 20u, "cycle-b"),
        });

        var entries = tree.InventoryFor(10u);

        var owner = Assert.Single(entries);
        Assert.Equal(10u, owner.Pid);
        Assert.Equal(0, owner.Depth);
    }

    [Fact]
    public void CombinedInventoriesKeepLabeledWindowGroupsWarningsAndDeduplicatedChoices()
    {
        var firstWindow = Window(10u, new IntPtr(100)) with { Title = "Terminal One" };
        var first = ProcessInventoryBuilder.Build(firstWindow, new[]
        {
            firstWindow,
            firstWindow with { Hwnd = new IntPtr(101), Title = "Sibling" },
        }, ProcessTree.FromMap(new[]
        {
            (10u, 1u, "WindowsTerminal"),
            (20u, 10u, "codex"),
        }));
        var secondWindow = Window(50u, new IntPtr(200)) with
        {
            ProcessName = "wezterm",
            Title = "Terminal Two",
        };
        var second = ProcessInventoryBuilder.Build(secondWindow, new[] { secondWindow },
            ProcessTree.FromMap(new[]
            {
                (50u, 1u, "wezterm"),
                (60u, 50u, "CODEX"),
                (70u, 50u, "pwsh"),
            }));

        var combined = ProcessInventory.Combine(first, second);

        Assert.Equal(2, combined.Groups.Count);
        var firstGroup = Assert.Single(combined.Groups, group => group.Hwnd == firstWindow.Hwnd);
        Assert.Equal(10u, firstGroup.OwnerPid);
        Assert.Contains("Terminal One", firstGroup.Label);
        Assert.Contains("10", firstGroup.Label);
        Assert.True(firstGroup.MayIncludeSiblingWindows);
        Assert.Equal(new uint[] { 10, 20 }, firstGroup.Entries.Select(entry => entry.Pid).Order());

        var secondGroup = Assert.Single(combined.Groups, group => group.Hwnd == secondWindow.Hwnd);
        Assert.Equal(50u, secondGroup.OwnerPid);
        Assert.Contains("Terminal Two", secondGroup.Label);
        Assert.Contains("50", secondGroup.Label);
        Assert.False(secondGroup.MayIncludeSiblingWindows);
        Assert.Equal(new uint[] { 50, 60, 70 }, secondGroup.Entries.Select(entry => entry.Pid).Order());
        Assert.Equal(new[] { "codex", "pwsh", "wezterm", "WindowsTerminal" },
            combined.SelectableNames(showSystemHelpers: false));
    }

    [Fact]
    public void GroupedFingerprintIsOrderIndependentAndIncludesEveryScopeField()
    {
        var first = new ProcessInventoryGroup(
            new IntPtr(100), 10, "Terminal One (PID 10)",
            new[] { Entry(10, "terminal", parentPid: 0, depth: 0) }, false);
        var second = new ProcessInventoryGroup(
            new IntPtr(200), 20, "Terminal Two (PID 20)",
            new[] { Entry(20, "pwsh", parentPid: 0, depth: 0) }, true);
        var fingerprint = new ProcessInventory(new[] { first, second }).Fingerprint(false);

        Assert.Equal(fingerprint,
            new ProcessInventory(new[] { second, first }).Fingerprint(false));
        Assert.NotEqual(fingerprint,
            new ProcessInventory(new[] { first with { Hwnd = new IntPtr(101) }, second }).Fingerprint(false));
        Assert.NotEqual(fingerprint,
            new ProcessInventory(new[] { first with { OwnerPid = 11 }, second }).Fingerprint(false));
        Assert.NotEqual(fingerprint,
            new ProcessInventory(new[] { first with { Label = "Renamed (PID 10)" }, second }).Fingerprint(false));
        Assert.NotEqual(fingerprint,
            new ProcessInventory(new[] { first with { MayIncludeSiblingWindows = true }, second }).Fingerprint(false));
    }

    private static WindowSnapshot Window(uint pid, IntPtr hwnd) =>
        new(hwnd, pid, "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS", "Terminal", true);

    private static ProcessTree TreeFor(uint ownerPid) => ProcessTree.FromMap(new[]
    {
        (ownerPid, 1u, "WindowsTerminal"),
    });

    private static ProcessInventoryEntry Entry(
        uint pid,
        string name,
        bool helper = false,
        uint parentPid = 10,
        int depth = 1) =>
        new(pid, parentPid, name, depth, $"WindowsTerminal > {name}", helper);
}
