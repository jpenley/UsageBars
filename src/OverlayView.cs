using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace UsageBars;

/// <summary>
/// The pill itself: one bar per rate-limit window (or a single bar for the scraped credit
/// meter), plus an optional clickable action icon. Drawn straight to a DrawingContext so the
/// whole overlay is one small visual with no XAML and no templates.
/// </summary>
internal sealed class OverlayView : FrameworkElement
{
    private const double PadX = 11;
    private const double BarWidth = 54;
    private const double BarHeight = 6;
    private const double Gap = 7;
    private const double GroupGap = 13;
    private const double FontSize = 11;
    private const double IconSize = 15;

    public const double LogicalHeight = 24;

    private static readonly Typeface Face = new(
        new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private UsageModel _model = new();
    private bool _light;
    private string? _prefix;

    /// <summary>
    /// Provider marker drawn at the head of the pill — "C:" or "X:".
    ///
    /// Set only while both pills share one host window. On a host of its own a pill needs no
    /// marker: it is the only one there, and its label is already the answer. Two of them side by
    /// side in the same title bar is the one case where "which provider is this?" is a real
    /// question.
    /// </summary>
    public void SetPrefix(string? prefix)
    {
        if (string.Equals(_prefix, prefix, StringComparison.Ordinal)) return;

        _prefix = prefix;

        var width = Measure(_model);
        if (Math.Abs(width - LogicalWidth) > 0.5)
        {
            LogicalWidth = width;
            WidthChanged?.Invoke();
        }
        InvalidateVisual();
    }

    /// <summary>Bounds of the clickable icon in view-local DIPs, or null when hidden.</summary>
    public Rect? ActionIconBounds { get; private set; }

    /// <summary>Width the pill needs for the current content, in DIPs.</summary>
    public double LogicalWidth { get; private set; } = 120;

    /// <summary>Raised when <see cref="LogicalWidth"/> changes so the window can resize.</summary>
    public event Action? WidthChanged;

    /// <summary>Hover text for what is currently drawn. See <see cref="UsageModel.ResetTooltip"/>.</summary>
    public string? ResetTooltip(DateTimeOffset now) => _model.ResetTooltip(now);

    public void Update(UsageModel model, bool lightTheme, bool? dimmedOverride = null)
    {
        _model = model.ForPill();
        _light = lightTheme;
        Opacity = dimmedOverride ?? (model.Stale || !model.HasValues) ? 0.6 : 1.0;

        var width = Measure(_model);
        if (Math.Abs(width - LogicalWidth) > 0.5)
        {
            LogicalWidth = width;
            WidthChanged?.Invoke();
        }
        InvalidateVisual();
    }

    /// <summary>Computes the needed width, measuring real text so labels can vary.</summary>
    private double Measure(UsageModel model)
    {
        var dpi = DpiScale();
        double total = PadX;
        if (_prefix is { Length: > 0 }) total += Text(_prefix, Label, dpi).Width + Gap;
        for (var i = 0; i < model.Bars.Count; i++)
        {
            if (i > 0) total += GroupGap;
            total += Text(model.Bars[i].Label, Label, dpi).Width + Gap;
            total += BarWidth + Gap;
            total += Text(PercentText(model.Bars[i]), Value, dpi).Width;
        }
        if (model.Action != PillAction.None) total += Gap + IconSize;
        return total + PadX;
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

        dc.DrawRoundedRectangle(Pill, null, new Rect(0, 0, width, height), height / 2, height / 2);

        var x = PadX;

        if (_prefix is { Length: > 0 })
        {
            // Drawn in the label brush, not the value brush: it identifies the pill rather than
            // reporting anything, so it should sit back from the numbers.
            var prefixText = Text(_prefix, Label, dpi);
            dc.DrawText(prefixText, new Point(x, (height - prefixText.Height) / 2));
            x += prefixText.Width + Gap;
        }

        for (var i = 0; i < _model.Bars.Count; i++)
        {
            if (i > 0) x += GroupGap;
            DrawGroup(dc, ref x, height, dpi, _model.Bars[i], DateTimeOffset.UtcNow);
        }

        if (_model.Action != PillAction.None)
        {
            x += Gap;
            var rect = new Rect(x, (height - IconSize) / 2, IconSize, IconSize);
            DrawActionIcon(dc, rect, dpi);
            ActionIconBounds = rect;
        }
        else
        {
            ActionIconBounds = null;
        }
    }

    private void DrawGroup(DrawingContext dc, ref double x, double height, double dpi, UsageBar bar, DateTimeOffset now)
    {
        var labelText = Text(bar.Label, Label, dpi);
        dc.DrawText(labelText, new Point(x, (height - labelText.Height) / 2));
        x += labelText.Width + Gap;

        var track = new Rect(x, (height - BarHeight) / 2, BarWidth, BarHeight);
        dc.DrawRoundedRectangle(Track, null, track, BarHeight / 2, BarHeight / 2);
        var pace = ResetPace.For(bar, now);

        if (bar.RemainingPct is { } remaining && double.IsFinite(remaining))
        {
            var fraction = Math.Clamp(remaining / 100.0, 0, 1);
            if (fraction > 0)
            {
                // Keep the endpoint proportional: a minimum-width sliver can falsely pass a
                // nearby reset pointer when the allowance is almost empty.
                var fillWidth = BarWidth * fraction;
                var fill = pace is { } known ? Frozen(known.FillColor) : NeutralFill;
                dc.DrawRoundedRectangle(fill, null,
                    new Rect(track.X, track.Y, fillWidth, BarHeight), BarHeight / 2, BarHeight / 2);
            }
        }
        if (pace is { } timing)
        {
            var pointerX = track.X + BarWidth * timing.TimeRemainingPct / 100;
            var pointer = new StreamGeometry();
            using (var path = pointer.Open())
            {
                path.BeginFigure(new Point(pointerX - 3.5, track.Y - 5), isFilled: true, isClosed: true);
                path.LineTo(new Point(pointerX + 3.5, track.Y - 5), isStroked: false, isSmoothJoin: false);
                path.LineTo(new Point(pointerX, track.Y - 1), isStroked: false, isSmoothJoin: false);
            }
            pointer.Freeze();
            dc.DrawGeometry(Value, null, pointer);
        }
        x += BarWidth + Gap;

        var valueText = Text(PercentText(bar), bar.RemainingPct is null ? Label : Value, dpi);
        dc.DrawText(valueText, new Point(x, (height - valueText.Height) / 2));
        x += valueText.Width;
    }

    private void DrawActionIcon(DrawingContext dc, Rect rect, double dpi)
    {
        var centre = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        var accent = Amber;
        dc.DrawEllipse(accent, null, centre, rect.Width / 2, rect.Height / 2);

        var glyph = "!";
        var text = new FormattedText(glyph, CultureInfo.InvariantCulture,
            System.Windows.FlowDirection.LeftToRight, Face, FontSize + 1, IconGlyph, dpi);
        dc.DrawText(text, new Point(centre.X - text.Width / 2, centre.Y - text.Height / 2));
    }

    private static string PercentText(UsageBar bar) =>
        bar.RemainingPct is { } r
            ? Math.Round(r).ToString("0", CultureInfo.InvariantCulture) + "%"
            : "--";

    private static FormattedText Text(string text, Brush brush, double dpi) =>
        new(text, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
            Face, FontSize, brush, dpi);

    // Colours are tuned to sit quietly on Claude's own title bar in either theme.
    private Brush Pill => _light ? LightPill : DarkPill;
    private Brush Track => _light ? LightTrack : DarkTrack;
    private Brush Label => _light ? LightLabel : DarkLabel;
    private Brush Value => _light ? LightValue : DarkValue;
    private Brush IconGlyph => _light ? LightPillSolid : DarkPillSolid;

    private Brush NeutralFill => _light ? LightNeutralFill : DarkNeutralFill;

    private static readonly Brush DarkPill = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
    private static readonly Brush DarkTrack = Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
    private static readonly Brush DarkLabel = Frozen(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
    private static readonly Brush DarkValue = Frozen(Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF));
    private static readonly Brush DarkPillSolid = Frozen(Color.FromRgb(0x26, 0x26, 0x24));

    private static readonly Brush LightPill = Frozen(Color.FromArgb(0x14, 0x00, 0x00, 0x00));
    private static readonly Brush LightTrack = Frozen(Color.FromArgb(0x24, 0x00, 0x00, 0x00));
    private static readonly Brush LightLabel = Frozen(Color.FromArgb(0x8A, 0x00, 0x00, 0x00));
    private static readonly Brush LightValue = Frozen(Color.FromArgb(0xE0, 0x1A, 0x1A, 0x18));
    private static readonly Brush LightPillSolid = Frozen(Color.FromRgb(0xFF, 0xFF, 0xFF));

    private static readonly Brush LightNeutralFill = Frozen(Color.FromRgb(0x77, 0x77, 0x73));
    private static readonly Brush DarkNeutralFill = Frozen(Color.FromRgb(0xB0, 0xB0, 0xAA));
    private static readonly Brush Amber = Frozen(Color.FromRgb(0xE3, 0xA0, 0x08));

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
