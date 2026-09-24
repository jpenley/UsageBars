using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace UsageBars;

internal sealed class TitleRuleEditorControl : UserControl
{
    private readonly CheckBox _enabled = new() { Text = "Use title matcher", AutoSize = true };
    private readonly ComboBox _kind = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        AccessibleName = "Title matcher kind",
    };
    private readonly TextBox _value = new() { AccessibleName = "Title matcher value" };
    private readonly TextBox _capturedTitle = new() { ReadOnly = true };
    private readonly Button _useCurrentTitle = new() { Text = "Use current title", AutoSize = true };
    private readonly Label _validation = new()
    {
        AutoSize = true,
        ForeColor = Color.Firebrick,
        AccessibleName = "Title matcher validation",
    };
    private bool _loading;

    public TitleRuleEditorControl()
    {
        AutoSize = true;
        Dock = DockStyle.Top;
        foreach (var kind in Enum.GetValues<TitleMatchKind>()) _kind.Items.Add(kind);
        _kind.SelectedIndex = 0;

        var currentTitle = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Margin = Padding.Empty,
        };
        currentTitle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        currentTitle.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _capturedTitle.Dock = DockStyle.Fill;
        currentTitle.Controls.Add(_capturedTitle, 0, 0);
        currentTitle.Controls.Add(_useCurrentTitle, 1, 0);

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            ColumnCount = 2,
            Margin = Padding.Empty,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddSpanningRow(layout, _enabled);
        AddRow(layout, "Match type", _kind);
        AddRow(layout, "Matcher", _value);
        AddRow(layout, "Current title", currentTitle);
        AddSpanningRow(layout, _validation);
        Controls.Add(layout);

        _enabled.CheckedChanged += (_, _) => OnValueChanged();
        _kind.SelectedIndexChanged += (_, _) => OnValueChanged();
        _value.TextChanged += (_, _) => OnValueChanged();
        _useCurrentTitle.Click += (_, _) =>
        {
            _enabled.Checked = true;
            _kind.SelectedItem = TitleMatchKind.Literal;
            _value.Text = _capturedTitle.Text;
        };
        UpdateState();
    }

    public event EventHandler? ValueChanged;

    public TitleRule? Rule
    {
        get => !_enabled.Checked
            ? null
            : new TitleRule(SelectedKind, _value.Text.Trim());
        set
        {
            _loading = true;
            try
            {
                _enabled.Checked = value is not null;
                _kind.SelectedIndex = (int)(value?.Kind ?? TitleMatchKind.Literal);
                _value.Text = value?.Value ?? "";
            }
            finally
            {
                _loading = false;
            }
            UpdateState();
        }
    }

    public string CapturedTitle
    {
        get => _capturedTitle.Text;
        set => _capturedTitle.Text = value ?? "";
    }

    public IReadOnlyList<string> ValidateRule()
    {
        if (!_enabled.Checked) return Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(_value.Text))
            return new[] { "Title matcher value is required." };
        if (SelectedKind == TitleMatchKind.Regex && !IsValidRegex(_value.Text.Trim()))
            return new[] { "Title matcher regex is invalid." };
        return Array.Empty<string>();
    }

    private void OnValueChanged()
    {
        UpdateState();
        if (_loading) return;
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateState()
    {
        _kind.Enabled = _enabled.Checked;
        _value.Enabled = _enabled.Checked;
        _validation.Text = string.Join(Environment.NewLine, ValidateRule());
    }

    private TitleMatchKind SelectedKind => _kind.SelectedIndex >= 0
        ? (TitleMatchKind)_kind.SelectedIndex
        : TitleMatchKind.Literal;

    private static bool IsValidRegex(string value)
    {
        try
        {
            _ = new Regex(value, RegexOptions.None, TimeSpan.FromMilliseconds(100));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void AddRow(TableLayoutPanel layout, string label, Control control)
    {
        var row = layout.RowCount;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 6, 0),
        }, 0, row);
        control.Dock = DockStyle.Top;
        layout.Controls.Add(control, 1, row);
        layout.RowCount++;
    }

    private static void AddSpanningRow(TableLayoutPanel layout, Control control)
    {
        var row = layout.RowCount;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Dock = DockStyle.Top;
        layout.Controls.Add(control, 0, row);
        layout.SetColumnSpan(control, 2);
        layout.RowCount++;
    }
}
