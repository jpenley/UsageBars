using Xunit;

namespace UsageBars.Tests;

public class TargetActivityTests
{
    [Theory]
    [InlineData(" codex.exe ", "codex")]
    [InlineData("CODEX", "CODEX")]
    [InlineData("code - insiders.exe", "code - insiders")]
    public void NormalizeRemovesOnlyWhitespaceAndExeSuffix(string input, string expected)
    {
        Assert.True(ActivityProcessName.TryNormalize(input, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("C:\\tools\\codex.exe")]
    [InlineData("../codex")]
    [InlineData("bad:name")]
    public void NormalizeRejectsBlankPathsAndInvalidBaseNames(string input) =>
        Assert.False(ActivityProcessName.TryNormalize(input, out _));

    [Fact]
    public void NormalizeManyDeduplicatesCaseInsensitivelyAndSorts()
    {
        Assert.Equal(new[] { "codex", "omp" },
            ActivityProcessName.NormalizeMany(new[] { "omp.exe", "CODEX", "codex.exe", " omp " }));
    }
}
