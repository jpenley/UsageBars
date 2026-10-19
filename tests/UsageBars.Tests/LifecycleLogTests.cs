using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace UsageBars.Tests;

public class LifecycleLogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "UsageBars-lifecycle-" + Guid.NewGuid());
    private string Journal => Path.Combine(_directory, "lifecycle.jsonl");

    [Fact]
    public void PillRemovalAndExceptionAreDurableBeforeDisposalAndRedacted()
    {
        using var log = new LifecycleLog(_directory);
        log.PillActivity(1, "created Codex; host=0x123");
        log.PillActivity(0, "retired Codex; host=0x123");
        try { throw new InvalidOperationException("token=verysecretvalue"); }
        catch (Exception ex) { log.Exception("dispatcher-unhandled-exception", ex); }

        var text = File.ReadAllText(Journal);
        Assert.DoesNotContain("verysecretvalue", text);
        using var last = JsonDocument.Parse(File.ReadLines(Journal).Last());
        Assert.Equal("dispatcher-unhandled-exception", last.RootElement.GetProperty("ev").GetString());
        Assert.Equal(0, last.RootElement.GetProperty("pills").GetInt32());
        Assert.Contains("retired Codex", last.RootElement.GetProperty("lastPillActivity").GetString());
        Assert.Contains(nameof(PillRemovalAndExceptionAreDurableBeforeDisposalAndRedacted),
            last.RootElement.GetProperty("stack").GetString());
    }

    [Fact]
    public void NextStartupReportsUnfinishedRunWithLastPillState()
    {
        using (var first = new LifecycleLog(_directory))
            first.PillActivity(0, "retired Claude; host=0x456");
        using var second = new LifecycleLog(_directory);

        var lines = File.ReadLines(Journal).ToArray();
        using var previous = JsonDocument.Parse(lines[^2]);
        Assert.Equal("previous-run-incomplete", previous.RootElement.GetProperty("ev").GetString());
        Assert.Contains("retired Claude", previous.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public void ObservedExitDoesNotReportUnfinishedRun()
    {
        using (var first = new LifecycleLog(_directory)) first.Record("process-exit");
        using var second = new LifecycleLog(_directory);
        Assert.DoesNotContain("previous-run-incomplete", File.ReadAllText(Journal));
    }

    [Fact]
    public void JournalRotatesAndKeepsPreviousEvidence()
    {
        using var log = new LifecycleLog(_directory, maxBytes: 2048);
        for (var i = 0; i < 30; i++) log.PillActivity(i, "created Codex; host=0x123");
        Assert.True(File.Exists(Journal + ".1"));
        Assert.InRange(new FileInfo(Journal).Length, 1, 2048);
        Assert.InRange(new FileInfo(Journal + ".1").Length, 1, 2048);
    }

    [Fact]
    public void UnwritableJournalDoesNotBreakObservedOperations()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "blocked"), "file, not directory");
        using var log = new LifecycleLog(Path.Combine(_directory, "blocked"));
        log.PillActivity(0, "retired Claude");
        log.Exception("unhandled-exception", new Exception("failure"));
        log.Record("process-exit");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
