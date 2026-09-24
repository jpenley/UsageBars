using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace UsageBars;

/// <summary>
/// The only interactive surface: the pills themselves are click-through, so the tray icon is how
/// you read details, manage targets, change settings and quit.
/// </summary>
internal sealed class Tray : IDisposable
{
    internal static IReadOnlyList<string> CommandLabels { get; } = new[]
    {
        "Refresh now",
        "Targets",
        "Start with Windows",
        "Copy diagnostics",
        "Open diagnostics folder",
        "Exit",
    };

    internal static IReadOnlyList<string> TargetCommandLabels { get; } = new[]
    {
        "Manage targets...",
    };

    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _claudeStatus = new() { Enabled = false };
    private readonly ToolStripMenuItem _codexStatus = new() { Enabled = false };
    private readonly ToolStripMenuItem _targetSummary = new() { Enabled = false };

    // Hidden until there is a projection to show, rather than displayed saying "unknown": on a
    // fresh install there is no history behind either provider for the first several minutes.
    private readonly ToolStripMenuItem _claudeProjection = new() { Enabled = false, Visible = false };
    private readonly ToolStripMenuItem _codexProjection = new() { Enabled = false, Visible = false };

    private readonly ToolStripMenuItem _startup = new(CommandLabels[2]);
    private Icon? _generated;
    private IntPtr _iconHandle = IntPtr.Zero;

    /// <summary>Guards against the programmatic tick in <see cref="SetStartupState"/> re-firing.</summary>
    private bool _suppressStartupEvent;

    public event Action? RefreshRequested;
    public event Action? ExitRequested;
    public event Action? DiagnosticsRequested;
    public event Action? DiagnosticsFolderRequested;
    public event Action? ManageTargetsRequested;

    /// <summary>Raised with the requested state; the handler confirms or rolls the tick back.</summary>
    public event Action<bool>? StartupToggleRequested;

    public Tray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(_claudeStatus);
        menu.Items.Add(_claudeProjection);
        menu.Items.Add(_codexStatus);
        menu.Items.Add(_codexProjection);
        menu.Items.Add(new ToolStripSeparator());

        var refresh = new ToolStripMenuItem(CommandLabels[0]);
        refresh.Click += (_, _) => RefreshRequested?.Invoke();
        menu.Items.Add(refresh);

        var targets = new ToolStripMenuItem(CommandLabels[1]);
        var manageTargets = new ToolStripMenuItem(TargetCommandLabels[0]);
        manageTargets.Click += (_, _) => ManageTargetsRequested?.Invoke();
        targets.DropDownItems.Add(manageTargets);
        targets.DropDownItems.Add(new ToolStripSeparator());
        targets.DropDownItems.Add(_targetSummary);
        menu.Items.Add(targets);

        // Not CheckOnClick: the registry write can fail, and the tick must reflect what actually
        // happened rather than what was clicked.
        _startup.Click += (_, _) =>
        {
            if (_suppressStartupEvent) return;
            StartupToggleRequested?.Invoke(!_startup.Checked);
        };
        menu.Items.Add(_startup);

        menu.Items.Add(new ToolStripSeparator());

        var diagnostics = new ToolStripMenuItem(CommandLabels[3]);
        diagnostics.Click += (_, _) => DiagnosticsRequested?.Invoke();
        menu.Items.Add(diagnostics);

        var diagnosticsFolder = new ToolStripMenuItem(CommandLabels[4]);
        diagnosticsFolder.Click += (_, _) => DiagnosticsFolderRequested?.Invoke();
        menu.Items.Add(diagnosticsFolder);

        menu.Items.Add(new ToolStripSeparator());
        var exit = new ToolStripMenuItem(CommandLabels[5]);
        exit.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(exit);

