using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Xunit;

namespace UsageBars.Tests;

public class ShutdownLifecycleTests
{
    [Fact]
    public void OwnedWindowsAreDisposedBeforeApplicationShutdownBegins()
    {
        var order = new List<string>();

        ShutdownSequence.Run(
            disposeOwnedWindows: () => order.Add("dispose"),
            shutdownApplication: () => order.Add("shutdown"));

        Assert.Equal(new[] { "dispose", "shutdown" }, order);
    }
}

public class TargetDiagnosticsTests
{
    [Fact]
    public void TargetDiagnosticsIncludeEnabledDisabledAndRecoveryIssues()
    {
        var storage = new MemoryTargetStorage("{bad");
        var store = TargetStore.Load(storage, new Config());
        var text = new StringBuilder();

        TargetDiagnostics.Append(text, store, new Dictionary<ProviderId, string>
        {
            [ProviderId.Claude] = "no matching Claude target",
        });

        Assert.Contains("dynamic targets", text.ToString());
        Assert.Contains("recovery issue", text.ToString());
        Assert.Contains("Claude Desktop", text.ToString());
        Assert.Contains("(memory)", text.ToString());
        Assert.Contains("no matching Claude target", text.ToString());
    }

    [Fact]
    public void TargetDiagnosticsIncludeCanonicalActivityFilters()
    {
        var id = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var store = TargetStore.Load(new MemoryTargetStorage(
            $"{{\"Version\":3,\"Targets\":[{{\"Id\":\"{id}\",\"Name\":\"Terminal\",\"Enabled\":true,\"Providers\":[{{\"Provider\":\"Codex\"}}],\"ActivityProcessNames\":[\"OMP\",\"codex.exe\"],\"ProcessName\":\"WindowsTerminal\",\"WindowClass\":\"CASCADIA_HOSTING_WINDOW_CLASS\",\"RequireResizableFrame\":true,\"WindowTitle\":null,\"Placement\":\"Terminal\",\"Activation\":\"RequireActivityProcess\",\"Source\":\"Captured\",\"StatusBarHeight\":0,\"Revision\":1,\"Anchor\":\"TopRight\",\"OffsetX\":0,\"OffsetY\":0}}]}}"),
            new Config());
        var text = new StringBuilder();

        TargetDiagnostics.Append(text, store, new Dictionary<ProviderId, string>());

        Assert.Contains("activity=codex|omp", text.ToString());
    }

    [Fact]
    public void RecoveryWarningNamesTheTargetPathAndDiagnosticsAction()
    {
        var store = TargetStore.Load(new MemoryTargetStorage("{bad"), new Config());

        var warning = TargetRecoveryWarning.Format(store);

        Assert.Contains("(memory)", warning);
        Assert.Contains("recover", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Copy diagnostics", warning, StringComparison.OrdinalIgnoreCase);
    }
}

public class RedactTests
{
    /// <summary>
    /// The field name is deliberately kept: a log saying "sessionKey=&lt;redacted&gt;" is useful,
    /// one saying only "&lt;redacted&gt;" is not.
    /// </summary>
    [Theory]
    [InlineData("sessionKey=sk-ant-abc123def456", "sessionKey", "abc123def456")]
    [InlineData("Cookie: sessionKey=verysecretvalue", "Cookie", "verysecretvalue")]
    [InlineData("access_token: abcdefghijklmnop", "access_token", "abcdefghijklmnop")]
    public void SecretsAreReplacedButTheirNamesSurvive(
        string input, string keptName, string secret)
    {
        var scrubbed = Redact.Scrub(input);

        Assert.Contains(keptName, scrubbed);
        Assert.Contains("<redacted>", scrubbed);
        Assert.DoesNotContain(secret, scrubbed);
    }

    [Fact]
    public void ApiKeysAreRedacted()
    {
        var scrubbed = Redact.Scrub("using sk-ant-api03-abcdefghijklmnop for auth");
        Assert.DoesNotContain("abcdefghijklmnop", scrubbed);
    }

    [Fact]
    public void BearerTokensAreRedacted()
    {
        Assert.DoesNotContain("abcdefghij", Redact.Scrub("Authorization: Bearer abcdefghij.klm"));
    }

    [Fact]
    public void EmailAddressesAreRedacted()
    {
        Assert.DoesNotContain("someone@example.com", Redact.Scrub("signed in as someone@example.com"));
    }

    [Fact]
    public void NullStaysNullSoAbsentIsDistinguishableFromRedacted()
    {
        Assert.Null(Redact.Scrub(null));
    }

