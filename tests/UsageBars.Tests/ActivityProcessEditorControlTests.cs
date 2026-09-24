using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Xunit;

namespace UsageBars.Tests;

public class ActivityProcessEditorControlTests
{
    [Fact]
    public void CheckingAnyPidAddsOneNormalizedProcessChip()
    {
        using var control = new ActivityProcessEditorControl(Inventory(
            Entry(10, 0, "terminal", 0),
            Entry(20, 10, "OMP.exe", 1),
            Entry(30, 10, "omp", 1)));
        var changes = 0;
        control.ValueChanged += (_, _) => changes++;
        var tree = FindControls<TreeView>(control).Single();
        var omp = AllNodes(tree.Nodes).Single(node => node.Text.StartsWith("OMP.exe (20)", StringComparison.Ordinal));

        omp.Checked = true;

        Assert.Equal(new[] { "omp" }, control.ActivityProcessNames);
        Assert.Equal(1, changes);
        Assert.Contains(FindControls<Button>(control),
            button => button.AccessibleName == "Remove activity process omp");
    }

    [Fact]
    public void UncheckingLastVisiblePidRemovesTheFilter()
    {
        using var control = new ActivityProcessEditorControl(Inventory(
            Entry(10, 0, "terminal", 0),
            Entry(20, 10, "omp", 1)));
        var omp = AllNodes(FindControls<TreeView>(control).Single().Nodes)
            .Single(node => node.Text.StartsWith("omp (20)", StringComparison.Ordinal));

        omp.Checked = true;
        omp.Checked = false;

        Assert.Empty(control.ActivityProcessNames);
    }

