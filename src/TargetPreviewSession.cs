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
            .Where(provider => provider == ProviderId.Claude
                ? config.EnableClaude
                : config.EnableCodex)
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
    ProviderId? Provider { get; }
    double LogicalWidth { get; }
    event Action? WidthChanged;
    void Update(HostWindowInfo host, UsageModel model, bool dimmed, List<IntPtr> zOrder, Config config);
    void SetSharedPrefix(string? prefix);
    void SetSharedGroup(double groupWidth, double memberOffset);
}

internal interface ITargetPreviewPillFactory
{
    ITargetPreviewPill Create(ProviderId? provider, HostWindowInfo host);
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

internal sealed class TargetPreviewSession : ITargetPreviewSession
{
    private enum PreviewPillKey { Neutral, Claude, Codex }

    private const double SharedGap = 12;
    private WindowSnapshot _window;
    private readonly ITargetPreviewPillFactory _factory;
    private readonly Func<ProviderId, UsageModel> _modelFor;
    private readonly Config _config;
    private readonly Func<ProcessTree> _processTree;
    private readonly Func<List<IntPtr>> _zOrder;
    private readonly uint _ownPid;
    private readonly Dictionary<PreviewPillKey, ITargetPreviewPill> _pills = new();
    private TargetDefinition? _draft;
    private bool _disposed;
    private bool _windowClosed;
    private bool _layingOut;

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
        var desired = Evaluation.PreviewProviders.Count == 0
            ? new[] { PreviewPillKey.Neutral }
            : Evaluation.PreviewProviders.Select(KeyFor).ToArray();

        foreach (var key in _pills.Keys.Where(key => !desired.Contains(key)).ToList())
            Remove(key);

        var zOrder = _zOrder();
        var dimmed = Evaluation.State != TargetPreviewState.Normal;
        foreach (var key in desired)
        {
            var provider = ProviderFor(key);
            var host = HostFor(draft, provider);
            if (!_pills.TryGetValue(key, out var pill))
            {
                pill = _factory.Create(provider, host);
                pill.WidthChanged += Reflow;
                _pills.Add(key, pill);
            }
            pill.Update(host, ModelFor(provider), dimmed, zOrder, _config);
        }

        Reflow();
        foreach (var key in desired)
        {
            var provider = ProviderFor(key);
            _pills[key].Update(HostFor(draft, provider), ModelFor(provider), dimmed, zOrder, _config);
        }
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
        DisposePills();
        Evaluation = new(
            TargetPreviewState.WindowClosed,
            "Preview unavailable—the selected window closed.",
            (IReadOnlyList<ProviderId>?)_draft?.Providers
                .Select(binding => binding.Provider).Distinct().ToList()
                ?? Array.Empty<ProviderId>());
    }

    private UsageModel ModelFor(ProviderId? provider) => provider is { } actual
        ? _modelFor(actual)
        : new UsageModel { Bars = new[] { new UsageBar("Preview", null) }, Stale = true };

    private HostWindowInfo HostFor(TargetDefinition draft, ProviderId? provider) => new(
        _window.Hwnd,
        draft.Placement switch
        {
            TargetPlacementKind.Desktop => HostKind.Desktop,
            TargetPlacementKind.Ide => HostKind.Ide,
            _ => HostKind.Terminal,
        },
        draft.Name,
        provider ?? ProviderId.Claude,
        HostEvidence.Open,
        draft.StatusBarHeight,
        draft.Id,
        draft.Anchor,
        draft.OffsetX,
        draft.OffsetY);

    private void Reflow()
    {
        if (_layingOut) return;
        _layingOut = true;
        try
        {
            if (_pills.TryGetValue(PreviewPillKey.Claude, out var claude)
                && _pills.TryGetValue(PreviewPillKey.Codex, out var codex)
                && _draft is { } draft)
            {
                claude.SetSharedPrefix("C:");
                codex.SetSharedPrefix("X:");
                var layout = SharedPillLayout.Create(
                    HostFor(draft, ProviderId.Claude), claude.LogicalWidth,
                    HostFor(draft, ProviderId.Codex), codex.LogicalWidth,
                    SharedGap);
                if (layout is { } shared)
                {
                    claude.SetSharedGroup(shared.First.GroupWidth, shared.First.MemberOffset);
                    codex.SetSharedGroup(shared.Second.GroupWidth, shared.Second.MemberOffset);
                }
            }
            else
            {
                foreach (var pill in _pills.Values)
                {
                    pill.SetSharedPrefix(null);
                    pill.SetSharedGroup(0, 0);
                }
            }
        }
        finally { _layingOut = false; }
    }

    private static PreviewPillKey KeyFor(ProviderId provider) =>
        provider == ProviderId.Claude ? PreviewPillKey.Claude : PreviewPillKey.Codex;

    private static ProviderId? ProviderFor(PreviewPillKey key) => key switch
    {
        PreviewPillKey.Claude => ProviderId.Claude,
        PreviewPillKey.Codex => ProviderId.Codex,
        _ => null,
    };

    private void Remove(PreviewPillKey key)
    {
        var pill = _pills[key];
        pill.WidthChanged -= Reflow;
        pill.Dispose();
        _pills.Remove(key);
    }

    private void DisposePills()
    {
        foreach (var key in _pills.Keys.ToList()) Remove(key);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposePills();
    }
}

internal sealed class ProviderPreviewPillFactory : ITargetPreviewPillFactory
{
    private readonly Func<bool> _lightTheme;

    public ProviderPreviewPillFactory(Func<bool> lightTheme) => _lightTheme = lightTheme;

    public ITargetPreviewPill Create(ProviderId? provider, HostWindowInfo host) =>
        new ProviderPreviewPill(provider, host, _lightTheme);
}

internal sealed class ProviderPreviewPill : ITargetPreviewPill
{
    private readonly ProviderPill _pill;
    private readonly Func<bool> _lightTheme;

    public ProviderPreviewPill(ProviderId? provider, HostWindowInfo host, Func<bool> lightTheme)
    {
        Provider = provider;
        _lightTheme = lightTheme;
        var target = provider switch
        {
            ProviderId.Codex => ProviderTarget.Codex,
            ProviderId.Claude => ProviderTarget.Claude,
            _ => new ProviderTarget(ProviderId.Claude, "Preview"),
        };
        _pill = new ProviderPill(target, host);
        _pill.WidthChanged += OnWidthChanged;
        _pill.Show();
    }

    public ProviderId? Provider { get; }
    public double LogicalWidth => _pill.LogicalWidth;
    public event Action? WidthChanged;

    public void Update(
        HostWindowInfo host, UsageModel model, bool dimmed, List<IntPtr> zOrder, Config config)
    {
        _pill.SetPreviewDimmed(dimmed);
        _pill.UpdateModel(model, _lightTheme(), config);
        _pill.Update(host, zOrder, config, moveInProgress: false);
    }

    public void SetSharedPrefix(string? prefix) => _pill.SetSharedPrefix(prefix);
    public void SetSharedGroup(double groupWidth, double memberOffset) =>
        _pill.SetSharedGroup(groupWidth, memberOffset);

    private void OnWidthChanged(ProviderPill _) => WidthChanged?.Invoke();

    public void Dispose()
    {
        _pill.WidthChanged -= OnWidthChanged;
        _pill.Dispose();
    }
}
