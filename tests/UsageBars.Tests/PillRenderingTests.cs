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
    public void RendersTriangleAndDistanceColor(double remaining, byte red, byte green, byte blue)
    {
        OnSta(() =>
        {
            var snapshot = new CodexSnapshot(
                new CodexWindow(100 - remaining, 300, DateTimeOffset.Now.AddMinutes(150)),
                null, DateTimeOffset.Now);
            var model = UsageModel.BuildCodex(snapshot, true, null, new Config()) with { Warned = true };
            var view = new OverlayView();
            view.Update(model, lightTheme: false);
            var drawings = Render(view);

            var triangle = Assert.Single(drawings, d => d.Geometry is StreamGeometry);
            var track = Assert.Single(drawings, d => d.Geometry.Bounds.Width == 54 && d.Geometry.Bounds.Height == 6);
            Assert.InRange(triangle.Geometry.Bounds.X + triangle.Geometry.Bounds.Width / 2,
                track.Geometry.Bounds.X + 26.9, track.Geometry.Bounds.X + 27.1);
            Assert.Contains(drawings, d => d.Brush is SolidColorBrush brush && brush.Color == Color.FromRgb(red, green, blue));
        });
    }

    [Fact]
    public void UnknownResetDoesNotDrawAPointer()
    {
        OnSta(() =>
        {
            var model = UsageModel.BuildCodex(new CodexSnapshot(new CodexWindow(50, 300, null), null, DateTimeOffset.Now),
                true, null, new Config());
            var view = new OverlayView();
            view.Update(model, lightTheme: false);
            var drawings = Render(view);
            Assert.DoesNotContain(drawings, d => d.Geometry is StreamGeometry);
            Assert.DoesNotContain(drawings, d => d.Brush is SolidColorBrush brush &&
                (brush.Color == Color.FromRgb(63, 185, 80) || brush.Color == Color.FromRgb(248, 81, 73)));
        });
    }

    private static List<GeometryDrawing> Render(OverlayView view)
    {
        view.Measure(new Size(double.PositiveInfinity, OverlayView.LogicalHeight));
        view.Arrange(new Rect(0, 0, view.LogicalWidth, OverlayView.LogicalHeight));
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.LogicalWidth), 24, 96, 96, PixelFormats.Pbgra32);
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