    [Fact]
    public void UncheckingOneOfDuplicateVisiblePidsRemovesTheFilter()
    {
        using var control = new ActivityProcessEditorControl(Inventory(
            Entry(10, 0, "terminal", 0),
            Entry(20, 10, "omp", 1),
            Entry(30, 10, "OMP.exe", 1)));
        var nodes = AllNodes(FindControls<TreeView>(control).Single().Nodes)
            .Where(node => node.Text.StartsWith("omp", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        nodes[0].Checked = true;
        nodes[0].Checked = false;

        Assert.Empty(control.ActivityProcessNames);
        Assert.All(nodes, node => Assert.False(node.Checked));
    }

    [Fact]
    public void RemovingAChipUnchecksEveryPidWithThatName()
    {
        using var control = new ActivityProcessEditorControl(Inventory(
            Entry(10, 0, "terminal", 0),
            Entry(20, 10, "omp", 1),
            Entry(30, 10, "OMP.exe", 1)));
        control.SetActivityProcesses(new[] { "omp" });

        FindControls<Button>(control)
            .Single(button => button.AccessibleName == "Remove activity process omp")
            .PerformClick();

        Assert.Empty(control.ActivityProcessNames);
        Assert.All(AllNodes(FindControls<TreeView>(control).Single().Nodes)
                .Where(node => node.Text.StartsWith("omp", StringComparison.OrdinalIgnoreCase)),
            node => Assert.False(node.Checked));
    }

    [Fact]
    public void SavedAbsentProcessRemainsMarkedNotCurrentlyRunning()
    {
        using var control = new ActivityProcessEditorControl(Inventory(Entry(10, 0, "terminal", 0)));

        control.SetActivityProcesses(new[] { "missing.exe" });

        Assert.Equal(new[] { "missing" }, control.ActivityProcessNames);
        Assert.Equal(new[] { "missing" }, control.MissingActivityProcessNames);
        Assert.Contains(FindControls<Label>(control),
            label => label.Text.Contains("missing", StringComparison.Ordinal)
                && label.Text.Contains("not currently running", StringComparison.Ordinal));
    }

    [Fact]
    public void ManualEntryAddsAProcessThatIsNotInInventory()
    {
        using var control = new ActivityProcessEditorControl(ProcessInventory.Empty);
        var entry = FindControls<TextBox>(control)
            .Single(textBox => textBox.AccessibleName == "Activity process entry");

        entry.Text = "Codex.exe";
        FindControls<Button>(control)
            .Single(button => button.AccessibleName == "Add activity process")
            .PerformClick();

        Assert.Equal(new[] { "codex" }, control.ActivityProcessNames);
        Assert.Equal("", entry.Text);
        Assert.Equal(new[] { "codex" }, control.MissingActivityProcessNames);
    }

    [Fact]
    public void InvalidManualEntryShowsValidationAndDoesNotChangeDraft()
    {
        using var control = new ActivityProcessEditorControl(ProcessInventory.Empty);
        control.SetActivityProcesses(new[] { "codex" });
        var entry = FindControls<TextBox>(control)
            .Single(textBox => textBox.AccessibleName == "Activity process entry");

        entry.Text = "bad/name";
        FindControls<Button>(control)
            .Single(button => button.AccessibleName == "Add activity process")
            .PerformClick();

        Assert.Equal(new[] { "codex" }, control.ActivityProcessNames);
        Assert.NotEmpty(control.ManualEntryValidationMessage);
    }

    [Fact]
    public void RefreshButtonRaisesOneRequestAndNeverEnumeratesItself()
    {
        using var control = new ActivityProcessEditorControl(Inventory(Entry(10, 0, "terminal", 0)));
        var requests = 0;
        control.InventoryRefreshRequested += (_, _) => requests++;

        FindControls<Button>(control)
            .Single(button => button.AccessibleName == "Refresh processes")
            .PerformClick();

        Assert.Equal(1, requests);
    }

    [Fact]
    public void ReplacingInventoryPreservesViewportAndCheckedFilters()
    {
        var initial = Entries().ToArray();
        using var control = new ActivityProcessEditorControl(Inventory(initial));
        using var host = new Form { Width = 800, Height = 700 };
        host.Controls.Add(control);
        host.Show();
        Application.DoEvents();

        var tree = FindControls<TreeView>(control).Single();
        var selected = AllNodes(tree.Nodes).Single(node => node.Text.StartsWith("child-12 (32)", StringComparison.Ordinal));
        var top = AllNodes(tree.Nodes).Single(node => node.Text.StartsWith("child-18 (38)", StringComparison.Ordinal));
        var checkedNode = AllNodes(tree.Nodes).Single(node => node.Text.StartsWith("child-0 (20)", StringComparison.Ordinal));
        tree.SelectedNode = selected;
        tree.TopNode = top;
        tree.Focus();
        checkedNode.Checked = true;
        var topBeforeReplacement = tree.TopNode?.Text;

        control.SetInventory(Inventory(Entries().Append(Entry(99, 10, "new-child", 1)).ToArray()));

        Assert.Equal("child-12 (32)", tree.SelectedNode?.Text);
        Assert.Equal(topBeforeReplacement, tree.TopNode?.Text);
        Assert.True(tree.Focused);
        Assert.Equal(new[] { "child-0" }, control.ActivityProcessNames);
        Assert.True(AllNodes(tree.Nodes).Single(node => node.Text.StartsWith("child-0 (20)", StringComparison.Ordinal)).Checked);
    }

    [Fact]
    public void SharedOwnerInventoryShowsScopeWarning()
    {
        var inventory = new ProcessInventory(new[]
        {
            new ProcessInventoryGroup(new IntPtr(100), 10, "Terminal (PID 10)",
                new[] { Entry(10, 0, "terminal", 0) }, true),
        });
        using var control = new ActivityProcessEditorControl(inventory);

        Assert.Contains("shared by another visible window", control.ScopeWarning, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupScopeNodesCannotBeChecked()
    {
        var inventory = new ProcessInventory(new[]
        {
            new ProcessInventoryGroup(new IntPtr(100), 10, "First (PID 10)",
                new[] { Entry(10, 0, "terminal", 0) }, false),
            new ProcessInventoryGroup(new IntPtr(200), 20, "Second (PID 20)",
                new[] { Entry(20, 0, "terminal", 0) }, false),
        });
        using var control = new ActivityProcessEditorControl(inventory);
        var firstScope = FindControls<TreeView>(control).Single().Nodes[0];

        firstScope.Checked = true;

        Assert.False(firstScope.Checked);
    }

    [Fact]
    public void FailedInventoryRenderingKeepsThePreviousTreeAndFilters()
    {
        using var control = new ActivityProcessEditorControl(Inventory(
            Entry(10, 0, "terminal", 0),
            Entry(20, 10, "codex", 1)));
        control.SetActivityProcesses(new[] { "codex" });
        var tree = FindControls<TreeView>(control).Single();
        var original = AllNodes(tree.Nodes).Single(node => node.Text.StartsWith("codex (20)", StringComparison.Ordinal));
        var malformed = Inventory(
            Entry(10, 0, "terminal", 0),
            Entry(20, 10, "codex", 1),
            Entry(20, 10, "omp", 1));

        Assert.Throws<ArgumentException>(() => control.SetInventory(malformed));

        Assert.Same(original, AllNodes(tree.Nodes).Single(node => node.Text.StartsWith("codex (20)", StringComparison.Ordinal)));
        Assert.Equal(new[] { "codex" }, control.ActivityProcessNames);
    }

    private static IEnumerable<ProcessInventoryEntry> Entries() => Enumerable.Range(0, 24)
        .Select(index => Entry((uint)(20 + index), 10, $"child-{index}", 1))
        .Prepend(Entry(10, 0, "terminal", 0));

    private static ProcessInventory Inventory(params ProcessInventoryEntry[] entries) => new(entries, 1);

    private static ProcessInventoryEntry Entry(uint pid, uint parentPid, string name, int depth) =>
        new(pid, parentPid, name, depth, name, false);

    private static IEnumerable<T> FindControls<T>(Control root) where T : Control => root.Controls.Cast<Control>()
        .SelectMany(control => Descendants(control).Append(control))
        .OfType<T>();

    private static IEnumerable<Control> Descendants(Control control) => control.Controls.Cast<Control>()
        .SelectMany(child => Descendants(child).Append(child));

    private static IEnumerable<TreeNode> AllNodes(TreeNodeCollection nodes) => nodes.Cast<TreeNode>()
        .SelectMany(node => AllNodes(node.Nodes).Append(node));
}
