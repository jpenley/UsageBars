using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace UsageBars;

internal sealed class ActivityProcessEditorControl : UserControl
{
    private sealed record NodeTag(
        IntPtr Hwnd,
        uint? Pid,
        string? ProcessName,
        bool Selectable);

    private sealed record ProcessTreeViewport(
        NodeTag? SelectedNode,
        IReadOnlySet<NodeTag> ExpandedNodes,
        NodeTag? TopNode,
        bool HadFocus)
    {
        public static ProcessTreeViewport Capture(TreeView tree) => new(
            TagOf(tree.SelectedNode),
            AllNodes(tree.Nodes)
                .Where(node => node.IsExpanded)
                .Select(TagOf)
                .Where(tag => tag is not null)
                .Select(tag => tag!)
                .ToHashSet(),
            TagOf(tree.TopNode),
            tree.Focused);

        public void Restore(TreeView tree, IReadOnlyDictionary<NodeTag, TreeNode> nodes)
        {
            tree.CollapseAll();
            foreach (var node in nodes
                         .Where(pair => ExpandedNodes.Contains(pair.Key))
                         .Select(pair => pair.Value)
                         .OrderBy(node => node.Level))
            {
                node.Expand();
            }

            if (SelectedNode is { } selected && nodes.TryGetValue(selected, out var selectedNode))
                tree.SelectedNode = selectedNode;
            if (TopNode is { } top && nodes.TryGetValue(top, out var topNode))
                tree.TopNode = topNode;
            if (HadFocus) tree.Focus();
        }

        private static NodeTag? TagOf(TreeNode? node) => node?.Tag as NodeTag;

        private static IEnumerable<TreeNode> AllNodes(TreeNodeCollection nodes) => nodes.Cast<TreeNode>()
            .SelectMany(node => AllNodes(node.Nodes).Append(node));
    }

    private readonly CheckBox _showHelpers = new() { Text = "Show system helpers", AutoSize = true };
    private readonly TreeView _processTree = new()
    {
        CheckBoxes = true,
        Height = 150,
        Dock = DockStyle.Top,
        ShowNodeToolTips = true,
    };
    private readonly Button _refresh = new()
    {
        Text = "↻",
        AutoSize = true,
        AccessibleName = "Refresh processes",
        Margin = Padding.Empty,
    };
    private readonly FlowLayoutPanel _chips = new()
    {
        AutoSize = true,
        Dock = DockStyle.Top,
        WrapContents = true,
        Margin = Padding.Empty,
    };
    private readonly TextBox _manualEntry = new()
    {
        AccessibleName = "Activity process entry",
        Width = 180,
    };
    private readonly Button _addManual = new()
    {
        Text = "Add process",
        AutoSize = true,
        AccessibleName = "Add activity process",
    };
    private readonly Label _scope = new()
    {
        AutoSize = true,
        Text = "All tabs/panes under this window owner",
        ForeColor = SystemColors.GrayText,
    };
    private readonly Label _scopeWarning = new()
    {
        AutoSize = true,
        ForeColor = Color.DarkGoldenrod,
        AccessibleName = "Activity process scope warning",
    };
    private readonly Label _missing = new()
    {
        AutoSize = true,
        ForeColor = SystemColors.GrayText,
        AccessibleName = "Activity process availability",
    };
    private readonly Label _manualValidation = new()
    {
        AutoSize = true,
        ForeColor = Color.Firebrick,
        AccessibleName = "Activity process entry validation",
    };
    private readonly ToolTip _toolTip = new();

    private ProcessInventory _inventory;
    private string? _inventoryFingerprint;
    private bool _hasBuiltProcessTree;
    private bool _loading;
    private List<string> _activityProcessNames = new();

