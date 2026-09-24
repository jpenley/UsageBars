using System;
using System.Drawing;
using System.Windows.Forms;

namespace UsageBars;

internal interface IPickerBanner : IDisposable
{
    void Show();
    void ShowMessage(string message);
    void Hide();
}

internal sealed class PickerBanner : Form, IPickerBanner
{
    private const string InitialMessage = "Click a window to target · Esc or right-click to cancel";
    private readonly Label _message;

    public PickerBanner()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(36, 36, 36);
        ClientSize = new Size(440, 48);

        _message = new Label
        {
            Dock = DockStyle.Fill,
            Text = InitialMessage,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Font = SystemFonts.MessageBoxFont,
        };
        Controls.Add(_message);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            return parameters;
        }
    }

    public new void Show()
    {
        _message.Text = InitialMessage;
        PositionNearTopOfPrimaryWorkingArea();
        base.Show();
    }

    public void ShowMessage(string message)
    {
        _message.Text = $"{message} Choose another window · Esc to cancel";
    }

    private void PositionNearTopOfPrimaryWorkingArea()
    {
        var workingArea = Screen.PrimaryScreen?.WorkingArea ?? Screen.FromPoint(Point.Empty).WorkingArea;
        Location = new Point(
            workingArea.Left + (workingArea.Width - Width) / 2,
            workingArea.Top + 24);
    }
}
