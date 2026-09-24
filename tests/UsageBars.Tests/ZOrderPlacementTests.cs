using System;
using System.Collections.Generic;
using Xunit;

namespace UsageBars.Tests;

/// <summary>
/// The z-order insertion fallback, used when a host refuses ownership. Exercised through a fake
/// window manager so the walk can be reasoned about without live windows.
/// </summary>
public class ZOrderPlacementTests
{
    /// <summary>Windows front-to-back, as EnumWindows reports them.</summary>
    private sealed class FakeZOrder : ZOrder.IZOrderApi
    {
        private readonly List<IntPtr> _order = new();
        private readonly HashSet<IntPtr> _topmost = new();

        public FakeZOrder Add(int handle, bool topmost = false)
        {
            var h = new IntPtr(handle);
            _order.Add(h);
            if (topmost) _topmost.Add(h);
            return this;
        }

        public bool IsWindow(IntPtr hWnd) => _order.Contains(hWnd);

        public IntPtr PreviousWindow(IntPtr hWnd)
        {
            var index = _order.IndexOf(hWnd);
            return index <= 0 ? IntPtr.Zero : _order[index - 1];
        }

        public bool IsTopmost(IntPtr hWnd) => _topmost.Contains(hWnd);
    }

    private static IntPtr H(int handle) => new(handle);

    [Fact]
    public void InsertsDirectlyAboveTheHost()
    {
        // front .. back:  other(1)  host(2)  overlay(9) parked behind
        var api = new FakeZOrder().Add(1).Add(2).Add(9);

        var placement = ZOrder.FindInsertAfter(api, H(9), H(2));

        Assert.True(placement.Success);
        Assert.False(placement.AlreadyPlaced);
        Assert.Equal(H(1), placement.InsertAfter);
    }

    [Fact]
    public void GoesToTheTopWhenNothingIsAboveTheHost()
    {
        var api = new FakeZOrder().Add(2).Add(9);

        var placement = ZOrder.FindInsertAfter(api, H(9), H(2));

        Assert.True(placement.Success);
        Assert.Equal(Native.HWND_TOP, placement.InsertAfter);
    }

    /// <summary>
    /// Topmost windows live in a separate band a non-topmost window can never be inserted into,
    /// so the walk skips over them to find a reachable position.
    /// </summary>
    [Fact]
    public void SkipsOverTopmostWindows()
    {
        // front .. back:  toast(1, topmost)  other(2)  host(3)
        var api = new FakeZOrder().Add(1, topmost: true).Add(2).Add(3).Add(9);

        var placement = ZOrder.FindInsertAfter(api, H(9), H(3));

        Assert.True(placement.Success);
        Assert.Equal(H(2), placement.InsertAfter);
    }

    [Fact]
    public void GoesToTheTopWhenOnlyTopmostWindowsAreAbove()
    {
        var api = new FakeZOrder().Add(1, topmost: true).Add(2, topmost: true).Add(3).Add(9);

        var placement = ZOrder.FindInsertAfter(api, H(9), H(3));

        Assert.True(placement.Success);
        Assert.Equal(Native.HWND_TOP, placement.InsertAfter);
    }

    /// <summary>
    /// There is no position above a topmost host available to a non-topmost pill, so the caller
    /// must fall back to being topmost itself rather than sitting uselessly behind.
    /// </summary>
    [Fact]
    public void RefusesWhenTheHostIsItselfTopmost()
    {
        var api = new FakeZOrder().Add(1).Add(2, topmost: true).Add(9);

        var placement = ZOrder.FindInsertAfter(api, H(9), H(2));

        Assert.False(placement.Success);
    }

    [Fact]
    public void ReportsAlreadyPlacedWhenTheOverlayIsDirectlyAbove()
    {
        // front .. back:  overlay(9)  host(2)
        var api = new FakeZOrder().Add(9).Add(2);

        var placement = ZOrder.FindInsertAfter(api, H(9), H(2));

        Assert.True(placement.Success);
        Assert.True(placement.AlreadyPlaced);
    }

    [Fact]
    public void FailsWhenEitherWindowIsGone()
    {
        var api = new FakeZOrder().Add(2);

        Assert.False(ZOrder.FindInsertAfter(api, H(9), H(2)).Success);
        Assert.False(ZOrder.FindInsertAfter(api, H(2), H(9)).Success);
    }
}

public class PlacementModeConfigTests
{
    [Theory]
    [InlineData("auto")]
    [InlineData("owned")]
    [InlineData("zorder")]
    [InlineData("topmost")]
    public void RecognisedModesSurvive(string mode)
    {
        var config = new Config { PlacementMode = mode };

        config.Normalize();

        Assert.Equal(mode, config.PlacementMode);
        Assert.Empty(config.Adjustments);
    }

    [Theory]
    [InlineData("AUTO", "auto")]
    [InlineData("  ZOrder  ", "zorder")]
    public void ModesAreCaseAndWhitespaceInsensitive(string given, string expected)
    {
        var config = new Config { PlacementMode = given };

        config.Normalize();

        Assert.Equal(expected, config.PlacementMode);
    }

    [Fact]
    public void AnUnknownModeFallsBackToAuto()
    {
        var config = new Config { PlacementMode = "float-above-everything" };

        config.Normalize();

        Assert.Equal("auto", config.PlacementMode);
        Assert.Contains(config.Adjustments, a => a.Contains(nameof(Config.PlacementMode)));
    }

    [Fact]
    public void TheDefaultPrefersOwnership()
    {
        Assert.Equal("auto", new Config().PlacementMode);
    }
}
