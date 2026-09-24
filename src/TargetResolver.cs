using System;
using System.Collections.Generic;
using System.Linq;

namespace UsageBars;

internal sealed record TargetResolution(
    List<HostWindowInfo> Hosts,
    IReadOnlyDictionary<ProviderId, string> Rejections);

/// <summary>
/// Resolves configured target definitions against a snapshot of visible windows.
///
/// The pure <see cref="Resolve"/> overload deliberately accepts every input it needs so target
/// matching can be tested without native windows. <see cref="FindAll"/> is the native boundary:
/// it captures the process tree once and turns eligible native candidates into those snapshots.
/// </summary>
internal sealed class TargetResolver
{
    private readonly uint _ownPid = (uint)Environment.ProcessId;

    /// <summary>Reasons no host was found for either provider in the previous native lookup.</summary>
    public IReadOnlyDictionary<ProviderId, string> LastRejections { get; private set; } =
        new Dictionary<ProviderId, string>();

    internal static List<HostWindowInfo> Resolve(
        IReadOnlyList<WindowSnapshot> windows, IReadOnlyList<TargetDefinition> targets,
        ProcessTree tree, Config config, uint ownPid)
    {
        var matches = new List<(WindowSnapshot Window, TargetDefinition Target,
            ProviderId Provider, HostEvidence Evidence)>();

        foreach (var window in windows)
        foreach (var target in targets.Where(target => target.Enabled && target.Matches(window)))
        {
            var evidence = EvidenceFor(target, window, tree, ownPid);
            if (evidence is null) continue;

            foreach (var provider in target.Providers.Select(binding => binding.Provider))
            {
                if (ProviderEnabled(provider, config))
                    matches.Add((window, target, provider, evidence.Value));
            }
        }

        return matches
            .GroupBy(match => (match.Provider, match.Window.Hwnd))
            .Select(group => group
                .OrderByDescending(match => match.Target.Specificity)
                .ThenByDescending(match => match.Target.Revision)
                .First())
            .Select(match => new HostWindowInfo(
                match.Window.Hwnd, KindFor(match.Target.Placement), match.Target.Name,
                match.Provider, match.Evidence, match.Target.StatusBarHeight, match.Target.Id,
                match.Target.Anchor, match.Target.OffsetX, match.Target.OffsetY))
            .ToList();
    }

    /// <summary>
    /// Resolves eligible native windows. Process inspection is deliberately done once for the
    /// entire candidate set so every host decision observes the same running-process snapshot.
    /// </summary>
    public List<HostWindowInfo> FindAll(
        IReadOnlyList<HostCandidate> candidates, IReadOnlyList<TargetDefinition> targets,
        Config config)
    {
        var tree = ProcessTreeCache.Current();
        var windows = candidates
            .Select(candidate => new WindowSnapshot(
                candidate.Hwnd,
                candidate.Pid,
                tree.NameOf(candidate.Pid),
                Native.ClassNameOf(candidate.Hwnd),
                Native.WindowTextOf(candidate.Hwnd),
                DesktopFrame.IsResizableFrame(candidate.Hwnd)))
            .ToList();
        var result = ResolveWithRejections(windows, targets, tree, config, _ownPid);
        LastRejections = result.Rejections;
        return result.Hosts;
    }

    internal static TargetResolution ResolveWithRejections(
        IReadOnlyList<WindowSnapshot> windows, IReadOnlyList<TargetDefinition> targets,
        ProcessTree tree, Config config, uint ownPid)
    {
        var hosts = Resolve(windows, targets, tree, config, ownPid);
        var rejections = Enum.GetValues<ProviderId>()
            .Where(provider => ProviderEnabled(provider, config))
            .Where(provider => targets.Any(target => target.Enabled
                && target.Providers.Any(binding => binding.Provider == provider)))
            .Where(provider => hosts.All(host => host.Provider != provider))
            .ToDictionary(provider => provider, provider => RejectionFor(provider, windows.Count));

        return new TargetResolution(hosts, rejections);
    }

    private static HostEvidence? EvidenceFor(
        TargetDefinition target, WindowSnapshot window, ProcessTree tree, uint ownPid)
    {
        if (target.Activation == TargetActivationPolicy.AlwaysWhileOpen)
            return target.Placement == TargetPlacementKind.Desktop
                ? HostEvidence.Desktop
                : HostEvidence.Open;

        return tree.HasAnyProcessUnder(window.Pid, target.ActivityProcessNames, ownPid)
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

    private static string RejectionFor(ProviderId provider, int candidateCount) =>
        candidateCount == 0
            ? "no eligible windows on screen"
            : $"none of {candidateCount} candidate windows matched an enabled {provider} target";
}
