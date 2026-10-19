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

        // Keep the journal alive through stack unwinding and ProcessExit, including startup failures.
        var lifecycle = new LifecycleLog(AppPaths.DiagnosticsDir);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
                lifecycle.Exception("unhandled-exception", exception);
            lifecycle.Record("runtime-termination", $"isTerminating={e.IsTerminating}");
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            lifecycle.Record("process-exit");
            lifecycle.Dispose();
        };

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
            lifecycle.Exception("dispatcher-unhandled-exception", e.Exception);
        app.SessionEnding += (_, e) => lifecycle.Record("session-ending", e.ReasonSessionEnding.ToString());
        app.Exit += (_, e) => lifecycle.Record("application-exit", $"exitCode={e.ApplicationExitCode}");
        var controller = new OverlayController(lifecycle);

        app.Startup += (_, _) => controller.Start();
        app.Exit += (_, _) => controller.Dispose();

        var exitCode = app.Run();
        lifecycle.Record("main-return", $"exitCode={exitCode}");
    }
}
