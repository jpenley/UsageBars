using System;
using System.IO;
using Xunit;

namespace UsageBars.Tests;

/// <summary>
/// Exercises the real reader end to end by pointing USAGEBARS_FILE at a temp file, which is the
/// same override used for support. Serialized because that variable is process-wide.
/// </summary>
[Collection("env")]
public class UsageSourceTests : IDisposable
{
    private readonly string _dir;
    private readonly string? _previous;

    public UsageSourceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "usagebars-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _previous = Environment.GetEnvironmentVariable(UsageSource.OverrideVariable);
    }

    private UsageSnapshot ReadJson(string json)
    {
        var path = Path.Combine(_dir, "plan-usage-history.json");
        File.WriteAllText(path, json);
        Environment.SetEnvironmentVariable(UsageSource.OverrideVariable, path);
        return UsageSource.Read();
    }

    // Built by concatenation rather than a raw interpolated string: JSON's runs of closing braces
    // collide with the {{ }} interpolation delimiters and make the literal unreadable.
    private static string SampleAt(DateTimeOffset at, double fh, double sd) =>
        "{\"t\":" + at.ToUnixTimeMilliseconds() +
        ",\"org\":\"org-1\",\"u\":{\"fh\":" + fh + ",\"sd\":" + sd + "}}";

    private static string Document(params string[] samples) =>
        "{\"version\":2,\"samples\":[" + string.Join(",", samples) + "]}";

    [Fact]
    public void ParsesVersionTwoNestedUnderU()
    {
        var now = DateTimeOffset.Now;
        var snapshot = ReadJson(Document(SampleAt(now.AddMinutes(-5), 10, 20), SampleAt(now, 12, 22)));

        Assert.Null(snapshot.Error);
        Assert.Equal(12, snapshot.FiveHourUsedPct);
        Assert.Equal(22, snapshot.SevenDayUsedPct);
        Assert.Equal("org-1", snapshot.Org);
        Assert.True(snapshot.HasValues);
    }

    [Fact]
    public void ParsesVersionOneOnTheSampleItself()
    {
        var now = DateTimeOffset.Now;
        var snapshot = ReadJson(
            "{\"version\":1,\"samples\":[{\"t\":" + now.ToUnixTimeMilliseconds() +
            ",\"fh\":30,\"sd\":40}]}");

        Assert.Equal(30, snapshot.FiveHourUsedPct);
        Assert.Equal(40, snapshot.SevenDayUsedPct);
    }

    [Fact]
    public void KeepsTheSampleWithTheLargestTimestampRegardlessOfOrder()
    {
        var now = DateTimeOffset.Now;
        var snapshot = ReadJson(Document(SampleAt(now, 99, 99), SampleAt(now.AddHours(-1), 1, 1)));

        Assert.Equal(99, snapshot.FiveHourUsedPct);
    }

    [Theory]
    [InlineData("101")]
    [InlineData("-1")]
    public void OutOfRangePercentagesAreRejected(string value)
    {
        var now = DateTimeOffset.Now;
        var snapshot = ReadJson(
            "{\"version\":2,\"samples\":[{\"t\":" + now.ToUnixTimeMilliseconds() +
            ",\"u\":{\"fh\":" + value + ",\"sd\":20}}]}");

        Assert.Null(snapshot.FiveHourUsedPct);
        Assert.Equal(20, snapshot.SevenDayUsedPct);
    }

    [Fact]
    public void EmptyUtilizationIsNotAFailure()
    {
        // What a usage-based or enterprise seat actually looks like: samples written, u empty.
        var now = DateTimeOffset.Now;
        var snapshot = ReadJson(
            "{\"version\":2,\"samples\":[{\"t\":" + now.ToUnixTimeMilliseconds() + ",\"u\":{}}]}");

        Assert.False(snapshot.HasValues);
        Assert.Null(snapshot.Error);
    }

    [Theory]
    [InlineData("""{"version":3,"samples":[]}""")]
    [InlineData("""{"samples":[]}""")]
    [InlineData("""{"version":2}""")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void UnusableFilesReportAnError(string json)
    {
        var snapshot = ReadJson(json);

        Assert.NotNull(snapshot.Error);
        Assert.False(snapshot.HasValues);
    }

    [Fact]
    public void DerivesResetsFromTheSampleSeries()
    {
        var now = DateTimeOffset.Now;
        // Climb, reset to zero three hours ago, resume immediately.
        var snapshot = ReadJson(Document(
            SampleAt(now.AddHours(-4), 20, 20),
            SampleAt(now.AddHours(-3), 0, 20),
            SampleAt(now.AddHours(-2.9), 2, 20),
            SampleAt(now, 12, 22)));

        Assert.NotNull(snapshot.FiveHourResetsAt);
        // Window resumed 2.9 h ago, so it closes 5 h after that.
        var expected = now.AddHours(-2.9).AddHours(5);
        Assert.True(Math.Abs((snapshot.FiveHourResetsAt!.Value - expected).TotalMinutes) < 1);
    }

    [Fact]
    public void StalenessUsesTheConfiguredThreshold()
    {
        var now = DateTimeOffset.Now;
        var snapshot = ReadJson(Document(SampleAt(now.AddMinutes(-20), 10, 20)));

        Assert.True(snapshot.IsStaleAfter(TimeSpan.FromMinutes(15)));
        Assert.False(snapshot.IsStaleAfter(TimeSpan.FromMinutes(30)));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(UsageSource.OverrideVariable, _previous);
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
    }
}

[CollectionDefinition("env", DisableParallelization = true)]
public class EnvCollection
{
}
