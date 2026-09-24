using System;
using Xunit;

namespace UsageBars.Tests;

public class CodexParserTests
{
    /// <summary>
    /// A verbatim capture of a real <c>account/rateLimits/read</c> reply, kept whole rather than
    /// trimmed to the fields under test: the parser's job is to survive the actual payload,
    /// including the parts it ignores.
    /// </summary>
    private const string RealReadResponse = """
    {"id":2,"result":{"rateLimits":{"limitId":"codex","limitName":null,
    "primary":{"usedPercent":9,"windowDurationMins":10080,"resetsAt":1785902973},
    "secondary":null,"credits":{"hasCredits":false,"unlimited":false,"balance":"0"},
    "individualLimit":null,"spendControlReached":false,"planType":"pro","rateLimitReachedType":null},
    "rateLimitsByLimitId":{
      "codex":{"limitId":"codex","limitName":null,"primary":{"usedPercent":9,"windowDurationMins":10080,"resetsAt":1785902973},"secondary":null,"credits":{"hasCredits":false,"unlimited":false,"balance":"0"},"individualLimit":null,"spendControlReached":false,"planType":"pro","rateLimitReachedType":null},
      "codex_bengalfox":{"limitId":"codex_bengalfox","limitName":"GPT-5.3-Codex-Spark","primary":{"usedPercent":0,"windowDurationMins":10080,"resetsAt":1786064376},"secondary":null,"credits":null,"individualLimit":null,"spendControlReached":null,"planType":"pro","rateLimitReachedType":null}},
    "rateLimitResetCredits":{"availableCount":1,"credits":[{"id":"RateLimitResetCredit_6e05","resetType":"codexRateLimits","status":"available","grantedAt":1783963835,"expiresAt":1786555835,"title":"Full reset","description":"one free rate limit reset"}]}}}
    """;

    [Fact]
    public void ParsesARealReadResponse()
    {
        var snapshot = CodexRateLimitParser.ParseReadResponse(RealReadResponse, DateTimeOffset.Now);

        Assert.NotNull(snapshot.Primary);
        Assert.Equal(9, snapshot.Primary!.UsedPct);
        Assert.Equal(91, snapshot.Primary.RemainingPct);
        Assert.Equal(10080, snapshot.Primary.WindowMinutes);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(1785902973).ToLocalTime(),
            snapshot.Primary.ResetsAt);

