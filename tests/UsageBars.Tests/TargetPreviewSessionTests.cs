using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace UsageBars.Tests;

public class TargetPreviewSessionTests
{
    private const uint OwnPid = 999;

    [Fact]
    public void DraftWithoutProvidersUsesNeutralPreview()
    {
        var evaluation = TargetPreviewEvaluator.Evaluate(
            NoProviderDraft(), Window(), EmptyTree(), EnabledConfig(), OwnPid);

        Assert.Equal(TargetPreviewState.Neutral, evaluation.State);
        Assert.Empty(evaluation.PreviewProviders);
    }

    [Fact]
    public void DisabledDraftStaysDimmedWithReason()
    {
        var evaluation = TargetPreviewEvaluator.Evaluate(
            ClaudeDraft() with { Enabled = false }, Window(), EmptyTree(), EnabledConfig(), OwnPid);

        Assert.Equal(TargetPreviewState.Dimmed, evaluation.State);
        Assert.Contains("disabled", evaluation.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { ProviderId.Claude }, evaluation.PreviewProviders);
    }

    [Fact]
    public void TitleMismatchStaysDimmedWithReason()
    {
        var evaluation = TargetPreviewEvaluator.Evaluate(
            ClaudeDraft() with { WindowTitle = new(TitleMatchKind.Literal, "Different") },
            Window(), EmptyTree(), EnabledConfig(), OwnPid);

        Assert.Equal(TargetPreviewState.Dimmed, evaluation.State);
        Assert.Contains("title", evaluation.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TargetLevelOrActivityMakesUnifiedPreviewPillNormal()
    {
        var draft = BaseDraft(ProviderBinding.Claude, ProviderBinding.Codex) with
        {
            Activation = TargetActivationPolicy.RequireActivityProcess,
            ActivityProcessNames = new() { "omp", "codex" },
        };
        var tree = ProcessTree.FromMap(new[]
        {
            (7u, 1u, "terminal"),
            (8u, 7u, "omp"),
        });

        var evaluation = TargetPreviewEvaluator.Evaluate(
            draft, Window(), tree, EnabledConfig(), OwnPid);

        Assert.Equal(TargetPreviewState.Normal, evaluation.State);
        Assert.Null(evaluation.Reason);
        Assert.Equal(new[] { ProviderId.Claude, ProviderId.Codex }, evaluation.PreviewProviders);

        var factory = new RecordingPreviewPillFactory();
        using var session = NewSession(factory, processTree: () => tree);
        session.Update(draft);
        var pill = Assert.Single(factory.Live);
        Assert.Equal(new[] { ProviderId.Claude, ProviderId.Codex }, pill.Providers);
        Assert.False(pill.Dimmed);
    }

    [Fact]
    public void InactiveTargetLevelOrActivityKeepsUnifiedPreviewPillDimmed()
    {
        var draft = BaseDraft(ProviderBinding.Claude, ProviderBinding.Codex) with
        {
            Activation = TargetActivationPolicy.RequireActivityProcess,
            ActivityProcessNames = new() { "omp", "codex" },
        };

        var evaluation = TargetPreviewEvaluator.Evaluate(
            draft, Window(), EmptyTree(), EnabledConfig(), OwnPid);

        Assert.Equal(TargetPreviewState.Dimmed, evaluation.State);
        Assert.Equal(
            "None of the selected activity processes (omp OR codex) is running under this window owner.",
            evaluation.Reason);
        Assert.Equal(new[] { ProviderId.Claude, ProviderId.Codex }, evaluation.PreviewProviders);

        var factory = new RecordingPreviewPillFactory();
        using var session = NewSession(factory);
        session.Update(draft);
        var pill = Assert.Single(factory.Live);
        Assert.Equal(new[] { ProviderId.Claude, ProviderId.Codex }, pill.Providers);
        Assert.True(pill.Dimmed);
    }

    [Fact]
    public void GloballyDisabledProviderIsExcludedWithoutDimmingEnabledProvider()
    {
        var config = EnabledConfig();
        config.EnableCodex = false;

        var evaluation = TargetPreviewEvaluator.Evaluate(
            BaseDraft(ProviderBinding.Claude, ProviderBinding.Codex),
            Window(), EmptyTree(), config, OwnPid);

        Assert.Equal(TargetPreviewState.Normal, evaluation.State);
        Assert.Equal(new[] { ProviderId.Claude }, evaluation.PreviewProviders);
    }

    [Fact]
    public void ApplicableDraftPreviewsNormally()
    {
        var evaluation = TargetPreviewEvaluator.Evaluate(
            ClaudeDraft(), Window(), EmptyTree(), EnabledConfig(), OwnPid);

        Assert.Equal(TargetPreviewState.Normal, evaluation.State);
        Assert.Null(evaluation.Reason);
    }

    [Fact]
    public void SessionReusesOneUnifiedPreviewPillAndDisposesExactlyOnce()
    {
        var factory = new RecordingPreviewPillFactory();
        using var session = NewSession(factory);

        session.Update(ClaudeDraft());
        var pill = Assert.Single(factory.Live);
        Assert.Equal(new[] { ProviderId.Claude }, pill.Providers);

        session.Update(CodexDraft());
        Assert.Same(pill, Assert.Single(factory.Live));
        Assert.Equal(new[] { ProviderId.Codex }, pill.Providers);

        session.Dispose();
        session.Dispose();
        Assert.All(factory.Created, p => Assert.Equal(1, p.DisposeCount));
    }

    [Fact]
    public void SessionNeverChangesSuppressedHandle()
    {
        using var session = NewSession(new RecordingPreviewPillFactory(), new IntPtr(77));
        session.Update(ClaudeDraft());

        Assert.True(session.Suppresses(new IntPtr(77)));
        Assert.False(session.Suppresses(new IntPtr(78)));
    }

    [Fact]
    public void MultipleProvidersRenderInsideOneUnifiedPreviewPill()
    {
        var factory = new RecordingPreviewPillFactory();
        using var session = NewSession(factory);

        session.Update(BaseDraft(ProviderBinding.Antigravity, ProviderBinding.Codex, ProviderBinding.Claude));

        var pill = Assert.Single(factory.Live);
        Assert.Equal(new[] { ProviderId.Claude, ProviderId.Codex, ProviderId.Antigravity }, pill.Providers);
    }

    [Fact]
    public void WindowClosedDisposesPillsButRetainsUnavailableState()
    {
        var factory = new RecordingPreviewPillFactory();
        using var session = NewSession(factory);
        session.Update(ClaudeDraft());

        session.WindowClosed();

        Assert.Empty(factory.Live);
        Assert.Equal(TargetPreviewState.WindowClosed, session.Evaluation.State);
        Assert.Contains("closed", session.Evaluation.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefreshUsesLatestWindowTitleForMatcherEvaluation()
    {
        using var session = NewSession(new RecordingPreviewPillFactory());
        var draft = ClaudeDraft() with
        {
            WindowTitle = new(TitleMatchKind.Literal, "Renamed"),
        };
        session.Update(draft);
        Assert.Equal(TargetPreviewState.Dimmed, session.Evaluation.State);

        session.Refresh(Window() with { Title = "Renamed" });

        Assert.Equal(TargetPreviewState.Normal, session.Evaluation.State);
    }

    private static WindowSnapshot Window() =>
        new(new IntPtr(42), 7, "terminal", "frame", "Codex", true);

    private static ProcessTree EmptyTree() =>
        ProcessTree.FromMap(new[] { (7u, 1u, "terminal") });

    private static Config EnabledConfig() => new() { EnableClaude = true, EnableCodex = true, EnableAntigravity = true };

    private static TargetDefinition BaseDraft(params ProviderBinding[] providers) =>
        TargetDefinition.Captured("Draft", "terminal", "frame", providers,
            TargetPlacementKind.Terminal, TargetActivationPolicy.AlwaysWhileOpen, true, 0);

    private static TargetDefinition ClaudeDraft() => BaseDraft(ProviderBinding.Claude);
    private static TargetDefinition CodexDraft() => BaseDraft(ProviderBinding.Codex);
    private static TargetDefinition NoProviderDraft() => BaseDraft();

    private static TargetPreviewSession NewSession(
        RecordingPreviewPillFactory factory,
        IntPtr? hwnd = null,
        Func<ProcessTree>? processTree = null) =>
        new(Window() with { Hwnd = hwnd ?? Window().Hwnd }, factory,
            _ => new UsageModel { Bars = new[] { new UsageBar("test", 50) } },
            EnabledConfig(), processTree ?? EmptyTree,
            () => new List<IntPtr> { hwnd ?? Window().Hwnd }, OwnPid);
}

internal sealed class RecordingPreviewPillFactory : ITargetPreviewPillFactory
{
    public List<RecordingPreviewPill> Created { get; } = new();
    public IEnumerable<RecordingPreviewPill> Live => Created.Where(pill => !pill.IsDisposed);

    public ITargetPreviewPill Create(IReadOnlyList<ProviderId> providers, HostWindowInfo host)
    {
        var pill = new RecordingPreviewPill(providers, host);
        Created.Add(pill);
        return pill;
    }
}

internal sealed class RecordingPreviewPill : ITargetPreviewPill
{
    public RecordingPreviewPill(IReadOnlyList<ProviderId> providers, HostWindowInfo host)
    {
        Providers = providers;
        Host = host;
    }

    public IReadOnlyList<ProviderId> Providers { get; private set; }
    public HostWindowInfo Host { get; private set; }
    public double LogicalWidth { get; } = 100;
    public bool IsDisposed { get; private set; }
    public int DisposeCount { get; private set; }
    public bool Dimmed { get; private set; }
    public event Action? WidthChanged;

    public void Update(
        HostWindowInfo host,
        IReadOnlyList<(ProviderId? Provider, UsageModel Model)> models,
        bool dimmed,
        List<IntPtr> zOrder,
        Config config)
    {
        Host = host;
        Providers = models.Where(m => m.Provider.HasValue).Select(m => m.Provider!.Value).ToArray();
        Dimmed = dimmed;
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        DisposeCount++;
    }

    public void RaiseWidthChanged() => WidthChanged?.Invoke();
}
