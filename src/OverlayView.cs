using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace UsageBars;

/// <summary>
/// One visual bar row inside the unified pill: a provider icon paired with a primary bar (7d or
/// single meter) and an optional bottom-third overlay bar (5h).
/// </summary>
internal sealed record PillBarItem(
    ProviderId? Provider,
    UsageBar MainBar,
    UsageBar? SubBar,
    bool Stale,
    bool IsThirdParty = false);

/// <summary>
/// Pure geometry for the unified stack-then-column pill grid, independent of WPF so layout math
/// can be verified directly in unit tests.
/// </summary>
internal sealed record PillGridMetrics(
    int Columns,
    int RowsPerColumn,
    int ActualRows,
    double Width,
    double Height,
    IReadOnlyList<double> ColumnWidths);

internal static class PillLayoutMath
{
    public const double PadX = 8.0;
    public const double PadY = 3.0;
    public const double RowHeight = 9.0;
    public const double RowGap = 2.0;
    public const double IconSize = 9.5;
    public const double IconGap = 5.0;
    public const double BarWidth = 58.0;
    public const double ColumnGap = 10.0;
    public const double TrailingTextGap = 4.0;
    public const double ActionIconSize = 14.0;
    public const double ActionGap = 6.0;
    public const double DefaultMaxHeight = 39.0;

    /// <summary>
    /// Computes how many rows stack vertically before spilling into additional columns, staying
    /// within <paramref name="maxHeight"/> (clamped to fit at least one row).
    /// </summary>
    public static PillGridMetrics Compute(
        IReadOnlyList<double> itemTrailingWidths,
        double maxHeight,
        bool hasActionIcon)
    {
        var count = Math.Max(1, itemTrailingWidths.Count);
        var usableHeight = Math.Max(RowHeight, maxHeight - 2 * PadY);
        var maxRows = Math.Max(1, (int)Math.Floor((usableHeight + RowGap + 1e-6) / (RowHeight + RowGap)));
        var rowsPerCol = Math.Min(count, maxRows);
        var cols = (int)Math.Ceiling((double)count / rowsPerCol);

        var colWidths = new double[cols];
        var actualRows = 0;
        for (var c = 0; c < cols; c++)
        {
            var start = c * rowsPerCol;
            var end = Math.Min(count, start + rowsPerCol);
            actualRows = Math.Max(actualRows, end - start);

            var maxExtra = 0.0;
            for (var i = start; i < end && i < itemTrailingWidths.Count; i++)
            {
                if (itemTrailingWidths[i] > maxExtra) maxExtra = itemTrailingWidths[i];
            }

            colWidths[c] = IconSize + IconGap + BarWidth + (maxExtra > 0 ? TrailingTextGap + maxExtra : 0);
        }

        var width = PadX * 2 + colWidths.Sum() + ColumnGap * Math.Max(0, cols - 1);
        if (hasActionIcon) width += ActionGap + ActionIconSize;
        var height = PadY * 2 + actualRows * RowHeight + Math.Max(0, actualRows - 1) * RowGap;
        return new PillGridMetrics(cols, rowsPerCol, actualRows, width, height, colWidths);
    }
}

