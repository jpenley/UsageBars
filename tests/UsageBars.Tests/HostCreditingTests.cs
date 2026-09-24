using System;
using System.Text.RegularExpressions;
using Xunit;

namespace UsageBars.Tests;

/// <summary>
/// Which window may be credited with hosting a CLI session. Every case here is a chain measured
/// on a real desktop, not an invented one.
/// </summary>
public class HostCreditingTests
{
    private const uint OwnPid = 999;

    /// <summary>
    /// The chain that put the Codex pill on a browser window:
    /// <c>codex &lt;- node &lt;- cmd &lt;- node &lt;- node &lt;- cmd &lt;- firefox</c>.
    /// A build-tool chain launched from Firefox is not a session anyone is looking at.
    /// </summary>
    private static ProcessTree FirefoxChain() => ProcessTree.FromMap(new[]
    {
        (18612u, 18824u, "codex"),
        (18824u, 38736u, "node"),
        (38736u, 38032u, "cmd"),
        (38032u, 61796u, "node"),
        (61796u, 57324u, "node"),
        (57324u, 66168u, "cmd"),
        (66168u, 11920u, "firefox"),
        (11920u, 0u, "explorer"),
    });

    [Fact]
    public void ABrowserSixLevelsAboveTheCliIsNotCredited()
    {
        var tree = FirefoxChain();

        Assert.False(tree.HasProcessUnder(66168, "codex", OwnPid));
    }

    /// <summary>An integrated terminal is short, and must still be credited.</summary>
    [Fact]
    public void AnEditorsIntegratedTerminalIsStillCredited()
    {
        var tree = ProcessTree.FromMap(new[]
        {
            (100u, 200u, "claude"),
            (200u, 300u, "pwsh"),
            (300u, 400u, "Code"),
            (400u, 0u, "explorer"),
        });

        Assert.True(tree.HasProcessUnder(300, "claude", OwnPid));
    }

    [Fact]
    public void ADirectChildIsCredited()
    {
        var tree = ProcessTree.FromMap(new[]
        {
            (100u, 200u, "codex"),
            (200u, 0u, "WindowsTerminal"),
        });

        Assert.True(tree.HasProcessUnder(200, "codex", OwnPid));
    }

    /// <summary>
    /// explorer.exe is an ancestor of nearly every user process, so an unguarded walk made the
    /// desktop and every File Explorer window qualify.
    /// </summary>
    [Fact]
    public void TheShellIsNeverCredited()
    {
        var tree = ProcessTree.FromMap(new[]
        {
            (100u, 200u, "claude"),
            (200u, 300u, "pwsh"),
            (300u, 0u, "explorer"),
        });

        Assert.False(tree.HasProcessUnder(300, "claude", OwnPid));
    }

    /// <summary>
    /// UsageBars spawns the Codex app-server itself. Measured chain:
    /// <c>codex &lt;- UsageBars &lt;- explorer</c>. Reacting to it would be self-inflicted.
    /// </summary>
    [Fact]
    public void OurOwnChildSessionIsIgnored()
    {
        var tree = ProcessTree.FromMap(new[]
        {
            (74768u, OwnPid, "codex"),
            (OwnPid, 11920u, "UsageBars"),
            (11920u, 0u, "explorer"),
        });

        Assert.False(tree.HasProcessUnder(OwnPid, "codex", OwnPid));
        Assert.False(tree.AnyRunning("codex", OwnPid));
    }

    [Fact]
    public void AnUnrelatedSessionStillCountsAsRunning()
    {
        var tree = ProcessTree.FromMap(new[]
        {
            (100u, 200u, "codex"),
            (200u, 0u, "WindowsTerminal"),
        });

        Assert.True(tree.AnyRunning("codex", OwnPid));
    }

    [Fact]
    public void TheDepthCapIsTightEnoughToExcludeTheMeasuredBrowserChain()
    {
        // Documents the boundary rather than leaving it implicit: the browser sat six levels up.
        Assert.True(ProcessTree.MaxHostDepth < 6);
        Assert.True(ProcessTree.MaxHostDepth >= 3, "an editor + shell chain needs at least 3");
    }
}

public class TitlePatternTests
{
    /// <summary>
    /// Both providers' patterns must be equally specific. The Codex one started as a bare
    /// <c>\bcodex\b</c>, which matches any terminal whose title merely contains the word.
    /// </summary>
    [Theory]
    [InlineData("codex", true)]
    [InlineData("codex cli", true)]
    [InlineData("codex session", true)]
    [InlineData("C:\\src\\codex — pwsh", false)]
    [InlineData("~/work/codex", false)]
    [InlineData("git log codex-branch", false)]
    [InlineData("Windows PowerShell", false)]
    [InlineData("codexterous", false)]
    public void CodexTitlePatternDoesNotMatchIncidentalMentions(string title, bool expected)
    {
        Assert.Equal(expected, Regex.IsMatch(title, new Config().CodexTitlePattern));
    }

    [Theory]
    [InlineData("✳ Claude Code", true)]
    [InlineData("Claude Code", true)]
    [InlineData("claude", true)]
    [InlineData("~/src/claude — bash", false)]
    [InlineData("some-claude-notes.md - Notepad", false)]
    public void ClaudeTitlePatternDoesNotMatchIncidentalMentions(string title, bool expected)
    {
        Assert.Equal(expected, Regex.IsMatch(title, new Config().TerminalTitlePattern));
    }

    /// <summary>
    /// Claude Code animates the terminal title with a braille spinner while it is working, so the
    /// title only reads "✳ ..." when it is idle. Measured on a live session:
    /// <c>"⠐ Access previous session from different folder"</c> (U+2810). Matching "✳" alone made
    /// title detection drop out for the entire duration of every task, leaving the pill to be
    /// found by process ancestry or not at all.
    /// </summary>
    [Theory]
    [InlineData("⠐ Access previous session from different folder", true)]
    [InlineData("⠋ Building the thing", true)]
    [InlineData("⣿ Working", true)]
    [InlineData("✳ Claude Code", true)]
    [InlineData("someuser", false)]
    [InlineData("pwsh", false)]
    public void ClaudeTitlePatternSurvivesTheSpinnerFrames(string title, bool expected)
    {
        Assert.Equal(expected, Regex.IsMatch(title, new Config().TerminalTitlePattern));
    }

    /// <summary>
    /// Evidence has to rank: title beats ancestry.
    ///
    /// The failure this encodes was measured, not imagined. With a Claude Code session in one
    /// Windows Terminal window and a Codex session in another, the log showed both providers
    /// attaching to the *same* window in the same millisecond — Claude by title, Codex by ancestry
    /// — because WT runs every window in one process and ancestry cannot tell them apart. The
    /// Codex pill sat on the Claude window while the window actually running Codex stayed bare.
    /// </summary>
    [Fact]
    public void TitleEvidenceOutranksAncestry()
    {
        Assert.True(HostEvidence.Title < HostEvidence.Ancestry);
        Assert.True(HostEvidence.Desktop < HostEvidence.Ancestry);
    }

    /// <summary>
    /// The Codex CLI sets no terminal title at all — a live session's Windows Terminal window read
    /// "someuser", the shell's own title. Its pattern therefore almost never fires, and Codex
    /// terminals are found by process ancestry instead. Recorded here so the pattern is never
    /// mistaken for the mechanism that actually works.
    /// </summary>
    [Fact]
    public void ARealCodexSessionsTerminalTitleDoesNotMatch()
    {
        Assert.DoesNotMatch(new Config().CodexTitlePattern, "someuser");
    }
}
