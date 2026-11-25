using System;
using System.Linq;
using Xunit;

namespace UsageBars.Tests;

public class AntigravityUsageTests
{
    private const string SampleStatusLineJson = """
    {
      "product": "antigravity",
      "version": "1.0.0",
      "plan_tier": "Example Plan",
      "model": {
        "id": "gemini-example",
        "display_name": "Gemini Example",
        "effort": "high"
      },
      "quota": {
        "3p-5h": {
          "remaining_fraction": 1,
          "reset_time": "1970-01-01T00:00:00Z",
          "reset_in_seconds": 0
        },
        "3p-weekly": {
          "remaining_fraction": 1,
          "reset_time": "1970-01-01T00:00:00Z",
          "reset_in_seconds": 0
        },
        "gemini-5h": {
          "remaining_fraction": 0.85,
          "reset_time": "2026-01-15T12:00:00Z",
          "reset_in_seconds": 14400
        },
        "gemini-weekly": {
          "remaining_fraction": 0.25,
          "reset_time": "2026-01-20T18:00:00Z",
          "reset_in_seconds": 432000
        }
      }
    }
    """;

    [Fact]
    public void ParsesDocumentedStatusLineQuotaPayload()
    {
        var readAt = DateTimeOffset.Now;
        var snap = AntigravityUsageParser.Parse(SampleStatusLineJson, readAt);

        Assert.NotNull(snap);
        Assert.True(snap!.HasValues);
        Assert.Equal("Example Plan", snap.PlanTier);
        Assert.Equal("Gemini Example", snap.ModelName);

        var g5 = snap.Bucket("gemini-5h")!;
        Assert.Equal(85.0, g5.RemainingPct!.Value, 4);
        Assert.Equal(DateTimeOffset.Parse("2026-01-15T12:00:00Z"), g5.EffectiveResetsAt);

        var g7 = snap.Bucket("gemini-weekly")!;
        Assert.Equal(25.0, g7.RemainingPct!.Value, 4);
        Assert.Equal(DateTimeOffset.Parse("2026-01-20T18:00:00Z"), g7.EffectiveResetsAt);

        // Untouched 3p pools emit Unix epoch + 0 seconds -> EffectiveResetsAt must be null.
        Assert.Null(snap.Bucket("3p-5h")!.EffectiveResetsAt);
        Assert.Null(snap.Bucket("3p-weekly")!.EffectiveResetsAt);
    }

    [Fact]
    public void BuildAntigravityHidesUntouched3pPoolFromPillButShowsWhenConsumed()
    {
        var untouched = AntigravityUsageParser.Parse(SampleStatusLineJson, DateTimeOffset.Now)!;
        var model = UsageModel.BuildAntigravity(untouched, installed: true, null, new Config());

        Assert.Equal(new[] { "5h", "7d" }, model.ForPill().Bars.Select(b => b.Label).ToArray());
        Assert.Contains("Example Plan", model.SourceText);

        var consumedJson = SampleStatusLineJson.Replace("\"remaining_fraction\": 1", "\"remaining_fraction\": 0.72");
        var consumed = AntigravityUsageParser.Parse(consumedJson, DateTimeOffset.Now)!;
        var consumedModel = UsageModel.BuildAntigravity(consumed, installed: true, null, new Config());

        Assert.Equal(new[] { "5h", "7d", "3p 5h", "3p 7d" },
            consumedModel.ForPill().Bars.Select(b => b.Label).ToArray());
    }

    [Fact]
    public void BuildAntigravitySurfacesInstallActionWhenCliMissing()
    {
        var model = UsageModel.BuildAntigravity(null, installed: false, "Antigravity CLI not found", new Config());

        Assert.Equal(PillAction.InstallAntigravity, model.Action);
        Assert.Equal(AntigravityCli.InstallUrl, model.ActionUrl);
        Assert.True(model.Stale);
    }
}