/// <summary>
/// Converts one or more provider models into compact layered rows for the unified pill:
/// providers with both a 7d and 5h window collapse into a single layered bar (7d top 2/3, 5h bottom
/// 1/3), while single-meter or extra pools stay individual rows.
/// </summary>
internal static class PillItemBuilder
{
    public static List<PillBarItem> Build(IReadOnlyList<(ProviderId? Provider, UsageModel Model)> providers)
    {
        var items = new List<PillBarItem>();

        foreach (var (provider, model) in providers)
        {
            var visible = model.Bars.Where(b => b.ShowInPill).ToList();
            if (visible.Count == 0)
            {
                items.Add(new PillBarItem(provider, new UsageBar("", null), null, Stale: true));
                continue;
            }

            UsageBar? bar7d = null;
            UsageBar? bar5h = null;
            UsageBar? bar3p7d = null;
            UsageBar? bar3p5h = null;
            var others = new List<UsageBar>();

            foreach (var bar in visible)
            {
                if (bar.Label.Equals("7d", StringComparison.OrdinalIgnoreCase) && bar7d is null)
                    bar7d = bar;
                else if (bar.Label.Equals("5h", StringComparison.OrdinalIgnoreCase) && bar5h is null)
                    bar5h = bar;
                else if (bar.Label.Equals("3p 7d", StringComparison.OrdinalIgnoreCase) && bar3p7d is null)
                    bar3p7d = bar;
                else if (bar.Label.Equals("3p 5h", StringComparison.OrdinalIgnoreCase) && bar3p5h is null)
                    bar3p5h = bar;
                else
                    others.Add(bar);
            }

            if (bar7d is not null || bar5h is not null)
            {
                // 7d is the main layer (top 2/3), 5h overlays the bottom 1/3.
                var main = bar7d ?? bar5h!;
                var sub = bar7d is not null ? bar5h : null;
                items.Add(new PillBarItem(provider, main, sub, model.Stale));
            }

            foreach (var bar in others)
                items.Add(new PillBarItem(provider, bar, null, model.Stale));

            if (bar3p7d is not null || bar3p5h is not null)
            {
                var main = bar3p7d ?? bar3p5h!;
                var sub = bar3p7d is not null ? bar3p5h : null;
                items.Add(new PillBarItem(provider, main, sub, model.Stale, IsThirdParty: true));
            }
        }

        if (items.Count == 0)
            items.Add(new PillBarItem(null, new UsageBar("", null), null, Stale: true));

        return items;
    }
}

/// <summary>
/// Unified Style A pill: stacks compact provider rows (icon + layered 7d/5h split bar + cased
/// vertical crossbar reset pointers, no % labels) vertically up to the host strip height, then
/// spills into additional columns.
/// </summary>
internal sealed class OverlayView : FrameworkElement
{
    private const double FontSize = 9.5;

