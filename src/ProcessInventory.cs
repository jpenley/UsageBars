using System;
using System.Collections.Generic;
using System.Linq;

namespace UsageBars;

internal sealed record ProcessInventoryEntry(
    uint Pid,
    uint ParentPid,
    string Name,
    int Depth,
    string AncestryPath,
    bool IsSystemHelper);

internal sealed record ProcessInventoryGroup(
    IntPtr Hwnd,
    uint OwnerPid,
    string Label,
    IReadOnlyList<ProcessInventoryEntry> Entries,
    bool MayIncludeSiblingWindows);

internal sealed record ProcessInventory(IReadOnlyList<ProcessInventoryGroup> Groups)
{
    public static ProcessInventory Empty { get; } =
        new(Array.Empty<ProcessInventoryGroup>());

    public ProcessInventory(
        IReadOnlyList<ProcessInventoryEntry> entries,
        int visibleWindowsWithOwner)
        : this(new[]
        {
            new ProcessInventoryGroup(
                IntPtr.Zero,
                entries.OrderBy(entry => entry.Depth).FirstOrDefault()?.Pid ?? 0,
                "Process scope",
                entries,
                visibleWindowsWithOwner > 1),
        })
    {
    }

    public IReadOnlyList<ProcessInventoryEntry> Entries =>
        Groups.SelectMany(group => group.Entries).ToList();

    public int VisibleWindowsWithOwner => Groups.Count switch
    {
        0 => 0,
        1 => Groups[0].MayIncludeSiblingWindows ? 2 : 1,
        _ => Groups.Count,
    };

    public bool MayIncludeSiblingWindows =>
        Groups.Any(group => group.MayIncludeSiblingWindows);

    public static ProcessInventory Combine(params ProcessInventory[] inventories) =>
        Combine((IEnumerable<ProcessInventory>)inventories);

    public static ProcessInventory Combine(IEnumerable<ProcessInventory> inventories) =>
        new(inventories.SelectMany(inventory => inventory.Groups).ToList());

    public string Fingerprint(bool showSystemHelpers) => string.Join("\n",
        Groups
            .OrderBy(group => group.Hwnd.ToInt64())
            .ThenBy(group => group.OwnerPid)
            .ThenBy(group => group.Label, StringComparer.Ordinal)
            .SelectMany(group => new[]
            {
                $"group={group.Hwnd.ToInt64()}|{group.OwnerPid}|{group.Label}|{group.MayIncludeSiblingWindows}",
            }.Concat(group.Entries
                .Where(entry => showSystemHelpers || !entry.IsSystemHelper)
                .OrderBy(entry => entry.Pid)
                .Select(entry => $"entry={entry.Pid}|{entry.ParentPid}|{entry.Name}|{entry.Depth}|{entry.IsSystemHelper}"))))
        + $"\nhelpers={showSystemHelpers}";

    public IReadOnlyList<string> SelectableNames(bool showSystemHelpers) => Entries
        .Where(entry => showSystemHelpers || !entry.IsSystemHelper)
        .Select(entry => entry.Name)
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

internal static class ProcessInventoryBuilder
{
    public static ProcessInventory Build(
        WindowSnapshot selectedWindow,
        IReadOnlyList<WindowSnapshot> topLevelWindows,
        ProcessTree tree)
    {
        var entries = tree.InventoryFor(selectedWindow.Pid).ToList();
        if (entries.All(entry => entry.Pid != selectedWindow.Pid))
        {
            var ownerName = ProcessInventoryHelpers.NormalizeName(selectedWindow.ProcessName);
            entries.Insert(0, new ProcessInventoryEntry(
                selectedWindow.Pid,
                0,
                ownerName,
                0,
                ownerName,
                ProcessInventoryHelpers.IsKnownHelper(ownerName)));
        }

        var visibleOwnerWindows = topLevelWindows
            .Where(window => window.Pid == selectedWindow.Pid)
            .Select(window => window.Hwnd)
            .Append(selectedWindow.Hwnd)
            .Distinct()
            .Count();

        var title = string.IsNullOrWhiteSpace(selectedWindow.Title)
            ? "Untitled window"
            : selectedWindow.Title;
        var group = new ProcessInventoryGroup(
            selectedWindow.Hwnd,
            selectedWindow.Pid,
            $"{title} (PID {selectedWindow.Pid})",
            entries,
            visibleOwnerWindows > 1);
        return new ProcessInventory(new[] { group });
    }
}

internal static class ProcessInventoryHelpers
{
    private static readonly HashSet<string> KnownHelpers = new(StringComparer.OrdinalIgnoreCase)
    {
        "OpenConsole",
        "conhost",
    };

    public static string NormalizeName(string name)
    {
        var trimmed = name.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
            : trimmed;
    }

    public static bool IsKnownHelper(string name) =>
        KnownHelpers.Contains(NormalizeName(name));
}
