using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace UsageBars.Tests;

public class TargetStoreTests
{
    [Fact]
    public void MissingDocumentSeedsLegacyBehaviorAndPersistsIt()
    {
        var storage = new MemoryTargetStorage();
        var store = TargetStore.Load(storage, new Config
        {
            AttachToClaudeCodeTerminals = false,
            AttachToCodexTerminals = true,
        });

        Assert.Contains(store.Targets, x => x.ProcessName == "claude");
        Assert.Contains(store.Targets, x => x.ProcessName == "ChatGPT");
        Assert.Contains(store.Targets, x => x.ProcessName == "rider64" && x.StatusBarHeight == 26);
        Assert.Contains(store.Targets, x => x.ProcessName == "code" && x.StatusBarHeight == 22);
        Assert.Contains(store.Targets, x =>
            x.WindowClass == "CASCADIA_HOSTING_WINDOW_CLASS"
            && x.Providers.Any(p => p.Provider == ProviderId.Claude)
            && !x.Enabled);
        Assert.True(storage.Exists);
    }

    [Fact]
    public void FailedSaveDoesNotPublishTheMutation()
    {
        var storage = new MemoryTargetStorage();
        var store = TargetStore.Load(storage, new Config());
        var count = store.Targets.Count;
        storage.WriteError = new UnauthorizedAccessException("locked");

        var result = store.Add(TargetDefinition.Captured(
            "Notepad", "notepad", "Notepad", new[] { ProviderBinding.Claude },
            TargetPlacementKind.Desktop, TargetActivationPolicy.AlwaysWhileOpen, true, 0));

        Assert.False(result.Success);
        Assert.Equal(count, store.Targets.Count);
    }

    [Fact]
    public void DuplicateAddReturnsTheExistingId()
    {
        var storage = new MemoryTargetStorage();
        var store = TargetStore.Load(storage, new Config());
        var existing = store.Targets.First();

        var result = store.Add(existing with { Id = Guid.NewGuid(), Name = "Duplicate" });

        Assert.False(result.Success);
        Assert.Equal(existing.Id, result.ExistingId);
    }

    [Fact]
    public void CorruptInputIsPreservedBeforeFirstRecoveryWrite()
    {
        var storage = new MemoryTargetStorage("{not-json");
        var store = TargetStore.Load(storage, new Config());

        var result = store.SetEnabled(store.Targets[0].Id, false);

        Assert.True(result.Success);
        Assert.Contains("corrupt", storage.PreservedLabels);
    }

    [Fact]
    public void ThrowingChangedSubscriberDoesNotTurnAPersistedAddIntoAFailure()
    {
        var storage = new MemoryTargetStorage();
        var store = TargetStore.Load(storage, new Config());
        var target = TargetDefinition.Captured(
            "Notepad", "notepad", "Notepad", new[] { ProviderBinding.Claude },
            TargetPlacementKind.Desktop, TargetActivationPolicy.AlwaysWhileOpen, true, 0);
        store.Changed += () => throw new InvalidOperationException("observer failed");

        var result = store.Add(target);

        Assert.True(result.Success);
        Assert.Contains(store.Targets, x => x.Id == target.Id);
        Assert.Contains("Notepad", storage.Text);
    }

    [Fact]
    public void LoadRetainsValidEntriesAlongsideInvalidEntriesAndPreservesThemAsInvalid()
    {
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var storage = new MemoryTargetStorage(
            "{\"Version\":1,\"Targets\":[" + ValidTargetJson(id, "Survives") + ",null]}");

        var store = TargetStore.Load(storage, new Config());
        var result = store.SetEnabled(id, false);

        var target = Assert.Single(store.Targets);
        Assert.Equal(id, target.Id);
        Assert.Equal("Survives", target.Name);
        Assert.Single(store.Issues);
        Assert.True(result.Success);
        Assert.Contains("invalid", storage.PreservedLabels);
    }