        Assert.Null(snapshot.Secondary);
        Assert.Equal("pro", snapshot.PlanType);
        Assert.Equal(1, snapshot.ResetCreditsAvailable);
        Assert.True(snapshot.HasValues);
    }

    [Fact]
    public void A10080MinuteWindowIsLabelledSevenDays()
    {
        var snapshot = CodexRateLimitParser.ParseReadResponse(RealReadResponse, DateTimeOffset.Now);
        Assert.Equal("7d", snapshot.Primary!.Label);
    }

    [Theory]
    [InlineData(300, "5h")]
    [InlineData(270, "5h")]
    [InlineData(330, "5h")]
    [InlineData(10080, "7d")]
    [InlineData(9000, "7d")]
    [InlineData(1440, "1d")]
    [InlineData(180, "3h")]
    [InlineData(45, "45m")]
    [InlineData(null, "usage")]
    public void WindowLabelsComeFromTheirDuration(int? minutes, string expected)
    {
        Assert.Equal(expected, new CodexWindow(0, minutes, null).Label);
    }

    [Fact]
    public void CapturesPerModelBuckets()
    {
        var snapshot = CodexRateLimitParser.ParseReadResponse(RealReadResponse, DateTimeOffset.Now);

        Assert.Equal(2, snapshot.Buckets.Count);
        var named = Assert.Single(snapshot.Buckets, b => b.LimitId == "codex_bengalfox");
        Assert.Equal("GPT-5.3-Codex-Spark", named.Display);
        Assert.Equal(0, named.Primary!.UsedPct);
    }

    [Fact]
    public void UsedPercentAboveOneHundredClampsRatherThanFailing()
    {
        // Overage plans can report past 100; a full bar is the right answer, not a parse error.
        const string json = """
        {"id":1,"result":{"rateLimits":{"primary":{"usedPercent":140,"windowDurationMins":300}}}}
        """;

        var snapshot = CodexRateLimitParser.ParseReadResponse(json, DateTimeOffset.Now);

        Assert.Equal(100, snapshot.Primary!.UsedPct);
        Assert.Equal(0, snapshot.Primary.RemainingPct);
    }

    [Fact]
    public void MissingWindowsAreAbsentRatherThanZero()
    {
        const string json = """
        {"id":1,"result":{"rateLimits":{"primary":null,"secondary":null}}}
        """;

        var snapshot = CodexRateLimitParser.ParseReadResponse(json, DateTimeOffset.Now);

        Assert.Null(snapshot.Primary);
        Assert.Null(snapshot.Secondary);
        Assert.False(snapshot.HasValues);
    }

    [Fact]
    public void ParsesASpendLimit()
    {
        const string json = """
        {"id":1,"result":{"rateLimits":{"primary":null,"individualLimit":
          {"limit":"$100.00","remainingPercent":35,"resetsAt":1785902973,"used":"$65.00"}}}}
        """;

        var snapshot = CodexRateLimitParser.ParseReadResponse(json, DateTimeOffset.Now);

        Assert.NotNull(snapshot.SpendLimit);
        Assert.Equal(35, snapshot.SpendLimit!.RemainingPct);
        Assert.Equal("$65.00", snapshot.SpendLimit.Used);
        Assert.Equal("$100.00", snapshot.SpendLimit.Limit);
        Assert.True(snapshot.HasValues);
    }

    /// <summary>
    /// The server documents pushed updates as sparse. Replacing rather than merging would blank a
    /// bar every time the other one moved.
    /// </summary>
    [Fact]
    public void SparseUpdateKeepsTheWindowItDoesNotMention()
    {
        var current = CodexRateLimitParser.ParseReadResponse("""
        {"id":1,"result":{"rateLimits":{
          "primary":{"usedPercent":10,"windowDurationMins":300},
          "secondary":{"usedPercent":20,"windowDurationMins":10080}}}}
        """, DateTimeOffset.Now);

        var merged = CodexRateLimitParser.MergeNotification(current, """
        {"method":"account/rateLimits/updated","params":{"rateLimits":
          {"primary":{"usedPercent":15,"windowDurationMins":300}}}}
        """, DateTimeOffset.Now);

        Assert.Equal(15, merged.Primary!.UsedPct);
        Assert.NotNull(merged.Secondary);
        Assert.Equal(20, merged.Secondary!.UsedPct);
    }

    [Fact]
    public void NotificationWithTheWrongMethodIsRejected()
    {
        Assert.Throws<FormatException>(() => CodexRateLimitParser.MergeNotification(
            null,
            """{"method":"something/else","params":{"rateLimits":{}}}""",
            DateTimeOffset.Now));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"id":1,"result":{}}""")]
    [InlineData("""{"id":1,"result":{"rateLimits":{"primary":{"windowDurationMins":300}}}}""")]
    [InlineData("""{"id":1,"result":{"rateLimits":{"primary":{"usedPercent":"lots"}}}}""")]
    public void MalformedPayloadsFailLoudly(string json)
    {
        Assert.Throws<FormatException>(
            () => CodexRateLimitParser.ParseReadResponse(json, DateTimeOffset.Now));
    }

    [Fact]
    public void NextResetIsTheSoonestAcrossWindows()
    {
        var soon = DateTimeOffset.Now.AddHours(2);
        var later = DateTimeOffset.Now.AddDays(3);

        var snapshot = new CodexSnapshot(
            new CodexWindow(10, 300, later),
            new CodexWindow(20, 10080, soon),
            DateTimeOffset.Now);

        Assert.Equal(soon, snapshot.NextReset);
    }
}
