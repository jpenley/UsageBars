using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Xunit;

namespace UsageBars.Tests;

public class TargetEditorLayoutTests
{
    [Fact]
    public void RefreshProcessesButtonIsAccessibleAndRaisesOneRequestPerClick()
    {
        using var control = new TargetEditorControl(
            TargetEditorModel.FromExisting(TwoProviderTarget()), ProcessInventory.Empty);
        var requests = 0;
        control.InventoryRefreshRequested += (_, _) => requests++;
        EnableProcessActivation(control);

        var refresh = FindControls<Button>(control)
            .Single(button => button.AccessibleName == "Refresh processes");
        refresh.PerformClick();

        Assert.Equal("↻", refresh.Text);
        Assert.Equal(1, requests);
    }

    [Fact]
    public void SharedEditorUsesReadableStackedOptionsLayout()
    {
        using var control = new TargetEditorControl(
            TargetEditorModel.FromExisting(TwoProviderTarget()), ProcessInventory.Empty);
        control.Size = control.MinimumSize;
        control.PerformLayout();

        Assert.Equal(Orientation.Vertical, control.OptionsOrientation);
        Assert.True(control.OptionsContentWidth >= control.ResizableOptionPreferredWidth);
        Assert.True(control.MinimumSize.Width >= 620);
        Assert.True(control.MinimumSize.Height >= 540);
    }

    [Fact]
    public void PositionEditorReportsOffsetChangesForLivePreview()
    {
        using var control = new TargetPositionEditorControl();
        var changes = 0;
        control.ValueChanged += (_, _) => changes++;

        control.OffsetX = 12;
        control.OffsetY = -8;

        Assert.Equal(2, changes);
    }

    [Fact]
    public void EditorShowsProviderCheckboxesWithoutProviderSpecificProcessEditors()
    {
        using var control = new TargetEditorControl(
            TargetEditorModel.FromExisting(TwoProviderTarget()), ProcessInventory.Empty);

        Assert.Contains(FindControls<CheckBox>(control), check => check.Text == "Claude");
        Assert.Contains(FindControls<CheckBox>(control), check => check.Text == "Codex");
        Assert.DoesNotContain(FindControls<ComboBox>(control), combo =>
            combo.AccessibleName?.Contains("provider process", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void TitleControlsLoadTheSavedMatcherAndUpdateCurrentDefinition()
    {
        var target = TwoProviderTarget() with
        {
            WindowTitle = new TitleRule(TitleMatchKind.Prefix, "WorldGen"),
        };
        using var control = new TargetEditorControl(TargetEditorModel.FromExisting(target), ProcessInventory.Empty);
        var title = Assert.Single(FindControls<TitleRuleEditorControl>(control));

        Assert.Equal(new TitleRule(TitleMatchKind.Prefix, "WorldGen"), title.Rule);
        var matcher = FindControls<TextBox>(title).Single(text => text.AccessibleName == "Title matcher value");
        matcher.Text = "Project";

        Assert.Equal(new TitleRule(TitleMatchKind.Prefix, "Project"), control.CurrentDefinition.WindowTitle);
    }

    [Fact]
    public void ActivityCheckboxChangesUpdateCurrentDefinitionAndActivation()
    {
        using var control = new TargetEditorControl(
            TargetEditorModel.FromExisting(TwoProviderTarget()),
            Inventory(Entry(10, 0, "WindowsTerminal", 0), Entry(20, 10, "codex", 1)));
        EnableProcessActivation(control);
        var tree = FindControls<TreeView>(Assert.Single(FindControls<ActivityProcessEditorControl>(control))).Single();

        FindNode(tree.Nodes, 20).Checked = true;

        var edited = control.CurrentDefinition;
        Assert.Equal(new[] { "codex" }, edited.ActivityProcessNames);
        Assert.Equal(TargetActivationPolicy.RequireActivityProcess, edited.Activation);
    }

    [Fact]
    public void SwitchingToAlwaysOpenClearsActivityProcesses()
    {
        using var control = new TargetEditorControl(
            TargetEditorModel.FromExisting(TargetWithActivity("codex", "omp")), ProcessInventory.Empty);

        FindControls<RadioButton>(control)
            .Single(button => button.Text == "Always while the window is open")
            .Checked = true;

        Assert.Empty(control.CurrentDefinition.ActivityProcessNames);
        Assert.Equal(TargetActivationPolicy.AlwaysWhileOpen, control.CurrentDefinition.Activation);
    }

    [Fact]
    public void ActivityEditorIsVisibleOnlyInProcessRunningMode()
    {
        using var host = new Form { Width = 800, Height = 700 };
        var control = new TargetEditorControl(
            TargetEditorModel.FromExisting(TargetWithActivity("codex", "omp")), ProcessInventory.Empty);
        host.Controls.Add(control);
        host.Show();
        Application.DoEvents();
        var activity = Assert.Single(FindControls<ActivityProcessEditorControl>(control));

        Assert.True(activity.Visible);

        FindControls<RadioButton>(control)
            .Single(button => button.Text == "Always while the window is open")
            .Checked = true;
        Assert.False(activity.Visible);
        Assert.Empty(control.CurrentDefinition.ActivityProcessNames);

        EnableProcessActivation(control);
        Assert.True(activity.Visible);
    }

    [Fact]
    public void ProcessActivationRequiresASelectedProcessBeforeSave()
    {
        using var control = new TargetEditorControl(
            TargetEditorModel.FromExisting(TwoProviderTarget()), ProcessInventory.Empty);
        EnableProcessActivation(control);

        Assert.Contains("Select at least one activity process.", control.ValidateDefinition());
    }

    private static void EnableProcessActivation(Control control) => FindControls<RadioButton>(control)
        .Single(button => button.Text == "When any selected process is running")
        .Checked = true;

    private static TargetDefinition TwoProviderTarget() => TargetDefinition.Captured(
        "Terminal", "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS",
        new[] { ProviderBinding.Claude, ProviderBinding.Codex },
        TargetPlacementKind.Terminal, TargetActivationPolicy.AlwaysWhileOpen, true, 0);

    private static TargetDefinition TargetWithActivity(string first, string second) => TargetDefinition.Captured(
        "Terminal", "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS",
        new[] { ProviderBinding.Claude, ProviderBinding.Codex },
        TargetPlacementKind.Terminal, TargetActivationPolicy.RequireActivityProcess, true, 0,
        new[] { first, second });

    private static ProcessInventory Inventory(params ProcessInventoryEntry[] entries) => new(entries, 1);

    private static ProcessInventoryEntry Entry(uint pid, uint parentPid, string name, int depth) =>
        new(pid, parentPid, name, depth, name, false);

    private static T FindControl<T>(Control root) where T : Control =>
        FindControls<T>(root).Single();

    private static IEnumerable<T> FindControls<T>(Control root) where T : Control => root.Controls.Cast<Control>()
        .SelectMany(control => Descendants(control).Append(control))
        .OfType<T>();

    private static IEnumerable<Control> Descendants(Control control) => control.Controls.Cast<Control>()
        .SelectMany(child => Descendants(child).Append(child));

    private static TreeNode FindNode(TreeNodeCollection nodes, uint pid) => nodes.Cast<TreeNode>()
        .SelectMany(node => Descendants(node).Append(node))
        .Single(node => node.Text.Contains($"({pid})", StringComparison.Ordinal));

    private static IEnumerable<TreeNode> Descendants(TreeNode node) => node.Nodes.Cast<TreeNode>()
        .SelectMany(child => Descendants(child).Append(child));
}
