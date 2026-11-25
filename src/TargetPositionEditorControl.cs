using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace UsageBars;

internal sealed class TargetPositionEditorControl : UserControl
{
    private sealed record AnchorOption(PillAnchor Value, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly ComboBox _anchor = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _x = new()
    {
        Minimum = TargetPositionRules.MinimumOffset,
        Maximum = TargetPositionRules.MaximumOffset,
        Increment = TargetPositionRules.NudgeSize,
    };
    private readonly NumericUpDown _y = new()
    {
        Minimum = TargetPositionRules.MinimumOffset,
        Maximum = TargetPositionRules.MaximumOffset,
        Increment = TargetPositionRules.NudgeSize,
    };
    private (PillAnchor Anchor, int OffsetX, int OffsetY) _lastNotified;

    public TargetPositionEditorControl()
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        _anchor.Items.AddRange(new object[]
        {
            new AnchorOption(PillAnchor.TopCenter, "Top center"),
            new AnchorOption(PillAnchor.TopRight, "Top right"),
            new AnchorOption(PillAnchor.BottomCenter, "Bottom center"),
        });
        Anchor = PillAnchor.TopCenter;

        var left = DirectionButton("Left", "←", () => OffsetX = TargetPositionRules.Nudge(OffsetX, -1));
        var right = DirectionButton("Right", "→", () => OffsetX = TargetPositionRules.Nudge(OffsetX, 1));
        var up = DirectionButton("Up", "↑", () => OffsetY = TargetPositionRules.Nudge(OffsetY, -1));
        var down = DirectionButton("Down", "↓", () => OffsetY = TargetPositionRules.Nudge(OffsetY, 1));
        var reset = new Button { Text = "Reset offset", AutoSize = true };
        reset.Click += (_, _) => ResetOffsets();

        var xControls = new FlowLayoutPanel
        {
            AutoSize = true,
            Margin = Padding.Empty,
            WrapContents = false,
        };
        xControls.Controls.Add(_x);
        xControls.Controls.Add(left);
        xControls.Controls.Add(right);

        var yControls = new FlowLayoutPanel
        {
            AutoSize = true,
            Margin = Padding.Empty,
            WrapContents = false,
        };
        yControls.Controls.Add(_y);
        yControls.Controls.Add(up);
        yControls.Controls.Add(down);

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            Margin = Padding.Empty,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddRow(layout, "Anchor", _anchor);
        AddRow(layout, "X", xControls);
        AddRow(layout, "Y", yControls);
        layout.Controls.Add(reset, 1, layout.RowCount);
        layout.RowCount++;
        Controls.Add(layout);

        _lastNotified = (Anchor, OffsetX, OffsetY);
        _anchor.SelectedIndexChanged += (_, _) => NotifyIfChanged();
        _x.ValueChanged += (_, _) => NotifyIfChanged();
        _y.ValueChanged += (_, _) => NotifyIfChanged();
        _x.TextChanged += (_, _) => NotifyIfChanged();
        _y.TextChanged += (_, _) => NotifyIfChanged();
    }

    public event EventHandler? ValueChanged;

    public new PillAnchor Anchor
    {
        get => ((AnchorOption)_anchor.SelectedItem!).Value;
        set => _anchor.SelectedItem = _anchor.Items.Cast<AnchorOption>()
            .Single(option => option.Value == value);
    }

    public int OffsetX
    {
        get => ReadOffset(_x);
        set => WriteOffset(_x, value);
    }

    public int OffsetY
    {
        get => ReadOffset(_y);
        set => WriteOffset(_y, value);
    }

    public void ResetOffsets()
    {
        OffsetX = 0;
        OffsetY = 0;
    }

    private void NotifyIfChanged()
    {
        if (_anchor.SelectedItem is not AnchorOption) return;
        var current = (Anchor, OffsetX, OffsetY);
        if (current == _lastNotified) return;
        _lastNotified = current;
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private static int ReadOffset(NumericUpDown control) =>
        TryParseOffset(control.Text, out var parsed)
            ? parsed
            : decimal.ToInt32(control.Value);

    private static void WriteOffset(NumericUpDown control, int value)
    {
        var clamped = TargetPositionRules.ClampOffset(value);
        control.Value = clamped;
        var text = clamped.ToString(CultureInfo.CurrentCulture);
        if (!string.Equals(control.Text, text, StringComparison.Ordinal))
            control.Text = text;
    }

    private static bool TryParseOffset(string? text, out int offset)
    {
        if (int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var parsed))
        {
            offset = TargetPositionRules.ClampOffset(parsed);
            return true;
        }

        offset = 0;
        return false;
    }

    private static Button DirectionButton(string accessibleName, string text, Action click)
    {
        var button = new Button { Text = text, AccessibleName = accessibleName, AutoSize = true };
        button.Click += (_, _) => click();
        return button;
    }

    private static void AddRow(TableLayoutPanel layout, string label, Control control)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, layout.RowCount);
        layout.Controls.Add(control, 1, layout.RowCount);
        layout.RowCount++;
    }
}
