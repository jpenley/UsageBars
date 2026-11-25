using System;
using System.Collections.Generic;
using System.Linq;

namespace UsageBars;

internal enum TargetPreviewState
{
    Normal,
    Dimmed,
    Neutral,
    WindowClosed,
    Failed,
}

internal sealed record TargetPreviewEvaluation(
    TargetPreviewState State,
    string? Reason,
    IReadOnlyList<ProviderId> PreviewProviders);

internal static class TargetPreviewEvaluator
{
    public static TargetPreviewEvaluation Evaluate(
        TargetDefinition draft,
        WindowSnapshot window,
        ProcessTree tree,
        Config config,
        uint ownPid)
    {
        var providers = draft.Providers
            .Select(binding => binding.Provider)
            .Distinct()
            .Where(provider => TargetResolver.ProviderEnabled(provider, config))
            .OrderBy(provider => provider)
            .ToList();
        if (providers.Count == 0) return new(TargetPreviewState.Neutral, null, providers);
        if (!draft.Enabled)
            return new(TargetPreviewState.Dimmed, "The draft target is disabled.", providers);
        if (draft.ProcessName is { } process
            && !process.Equals(window.ProcessName, StringComparison.OrdinalIgnoreCase))
            return new(TargetPreviewState.Dimmed, "The application process does not match this window.", providers);
        if (draft.WindowClass is { } windowClass
            && !windowClass.Equals(window.ClassName, StringComparison.OrdinalIgnoreCase))
            return new(TargetPreviewState.Dimmed, "The window class does not match this window.", providers);
        if (draft.WindowTitle is { } title && !title.Matches(window.Title))
            return new(TargetPreviewState.Dimmed, "The title matcher does not match the current title.", providers);
        if (draft.RequireResizableFrame && !window.IsResizable)
            return new(TargetPreviewState.Dimmed, "This window does not have the required resizable frame.", providers);

        if (draft.Activation == TargetActivationPolicy.RequireActivityProcess
            && !tree.HasAnyProcessUnder(window.Pid, draft.ActivityProcessNames, ownPid))
        {
            var names = string.Join(" OR ", draft.ActivityProcessNames);
            return new(TargetPreviewState.Dimmed,
                $"None of the selected activity processes ({names}) is running under this window owner.",
                providers);
        }

        return new(TargetPreviewState.Normal, null, providers);
    }
}

internal interface ITargetPreviewPill : IDisposable
{
    IReadOnlyList<ProviderId> Providers { get; }
    double LogicalWidth { get; }
    event Action? WidthChanged;
    void Update(
        HostWindowInfo host,
        IReadOnlyList<(ProviderId? Provider, UsageModel Model)> models,
        bool dimmed,
        List<IntPtr> zOrder,
        Config config);
}

internal interface ITargetPreviewPillFactory
{
    ITargetPreviewPill Create(IReadOnlyList<ProviderId> providers, HostWindowInfo host);
}

internal interface ITargetPreviewSession : IDisposable
{
    IntPtr Hwnd { get; }
    TargetPreviewEvaluation Evaluation { get; }
    bool Suppresses(IntPtr hwnd);
    void Update(TargetDefinition draft);
    void Update(WindowSnapshot latestWindow, TargetDefinition draft);
    void Refresh(WindowSnapshot? latestWindow = null);
    void WindowClosed();
}

/// <summary>
/// Previews a target draft on a single window using the exact same unified multi-provider pill
/// drawn by the live overlay, so editing placement, offsets, and providers reflects the real size
/// and stack-then-column layout.
/// </summary>
internal sealed class TargetPreviewSession : ITargetPreviewSession
{
    private WindowSnapshot _window;
    private readonly ITargetPreviewPillFactory _factory;
    private readonly Func<ProviderId, UsageModel> _modelFor;
    private readonly Config _config;
    private readonly Func<ProcessTree> _processTree;
    private readonly Func<List<IntPtr>> _zOrder;
    private readonly uint _ownPid;
    private ITargetPreviewPill? _pill;
    private TargetDefinition? _draft;
    private bool _disposed;
    private bool _windowClosed;
    private bool _updating;

    public TargetPreviewSession(
        WindowSnapshot window,
        ITargetPreviewPillFactory factory,
        Func<ProviderId, UsageModel> modelFor,
        Config config,
        Func<ProcessTree> processTree,
        Func<List<IntPtr>> zOrder,
        uint ownPid)
    {
        _window = window;
        _factory = factory;
        _modelFor = modelFor;
        _config = config;
        _processTree = processTree;
        _zOrder = zOrder;
        _ownPid = ownPid;
        Evaluation = new(TargetPreviewState.Neutral, null, Array.Empty<ProviderId>());
    }

    public IntPtr Hwnd => _window.Hwnd;
    public TargetPreviewEvaluation Evaluation { get; private set; }
    public bool Suppresses(IntPtr hwnd) => !_disposed && hwnd == Hwnd;

