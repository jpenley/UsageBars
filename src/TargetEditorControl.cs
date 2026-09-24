using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace UsageBars;

internal sealed class TargetEditorControl : UserControl
{
    private TargetEditorModel _model;
    private ProcessInventory _inventory;
    private bool _loading;

    private readonly TextBox _name = new();
    private readonly TextBox _application = new() { ReadOnly = true };
    private readonly TextBox _windowClass = new() { ReadOnly = true };
    private readonly CheckBox _claude = new() { Text = "Claude" };
    private readonly CheckBox _codex = new() { Text = "Codex" };
    private readonly TitleRuleEditorControl _title = new();
    private readonly RadioButton _alwaysOpen = new() { Text = "Always while the window is open", AutoSize = true };
    private readonly RadioButton _whenProcessRunning = new()
    {
        Text = "When any selected process is running",
        AutoSize = true,
    };
    private readonly ActivityProcessEditorControl _activity = new(ProcessInventory.Empty);
    private readonly ComboBox _placement = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TargetPositionEditorControl _position = new();
    private readonly CheckBox _enabled = new() { Text = "Enabled", AutoSize = true };
    private readonly CheckBox _resizable = new() { Text = "Require resizable frame", AutoSize = true };
    private readonly NumericUpDown _statusHeight = new() { DecimalPlaces = 1, Maximum = 500 };
    private readonly Label _notes = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Label _validation = new() { AutoSize = true, ForeColor = Color.Firebrick };
    private readonly FlowLayoutPanel _options = new()
    {
        AutoSize = true,
        Dock = DockStyle.Top,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        Margin = Padding.Empty,
    };

    public TargetEditorControl(TargetEditorModel model, ProcessInventory inventory)
    {
        _model = model;
        _inventory = inventory;
        MinimumSize = new Size(620, 540);
        Dock = DockStyle.Fill;

        _placement.DataSource = Enum.GetValues<TargetPlacementKind>();
        _options.Controls.Add(_enabled);
        _options.Controls.Add(_resizable);
        _options.MinimumSize = new Size(_resizable.PreferredSize.Width + 8, 0);

        var activation = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = Padding.Empty,
        };
        activation.Controls.Add(_alwaysOpen);
        activation.Controls.Add(_whenProcessRunning);

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Padding = new Padding(10),
            ColumnCount = 2,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(layout, "Name", _name);
        AddRow(layout, "Application", _application);
        AddRow(layout, "Window class", _windowClass);
        AddRow(layout, "Providers", ProviderRow(_claude, _codex));
        AddRow(layout, "Title match", _title);
        AddRow(layout, "Activation", activation);
        AddRow(layout, "Activity processes", _activity);
        AddRow(layout, "Placement", _placement);
        AddRow(layout, "Position", _position);
        AddRow(layout, "Status-bar height", _statusHeight);
        AddFullWidthOptionsRow(layout, _options);
        AddSpanningRow(layout, _notes);
        AddSpanningRow(layout, _validation);

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        scroll.Controls.Add(layout);
        Controls.Add(scroll);

