using System;
using System.Threading;
using System.Windows;

namespace UsageBars;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // One pill only, however many times the exe gets double-clicked.
        using var mutex = new Mutex(initiallyOwned: true, "Local\\UsageBars.SingleInstance", out var isNew);
        if (!isNew) return;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var controller = new OverlayController();

        app.Startup += (_, _) => controller.Start();
        app.Exit += (_, _) => controller.Dispose();

        app.Run();
    }
}