    [Fact]
    public void LoadRejectsTargetsWithNullProvidersWithoutDiscardingValidEntries()
    {
        var id = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var storage = new MemoryTargetStorage(
            "{\"Version\":1,\"Targets\":[" + ValidTargetJson(id, "Survives")
            + ",{\"Id\":\"33333333-3333-3333-3333-333333333333\",\"Name\":\"Null providers\",\"Providers\":null}]}");

        var store = TargetStore.Load(storage, new Config());

        Assert.Equal(id, Assert.Single(store.Targets).Id);
        Assert.Contains(store.Issues, issue => issue.Contains("providers", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LoadRejectsTargetsWithUnknownEnumsWithoutDiscardingValidEntries()
    {
        var id = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var storage = new MemoryTargetStorage(
            "{\"Version\":1,\"Targets\":[" + ValidTargetJson(id, "Survives")
            + ",{\"Id\":\"55555555-5555-5555-5555-555555555555\",\"Placement\":\"UnknownPlacement\"}]}");

        var store = TargetStore.Load(storage, new Config());

        Assert.Equal(id, Assert.Single(store.Targets).Id);
        Assert.NotEmpty(store.Issues);
    }

    [Theory]
    [InlineData("Provider")]
    [InlineData("Placement")]
    [InlineData("Activation")]
    [InlineData("Source")]
    [InlineData("TitleMatchKind")]
    public void LoadRejectsUndefinedNumericEnumsWithoutDiscardingValidEntries(string enumName)
    {
        var id = Guid.Parse("56565656-5656-5656-5656-565656565656");
        var storage = new MemoryTargetStorage(
            "{\"Version\":1,\"Targets\":[" + ValidTargetJson(id, "Survives")
            + "," + UndefinedNumericEnumTargetJson(enumName) + "]}");

        var store = TargetStore.Load(storage, new Config());
        var result = store.SetEnabled(id, false);

        Assert.Equal(id, Assert.Single(store.Targets).Id);
        Assert.NotEmpty(store.Issues);
        Assert.True(result.Success);
        Assert.Contains("invalid", storage.PreservedLabels);
    }

    [Theory]
    [InlineData("Literal", "null")]
    [InlineData("Literal", "\"\"")]
    [InlineData("Prefix", "null")]
    [InlineData("Prefix", "\"\"")]
    [InlineData("Regex", "null")]
    [InlineData("Regex", "\"\"")]
    public void LoadRejectsTitleRulesWithMissingValuesWithoutDiscardingValidEntries(
        string kind, string value)
    {
        var id = Guid.Parse("57575757-5757-5757-5757-575757575757");
        var storage = new MemoryTargetStorage(
            "{\"Version\":1,\"Targets\":[" + ValidTargetJson(id, "Survives")
            + "," + TitleTargetJson(kind, value) + "]}");

        var store = TargetStore.Load(storage, new Config());
        var result = store.SetEnabled(id, false);

        Assert.Equal(id, Assert.Single(store.Targets).Id);
        Assert.Contains(store.Issues, issue => issue.Contains("title value", StringComparison.OrdinalIgnoreCase));
        Assert.True(result.Success);
        Assert.Contains("invalid", storage.PreservedLabels);
    }

    [Fact]
    public void LoadRejectsDuplicateIdsAndKeepsTheFirstValidEntry()
    {
        var id = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var storage = new MemoryTargetStorage(
            "{\"Version\":1,\"Targets\":[" + ValidTargetJson(id, "First")
            + "," + ValidTargetJson(id, "Duplicate") + "]}");

        var store = TargetStore.Load(storage, new Config());

        var target = Assert.Single(store.Targets);
        Assert.Equal("First", target.Name);
        Assert.Contains(store.Issues, issue => issue.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IdMutationsReturnFailuresForMissingAndAmbiguousIds()
    {
        var storage = new MemoryTargetStorage();
        var store = TargetStore.Load(storage, new Config());
        var missing = Guid.Parse("77777777-7777-7777-7777-777777777777");
        var existing = store.Targets[0];
        var targets = (List<TargetDefinition>)typeof(TargetStore)
            .GetField("_targets", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(store)!;
        targets.Add(existing with { Name = "Ambiguous" });

        Assert.False(store.SetEnabled(missing, false).Success);
        Assert.False(store.Update(existing with { Id = missing }).Success);
        Assert.False(store.Delete(missing).Success);
        Assert.False(store.SetEnabled(existing.Id, false).Success);
        Assert.False(store.Update(existing).Success);
        Assert.False(store.Delete(existing.Id).Success);
    }

    [Fact]
    public void VersionOneTargetsMigrateToContextAnchorsAndSaveAsVersionThree()
    {
        var desktop = Guid.Parse("10101010-1010-1010-1010-101010101010");
        var terminal = Guid.Parse("20202020-2020-2020-2020-202020202020");
        var ide = Guid.Parse("30303030-3030-3030-3030-303030303030");
        var storage = new MemoryTargetStorage(
            "{\"Version\":1,\"Targets\":[" +
            ValidTargetJson(desktop, "Desktop") + "," +
            ValidTargetJson(terminal, "Terminal").Replace("\"Placement\":\"Desktop\"", "\"Placement\":\"Terminal\"") + "," +
            ValidTargetJson(ide, "IDE").Replace("\"Placement\":\"Desktop\"", "\"Placement\":\"Ide\"") + "]}");

        var store = TargetStore.Load(storage, new Config());

        Assert.Equal(PillAnchor.TopCenter, store.Targets.Single(x => x.Id == desktop).Anchor);
        Assert.Equal(PillAnchor.TopRight, store.Targets.Single(x => x.Id == terminal).Anchor);
        Assert.Equal(PillAnchor.BottomCenter, store.Targets.Single(x => x.Id == ide).Anchor);

        Assert.True(store.SetEnabled(desktop, false).Success);
        Assert.Contains("\"Version\": 3", storage.Text);
        Assert.Contains("\"Anchor\": \"TopRight\"", storage.Text);

        var reloaded = TargetStore.Load(storage, new Config());
        Assert.Equal(PillAnchor.TopCenter, reloaded.Targets.Single(x => x.Id == desktop).Anchor);
        Assert.Equal(PillAnchor.TopRight, reloaded.Targets.Single(x => x.Id == terminal).Anchor);
        Assert.Equal(PillAnchor.BottomCenter, reloaded.Targets.Single(x => x.Id == ide).Anchor);
    }

    [Fact]
    public void VersionTwoRejectsAnExplicitUnknownAnchorButKeepsValidEntries()
    {
        var good = Guid.Parse("40404040-4040-4040-4040-404040404040");
        var bad = Guid.Parse("50505050-5050-5050-5050-505050505050");
        var valid = VersionTwoTargetJson(good, "Good", "TopCenter", 0, 0);
        var invalid = VersionTwoTargetJson(bad, "Bad", "Sideways", 0, 0);
        var store = TargetStore.Load(
            new MemoryTargetStorage($"{{\"Version\":2,\"Targets\":[{valid},{invalid}]}}"),
            new Config());

        Assert.Equal(good, Assert.Single(store.Targets).Id);
        Assert.Contains(store.Issues,
            issue => issue.Contains("anchor", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VersionTwoRejectsANumericAnchorButKeepsValidEntries()
    {
        var good = Guid.Parse("41414141-4141-4141-4141-414141414141");
        var bad = Guid.Parse("42424242-4242-4242-4242-424242424242");
        var valid = VersionTwoTargetJson(good, "Good", "TopCenter", 0, 0);
        var invalid = VersionTwoTargetJson(bad, "Bad", "TopCenter", 0, 0)
            .Replace("\"Anchor\":\"TopCenter\"", "\"Anchor\":0");
        var store = TargetStore.Load(
            new MemoryTargetStorage($"{{\"Version\":2,\"Targets\":[{valid},{invalid}]}}"),
            new Config());

        Assert.Equal(good, Assert.Single(store.Targets).Id);
        Assert.Contains(store.Issues,
            issue => issue.Contains("anchor", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VersionTwoRejectsMissingAnchorButKeepsValidEntries()
    {
        var good = Guid.Parse("51515151-5151-5151-5151-515151515151");
        var missing = Guid.Parse("52525252-5252-5252-5252-525252525252");
        var valid = VersionTwoTargetJson(good, "Good", "TopCenter", 0, 0);
        var incomplete = ValidTargetJson(missing, "Missing anchor");
        var store = TargetStore.Load(
            new MemoryTargetStorage($"{{\"Version\":2,\"Targets\":[{valid},{incomplete}]}}"),
            new Config());

        Assert.Equal(good, Assert.Single(store.Targets).Id);
        Assert.Contains(store.Issues,
            issue => issue.Contains("anchor", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LoadClampsPersistedOffsetsAndReportsTheAdjustment()
    {
        var id = Guid.Parse("60606060-6060-6060-6060-606060606060");
        var storage = new MemoryTargetStorage(
            $"{{\"Version\":2,\"Targets\":[{VersionTwoTargetJson(id, "Clamped", "TopCenter", 9999, -9999)}]}}");

        var store = TargetStore.Load(storage, new Config());
        var target = Assert.Single(store.Targets);

        Assert.Equal(2000, target.OffsetX);
        Assert.Equal(-2000, target.OffsetY);
        Assert.Contains(store.Issues, issue => issue.Contains("offset", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VersionTwoProviderActivityMigratesToOneTargetLevelOrList()
    {
        var id = Guid.Parse("71717171-7171-7171-7171-717171717171");
        var storage = new MemoryTargetStorage(
            $"{{\"Version\":2,\"Targets\":[{LegacyTargetJson(id, "RequireProviderActivity", null)}]}}");

        var store = TargetStore.Load(storage, new Config());

        var target = Assert.Single(store.Targets);
        Assert.Equal(TargetActivationPolicy.RequireActivityProcess, target.Activation);
        Assert.Equal(new[] { "claude", "codex" }, target.ActivityProcessNames);
        Assert.Equal(new[] { ProviderId.Claude, ProviderId.Codex },
            target.Providers.Select(provider => provider.Provider).Order().ToArray());
        Assert.Null(target.WindowTitle);
    }

    [Fact]
    public void VersionTwoAlwaysOpenMigratesWithNoActivityProcesses()
    {
        var id = Guid.Parse("72727272-7272-7272-7272-727272727272");
        var store = TargetStore.Load(new MemoryTargetStorage(
            $"{{\"Version\":2,\"Targets\":[{LegacyTargetJson(id, "AlwaysWhileOpen", null)}]}}"), new Config());

        var target = Assert.Single(store.Targets);
        Assert.Equal(TargetActivationPolicy.AlwaysWhileOpen, target.Activation);
        Assert.Empty(target.ActivityProcessNames);
    }

    [Fact]
    public void VersionTwoActivityTitlePatternProducesReviewWarningWithoutBecomingWindowTitle()
    {
        var id = Guid.Parse("73737373-7373-7373-7373-737373737373");
        var store = TargetStore.Load(new MemoryTargetStorage(
            $"{{\"Version\":2,\"Targets\":[{LegacyTargetJson(id, "RequireProviderActivity", "codex session")}]}}"), new Config());

        var target = Assert.Single(store.Targets);
        Assert.Null(target.WindowTitle);
        Assert.Equal("Migrated provider-specific title activity to process-only activation; review this target.",
            target.MigrationWarning);
        Assert.Contains(store.Issues, issue => issue.Contains("review this target", StringComparison.Ordinal));
    }

    [Fact]
    public void MigrationWarningDoesNotPreserveTheSourceAsInvalidBeforeMutation()
    {
        var id = Guid.Parse("76767676-7676-7676-7676-767676767676");
        var storage = new MemoryTargetStorage(
            $"{{\"Version\":2,\"Targets\":[{LegacyTargetJson(id, "RequireProviderActivity", "codex session").Replace("\"OffsetX\":0", "\"OffsetX\":5000")}]}}");
        var store = TargetStore.Load(storage, new Config());

        Assert.True(store.SetEnabled(id, false).Success);
        Assert.Empty(storage.PreservedLabels);
    }

    [Fact]
    public void MigrationWarningSurvivesUnrelatedMutationAndReload()
    {
        var warningId = Guid.Parse("79797979-7979-7979-7979-797979797979");
        var unrelatedId = Guid.Parse("80808080-8080-8080-8080-808080808080");
        var storage = new MemoryTargetStorage(
            $"{{\"Version\":2,\"Targets\":[{LegacyTargetJson(warningId, "RequireProviderActivity", "codex session")},{LegacyTargetJson(unrelatedId, "AlwaysWhileOpen", null)}]}}");
        var store = TargetStore.Load(storage, new Config());

        Assert.True(store.SetEnabled(unrelatedId, false).Success);
        Assert.Contains("\"MigrationWarning\"", storage.Text);

        var reloaded = TargetStore.Load(storage, new Config());
        Assert.Equal("Migrated provider-specific title activity to process-only activation; review this target.",
            reloaded.Targets.Single(target => target.Id == warningId).MigrationWarning);
        Assert.Contains(reloaded.Issues, issue => issue.Contains("review this target", StringComparison.Ordinal));
    }

    [Fact]
    public void SavingMigratedWarningTargetClearsMarkerAndIssue()
    {
        var id = Guid.Parse("81818181-8181-8181-8181-818181818181");
        var storage = new MemoryTargetStorage(
            $"{{\"Version\":2,\"Targets\":[{LegacyTargetJson(id, "RequireProviderActivity", "codex session")}]}}");
        var store = TargetStore.Load(storage, new Config());

        Assert.Contains(store.Issues, issue => issue.Contains("review this target", StringComparison.Ordinal));
        Assert.True(store.SetEnabled(id, false).Success);
        Assert.Null(store.Targets.Single().MigrationWarning);
        Assert.DoesNotContain(store.Issues, issue => issue.Contains("review this target", StringComparison.Ordinal));

        var reloaded = TargetStore.Load(storage, new Config());
        Assert.Null(reloaded.Targets.Single().MigrationWarning);
        Assert.DoesNotContain(reloaded.Issues, issue => issue.Contains("review this target", StringComparison.Ordinal));
    }

    [Fact]
    public void ChangedObserverSeesMigrationIssueRemovedAfterSavingWarningTarget()
    {
        var id = Guid.Parse("82828282-8282-8282-8282-828282828282");
        var storage = new MemoryTargetStorage(
            $"{{\"Version\":2,\"Targets\":[{LegacyTargetJson(id, "RequireProviderActivity", "codex session")}]}}");
        var store = TargetStore.Load(storage, new Config());
        IReadOnlyList<string>? observedIssues = null;
        store.Changed += () => observedIssues = store.Issues;

        Assert.True(store.SetEnabled(id, false).Success);

        Assert.NotNull(observedIssues);
        Assert.DoesNotContain(observedIssues!, issue =>
            issue.Contains("review this target", StringComparison.Ordinal));
    }

    [Fact]
    public void DeleteRemovesOnlyDeletedTargetMigrationWarningIssue()
    {
        var deletedId = Guid.Parse("83838383-8383-8383-8383-838383838383");
        var retainedId = Guid.Parse("84848484-8484-8484-8484-848484848484");
        var storage = new MemoryTargetStorage(
            $"{{\"Version\":2,\"Targets\":[{LegacyTargetJson(deletedId, "RequireProviderActivity", "first session")},{LegacyTargetJson(retainedId, "RequireProviderActivity", "second session")},null]}}");
        var store = TargetStore.Load(storage, new Config());

        Assert.True(store.Delete(deletedId).Success);

        Assert.DoesNotContain(store.Issues, issue => issue.StartsWith($"target {deletedId}:", StringComparison.Ordinal));
        Assert.Contains(store.Issues, issue => issue.StartsWith($"target {retainedId}:", StringComparison.Ordinal));
        Assert.Contains(store.Issues, issue => issue.StartsWith("target 2:", StringComparison.Ordinal));
    }

    [Fact]
    public void FirstMutationWritesMigratedTargetsAsVersionThreeWithoutLegacyProviderFields()
    {
        var id = Guid.Parse("74747474-7474-7474-7474-747474747474");
        var storage = new MemoryTargetStorage(
            $"{{\"Version\":2,\"Targets\":[{LegacyTargetJson(id, "RequireProviderActivity", null)}]}}");
        var store = TargetStore.Load(storage, new Config());

        Assert.True(store.SetEnabled(id, false).Success);
        Assert.Contains("\"Version\": 3", storage.Text);
        Assert.Contains("\"ActivityProcessNames\"", storage.Text);
        Assert.DoesNotContain("CliProcessName", storage.Text);
        Assert.DoesNotContain("ActivityTitlePattern", storage.Text);
        Assert.DoesNotContain("RequireProviderActivity", storage.Text);
    }

    [Fact]
    public void VersionThreeRoundTripPreservesTitleAndActivityProcesses()
    {
        var storage = new MemoryTargetStorage();
        var store = TargetStore.Load(storage, new Config());
        var target = TargetDefinition.Captured(
            "Terminal", "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS",
            new[] { ProviderBinding.Claude, ProviderBinding.Codex }, TargetPlacementKind.Terminal,
            TargetActivationPolicy.RequireActivityProcess, true, 0, new[] { "codex.exe", "claude" }) with
        {
            WindowTitle = new TitleRule(TitleMatchKind.Prefix, "Project"),
        };

        Assert.True(store.Add(target).Success);
        var reloaded = TargetStore.Load(storage, new Config());
        var actual = Assert.Single(reloaded.Targets, candidate => candidate.Id == target.Id);
        Assert.Equal(target.WindowTitle, actual.WindowTitle);
        Assert.Equal(new[] { "claude", "codex" }, actual.ActivityProcessNames);
    }

    [Fact]
    public void VersionThreeRejectsMissingActivityProcessCollection()
    {
        var id = Guid.Parse("75757575-7575-7575-7575-757575757575");
        var target = LegacyTargetJson(id, "AlwaysWhileOpen", null);
        var storage = new MemoryTargetStorage($"{{\"Version\":3,\"Targets\":[{target}]}}");

        var store = TargetStore.Load(storage, new Config());

        Assert.Empty(store.Targets);
        Assert.Contains(store.Issues, issue => issue.Contains("ActivityProcessNames", StringComparison.Ordinal));
    }

    [Fact]
    public void VersionThreeRejectsLegacyProviderActivityActivation()
    {
        var id = Guid.Parse("77777777-7777-7777-7777-777777777778");
        var store = TargetStore.Load(new MemoryTargetStorage(
            $"{{\"Version\":3,\"Targets\":[{VersionThreeTargetJson(id, "RequireProviderActivity", "[\"codex\"]")}]}}"), new Config());

        Assert.Empty(store.Targets);
        Assert.NotEmpty(store.Issues);
    }

    [Fact]
    public void AddCanonicalizesActivityProcessesBeforePersisting()
    {
        var storage = new MemoryTargetStorage();
        var store = TargetStore.Load(storage, new Config());
        var target = TargetDefinition.Captured(
            "Terminal", "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS",
            new[] { ProviderBinding.Claude }, TargetPlacementKind.Terminal,
            TargetActivationPolicy.RequireActivityProcess, true, 0, new[] { "claude" }) with
        {
            ActivityProcessNames = new() { "CODEX", "claude" },
        };

        Assert.True(store.Add(target).Success);
        Assert.Equal(new[] { "claude", "codex" },
            store.Targets.Single(candidate => candidate.Id == target.Id).ActivityProcessNames);
        Assert.Contains("\"ActivityProcessNames\": [\r\n        \"claude\",\r\n        \"codex\"", storage.Text);
    }

    [Fact]
    public void UpdateCanonicalizesActivityProcessesBeforePersisting()
    {
        var storage = new MemoryTargetStorage();
        var store = TargetStore.Load(storage, new Config());
        var target = TargetDefinition.Captured(
            "Terminal", "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS",
            new[] { ProviderBinding.Claude }, TargetPlacementKind.Terminal,
            TargetActivationPolicy.RequireActivityProcess, true, 0, new[] { "claude", "codex" });
        Assert.True(store.Add(target).Success);

        Assert.True(store.Update(target with { ActivityProcessNames = new() { "CODEX", "claude" } }).Success);
        Assert.Equal(new[] { "claude", "codex" },
            store.Targets.Single(candidate => candidate.Id == target.Id).ActivityProcessNames);
        Assert.Contains("\"ActivityProcessNames\": [\r\n        \"claude\",\r\n        \"codex\"", storage.Text);
    }

    [Fact]
    public void VersionThreeLoadCanonicalizesActivityProcesses()
    {
        var id = Guid.Parse("78787878-7878-7878-7878-787878787878");
        var store = TargetStore.Load(new MemoryTargetStorage(
            $"{{\"Version\":3,\"Targets\":[{VersionThreeTargetJson(id, "RequireActivityProcess", "[\"CODEX\",\"claude\"]")}]}}"), new Config());

        var target = Assert.Single(store.Targets);
        Assert.Equal(new[] { "claude", "codex" }, target.ActivityProcessNames);
    }

    private static string ValidTargetJson(Guid id, string name) =>
        $"{{\"Id\":\"{id}\",\"Name\":\"{name}\",\"Enabled\":true,\"Providers\":[{{\"Provider\":\"Claude\",\"CliProcessName\":\"claude\",\"ActivityTitlePattern\":null}}],\"ProcessName\":\"notepad\",\"WindowClass\":\"Notepad\",\"RequireResizableFrame\":true,\"WindowTitle\":null,\"Placement\":\"Desktop\",\"Activation\":\"AlwaysWhileOpen\",\"Source\":\"Captured\",\"StatusBarHeight\":0,\"Revision\":1}}";

    private static string VersionTwoTargetJson(
        Guid id, string name, string anchor, int offsetX, int offsetY) =>
        ValidTargetJson(id, name)[..^1]
        + $",\"Anchor\":\"{anchor}\",\"OffsetX\":{offsetX},\"OffsetY\":{offsetY}}}";

    private static string LegacyTargetJson(Guid id, string activation, string? activityTitlePattern) =>
        $"{{\"Id\":\"{id}\",\"Name\":\"Terminal\",\"Enabled\":true,\"Providers\":[{{\"Provider\":\"Claude\",\"CliProcessName\":\"claude.exe\",\"ActivityTitlePattern\":{JsonString(activityTitlePattern)}}},{{\"Provider\":\"Codex\",\"CliProcessName\":\"codex\",\"ActivityTitlePattern\":null}}],\"ProcessName\":\"WindowsTerminal\",\"WindowClass\":\"CASCADIA_HOSTING_WINDOW_CLASS\",\"RequireResizableFrame\":true,\"WindowTitle\":null,\"Placement\":\"Terminal\",\"Activation\":\"{activation}\",\"Source\":\"Captured\",\"StatusBarHeight\":0,\"Revision\":1,\"Anchor\":\"TopRight\",\"OffsetX\":0,\"OffsetY\":0}}";

    private static string JsonString(string? value) => value is null ? "null" : $"\"{value}\"";

    private static string VersionThreeTargetJson(Guid id, string activation, string activityProcessNames) =>
        $"{{\"Id\":\"{id}\",\"Name\":\"Terminal\",\"Enabled\":true,\"Providers\":[{{\"Provider\":\"Claude\"}}],\"ActivityProcessNames\":{activityProcessNames},\"ProcessName\":\"WindowsTerminal\",\"WindowClass\":\"CASCADIA_HOSTING_WINDOW_CLASS\",\"RequireResizableFrame\":true,\"WindowTitle\":null,\"Placement\":\"Terminal\",\"Activation\":\"{activation}\",\"Source\":\"Captured\",\"StatusBarHeight\":0,\"Revision\":1,\"Anchor\":\"TopRight\",\"OffsetX\":0,\"OffsetY\":0}}";

    private static string UndefinedNumericEnumTargetJson(string enumName)
    {
        var target = ValidTargetJson(Guid.Parse("58585858-5858-5858-5858-585858585858"), enumName);
        return enumName switch
        {
            "Provider" => target.Replace("\"Provider\":\"Claude\"", "\"Provider\":99"),
            "Placement" => target.Replace("\"Placement\":\"Desktop\"", "\"Placement\":99"),
            "Activation" => target.Replace("\"Activation\":\"AlwaysWhileOpen\"", "\"Activation\":99"),
            "Source" => target.Replace("\"Source\":\"Captured\"", "\"Source\":99"),
            "TitleMatchKind" => target.Replace("\"WindowTitle\":null",
                "\"WindowTitle\":{\"Kind\":99,\"Value\":\"Title\"}"),
            _ => throw new ArgumentOutOfRangeException(nameof(enumName)),
        };
    }

    private static string TitleTargetJson(string kind, string value) =>
        ValidTargetJson(Guid.Parse("59595959-5959-5959-5959-595959595959"), kind)
            .Replace("\"WindowTitle\":null", $"\"WindowTitle\":{{\"Kind\":\"{kind}\",\"Value\":{value}}}");
}

internal sealed class MemoryTargetStorage : ITargetDocumentStorage
{
    public MemoryTargetStorage(string? text = null)
    {
        Text = text;
    }

    public string? Text { get; private set; }
    public Exception? WriteError { get; set; }
    public List<string> PreservedLabels { get; } = new();
    public bool Exists => Text is not null;
    public string DisplayPath => "(memory)";

    public string Read() => Text!;

    public void WriteAtomically(string text)
    {
        if (WriteError is not null) throw WriteError;
        Text = text;
    }

    public void Preserve(string label) => PreservedLabels.Add(label);
}
