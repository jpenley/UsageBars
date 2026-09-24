using System;
using System.Drawing;
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

        _anchor.SelectedIndexChanged += (_, _) => ValueChanged?.Invoke(this, EventArgs.Empty);
        _x.ValueChanged += (_, _) => ValueChanged?.Invoke(this, EventArgs.Empty);
        _y.ValueChanged += (_, _) => ValueChanged?.Invoke(this, EventArgs.Empty);
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
        get => decimal.ToInt32(_x.Value);
        set => _x.Value = TargetPositionRules.ClampOffset(value);
    }

    public int OffsetY
    {
        get => decimal.ToInt32(_y.Value);
        set => _y.Value = TargetPositionRules.ClampOffset(value);
    }

    public void ResetOffsets()
    {
        OffsetX = 0;
        OffsetY = 0;
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