    private static readonly Typeface Face = new(
        new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private IReadOnlyList<(ProviderId? Provider, UsageModel Model)> _providers =
        Array.Empty<(ProviderId?, UsageModel)>();
    private List<PillBarItem> _items = new();
    private PillGridMetrics _metrics = PillLayoutMath.Compute(new[] { 0.0 }, PillLayoutMath.DefaultMaxHeight, false);
    private double _maxHeight = PillLayoutMath.DefaultMaxHeight;
    private bool _light;
    private bool? _dimmedOverride;

    /// <summary>Bounds of the clickable icon in view-local DIPs, or null when hidden.</summary>
    public Rect? ActionIconBounds { get; private set; }

    /// <summary>Install documentation URL for the clickable action icon, if any.</summary>
    public string? ActionUrl { get; private set; }

    /// <summary>Width the pill needs for the current content, in DIPs.</summary>
    public double LogicalWidth { get; private set; } = 88;

    /// <summary>Height the pill needs for the current content, in DIPs.</summary>
    public double LogicalHeight { get; private set; } = 15;

    /// <summary>Raised when <see cref="LogicalWidth"/> or <see cref="LogicalHeight"/> changes.</summary>
    public event Action? WidthChanged;

    /// <summary>No-op retained for legacy callers; provider identity is drawn via icons.</summary>
    public void SetPrefix(string? prefix) { }

    /// <summary>
    /// Updates the maximum height available in the host's title bar or status bar, causing the
    /// pill to reflow between stacked rows and side-by-side columns automatically.
    /// </summary>
    public void SetMaxHeight(double maxHeightLogical)
    {
        var clamped = Math.Max(PillLayoutMath.RowHeight + 2 * PillLayoutMath.PadY, maxHeightLogical);
        if (Math.Abs(clamped - _maxHeight) < 0.5) return;

        _maxHeight = clamped;
        RecalculateLayout();
        InvalidateVisual();
    }

    public void Update(UsageModel model, bool lightTheme, bool? dimmedOverride = null, ProviderId? provider = null) =>
        Update(new (ProviderId?, UsageModel)[] { (provider, model) }, lightTheme, dimmedOverride);

    public void Update(
        IReadOnlyList<(ProviderId? Provider, UsageModel Model)> providers,
        bool lightTheme,
        bool? dimmedOverride = null)
    {
        _providers = providers;
        _light = lightTheme;
        _dimmedOverride = dimmedOverride;
        _items = PillItemBuilder.Build(providers);

        ActionUrl = providers
            .Select(p => p.Model)
            .FirstOrDefault(m => m.Action != PillAction.None && !string.IsNullOrWhiteSpace(m.ActionUrl))
            ?.ActionUrl;

        var allStaleOrEmpty = providers.Count == 0
            || providers.All(p => p.Model.Stale || !p.Model.HasValues);
        Opacity = dimmedOverride ?? allStaleOrEmpty ? 0.6 : 1.0;

        RecalculateLayout();
        InvalidateVisual();
    }

    /// <summary>
    /// Combined hover tooltip across all providers in this pill, including exact remaining
    /// percentages (since the pill omits % text), reset times, and reading ages.
    /// </summary>
    public string? ResetTooltip(DateTimeOffset now)
    {
        if (_providers.Count == 0) return null;
        if (_providers.Count == 1 && _providers[0].Provider is null)
            return _providers[0].Model.ResetTooltip(now);

        var sections = new List<string>();
        foreach (var (provider, model) in _providers)
        {
            var header = provider is { } id ? ProviderTarget.For(id).Name : "Preview";
            var lines = new List<string>();
            var visible = model.Bars.Where(b => b.ShowInPill).ToList();

            foreach (var bar in visible)
            {
                if (bar.Spend is { } spend)
                {
                    lines.Add($"  {bar.Label}: {UsageModel.Money(spend.RemainingUsd)} of {UsageModel.Money(spend.LimitUsd)} left");
                }
                else if (bar.RemainingPct is { } rem)
                {
                    var resetPart = !string.IsNullOrWhiteSpace(bar.ReportedResetText)
                        ? $" · {bar.ReportedResetText}"
                        : bar.ResetsAt is { } reset
                            ? $" · resets {ResetEstimator.Describe(reset, now)}"
                            : "";
                    lines.Add($"  {bar.Label}: {Math.Round(rem):0}% left{resetPart}");
                }
                else
                {
                    lines.Add($"  {bar.Label}: --");
                }

                if (bar.Spend is not null)
                {
                    if (bar.ResetsAt is { } reset)
                        lines.Add($"  {bar.Label} resets {ResetEstimator.Describe(reset, now)}");
                    lines.Add($"  {bar.Label} {UsageModel.DepletionText(bar, now)}");
                }
            }

            if (model.ReadingAt is { } readingAt)
            {
                var age = now - readingAt;
                if (age < TimeSpan.Zero) age = TimeSpan.Zero;
                var ageText = age.TotalDays >= 1 ? $"{(int)age.TotalDays}d {age.Hours}h"
                    : age.TotalHours >= 1 ? $"{(int)age.TotalHours}h {age.Minutes}m"
                    : age.TotalMinutes >= 1 ? $"{(int)age.TotalMinutes}m {age.Seconds}s"
                    : $"{age.Seconds}s";
                lines.Add($"  Age: {ageText}{(model.Stale ? " (stale)" : "")}");
            }
            else if (!string.IsNullOrWhiteSpace(model.StatusText))
            {
                lines.Add($"  {model.StatusText}");
            }

            if (lines.Count > 0)
                sections.Add(header + Environment.NewLine + string.Join(Environment.NewLine, lines));
        }

        return sections.Count == 0 ? null : string.Join(Environment.NewLine + Environment.NewLine, sections);
    }

    private void RecalculateLayout()
    {
        var dpi = DpiScale();
        var trailingWidths = new double[_items.Count];
        for (var i = 0; i < _items.Count; i++)
        {
            var trailing = TrailingText(_items[i]);
            trailingWidths[i] = trailing is null ? 0 : Text(trailing, Value, dpi).Width;
        }

        var hasAction = _providers.Any(p => p.Model.Action != PillAction.None);
        _metrics = PillLayoutMath.Compute(trailingWidths, _maxHeight, hasAction);

        var changed = Math.Abs(_metrics.Width - LogicalWidth) > 0.5
            || Math.Abs(_metrics.Height - LogicalHeight) > 0.5;
        LogicalWidth = _metrics.Width;
        LogicalHeight = _metrics.Height;
        if (changed) WidthChanged?.Invoke();
    }

    private double DpiScale()
    {
        try { return VisualTreeHelper.GetDpi(this).PixelsPerDip; }
        catch (Exception) { return 1.0; }
    }

    protected override Size MeasureOverride(Size availableSize) => new(LogicalWidth, LogicalHeight);

    protected override void OnRender(DrawingContext dc)
    {
        var width = RenderSize.Width <= 0 ? LogicalWidth : RenderSize.Width;
        var height = RenderSize.Height <= 0 ? LogicalHeight : RenderSize.Height;
        var dpi = DpiScale();
        var now = DateTimeOffset.UtcNow;

        var radius = Math.Min(height / 2.0, 11.0);
        dc.DrawRoundedRectangle(Pill, null, new Rect(0, 0, width, height), radius, radius);

        var curX = PillLayoutMath.PadX;
        for (var c = 0; c < _metrics.Columns; c++)
        {
            var start = c * _metrics.RowsPerColumn;
            var end = Math.Min(_items.Count, start + _metrics.RowsPerColumn);
            var colCount = end - start;
            if (colCount <= 0) break;

            var colH = colCount * PillLayoutMath.RowHeight
                + Math.Max(0, colCount - 1) * PillLayoutMath.RowGap;
            var startY = (height - colH) / 2.0;

            for (var r = 0; r < colCount; r++)
            {
                var item = _items[start + r];
                var ry = startY + r * (PillLayoutMath.RowHeight + PillLayoutMath.RowGap);
                var cy = ry + PillLayoutMath.RowHeight / 2.0;

                DrawProviderIcon(dc, item.Provider, item.IsThirdParty, item.Stale,
                    curX + PillLayoutMath.IconSize / 2.0, cy, PillLayoutMath.IconSize);

                var bx = curX + PillLayoutMath.IconSize + PillLayoutMath.IconGap;
                DrawStyleABar(dc, bx, ry, PillLayoutMath.BarWidth, PillLayoutMath.RowHeight, item, now);

                if (TrailingText(item) is { } trailing)
                {
                    var text = Text(trailing, Value, dpi);
                    dc.DrawText(text, new Point(bx + PillLayoutMath.BarWidth + PillLayoutMath.TrailingTextGap,
                        cy - text.Height / 2.0));
                }
            }

            curX += _metrics.ColumnWidths[c] + PillLayoutMath.ColumnGap;
        }

        var hasAction = _providers.Any(p => p.Model.Action != PillAction.None);
        if (hasAction)
        {
            var iconX = width - PillLayoutMath.PadX - PillLayoutMath.ActionIconSize;
            var rect = new Rect(iconX, (height - PillLayoutMath.ActionIconSize) / 2.0,
                PillLayoutMath.ActionIconSize, PillLayoutMath.ActionIconSize);
            DrawActionIcon(dc, rect, dpi);
            ActionIconBounds = rect;
        }
        else
        {
            ActionIconBounds = null;
        }
    }

    /// <summary>
    /// Style A · Split-Bar Clean:
    /// - Two-layered (7d + 5h): single rounded track split top 2/3 (7d) + 1px seam + bottom 1/3 (5h),
    ///   each layer independently pace-colored with its own cased vertical crossbar reset pointer.
    /// - Single bar (7d-only, dollar credit, etc.): full-height rounded bar with full-height cased
    ///   crossbar reset pointer.
    /// </summary>
    private void DrawStyleABar(
        DrawingContext dc, double x, double y, double w, double h, PillBarItem item, DateTimeOffset now)
    {
        var rad = h / 2.0;
        var track = new Rect(x, y, w, h);
        dc.DrawRoundedRectangle(Track, null, track, rad, rad);

        var main = item.MainBar;
        var sub = item.SubBar;

        if (sub is not null)
        {
            const double seam = 1.0;
            var topH = Math.Round((h - seam) * 0.66 * 2.0) / 2.0; // 5.5px for h=9
            var botY = y + topH + seam;
            var botH = h - topH - seam;                           // 2.5px for h=9

            var mainPace = ResetPace.For(main, now);
            DrawFill(dc, x, y, w, topH, main.RemainingPct, mainPace, item.Stale);

            // Subtle hairline seam separating 7d and 5h layers.
            dc.DrawRectangle(Seam, null, new Rect(x + rad * 0.3, y + topH, w - rad * 0.6, seam));

            var subPace = ResetPace.For(sub, now);
            DrawFill(dc, x, botY, w, botH, sub.RemainingPct, subPace, item.Stale);

            if (mainPace is { } mp)
                DrawCrossbar(dc, x + w * mp.TimeRemainingPct / 100.0, y - 0.5, y + topH, 1.5);
            if (subPace is { } sp)
                DrawCrossbar(dc, x + w * sp.TimeRemainingPct / 100.0, botY, y + h + 0.5, 1.35);
        }
        else
        {
            var mainPace = ResetPace.For(main, now);
            DrawFill(dc, x, y, w, h, main.RemainingPct, mainPace, item.Stale);

            if (mainPace is { } mp)
                DrawCrossbar(dc, x + w * mp.TimeRemainingPct / 100.0, y - 0.5, y + h + 0.5, 1.5);
        }
    }

    private void DrawFill(
        DrawingContext dc, double x, double y, double w, double h,
        double? remainingPct, ResetPace? pace, bool stale)
    {
        if (remainingPct is not { } remaining || !double.IsFinite(remaining)) return;
        var fraction = Math.Clamp(remaining / 100.0, 0.0, 1.0);
        if (fraction <= 0) return;

        var fillWidth = w * fraction;
        var brush = pace is { } known ? Frozen(known.FillColor) : NeutralFill;
        if (stale && _dimmedOverride is null)
        {
            var c = ((SolidColorBrush)brush).Color;
            brush = Frozen(Color.FromArgb(165, c.R, c.G, c.B));
        }
        dc.DrawRoundedRectangle(brush, null, new Rect(x, y, fillWidth, h), h / 2.0, h / 2.0);
    }

    /// <summary>High-contrast cased vertical bar crossing the usage bar at the reset fraction.</summary>
    private void DrawCrossbar(DrawingContext dc, double cx, double y1, double y2, double innerWidth)
    {
        const double casing = 0.8;
        var h = Math.Max(1.0, y2 - y1);
        var half = innerWidth / 2.0;

        dc.DrawRoundedRectangle(PointerCasing, null,
            new Rect(cx - half - casing, y1 - 0.25, innerWidth + casing * 2, h + 0.5), 0.6, 0.6);
        dc.DrawRoundedRectangle(PointerCore, null,
            new Rect(cx - half, y1, innerWidth, h), 0.4, 0.4);
    }

    private void DrawProviderIcon(
        DrawingContext dc, ProviderId? provider, bool isThirdParty, bool stale,
        double cx, double cy, double size)
    {
        var baseColor = provider switch
        {
            ProviderId.Claude => _light ? Color.FromRgb(198, 92, 58) : Color.FromRgb(224, 134, 102),
            ProviderId.Codex => _light ? Color.FromRgb(16, 142, 106) : Color.FromRgb(122, 212, 182),
            ProviderId.Antigravity when isThirdParty => _light ? Color.FromRgb(118, 86, 204) : Color.FromRgb(172, 152, 245),
            ProviderId.Antigravity => _light ? Color.FromRgb(48, 108, 218) : Color.FromRgb(126, 176, 255),
            _ => _light ? Color.FromRgb(110, 110, 106) : Color.FromRgb(180, 180, 175),
        };
        if (stale && _dimmedOverride is null)
            baseColor = Color.FromArgb(155, baseColor.R, baseColor.G, baseColor.B);

        var brush = Frozen(baseColor);
        var r = size * 0.46;

        if (provider == ProviderId.Claude)
        {
            // 8-ray Claude starburst
            var pen = FrozenPen(brush, size * 0.14);
            for (var i = 0; i < 8; i++)
            {
                var angle = i * Math.PI / 4.0;
                var inner = r * (i % 2 == 0 ? 0.25 : 0.32);
                var outer = r * (i % 2 == 0 ? 1.0 : 0.80);
                dc.DrawLine(pen,
                    new Point(cx + Math.Cos(angle) * inner, cy + Math.Sin(angle) * inner),
                    new Point(cx + Math.Cos(angle) * outer, cy + Math.Sin(angle) * outer));
            }
            dc.DrawEllipse(brush, null, new Point(cx, cy), r * 0.28, r * 0.28);
        }
        else if (provider == ProviderId.Codex)
        {
            // OpenAI / Codex 6-petal hexagonal rosette
            var pen = FrozenPen(brush, size * 0.115);
            var ringR = r * 0.54;
            var dotR = r * 0.36;
            for (var i = 0; i < 6; i++)
            {
                var angle = -Math.PI / 2.0 + i * Math.PI / 3.0;
                dc.DrawEllipse(null, pen,
                    new Point(cx + Math.Cos(angle) * ringR, cy + Math.Sin(angle) * ringR),
                    dotR, dotR);
            }
        }
        else if (provider == ProviderId.Antigravity)
        {
            // Antigravity lifted A-arch / delta glyph
            var pen = FrozenPen(brush, size * 0.17);
            var arch = new StreamGeometry();
            using (var ctx = arch.Open())
            {
                ctx.BeginFigure(new Point(cx - r * 0.82, cy + r * 0.76), isFilled: false, isClosed: false);
                ctx.LineTo(new Point(cx, cy - r * 0.84), isStroked: true, isSmoothJoin: true);
                ctx.LineTo(new Point(cx + r * 0.82, cy + r * 0.76), isStroked: true, isSmoothJoin: true);
            }
            arch.Freeze();
            dc.DrawGeometry(null, pen, arch);

            var crossbarPen = FrozenPen(brush, size * 0.13);
            var bar = new StreamGeometry();
            using (var ctx = bar.Open())
            {
                ctx.BeginFigure(new Point(cx - r * 0.48, cy + r * 0.30), isFilled: false, isClosed: false);
                ctx.QuadraticBezierTo(
                    new Point(cx, cy - r * 0.04),
                    new Point(cx + r * 0.48, cy + r * 0.30),
                    isStroked: true, isSmoothJoin: true);
            }
            bar.Freeze();
            dc.DrawGeometry(null, crossbarPen, bar);

            if (isThirdParty)
                dc.DrawEllipse(brush, null, new Point(cx, cy + r * 0.58), r * 0.20, r * 0.20);
        }
        else
        {
            // Neutral preview diamond
            var pen = FrozenPen(brush, size * 0.14);
            var diamond = new StreamGeometry();
            using (var ctx = diamond.Open())
            {
                ctx.BeginFigure(new Point(cx, cy - r * 0.75), isFilled: false, isClosed: true);
                ctx.LineTo(new Point(cx + r * 0.75, cy), isStroked: true, isSmoothJoin: false);
                ctx.LineTo(new Point(cx, cy + r * 0.75), isStroked: true, isSmoothJoin: false);
                ctx.LineTo(new Point(cx - r * 0.75, cy), isStroked: true, isSmoothJoin: false);
            }
            diamond.Freeze();
            dc.DrawGeometry(null, pen, diamond);
        }
    }

    private void DrawActionIcon(DrawingContext dc, Rect rect, double dpi)
    {
        var centre = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        dc.DrawEllipse(Amber, null, centre, rect.Width / 2, rect.Height / 2);

        var text = new FormattedText("!", CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Face, FontSize + 1.5, IconGlyph, dpi);
        dc.DrawText(text, new Point(centre.X - text.Width / 2, centre.Y - text.Height / 2));
    }

    /// <summary>Compact trailing label: only dollar-credit bars show text ($XX); % labels are omitted.</summary>
    private static string? TrailingText(PillBarItem item) =>
        item.MainBar.RemainingUsd is { } usd ? UsageModel.Money(usd) : null;

    /// <summary>Dollars left on a dollar-based bar, otherwise the remaining percent.</summary>
    internal static string PercentText(UsageBar bar) =>
        bar.RemainingUsd is { } usd
            ? UsageModel.Money(usd)
            : bar.RemainingPct is { } r
                ? Math.Round(r).ToString("0", CultureInfo.InvariantCulture) + "%"
                : "--";

    private static FormattedText Text(string text, Brush brush, double dpi) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Face, FontSize, brush, dpi);

