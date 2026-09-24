using System;
using System.Linq;
using Xunit;

namespace UsageBars.Tests;

public class TargetRuleImpactTests
{
    [Fact]
    public void ExactIdentityOutranksApplyAndOverlap()
    {
        var window = Window("terminal", "frame", "Codex");
        var draft = Target("Draft", "terminal", "frame", ProviderId.Codex);
        var exact = draft with { Id = Guid.NewGuid(), Name = "Exact", Enabled = false };

        var row = Assert.Single(TargetRuleImpactAnalyzer.Analyze(window, draft, new[] { exact }, null));

        Assert.Equal(TargetRuleRelationship.ExactIdentity, row.Relationship);
        Assert.True(row.AppliesNow);
        Assert.True(row.ExactIdentity);
    }

    [Fact]
    public void TitleMismatchCanStillBeReportedAsOverlap()
    {
        var window = Window("terminal", "frame", "Codex A");
        var draft = Target("A", "terminal", "frame", ProviderId.Codex) with
        {
            WindowTitle = new(TitleMatchKind.Prefix, "Codex A"),
        };
        var other = Target("B", "terminal", "frame", ProviderId.Codex) with
        {
            Id = Guid.NewGuid(),
            WindowTitle = new(TitleMatchKind.Regex, "^Codex B"),
        };

        var row = Assert.Single(TargetRuleImpactAnalyzer.Analyze(window, draft, new[] { other }, null));

        Assert.False(row.AppliesNow);
        Assert.Equal(TargetRuleRelationship.OverlapsDraft, row.Relationship);
    }

    [Theory]
    [InlineData("rider")]
    [InlineData("sunawtframe")]
    [InlineData("prefix")]
    [InlineData("workspace")]
    public void SearchCoversEveryApprovedRuleField(string query)
    {
        var target = Target("Rider project", "rider64", "SunAwtFrame", ProviderId.Claude) with
        {
            WindowTitle = new(TitleMatchKind.Prefix, "Workspace"),
        };

        Assert.Single(TargetRuleImpactAnalyzer.Analyze(
            Window("other", "other", "other"),
            Target("Draft", "new", "new", ProviderId.Codex),
            new[] { target }, query));
    }

    [Fact]
    public void SearchMatchesFullAndPartialRuleId()
    {
        var target = Target("Rule", "app", "frame", ProviderId.Claude) with
        {
            Id = Guid.Parse("12345678-90ab-cdef-1234-567890abcdef"),
        };
        var unrelatedWindow = Window("other", "other", "other");
        var unrelatedDraft = Target("Draft", "new", "new", ProviderId.Codex);

        Assert.Single(TargetRuleImpactAnalyzer.Analyze(
            unrelatedWindow, unrelatedDraft, new[] { target }, "12345678"));
        Assert.Single(TargetRuleImpactAnalyzer.Analyze(
            unrelatedWindow, unrelatedDraft, new[] { target }, target.Id.ToString()));
        Assert.Single(TargetRuleImpactAnalyzer.Analyze(
            unrelatedWindow, unrelatedDraft, new[] { target }, target.Id.ToString("N")));
    }

    [Theory]
    [InlineData("rider project", true)]
    [InlineData("90ab-cdef", true)]
    [InlineData("90abcdef", true)]
    [InlineData("rider64", true)]
    [InlineData("sunawt", true)]
    [InlineData("pre", true)]
    [InlineData("workspace", true)]
    [InlineData("cla", true)]
    [InlineData("omp", true)]
    [InlineData("codex.exe", true)]
    [InlineData("activity-runner", false)]
    [InlineData("unrelated", false)]
    public void SearchMatchesEveryDocumentedFieldCaseInsensitively(string query, bool expected)
    {
        var target = Target("Rider Project", "rider64", "SunAwtFrame", ProviderId.Claude) with
        {
            Id = Guid.Parse("12345678-90ab-cdef-1234-567890abcdef"),
            WindowTitle = new(TitleMatchKind.Prefix, "Workspace"),
            Providers = new System.Collections.Generic.List<ProviderBinding>
            {
                new(ProviderId.Claude),
            },
            ActivityProcessNames = new() { "omp", "codex" },
        };

        Assert.Equal(expected, TargetRuleSearch.Matches(target, query));
    }

    [Fact]
    public void OverlappingRulesExplainWhenTheirActivityFiltersDiffer()
    {
        var draft = Target("Draft", "terminal", "frame", ProviderId.Codex) with
        {
            ActivityProcessNames = new() { "codex" },
            Activation = TargetActivationPolicy.RequireActivityProcess,
        };
        var other = draft with
        {
            Id = Guid.NewGuid(),
            ActivityProcessNames = new() { "omp" },
        };

        var row = Assert.Single(TargetRuleImpactAnalyzer.Analyze(
            Window("other", "other", "other"), draft, new[] { other }, null));

        Assert.Equal(TargetRuleRelationship.OverlapsDraft, row.Relationship);
        Assert.Equal("activity filters differ", row.ImpactText);
    }

    [Fact]
    public void UnrelatedRulesStayHiddenWithoutSearch()
    {
        var unrelated = Target("Elsewhere", "other", "other", ProviderId.Claude);

        var rows = TargetRuleImpactAnalyzer.Analyze(
            Window("app", "frame", "Project"),
            Target("Draft", "app", "frame", ProviderId.Codex),
            new[] { unrelated }, null);

        Assert.Empty(rows);
    }

    [Fact]
    public void DefaultCandidateIsDeterministicAcrossEnabledSpecificityRevisionAndId()
    {
        var window = Window("app", "frame", "Project");
        var draft = Target("Draft", "app", "frame", ProviderId.Claude);
        var older = Target("Older", "app", "frame", ProviderId.Claude) with
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Revision = 4,
        };
        var newer = older with
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Name = "Newer",
            Revision = 5,
        };

        var selected = TargetRuleImpactAnalyzer.StrongestApplicable(
            TargetRuleImpactAnalyzer.Analyze(window, draft, new[] { older, newer }, null));

        Assert.Equal(newer.Id, selected!.Target.Id);
    }

    [Fact]
    public void EnabledRuleOutranksNewerDisabledRuleAtSameSpecificity()
    {
        var window = Window("app", "frame", "Project");
        var draft = Target("Draft", "app", "frame", ProviderId.Claude);
        var enabled = Target("Enabled", "app", "frame", ProviderId.Claude) with { Revision = 1 };
        var disabled = enabled with
        {
            Id = Guid.NewGuid(), Name = "Disabled", Enabled = false, Revision = 99,
        };

        var selected = TargetRuleImpactAnalyzer.StrongestApplicable(
            TargetRuleImpactAnalyzer.Analyze(window, draft, new[] { disabled, enabled }, null));

        Assert.Equal(enabled.Id, selected!.Target.Id);
    }

    private static WindowSnapshot Window(string process, string windowClass, string title) =>
        new(new IntPtr(42), 7, process, windowClass, title, true);

    private static TargetDefinition Target(
        string name, string process, string windowClass, ProviderId provider) =>
        TargetDefinition.Captured(name, process, windowClass,
            new[] { provider == ProviderId.Claude ? ProviderBinding.Claude : ProviderBinding.Codex },
            TargetPlacementKind.Terminal, TargetActivationPolicy.AlwaysWhileOpen, true, 0);
}