    public ActivityProcessEditorControl(ProcessInventory inventory)
    {
        _inventory = inventory;
        Dock = DockStyle.Top;
        AutoSize = true;

        var header = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            ColumnCount = 2,
            Margin = Padding.Empty,
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(_scope, 0, 0);
        header.Controls.Add(_refresh, 1, 0);

        var manual = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            WrapContents = false,
            Margin = Padding.Empty,
        };
        manual.Controls.Add(_manualEntry);
        manual.Controls.Add(_addManual);

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            ColumnCount = 1,
            Margin = Padding.Empty,
        };
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(_scopeWarning, 0, 1);
        layout.Controls.Add(_showHelpers, 0, 2);
        layout.Controls.Add(_processTree, 0, 3);
        layout.Controls.Add(_chips, 0, 4);
        layout.Controls.Add(_missing, 0, 5);
        layout.Controls.Add(manual, 0, 6);
        layout.Controls.Add(_manualValidation, 0, 7);
        Controls.Add(layout);

        _toolTip.SetToolTip(_refresh, "Refresh processes");
        _processTree.BeforeCheck += OnBeforeCheck;
        _processTree.AfterCheck += OnAfterCheck;
        _showHelpers.CheckedChanged += (_, _) => SetInventory(_inventory);
        _refresh.Click += (_, _) => InventoryRefreshRequested?.Invoke(this, EventArgs.Empty);
        _addManual.Click += (_, _) => AddManualEntry();
        SetInventory(inventory);
    }

    public event EventHandler? ValueChanged;
    public event EventHandler? InventoryRefreshRequested;

    public IReadOnlyList<string> ActivityProcessNames => _activityProcessNames.ToList();
    public IReadOnlyList<string> MissingActivityProcessNames => _activityProcessNames
        .Where(name => !_inventory.Entries.Any(entry =>
            ActivityProcessName.TryNormalize(entry.Name, out var entryName)
            && entryName.Equals(name, StringComparison.OrdinalIgnoreCase)))
        .ToList();
    public string ScopeWarning => _scopeWarning.Text;
    public string ManualEntryValidationMessage => _manualValidation.Text;

    public void SetActivityProcesses(IEnumerable<string> values) => ApplyActivityProcesses(values, raiseChanged: false);

    public void SetInventory(ProcessInventory inventory)
    {
        var fingerprint = inventory.Fingerprint(_showHelpers.Checked);
        var warning = ScopeWarningFor(inventory);
        if (_inventoryFingerprint == fingerprint && _scopeWarning.Text == warning) return;

        var viewport = _hasBuiltProcessTree ? ProcessTreeViewport.Capture(_processTree) : null;
        var rendered = RenderTree(inventory);
        _loading = true;
        try
        {
            _processTree.BeginUpdate();
            try
            {
                _processTree.Nodes.Clear();
                _processTree.Nodes.AddRange(rendered.Roots);
                if (viewport is null) _processTree.ExpandAll();
                viewport?.Restore(_processTree, rendered.Nodes);
            }
            finally
            {
                _processTree.EndUpdate();
            }
            _inventory = inventory;
            _inventoryFingerprint = fingerprint;
            _scopeWarning.Text = warning;
            _hasBuiltProcessTree = rendered.Nodes.Count > 0;
            SynchronizeCheckedNodes();
            RebuildChipsAndAvailability();
        }
        finally
        {
            _loading = false;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _toolTip.Dispose();
        base.Dispose(disposing);
    }

    private void OnAfterCheck(object? sender, TreeViewEventArgs e)
    {
        if (_loading || e.Node?.Tag is not NodeTag { Selectable: true, ProcessName: { } name }) return;
        var next = _activityProcessNames.ToList();
        if (e.Node.Checked) next.Add(name);
        else
            next.RemoveAll(value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
        ApplyActivityProcesses(next, raiseChanged: true);
    }

    private static void OnBeforeCheck(object? sender, TreeViewCancelEventArgs e)
    {
        if (e.Node?.Tag is NodeTag { Selectable: false }) e.Cancel = true;
    }

    private void AddManualEntry()
    {
        if (!ActivityProcessName.TryNormalize(_manualEntry.Text, out var name))
        {
            _manualValidation.Text = "Enter a valid process name.";
            return;
        }

        var next = _activityProcessNames.Append(name).ToList();
        ApplyActivityProcesses(next, raiseChanged: true);
        _manualEntry.Text = "";
        _manualValidation.Text = "";
    }

    private void ApplyActivityProcesses(IEnumerable<string> values, bool raiseChanged)
    {
        var next = ActivityProcessName.NormalizeMany(values);
        var changed = !_activityProcessNames.SequenceEqual(next, StringComparer.OrdinalIgnoreCase);
        _activityProcessNames = next;

        _loading = true;
        try
        {
            SynchronizeCheckedNodes();
            RebuildChipsAndAvailability();
        }
        finally
        {
            _loading = false;
        }
        if (changed && raiseChanged) ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SynchronizeCheckedNodes()
    {
        foreach (var node in AllNodes(_processTree.Nodes))
        {
            if (node.Tag is not NodeTag { Selectable: true, ProcessName: { } name }) continue;
            node.Checked = _activityProcessNames.Contains(name, StringComparer.OrdinalIgnoreCase);
        }
    }

    private void RebuildChipsAndAvailability()
    {
        foreach (var chip in _chips.Controls.Cast<Control>().ToArray()) chip.Dispose();
        _chips.Controls.Clear();
        foreach (var name in _activityProcessNames)
        {
            var chip = new Button
            {
                Text = $"{name} ×",
                AutoSize = true,
                AccessibleName = $"Remove activity process {name}",
            };
            chip.Click += (_, _) => ApplyActivityProcesses(
                _activityProcessNames.Where(value => !value.Equals(name, StringComparison.OrdinalIgnoreCase)),
                raiseChanged: true);
            _chips.Controls.Add(chip);
        }

        var missing = MissingActivityProcessNames;
        _missing.Text = missing.Count == 0
            ? ""
            : $"{string.Join(", ", missing)} — not currently running";
    }

    private (TreeNode[] Roots, IReadOnlyDictionary<NodeTag, TreeNode> Nodes) RenderTree(ProcessInventory inventory)
    {
        var roots = new List<TreeNode>();
        var nodes = new Dictionary<NodeTag, TreeNode>();
        var grouped = inventory.Groups.Count > 1;
        foreach (var group in inventory.Groups)
        {
            if (grouped)
            {
                var tag = new NodeTag(group.Hwnd, null, null, false);
                var groupRoot = new TreeNode(group.Label)
                {
                    ToolTipText = group.MayIncludeSiblingWindows
                        ? "This owner is shared by another visible window, so this scope may include sibling windows."
                        : group.Label,
                    Tag = tag,
                };
                roots.Add(groupRoot);
                nodes.Add(tag, groupRoot);
            }

            var groupNodes = new Dictionary<uint, TreeNode>();
            foreach (var entry in group.Entries
                         .Where(entry => _showHelpers.Checked || !entry.IsSystemHelper)
                         .OrderBy(entry => entry.Depth)
                         .ThenBy(entry => entry.Pid))
            {
                var selectable = ActivityProcessName.TryNormalize(entry.Name, out var name);
                var tag = new NodeTag(group.Hwnd, entry.Pid, selectable ? name : null, selectable);
                var node = new TreeNode($"{entry.Name} ({entry.Pid})" + (entry.IsSystemHelper ? " — system helper" : ""))
                {
                    ToolTipText = entry.AncestryPath,
                    Tag = tag,
                };
                groupNodes.Add(entry.Pid, node);
                nodes.Add(tag, node);
                if (groupNodes.TryGetValue(entry.ParentPid, out var parent)) parent.Nodes.Add(node);
                else if (grouped) roots.Single(root => root.Tag is NodeTag rootTag && rootTag.Hwnd == group.Hwnd).Nodes.Add(node);
                else roots.Add(node);
            }
        }
        return (roots.ToArray(), nodes);
    }

    private static IEnumerable<TreeNode> AllNodes(TreeNodeCollection nodes) => nodes.Cast<TreeNode>()
        .SelectMany(node => AllNodes(node.Nodes).Append(node));

    private static string ScopeWarningFor(ProcessInventory inventory)
    {
        var affected = inventory.Groups
            .Where(group => group.MayIncludeSiblingWindows)
            .Select(group => group.Label)
            .OrderBy(label => label, StringComparer.Ordinal)
            .ToList();
        if (affected.Count == 0) return "";
        if (inventory.Groups.Count == 1)
            return "This owner is shared by another visible window, so this list may include that window too.";
        return $"Sibling-window scope warning: {string.Join(", ", affected)}";
    }
}
