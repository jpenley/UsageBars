using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Xunit;

namespace UsageBars.Tests;

public class TitleRuleEditorControlTests
{
    [Fact]
    public void DisabledEditorReturnsNoRule()
    {
        using var control = new TitleRuleEditorControl();

        Assert.Null(control.Rule);
        Assert.Empty(control.ValidateRule());
    }

    [Theory]
    [InlineData(0, "Project")]
    [InlineData(1, "WorldGen")]
    [InlineData(2, "^WorldGen")]
    public void ExistingLiteralPrefixAndRegexRulesRoundTrip(int kindValue, string value)
    {
        var kind = (TitleMatchKind)kindValue;
        using var control = new TitleRuleEditorControl
        {
            Rule = new TitleRule(kind, value),
        };

        Assert.Equal(new TitleRule(kind, value), control.Rule);
    }

    [Fact]
    public void UseCurrentTitleEnablesLiteralRule()
    {
        using var control = new TitleRuleEditorControl();

        control.CapturedTitle = "WorldGen — PowerShell";
        FindControls<Button>(control).Single(x => x.Text == "Use current title").PerformClick();

        Assert.Equal(new TitleRule(TitleMatchKind.Literal, "WorldGen — PowerShell"), control.Rule);
    }

    [Fact]
    public void BlankEnabledMatcherIsInvalid()
    {
        using var control = new TitleRuleEditorControl
        {
            Rule = new TitleRule(TitleMatchKind.Literal, "title"),
        };
        var matcher = FindControls<TextBox>(control).Single(text => text.AccessibleName == "Title matcher value");

        matcher.Text = "   ";

        Assert.Equal(new[] { "Title matcher value is required." }, control.ValidateRule());
    }

    [Fact]
    public void InvalidRegexIsReportedWithoutThrowing()
    {
        using var control = new TitleRuleEditorControl
        {
            Rule = new TitleRule(TitleMatchKind.Regex, "["),
        };

        var errors = control.ValidateRule();

        Assert.Equal(new[] { "Title matcher regex is invalid." }, errors);
    }

    [Fact]
    public void ChangingKindOrValueRaisesOneValueChangedEvent()
    {
        using var control = new TitleRuleEditorControl
        {
            Rule = new TitleRule(TitleMatchKind.Literal, "Project"),
        };
        var changes = 0;
        control.ValueChanged += (_, _) => changes++;

        var kind = FindControls<ComboBox>(control).Single(combo => combo.AccessibleName == "Title matcher kind");
        kind.SelectedItem = TitleMatchKind.Prefix;
        Assert.Equal(1, changes);

        changes = 0;
        var value = FindControls<TextBox>(control).Single(text => text.AccessibleName == "Title matcher value");
        value.Text = "WorldGen";
        Assert.Equal(1, changes);
    }

    private static IEnumerable<T> FindControls<T>(Control root) where T : Control => root.Controls.Cast<Control>()
        .SelectMany(control => Descendants(control).Append(control))
        .OfType<T>();

    private static IEnumerable<Control> Descendants(Control control) => control.Controls.Cast<Control>()
        .SelectMany(child => Descendants(child).Append(child));
}