    public void Update(TargetDefinition draft)
    {
        if (_disposed || _windowClosed) return;
        _draft = draft;
        Evaluation = TargetPreviewEvaluator.Evaluate(
            draft, _window, _processTree(), _config, _ownPid);

        var providers = Evaluation.PreviewProviders;
        var models = providers.Count == 0
            ? new (ProviderId?, UsageModel)[] { (null, NeutralModel()) }
            : providers.Select(provider => ((ProviderId?)provider, _modelFor(provider))).ToArray();

        var host = HostFor(draft, providers.Count > 0 ? providers[0] : ProviderId.Claude);
        var dimmed = Evaluation.State != TargetPreviewState.Normal;
        var zOrder = _zOrder();

        if (_pill is null)
        {
            _pill = _factory.Create(providers, host);
            _pill.WidthChanged += OnPillWidthChanged;
        }

        _updating = true;
        try { _pill.Update(host, models, dimmed, zOrder, _config); }
        finally { _updating = false; }
    }

    public void Update(WindowSnapshot latestWindow, TargetDefinition draft)
    {
        if (_disposed || _windowClosed) return;
        _window = latestWindow;
        Update(draft);
    }

    public void Refresh(WindowSnapshot? latestWindow = null)
    {
        if (latestWindow is not null) _window = latestWindow;
        if (_draft is { } draft) Update(draft);
    }

    public void WindowClosed()
    {
        if (_disposed || _windowClosed) return;
        _windowClosed = true;
        DisposePill();
        Evaluation = new(
            TargetPreviewState.WindowClosed,
            "Preview unavailable—the selected window closed.",
            (IReadOnlyList<ProviderId>?)_draft?.Providers
                .Select(binding => binding.Provider).Distinct().ToList()
                ?? Array.Empty<ProviderId>());
    }

    private void OnPillWidthChanged()
    {
        if (_updating || _disposed || _windowClosed || _draft is null) return;
        Update(_draft);
    }

    private static UsageModel NeutralModel() =>
        new() { Bars = new[] { new UsageBar("Preview", null) }, Stale = true };

    private HostWindowInfo HostFor(TargetDefinition draft, ProviderId primaryProvider) => new(
        _window.Hwnd,
        draft.Placement switch
        {
            TargetPlacementKind.Desktop => HostKind.Desktop,
            TargetPlacementKind.Ide => HostKind.Ide,
            _ => HostKind.Terminal,
        },
        draft.Name,
        primaryProvider,
        HostEvidence.Open,
        draft.StatusBarHeight,
        draft.Id,
        draft.Anchor,
        draft.OffsetX,
        draft.OffsetY);

    private void DisposePill()
    {
        if (_pill is null) return;
        _pill.WidthChanged -= OnPillWidthChanged;
        _pill.Dispose();
        _pill = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposePill();
    }
}

internal sealed class ProviderPreviewPillFactory : ITargetPreviewPillFactory
{
    private readonly Func<bool> _lightTheme;

    public ProviderPreviewPillFactory(Func<bool> lightTheme) => _lightTheme = lightTheme;

    public ITargetPreviewPill Create(IReadOnlyList<ProviderId> providers, HostWindowInfo host) =>
        new ProviderPreviewPill(providers, host, _lightTheme);
}

internal sealed class ProviderPreviewPill : ITargetPreviewPill
{
    private readonly ProviderPill _pill;
    private readonly Func<bool> _lightTheme;

    public ProviderPreviewPill(
        IReadOnlyList<ProviderId> providers, HostWindowInfo host, Func<bool> lightTheme)
    {
        _lightTheme = lightTheme;
        var target = providers.Count == 1
            ? ProviderTarget.For(providers[0])
            : providers.Count > 1
                ? new ProviderTarget(providers[0], string.Join("+", providers))
                : new ProviderTarget(ProviderId.Claude, "Preview");
        _pill = new ProviderPill(target, host, providers);
        _pill.WidthChanged += OnWidthChanged;
        _pill.Show();
    }

    public IReadOnlyList<ProviderId> Providers => _pill.Providers;
    public double LogicalWidth => _pill.LogicalWidth;
    public event Action? WidthChanged;

    public void Update(
        HostWindowInfo host,
        IReadOnlyList<(ProviderId? Provider, UsageModel Model)> models,
        bool dimmed,
        List<IntPtr> zOrder,
        Config config)
    {
        _pill.SetPreviewDimmed(dimmed);
        _pill.UpdateModels(models, _lightTheme(), config);
        _pill.Update(host, zOrder, config, moveInProgress: false);
    }

    private void OnWidthChanged(ProviderPill _) => WidthChanged?.Invoke();

    public void Dispose()
    {
        _pill.WidthChanged -= OnWidthChanged;
        _pill.Dispose();
    }
}