    private Brush Pill => _light ? LightPill : DarkPill;
    private Brush Track => _light ? LightTrack : DarkTrack;
    private Brush Value => _light ? LightValue : DarkValue;
    private Brush IconGlyph => _light ? LightPillSolid : DarkPillSolid;
    private Brush NeutralFill => _light ? LightNeutralFill : DarkNeutralFill;
    private Brush Seam => _light ? LightSeam : DarkSeam;
    private Brush PointerCore => _light ? LightPointerCore : DarkPointerCore;
    private Brush PointerCasing => _light ? LightPointerCasing : DarkPointerCasing;

    private static readonly Brush DarkPill = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
    private static readonly Brush DarkTrack = Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
    private static readonly Brush DarkValue = Frozen(Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF));
    private static readonly Brush DarkPillSolid = Frozen(Color.FromRgb(0x26, 0x26, 0x24));
    private static readonly Brush DarkSeam = Frozen(Color.FromArgb(0xDC, 0x18, 0x18, 0x18));
    private static readonly Brush DarkPointerCore = Frozen(Color.FromArgb(0xF5, 0xF8, 0xF8, 0xF8));
    private static readonly Brush DarkPointerCasing = Frozen(Color.FromArgb(0xC8, 0x12, 0x12, 0x12));

    private static readonly Brush LightPill = Frozen(Color.FromArgb(0x16, 0x00, 0x00, 0x00));
    private static readonly Brush LightTrack = Frozen(Color.FromArgb(0x24, 0x00, 0x00, 0x00));
    private static readonly Brush LightValue = Frozen(Color.FromArgb(0xE0, 0x1A, 0x1A, 0x18));
    private static readonly Brush LightPillSolid = Frozen(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly Brush LightSeam = Frozen(Color.FromArgb(0xE8, 0xE8, 0xEA, 0xED));
    private static readonly Brush LightPointerCore = Frozen(Color.FromArgb(0xF5, 0x1A, 0x1A, 0x18));
    private static readonly Brush LightPointerCasing = Frozen(Color.FromArgb(0xDC, 0xFF, 0xFF, 0xFF));

    private static readonly Brush LightNeutralFill = Frozen(Color.FromRgb(0x77, 0x77, 0x73));
    private static readonly Brush DarkNeutralFill = Frozen(Color.FromRgb(0xB0, 0xB0, 0xAA));
    private static readonly Brush Amber = Frozen(Color.FromRgb(0xE3, 0xA0, 0x08));

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        return pen;
    }
}
