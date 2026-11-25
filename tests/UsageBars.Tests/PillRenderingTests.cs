using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace UsageBars.Tests;

public class PillRenderingTests
{
    [Theory]
    [InlineData(50, 232, 197, 71)]
    [InlineData(42, 239, 146, 72)]
    [InlineData(58, 157, 192, 75)]
    [InlineData(32, 248, 81, 73)]
    [InlineData(68, 63, 185, 80)]
    public void RendersCrossbarAndDistanceColor(double remaining, byte red, byte green, byte blue)
    {
        OnSta(() =>
        {
            var snapshot = new CodexSnapshot(
                new CodexWindow(100 - remaining, 300, DateTimeOffset.Now.AddMinutes(150)),
                null, DateTimeOffset.Now);
            var model = UsageModel.BuildCodex(snapshot, true, null, new Config()) with { Warned = true };
            var view = new OverlayView();
            view.Update(model, lightTheme: false, provider: ProviderId.Codex);
            var drawings = Render(view);

            var track = Assert.Single(drawings, d =>
                Math.Abs(d.Geometry.Bounds.Width - PillLayoutMath.BarWidth) < 0.01
                && Math.Abs(d.Geometry.Bounds.Height - PillLayoutMath.RowHeight) < 0.01);

            // High-contrast crossbar core centered at 50% of the track width.
            var crossbar = Assert.Single(drawings, d =>
                d.Brush is SolidColorBrush b && b.Color == Color.FromArgb(0xF5, 0xF8, 0xF8, 0xF8));
            Assert.InRange(
                crossbar.Geometry.Bounds.X + crossbar.Geometry.Bounds.Width / 2.0,
                track.Geometry.Bounds.X + PillLayoutMath.BarWidth / 2.0 - 0.5,
                track.Geometry.Bounds.X + PillLayoutMath.BarWidth / 2.0 + 0.5);

            Assert.Contains(drawings, d =>
                d.Brush is SolidColorBrush brush && brush.Color == Color.FromRgb(red, green, blue));
        });
    }

    [Fact]
    public void UnknownResetDoesNotDrawCrossbarPointer()
    {
        OnSta(() =>
        {
            var model = UsageModel.BuildCodex(
                new CodexSnapshot(new CodexWindow(50, 300, null), null, DateTimeOffset.Now),
                true, null, new Config());
            var view = new OverlayView();
            view.Update(model, lightTheme: false, provider: ProviderId.Codex);
            var drawings = Render(view);

            Assert.DoesNotContain(drawings, d =>
                d.Brush is SolidColorBrush b && b.Color == Color.FromArgb(0xF5, 0xF8, 0xF8, 0xF8));
            Assert.DoesNotContain(drawings, d => d.Brush is SolidColorBrush brush &&
                (brush.Color == Color.FromRgb(63, 185, 80) || brush.Color == Color.FromRgb(248, 81, 73)));
        });
    }

    [Fact]
    public void LayeredSevenDayAndFiveHourBarDrawsBothLayersAndTwoCrossbars()
    {
        OnSta(() =>
        {
            var now = DateTimeOffset.Now;
            var model = new UsageModel
            {
                Bars = new[]
                {
                    new UsageBar("5h", 80) { ResetsAt = now.AddMinutes(150), WindowDuration = ResetEstimator.FiveHourWindow },
                    new UsageBar("7d", 25) { ResetsAt = now.AddDays(4), WindowDuration = ResetEstimator.SevenDayWindow },
                },
            };

            var view = new OverlayView();
            view.Update(model, lightTheme: false, provider: ProviderId.Claude);
            var drawings = Render(view);

            // Both layers share one row: green 5h on bottom 1/3, red 7d on top 2/3, two crossbars.
            var crossbars = drawings.Where(d =>
                d.Brush is SolidColorBrush b && b.Color == Color.FromArgb(0xF5, 0xF8, 0xF8, 0xF8)).ToList();
            Assert.Equal(2, crossbars.Count);
            Assert.Contains(drawings, d => d.Brush is SolidColorBrush b && b.Color == Color.FromRgb(63, 185, 80));
            Assert.Contains(drawings, d => d.Brush is SolidColorBrush b && b.Color == Color.FromRgb(248, 81, 73));
        });
    }

    [Fact]
    public void UnifiedPillStacksUpToMaxHeightThenWrapsIntoColumns()
    {
        var three = new[] { 0.0, 0.0, 0.0 };

        // 40px title bar (maxHeight ~38): 1 column x 3 stacked rows = 37px tall
        var tall = PillLayoutMath.Compute(three, maxHeight: 38, hasActionIcon: false);
        Assert.Equal(1, tall.Columns);
        Assert.Equal(3, tall.ActualRows);
        Assert.True(tall.Height <= 38);

        // 32px tight title bar (maxHeight ~30): 2 columns x 2 rows = 26px tall
        var medium = PillLayoutMath.Compute(three, maxHeight: 30, hasActionIcon: false);
        Assert.Equal(2, medium.Columns);
        Assert.Equal(2, medium.ActualRows);
        Assert.True(medium.Height <= 30);

        // 22px IDE status bar (maxHeight ~20): 3 columns x 1 row = 15px tall
        var statusBar = PillLayoutMath.Compute(three, maxHeight: 20, hasActionIcon: false);
        Assert.Equal(3, statusBar.Columns);
        Assert.Equal(1, statusBar.ActualRows);
        Assert.True(statusBar.Height <= 20);
    }

    [Fact]
    public void PillItemBuilderCollapses7dAnd5hIntoOneLayeredRowPerProvider()
    {
        var model = new UsageModel
        {
            Bars = new[]
            {
                new UsageBar("5h", 75),
                new UsageBar("7d", 40),
            },
        };

        var items = PillItemBuilder.Build(new (ProviderId?, UsageModel)[]
        {
            (ProviderId.Claude, model),
            (ProviderId.Codex, model),
            (ProviderId.Antigravity, model),
        });

        Assert.Equal(3, items.Count);
        Assert.All(items, item =>
        {
            Assert.Equal("7d", item.MainBar.Label);
            Assert.NotNull(item.SubBar);
            Assert.Equal("5h", item.SubBar!.Label);
        });
    }

    private static List<GeometryDrawing> Render(OverlayView view)
    {
        view.Measure(new Size(double.PositiveInfinity, view.LogicalHeight));
        view.Arrange(new Rect(0, 0, view.LogicalWidth, view.LogicalHeight));
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(view.LogicalWidth),
            (int)Math.Ceiling(view.LogicalHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var result = new List<GeometryDrawing>();
        void Visit(Drawing drawing)
        {
            if (drawing is GeometryDrawing geometry) result.Add(geometry);
            if (drawing is DrawingGroup group) foreach (var child in group.Children) Visit(child);
        }
        Visit(VisualTreeHelper.GetDrawing(view));
        return result;
    }

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "WPF rendering did not finish");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
