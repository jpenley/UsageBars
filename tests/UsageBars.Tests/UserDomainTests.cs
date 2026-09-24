using System;
using Xunit;

namespace UsageBars.Tests;

/// <summary>
/// A configured request-credit link may be an internal page that only resolves inside one
/// company's network, and this is a portable exe. These pin who is offered it.
/// </summary>
public class UserDomainTests
{
    [Fact]
    public void TheNetbiosDomainMatches()
    {
        Assert.True(UserDomain.Matches("contoso", "CONTOSO", null));
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        Assert.True(UserDomain.Matches("ConToso", "contoso", null));
        Assert.True(UserDomain.Matches("contoso", "CONTOSO", null));
    }

    [Theory]
    [InlineData("contoso.com")]
    [InlineData("corp.contoso.com")]
    [InlineData("CONTOSO.COM")]
    [InlineData("ad.corp.contoso.local")]
    public void AnyLabelOfTheDnsDomainMatches(string dns)
    {
        Assert.True(UserDomain.Matches("contoso", "SOMEPC", dns));
    }

    /// <summary>
    /// Compared label by label rather than by substring, or a lookalike domain would be treated
    /// as the real one.
    /// </summary>
    [Theory]
    [InlineData("notcontoso.com")]
    [InlineData("contoso-external.com")]
    [InlineData("mycontoso.net")]
    public void LookalikeDomainsDoNotMatch(string dns)
    {
        Assert.False(UserDomain.Matches("contoso", "SOMEPC", dns));
    }

    [Fact]
    public void AHomeMachineDoesNotMatch()
    {
        // Not domain-joined: UserDomainName reports the machine name and there is no DNS domain.
        Assert.False(UserDomain.Matches("contoso", "DESKTOP-4B2C1", null));
    }

    /// <summary>An unset expectation means "no restriction", so a self-hosted URL just works.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoConfiguredDomainMeansNoRestriction(string? expected)
    {
        Assert.True(UserDomain.Matches(expected, "DESKTOP-4B2C1", null));
    }

    /// <summary>
    /// Workplace-specific values live in the local config file, never in the code defaults.
    /// </summary>
    [Fact]
    public void TheDefaultConfigCarriesNoWorkplaceLinkOrDomain()
    {
        var config = new Config();

        Assert.Equal("", config.RequestCreditUrl);
        Assert.Equal("", config.RequestCreditDomain);
    }
}

public class CreditIconGatingTests
{
    private const string CreditUrl = "https://example.com/credit";

    private static UsageSnapshot NearlyEmpty() => new()
    {
        // 2% remaining on both windows, well under the default 10% floor.
        FiveHourUsedPct = 98,
        SevenDayUsedPct = 98,
        SampleTime = DateTimeOffset.Now,
        SourceLabel = "claude-msix-history",
    };

    private static Config OffDomain() => new()
    {
        RequestCreditUrl = CreditUrl,
        RequestCreditDomain = "a-domain-nobody-is-in",
    };

    private static Config NoRestriction() => new()
    {
        RequestCreditUrl = CreditUrl,
        RequestCreditDomain = "",
    };

    [Fact]
    public void OnDomainUsersAreOfferedTheLink()
    {
        var config = NoRestriction();

        var model = UsageModel.Build(NearlyEmpty(), null, new UsageHistory(), config);

        Assert.Equal(PillAction.RequestCredit, model.Action);
        Assert.Equal(CreditUrl, model.ActionUrl);
    }

    [Fact]
    public void OffDomainUsersGetNoIconAndNoLink()
    {
        var model = UsageModel.Build(NearlyEmpty(), null, new UsageHistory(), OffDomain());

        Assert.Equal(PillAction.None, model.Action);
        Assert.Null(model.ActionUrl);
    }

    /// <summary>
    /// With nothing configured there is nowhere to send anyone, so no icon — but the bar still
    /// warns, exactly as it does off-domain.
    /// </summary>
    [Fact]
    public void NoConfiguredLinkMeansNoIconButStillAWarning()
    {
        var model = UsageModel.Build(NearlyEmpty(), null, new UsageHistory(), new Config());

        Assert.Equal(PillAction.None, model.Action);
        Assert.Null(model.ActionUrl);
        Assert.True(model.Warned);
        Assert.Contains("no RequestCreditUrl", model.WarnReason);
    }

    /// <summary>
    /// The point of the whole exercise: withholding the workplace link must not withhold the
    /// warning. The bar still goes red, which is what actually tells you you're nearly out.
    /// </summary>
    [Fact]
    public void OffDomainUsersStillGetTheWarning()
    {
        var model = UsageModel.Build(NearlyEmpty(), null, new UsageHistory(), OffDomain());

        Assert.True(model.Warned);
        Assert.Contains("withheld", model.WarnReason);
    }

    [Fact]
    public void TheWarningIsIdenticalOnAndOffDomain()
    {
        var on = UsageModel.Build(NearlyEmpty(), null, new UsageHistory(), NoRestriction());
        var off = UsageModel.Build(NearlyEmpty(), null, new UsageHistory(), OffDomain());

        Assert.Equal(on.Warned, off.Warned);
        Assert.Equal(on.Bars.Count, off.Bars.Count);
    }

    [Fact]
    public void PlentyRemainingWarnsNobodyEitherWay()
    {
        var healthy = new UsageSnapshot
        {
            FiveHourUsedPct = 5,
            SevenDayUsedPct = 5,
            SampleTime = DateTimeOffset.Now,
        };

        Assert.False(UsageModel.Build(healthy, null, new UsageHistory(), NoRestriction()).Warned);
        Assert.False(UsageModel.Build(healthy, null, new UsageHistory(), OffDomain()).Warned);
    }

    /// <summary>
    /// The install prompts are links to public documentation, not workplace resources, and must
    /// never be gated.
    /// </summary>
    [Fact]
    public void TheInstallPromptIsNeverGated()
    {
        var claude = UsageModel.Build(
            UsageSnapshot.Failed("not found"),
            ClaudeCodeUsage.NotInstalled(),
            new UsageHistory(),
            OffDomain());

        Assert.Equal(PillAction.InstallClaudeCode, claude.Action);
        Assert.Equal(ClaudeCli.InstallUrl, claude.ActionUrl);

        var codex = UsageModel.BuildCodex(null, codexInstalled: false, error: null, OffDomain());

        Assert.Equal(PillAction.InstallCodex, codex.Action);
        Assert.Equal(CodexCli.InstallUrl, codex.ActionUrl);
    }

    [Fact]
    public void TheCodexCreditLinkIsGatedToo()
    {
        var config = OffDomain();
        config.LowThresholdPct = 50;
        config.CodexRequestCreditUrl = "https://example.com/topup";

        var model = UsageModel.BuildCodex(
            new CodexSnapshot(new CodexWindow(95, 300, null), null, DateTimeOffset.Now),
            codexInstalled: true, error: null, config);

        Assert.Equal(PillAction.None, model.Action);
        Assert.True(model.Warned);
    }
}
