using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace UsageBars.Tests;

public class TargetPreviewSuppressionTests
{
    [Fact]
    public void SuppressionRemovesEveryPreviewedWindow()
    {
        var wanted = Wanted(new IntPtr(10), new IntPtr(20), new IntPtr(30));

        TargetPreviewSuppression.RemovePreviewWindows(
            wanted, new HashSet<IntPtr> { new(10), new(30) });

        Assert.Equal(new[] { new IntPtr(20) },
            wanted.Keys.Select(key => key.Hwnd).Distinct());
    }

    private static Dictionary<(ProviderId Provider, IntPtr Hwnd), HostWindowInfo> Wanted(
        params IntPtr[] handles)
    {
        var result = new Dictionary<(ProviderId Provider, IntPtr Hwnd), HostWindowInfo>();
        foreach (var hwnd in handles)
        {
            result[(ProviderId.Claude, hwnd)] = new HostWindowInfo(
                hwnd, HostKind.Terminal, "test", ProviderId.Claude, HostEvidence.Open,
                TargetId: Guid.NewGuid(), Anchor: PillAnchor.TopRight);
        }
        return result;
    }
}
