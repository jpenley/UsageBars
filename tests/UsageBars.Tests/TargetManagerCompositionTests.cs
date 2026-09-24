using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Xunit;

namespace UsageBars.Tests;

public class TargetManagerCompositionTests
{
    [Fact]
    public void SharedWindowSourceUsesTheSameEligibilityForTrackingAndManagerSnapshots()
    {
        var rawOrder = new List<IntPtr> { new(10), new(20) };
        var snapshots = new Dictionary<IntPtr, WindowSnapshot>
        {
            [new IntPtr(10)] = Window(10, 100, "Eligible"),
            [new IntPtr(20)] = Window(20, 999, "UsageBars manager"),
        };
        var source = new TargetWindowSource(
            ownPid: 999,
            zOrder: () => rawOrder,
            snapshotOf: hwnd => snapshots[hwnd],
            eligibleHosts: (order, _) => order
                .Where(hwnd => hwnd == new IntPtr(10))
                .Select(hwnd => new HostCandidate(hwnd, 100))
                .ToList());

        var trackingHosts = source.EligibleHosts(rawOrder);
        var managerWindows = source.EligibleWindows();

        Assert.Equal(new[] { new IntPtr(10) }, trackingHosts.Select(host => host.Hwnd));
        Assert.Equal(new[] { new IntPtr(10) }, managerWindows.Select(window => window.Hwnd));
    }

    [Fact]
    public void CaptureContextUsesInferenceAndCapturedOwnerInventoryFromOneLiveSnapshot()
    {
        var captured = Window(10, 100, "Terminal");
        var sibling = Window(20, 100, "Sibling");
        var tree = ProcessTree.FromMap(new[]
        {
            (100u, 1u, "terminal"),
            (101u, 100u, "claude"),
        });
        var composition = Composition(
            new RecordingTargetRuleStore(),
            new[] { captured, sibling }, tree);

        var context = composition.BuildCaptureContext(new WindowCaptureResult(
            true, captured, null, new[] { captured, sibling }));

        Assert.Contains(context.Draft.Definition.Providers,
            binding => binding.Provider == ProviderId.Claude);
        var group = Assert.Single(context.Inventory.Groups);
        Assert.Equal(captured.Hwnd, group.Hwnd);
        Assert.True(group.MayIncludeSiblingWindows);
        Assert.Contains(group.Entries, entry => entry.Pid == 101 && entry.Name == "claude");
    }

    [Fact]
    public void UnifiedManagerFactoryRefreshesTrackingWhenPreviewSuppressionChanges()
    {
        var captured = Window(10, 100, "Terminal");
        var tracks = 0;
        var composition = Composition(
            new RecordingTargetRuleStore(),
            new[] { captured },
            ProcessTree.FromMap(new[] { (100u, 1u, "terminal") }),
            previewSession: window => new RecordingSetPreviewSession(window),
            previewsChanged: () => tracks++);
        using var manager = composition.CreateManager();
        var definition = TargetDefinition.Captured(
            "Terminal", "terminal", "Frame", new[] { ProviderBinding.Claude },
            TargetPlacementKind.Terminal, TargetActivationPolicy.AlwaysWhileOpen, true, 0);

        manager.BeginCaptured(new TargetCaptureContext(
            captured,
            new TargetDraft(definition, captured.Title, Array.Empty<string>()),
            ProcessInventory.Empty));

        Assert.IsType<TargetManagerForm>(manager);
        Assert.Contains(captured.Hwnd, manager.SuppressedHwnds);
        Assert.Equal(1, tracks);

        manager.Dispose();

        Assert.Empty(manager.SuppressedHwnds);
        Assert.Equal(2, tracks);
    }

    [Fact]
    public void ExistingRuleInventoryUsesOneWindowEnumerationAndOneProcessSnapshot()
    {
        var target = TargetDefinition.Captured(
            "Terminal", "terminal", "Frame", new[] { ProviderBinding.Claude },
            TargetPlacementKind.Terminal, TargetActivationPolicy.RequireActivityProcess,
            true, 0, new[] { "omp" });
        var windows = new[]
        {
            Window(10, 100, "First"),
            Window(20, 200, "Second"),
        };
        var tree = ProcessTree.FromMap(new[]
        {
            (100u, 1u, "terminal"),
            (101u, 100u, "omp"),
            (200u, 1u, "terminal"),
            (201u, 200u, "codex"),
        });
        var windowEnumerations = 0;
        var processSnapshots = 0;
        var composition = new TargetManagerComposition(
            new RecordingTargetRuleStore(target),
            new Config(),
            ownPid: 999,
            liveWindows: () =>
            {
                windowEnumerations++;
                return windows;
            },
            processTree: () =>
            {
                processSnapshots++;
                return tree;
            },
            previewSession: window => new RecordingSetPreviewSession(window),
            previewsChanged: () => { });
        using var manager = Assert.IsType<TargetManagerForm>(composition.CreateManager());
        manager.Show();
        var rule = Assert.Single(FindControls<ListView>(manager));
        Assert.Single(rule.Items.Cast<ListViewItem>()).Selected = true;

        FindControls<Button>(manager).Single(button => button.Text == "Edit").PerformClick();

        Assert.Equal(2, windowEnumerations); // one inventory snapshot plus one preview reconciliation
        Assert.Equal(1, processSnapshots);
        var processTree = Assert.Single(FindControls<TreeView>(manager));
        Assert.Equal(2, processTree.Nodes.Count);

        FindControls<Button>(manager)
            .Single(button => button.AccessibleName == "Refresh processes")
            .PerformClick();

        Assert.Equal(3, windowEnumerations);
        Assert.Equal(2, processSnapshots);
    }

    private static TargetManagerComposition Composition(
        ITargetRuleStore store,
        IReadOnlyList<WindowSnapshot> windows,
        ProcessTree tree,
        Func<WindowSnapshot, ITargetPreviewSession>? previewSession = null,
        Action? previewsChanged = null) =>
        new(
            store,
            new Config(),
            ownPid: 999,
            liveWindows: () => windows,
            processTree: () => tree,
            previewSession: previewSession ?? (window => new RecordingSetPreviewSession(window)),
            previewsChanged: previewsChanged ?? (() => { }));

    private static WindowSnapshot Window(long hwnd, uint pid, string title) =>
        new(new IntPtr(hwnd), pid, "terminal", "Frame", title, true);

    private static IEnumerable<T> FindControls<T>(Control root) where T : Control =>
        root.Controls.Cast<Control>()
            .SelectMany(control => Descendants(control).Append(control))
            .OfType<T>();

    private static IEnumerable<Control> Descendants(Control control) =>
        control.Controls.Cast<Control>()
            .SelectMany(child => Descendants(child).Append(child));
}