        WireChanges();
        LoadModel(model);
    }

    public event Action<TargetDefinition>? DraftChanged;
    public event EventHandler? InventoryRefreshRequested;

    public Orientation OptionsOrientation => Orientation.Vertical;
    public int OptionsContentWidth => Math.Max(_options.ClientSize.Width, _options.MinimumSize.Width);
    public int ResizableOptionPreferredWidth => _resizable.PreferredSize.Width;
    public bool OptionsAreReadable => OptionsContentWidth >= ResizableOptionPreferredWidth;
    public TargetEditorModel Model => _model;

    public TargetDefinition CurrentDefinition
    {
        get
        {
            PullModel();
            return _model.ToDefinition();
        }
    }

    public IReadOnlyList<string> ValidateDefinition()
    {
        PullModel();
        var errors = _model.Validate().ToList();
        if (_whenProcessRunning.Checked && _activity.ActivityProcessNames.Count == 0)
            errors.Add("Select at least one activity process.");
        return errors;
    }

    public void ShowValidation(string? message) => _validation.Text = message ?? "";

    public void LoadModel(TargetEditorModel model)
    {
        _loading = true;
        try
        {
            _model = model;
            _name.Text = model.Name;
            _application.Text = model.Application;
            _windowClass.Text = model.WindowClass;
            _claude.Checked = model.Claude;
            _codex.Checked = model.Codex;
            _title.CapturedTitle = model.CapturedTitle;
            _title.Rule = model.TitleMatcherEnabled
                ? new TitleRule(model.TitleKind, model.TitleValue)
                : null;
            _alwaysOpen.Checked = model.ActivityProcessNames.Count == 0;
            _whenProcessRunning.Checked = model.ActivityProcessNames.Count != 0;
            _activity.SetActivityProcesses(model.ActivityProcessNames);
            _placement.SelectedItem = model.Placement;
            _position.Anchor = model.Anchor;
            _position.OffsetX = model.OffsetX;
            _position.OffsetY = model.OffsetY;
            _enabled.Checked = model.Enabled;
            _resizable.Checked = model.RequireResizableFrame;
            _statusHeight.Value = (decimal)Math.Clamp(model.StatusBarHeight, 0, 500);
            _notes.Text = string.Join(Environment.NewLine, model.InferenceNotes);
            _validation.Text = "";
            UpdateStatusHeightState();
            UpdateActivityVisibility();
        }
        finally
        {
            _loading = false;
        }
        SetInventory(_inventory);
    }

    public void SetInventory(ProcessInventory inventory)
    {
        _inventory = inventory;
        _activity.SetInventory(inventory);
    }

    private static Control ProviderRow(CheckBox claude, CheckBox codex)
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            WrapContents = false,
            Margin = Padding.Empty,
        };
        panel.Controls.Add(claude);
        panel.Controls.Add(codex);
        return panel;
    }

    private void WireChanges()
    {
        _name.TextChanged += (_, _) => RaiseDraftChanged();
        _claude.CheckedChanged += (_, _) => RaiseDraftChanged();
        _codex.CheckedChanged += (_, _) => RaiseDraftChanged();
        _title.ValueChanged += (_, _) => RaiseDraftChanged();
        _alwaysOpen.CheckedChanged += (_, _) => ChangeActivationMode(alwaysOpen: true);
        _whenProcessRunning.CheckedChanged += (_, _) => ChangeActivationMode(alwaysOpen: false);
        _activity.ValueChanged += (_, _) =>
        {
            if (_activity.ActivityProcessNames.Count > 0 && !_whenProcessRunning.Checked)
            {
                _whenProcessRunning.Checked = true;
                return;
            }
            RaiseDraftChanged();
        };
        _activity.InventoryRefreshRequested += (_, _) => InventoryRefreshRequested?.Invoke(this, EventArgs.Empty);
        _placement.SelectedIndexChanged += (_, _) => { UpdateStatusHeightState(); RaiseDraftChanged(); };
        _position.ValueChanged += (_, _) => RaiseDraftChanged();
        _enabled.CheckedChanged += (_, _) => RaiseDraftChanged();
        _resizable.CheckedChanged += (_, _) => RaiseDraftChanged();
        _statusHeight.ValueChanged += (_, _) => RaiseDraftChanged();
    }

    private void ChangeActivationMode(bool alwaysOpen)
    {
        if (_loading || (alwaysOpen ? !_alwaysOpen.Checked : !_whenProcessRunning.Checked)) return;
        if (alwaysOpen) _activity.SetActivityProcesses(Array.Empty<string>());
        UpdateActivityVisibility();
        RaiseDraftChanged();
    }

    private void PullModel()
    {
        _model.Name = _name.Text;
        _model.Claude = _claude.Checked;
        _model.Codex = _codex.Checked;
        _model.TitleMatcherEnabled = _title.Rule is not null;
        _model.TitleKind = _title.Rule?.Kind ?? TitleMatchKind.Literal;
        _model.TitleValue = _title.Rule?.Value ?? "";
        _model.SetActivityProcesses(_activity.ActivityProcessNames);
        if (_placement.SelectedItem is TargetPlacementKind placement)
            _model.Placement = placement;
        _model.Anchor = _position.Anchor;
        _model.OffsetX = _position.OffsetX;
        _model.OffsetY = _position.OffsetY;
        _model.Enabled = _enabled.Checked;
        _model.RequireResizableFrame = _resizable.Checked;
        _model.StatusBarHeight = (double)_statusHeight.Value;
    }

    private void RaiseDraftChanged()
    {
        if (_loading) return;
        DraftChanged?.Invoke(CurrentDefinition);
    }

    private void UpdateActivityVisibility() =>
        _activity.Visible = _whenProcessRunning.Checked;

    private void UpdateStatusHeightState() =>
        _statusHeight.Enabled = _placement.SelectedItem is TargetPlacementKind.Ide;

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

    private static void AddFullWidthOptionsRow(TableLayoutPanel layout, Control options)
    {
        var row = layout.RowCount;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            Text = "Options",
            AutoSize = true,
            Margin = new Padding(0, 6, 0, 0),
        }, 0, row);
        layout.SetColumnSpan(layout.GetControlFromPosition(0, row)!, 2);
        layout.RowCount++;
        AddSpanningRow(layout, options);
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