        _icon = new NotifyIcon
        {
            Icon = BuildIcon(),
            Text = "UsageBars",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) RefreshRequested?.Invoke();
        };
    }

    /// <summary>
    /// Shows what will actually happen at the next logon, rather than merely whether a registry
    /// value exists.
    ///
    /// Only <see cref="StartupState.On"/> ticks the item. The two broken states read as off,
    /// because that is the truth about this copy — and it makes the repair a single click, since
    /// clicking an unticked item registers this executable over whatever was there.
    /// </summary>
    public void SetStartupState(StartupState state)
    {
        _suppressStartupEvent = true;
        try
        {
            _startup.Checked = state == StartupState.On;
            _startup.Text = state switch
            {
                StartupState.OtherCopy => "Start with Windows — another copy is registered",
                StartupState.Dangling => "Start with Windows — registered copy is missing",
                _ => "Start with Windows",
            };
        }
        finally
        {
            _suppressStartupEvent = false;
        }
    }

    /// <summary>
    /// Refreshes the menu and hover text. Either provider may be null, meaning it's turned off in
    /// config — shown as such rather than silently omitted, so a disabled provider is not mistaken
    /// for a broken one.
    /// </summary>
    public void Update(UsageModel? claude, UsageModel? codex)
    {
        _claudeStatus.Text = "Claude: " + (claude?.StatusText is { Length: > 0 } c ? c : "(disabled)");
        _codexStatus.Text = "Codex: " + (codex?.StatusText is { Length: > 0 } x ? x : "(disabled)");

        SetProjection(_claudeProjection, claude);
        SetProjection(_codexProjection, codex);

        var parts = new List<string>();
        if (claude is not null) parts.Add("C " + Compact(claude));
        if (codex is not null) parts.Add("X " + Compact(codex));

        // NotifyIcon.Text is capped at 63 characters, and Windows silently truncates past that.
        var tip = "UsageBars — " + (parts.Count == 0 ? "no providers enabled" : string.Join("  ", parts));
        _icon.Text = tip.Length <= 63 ? tip : tip[..63];
    }

    public void SetTargetSummary(int enabled, int disabled) =>
        _targetSummary.Text = $"{enabled} enabled · {disabled} disabled";

    /// <summary>
    /// The projected-exhaustion line under a provider's status, indented so it reads as a
    /// subordinate detail. Hidden outright when nothing can be projected — a provider that has
    /// only just started being watched has no rate yet, and saying so on every open would be
    /// noise.
    /// </summary>
    private static void SetProjection(ToolStripMenuItem item, UsageModel? model)
    {
        if (model?.ProjectionText is not { Length: > 0 } text)
        {
            item.Visible = false;
            return;
        }

        item.Text = "      at this rate: " + text;
        item.Visible = true;
    }

    /// <summary>Shortest useful form of a model, for the 63-character tooltip.</summary>
    private static string Compact(UsageModel model)
    {
        if (!model.HasValues) return "--";

        var parts = new List<string>();
        foreach (var bar in model.Bars)
        {
            if (bar.RemainingPct is not { } remaining) continue;
            parts.Add($"{bar.Label} {Math.Round(remaining):0}%");
        }
        return parts.Count == 0 ? "--" : string.Join(" ", parts);
    }

    /// <summary>
    /// Surfaces a problem the user needs to know about but that has no place on the pill — a
    /// failed startup registration, an unopenable folder. The pill stays a status display.
    /// </summary>
    public void ShowWarning(string message)
    {
        try
        {
            _icon.BalloonTipTitle = "UsageBars";
            _icon.BalloonTipText = message;
            _icon.BalloonTipIcon = ToolTipIcon.Warning;
            _icon.ShowBalloonTip(5000);
        }
        catch (Exception)
        {
            // Notifications can be disabled by policy; not worth failing over.
        }
    }

    /// <summary>Drawn at runtime so the exe needs no embedded resources.</summary>
    private Icon BuildIcon()
    {
        using var bitmap = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            using var green = new SolidBrush(Color.FromArgb(0x3F, 0xB9, 0x50));
            using var amber = new SolidBrush(Color.FromArgb(0xE3, 0xA0, 0x08));
            using var track = new SolidBrush(Color.FromArgb(0x60, 0x99, 0x99, 0x99));
            g.FillRectangle(track, 1, 4, 14, 3);
            g.FillRectangle(track, 1, 9, 14, 3);
            g.FillRectangle(green, 1, 4, 10, 3);
            g.FillRectangle(amber, 1, 9, 6, 3);
        }

        _iconHandle = bitmap.GetHicon();
        _generated = Icon.FromHandle(_iconHandle);
        return _generated;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _generated?.Dispose();
        if (_iconHandle != IntPtr.Zero) Native.DestroyIcon(_iconHandle);
    }
}