    [Fact]
    public void OutputIsBounded()
    {
        Assert.True(Redact.Scrub(new string('x', 5000))!.Length <= 512);
    }

    [Fact]
    public void NewlinesAreFlattenedSoOneEventStaysOneLine()
    {
        var scrubbed = Redact.Scrub("first\r\nsecond");
        Assert.DoesNotContain("\n", scrubbed);
    }

    [Fact]
    public void TheUserProfilePathIsReplaced()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(string.IsNullOrEmpty(profile), "expected a user profile path on Windows");

        var replaced = Redact.Paths(Path.Combine(profile, "secretfolder", "file.json"));

        Assert.StartsWith("%USERPROFILE%", replaced);
        Assert.DoesNotContain(profile, replaced);
    }

    [Fact]
    public void OrdinaryTextIsLeftAlone()
    {
        Assert.Equal("5h 91% left, resets in 3 h", Redact.Scrub("5h 91% left, resets in 3 h"));
    }
}

public class DiagnosticLogTests : IDisposable
{
    private readonly string _dir;

    public DiagnosticLogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "usagebars-diag", Guid.NewGuid().ToString("N"));
    }

    private static void WaitForLines(string path, int atLeast)
    {
        // The writer is asynchronous by design; poll rather than sleep a fixed amount.
        // A read-only sharing mode can block its next append and make it drop an event.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var observed = 0;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                // Count completed lines, so a partial final append cannot satisfy the wait.
                observed = reader.ReadToEnd().Count(c => c == '\n');
                if (observed >= atLeast) return;
            }
            Thread.Sleep(25);
        }
        throw new TimeoutException($"Expected {atLeast} completed log lines; observed {observed}.");
    }

    [Fact]
    public void RecordsAnEventAsJsonLines()
    {
        using (var log = new DiagnosticLog(_dir))
        {
            log.Record("codex", "app-server", "connected", DiagLevel.Info, "succeeded", "all good");
        }

        var path = Path.Combine(_dir, "usagebars.jsonl");
        WaitForLines(path, 1);
        var line = File.ReadAllLines(path).Single();

        Assert.Contains("\"provider\":\"codex\"", line);
        Assert.Contains("\"ev\":\"connected\"", line);
        Assert.Contains("all good", line);
    }

    /// <summary>
    /// A source that fails identically every 15 minutes must not fill the log with the same line.
    /// Only transitions are worth recording.
    /// </summary>
    [Fact]
    public void RepeatedIdenticalStatesAreCoalesced()
    {
        using (var log = new DiagnosticLog(_dir))
        {
            for (var i = 0; i < 10; i++)
                log.Record("claude", "scrape", "failed", DiagLevel.Warn, "failed", "timed out");
        }

        var path = Path.Combine(_dir, "usagebars.jsonl");
        WaitForLines(path, 1);
        Assert.Single(File.ReadAllLines(path));
    }

    [Fact]
    public void AChangeOfStateIsRecordedAgain()
    {
        using (var log = new DiagnosticLog(_dir))
        {
            log.Record("claude", "scrape", "failed", DiagLevel.Warn, "failed", "timed out");
            log.Record("claude", "scrape", "read", DiagLevel.Info, "succeeded", "42% used");
            log.Record("claude", "scrape", "failed", DiagLevel.Warn, "failed", "timed out");
        }

        var path = Path.Combine(_dir, "usagebars.jsonl");
        WaitForLines(path, 3);
        Assert.Equal(3, File.ReadAllLines(path).Length);
    }

    [Fact]
    public void DifferentProvidersDoNotCoalesceIntoEachOther()
    {
        using (var log = new DiagnosticLog(_dir))
        {
            log.Record("claude", "src", "read", DiagLevel.Info, "succeeded", "x");
            log.Record("codex", "src", "read", DiagLevel.Info, "succeeded", "x");
        }

        var path = Path.Combine(_dir, "usagebars.jsonl");
        WaitForLines(path, 2);
        Assert.Equal(2, File.ReadAllLines(path).Length);
    }

    [Fact]
    public void SecretsInDetailAreScrubbedBeforeReachingDisk()
    {
        using (var log = new DiagnosticLog(_dir))
        {
            log.Record("claude", "api", "call", DiagLevel.Info, "succeeded",
                "sessionKey=sk-ant-supersecretvalue");
        }

        var path = Path.Combine(_dir, "usagebars.jsonl");
        WaitForLines(path, 1);
        Assert.DoesNotContain("supersecretvalue", File.ReadAllText(path));
    }

    [Fact]
    public void TheLogRotatesAndStaysBounded()
    {
        using (var log = new DiagnosticLog(_dir, maxBytes: 400, generations: 2))
        {
            for (var i = 0; i < 40; i++)
                log.Record("claude", "src", $"event{i}", DiagLevel.Info, "succeeded", $"detail {i}");
        }

        var files = Directory.GetFiles(_dir, "usagebars*.jsonl");

        // Current plus at most the retained generations, and nothing unbounded.
        Assert.InRange(files.Length, 1, 3);
        foreach (var file in files)
            Assert.True(new FileInfo(file).Length < 2000, $"{file} grew beyond its bound");
    }

    [Fact]
    public void TailReturnsTheMostRecentLines()
    {
        using (var log = new DiagnosticLog(_dir))
        {
            for (var i = 0; i < 10; i++)
                log.Record("claude", "src", $"event{i}", DiagLevel.Info, "succeeded");
            WaitForLines(Path.Combine(_dir, "usagebars.jsonl"), 10);

            var tail = log.Tail(3);

            Assert.Equal(3, tail.Count);
            Assert.Contains("event9", tail[^1]);
        }
    }

    [Fact]
    public void AnUnwritableDirectoryDoesNotThrow()
    {
        // Diagnostics must never disturb the path they observe.
        using var log = new DiagnosticLog(Path.Combine("Z:", "nonexistent", "usagebars"));

        var exception = Record.Exception(
            () => log.Record("claude", "src", "event", DiagLevel.Info, "succeeded"));

        Assert.Null(exception);
        Assert.Empty(log.Tail(5));
    }

    [Fact]
    public void LatestTracksThePerProviderState()
    {
        using var log = new DiagnosticLog(_dir);

        log.Record("codex", "app-server", "connected", DiagLevel.Info, "succeeded");
        log.Record("codex", "app-server", "failed", DiagLevel.Warn, "failed", "boom");

        Assert.Equal("failed", log.Latest("codex")!.Event);
        Assert.Null(log.Latest("claude"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
    }
}

public class StartupRegistrationTests
{
    private sealed class FakeStore : IStartupStore
    {
        public string? Value;
        public bool FailWrites;

        public string? Read() => Value;

        public void Write(string value)
        {
            if (FailWrites) throw new UnauthorizedAccessException("policy");
            Value = value;
        }

        public void Delete() => Value = null;
    }

    /// <summary>Only the paths handed to it exist; everything else is gone.</summary>
    private static Func<string, bool> OnDisk(params string[] paths) =>
        path => Array.Exists(paths, p => p.Equals(path, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void EnablingWritesTheQuotedExecutablePath()
    {
        var store = new FakeStore();
        var registration = new StartupRegistration(
            store, () => @"C:\Program Files\UsageBars\UsageBars.exe",
            OnDisk(@"C:\Program Files\UsageBars\UsageBars.exe"));

        registration.SetEnabled(true);

        // Quoted, because the path routinely contains spaces and Run would otherwise split it.
        Assert.Equal(@"""C:\Program Files\UsageBars\UsageBars.exe""", store.Value);
        Assert.Equal(StartupState.On, registration.Current());
    }

    [Fact]
    public void DisablingRemovesTheValue()
    {
        var store = new FakeStore { Value = "\"C:\\old\\UsageBars.exe\"" };
        var registration = new StartupRegistration(store, () => @"C:\new\UsageBars.exe");

        registration.SetEnabled(false);

        Assert.Null(store.Value);
        Assert.Equal(StartupState.Off, registration.Current());
    }

    [Fact]
    public void AFailedWriteThrowsSoTheMenuCanRollBack()
    {
        var store = new FakeStore { FailWrites = true };
        var registration = new StartupRegistration(store, () => @"C:\UsageBars.exe");

        Assert.Throws<UnauthorizedAccessException>(() => registration.SetEnabled(true));
        Assert.Equal(StartupState.Off, registration.Current());
    }

    [Fact]
    public void AnUnknownExecutablePathIsRefusedRatherThanWritingRubbish()
    {
        var registration = new StartupRegistration(new FakeStore(), () => null);

        Assert.Throws<InvalidOperationException>(() => registration.SetEnabled(true));
    }

    [Fact]
    public void AnEntryNamingThisCopyIsOn()
    {
        var store = new FakeStore { Value = @"""C:\here\UsageBars.exe""" };
        var registration = new StartupRegistration(
            store, () => @"C:\here\UsageBars.exe", OnDisk(@"C:\here\UsageBars.exe"));

        Assert.Equal(StartupState.On, registration.Current());
    }

    [Fact]
    public void NoEntryIsOff()
    {
        var registration = new StartupRegistration(new FakeStore(), () => @"C:\here\UsageBars.exe");

        Assert.Equal(StartupState.Off, registration.Current());
    }

    /// <summary>
    /// Another copy that still exists: something does start at logon, just not this one. Reported
    /// separately from dangling because it is the case that must NOT be silently rewritten.
    /// </summary>
    [Fact]
    public void AnEntryNamingAnotherCopyThatExistsIsOtherCopy()
    {
        var store = new FakeStore { Value = @"""C:\other\UsageBars.exe""" };
        var registration = new StartupRegistration(
            store, () => @"C:\here\UsageBars.exe",
            OnDisk(@"C:\other\UsageBars.exe", @"C:\here\UsageBars.exe"));

        Assert.Equal(StartupState.OtherCopy, registration.Current());
    }

    /// <summary>
    /// The measured failure, three times over: the build directory was renamed, so the entry names
    /// an executable that is simply gone. Windows ignores it silently, and the old "is the value
    /// non-empty" check reported this as enabled.
    /// </summary>
    [Fact]
    public void AnEntryWhoseTargetIsGoneIsDangling()
    {
        var store = new FakeStore
        {
            Value = @"""C:\Users\me\RiderProjects\NewUsageBars\dist\UsageBars.exe""",
        };
        var registration = new StartupRegistration(
            store, () => @"C:\Users\me\RiderProjects\UsageBars\dist\UsageBars.exe",
            OnDisk(@"C:\Users\me\RiderProjects\UsageBars\dist\UsageBars.exe"));

        Assert.Equal(StartupState.Dangling, registration.Current());
    }

    [Fact]
    public void RepairRewritesADanglingEntryToThisCopy()
    {
        var store = new FakeStore { Value = @"""C:\gone\UsageBars.exe""" };
        var registration = new StartupRegistration(
            store, () => @"C:\here\UsageBars.exe", OnDisk(@"C:\here\UsageBars.exe"));

        Assert.True(registration.TryRepair());
        Assert.Equal(@"""C:\here\UsageBars.exe""", store.Value);
        Assert.Equal(StartupState.On, registration.Current());
    }

    /// <summary>
    /// A working registration for another copy is left alone. Otherwise launching any build once —
    /// a Debug build to test something — would quietly inherit what starts at logon.
    /// </summary>
    [Fact]
    public void RepairLeavesAWorkingOtherCopyAlone()
    {
        var store = new FakeStore { Value = @"""C:\other\UsageBars.exe""" };
        var registration = new StartupRegistration(
            store, () => @"C:\here\UsageBars.exe",
            OnDisk(@"C:\other\UsageBars.exe", @"C:\here\UsageBars.exe"));

        Assert.False(registration.TryRepair());
        Assert.Equal(@"""C:\other\UsageBars.exe""", store.Value);
    }

    [Fact]
    public void RepairDoesNothingWhenStartupWasNeverAskedFor()
    {
        var store = new FakeStore();
        var registration = new StartupRegistration(store, () => @"C:\here\UsageBars.exe");

        Assert.False(registration.TryRepair());
        Assert.Null(store.Value);
    }

    /// <summary>
    /// The value is read unexpanded so diagnostics can quote it verbatim. A REG_EXPAND_SZ written
    /// by something else would then be checked as the literal "%USERPROFILE%\..." — which exists
    /// nowhere, so a perfectly good entry would look dangling and be rewritten.
    /// </summary>
    [Fact]
    public void AnEnvironmentVariableInTheValueIsExpandedBeforeItIsJudged()
    {
        var real = Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\UsageBars\UsageBars.exe");

        var store = new FakeStore { Value = @"""%USERPROFILE%\UsageBars\UsageBars.exe""" };
        var registration = new StartupRegistration(
            store, () => @"C:\here\UsageBars.exe", OnDisk(real));

        Assert.Equal(StartupState.OtherCopy, registration.Current());
    }

    /// <summary>
    /// Run values are command lines. Treating an argument as part of the filename would make every
    /// entry carrying one look dangling — and then get rewritten.
    /// </summary>
    [Theory]
    [InlineData(@"""C:\a b\UsageBars.exe""", @"C:\a b\UsageBars.exe")]
    [InlineData(@"""C:\a b\UsageBars.exe"" --minimized", @"C:\a b\UsageBars.exe")]
    [InlineData(@"  ""C:\x\UsageBars.exe""  ", @"C:\x\UsageBars.exe")]
    [InlineData(@"C:\x\UsageBars.exe", @"C:\x\UsageBars.exe")]
    public void TheTargetIsTheExecutableWithoutQuotesOrArguments(string value, string expected)
    {
        Assert.Equal(expected, StartupRegistration.TargetOf(value));
    }
}
