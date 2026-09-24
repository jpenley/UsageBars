using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Text.Json;
using Xunit;

namespace UsageBars.Tests;

/// <summary>
/// The commented file shipped beside the exe is parsed with the same tolerant reader the app uses.
/// A typo there fails silently — the app falls back to built-in defaults and the user's settings
/// are quietly ignored — so it is worth asserting the shipped artifact actually loads.
/// </summary>
public class DefaultConfigTests
{
    private static string DefaultConfigPath()
    {
        // Walk up from the test binary to the repository root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "UsageBars.config.default.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("could not locate UsageBars.config.default.json");
    }

    private static string ReadmePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "README.md");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("could not locate README.md");
    }

    [Fact]
    public void TheShippedDefaultConfigParses()
    {
        var json = File.ReadAllText(DefaultConfigPath());

        var config = JsonSerializer.Deserialize<Config>(json, new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        Assert.NotNull(config);
    }

    /// <summary>
    /// Every shipped value must already be in range. An adjustment here would mean the documented
    /// default and the effective default disagree.
    /// </summary>
    [Fact]
    public void TheShippedDefaultConfigNeedsNoClamping()
    {
        var json = File.ReadAllText(DefaultConfigPath());
        var config = JsonSerializer.Deserialize<Config>(json, new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        })!;

        config.Normalize();

        Assert.Empty(config.Adjustments);
    }

    [Fact]
    public void TheShippedDefaultsMatchTheBuiltInDefaults()
    {
        var json = File.ReadAllText(DefaultConfigPath());
        var shipped = JsonSerializer.Deserialize<Config>(json, new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        })!;
        var builtIn = new Config();

        // Spot-checks the values most likely to drift between the file and the code.
        Assert.Equal(builtIn.TitleBarHeight, shipped.TitleBarHeight);
        Assert.Equal(builtIn.RightReservePx, shipped.RightReservePx);
        Assert.Equal(builtIn.AmberAtPercent, shipped.AmberAtPercent);
        Assert.Equal(builtIn.RedAtPercent, shipped.RedAtPercent);
        Assert.Equal(builtIn.LowThresholdPct, shipped.LowThresholdPct);
        Assert.Equal(builtIn.CodexRefreshMinutes, shipped.CodexRefreshMinutes);
        Assert.Equal(builtIn.UseConfiguredResetDay, shipped.UseConfiguredResetDay);
        Assert.Equal(builtIn.TerminalTitlePattern, shipped.TerminalTitlePattern);
        Assert.Equal(builtIn.CodexTitlePattern, shipped.CodexTitlePattern);
    }

    [Fact]
    public void LegacyTargetingFieldsAreDocumentedAsFirstRunSeedInputs()
    {
        var json = File.ReadAllText(DefaultConfigPath()).Replace("\r\n", "\n");
        var blocks = Regex.Split(json, "\n\\s*\n");

        foreach (var key in new[]
                 {
                     "AttachToClaudeCodeTerminals", "AttachToCodexTerminals",
                     "AlwaysShowOnIdes",
                 })
        {
            var block = Assert.Single(blocks, candidate =>
                candidate.Contains($"\"{key}\"", StringComparison.Ordinal));
            Assert.Matches("(?i)\\b(first-run|seed(?:ing|ed|-only)?)\\b", block);
        }
    }

    [Fact]
    public void LegacyTitlePatternsAreDocumentedAsCaptureTimeHintsNotLiveRules()
    {
        var json = File.ReadAllText(DefaultConfigPath()).Replace("\r\n", "\n");
        var blocks = Regex.Split(json, "\n\\s*\n");
        var block = Assert.Single(blocks, candidate =>
            candidate.Contains("\"TerminalTitlePattern\"", StringComparison.Ordinal)
            && candidate.Contains("\"CodexTitlePattern\"", StringComparison.Ordinal));

        Assert.Contains("capture-time", block, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not", block, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("live", block, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadmeDescribesLegacyTitlePatternsAsCaptureTimeHintsNotSeededRules()
    {
        var readme = File.ReadAllText(ReadmePath());

        Assert.DoesNotContain("matched by the seeded `TerminalTitlePattern`", readme,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("copied into initial Claude terminal targets", readme,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("copied into initial Codex terminal targets", readme,
            StringComparison.OrdinalIgnoreCase);
        Assert.Matches("(?i)`TerminalTitlePattern`[^\\r\\n]*capture-time", readme);
        Assert.Matches("(?i)`CodexTitlePattern`[^\\r\\n]*capture-time", readme);
    }
}
