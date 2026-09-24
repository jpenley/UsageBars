using System.Collections.Generic;
using System.Linq;

namespace UsageBars;

internal static class TargetDefaults
{
    private static readonly string[] TerminalClasses =
    {
        "CASCADIA_HOSTING_WINDOW_CLASS", "ConsoleWindowClass", "VirtualConsoleClass",
        "mintty", "Alacritty", "org.wezfurlong.wezterm",
    };

    private static readonly string[] GenericEditors =
    {
        "codium", "cursor", "windsurf", "devenv", "idea64", "idea", "pycharm64",
        "webstorm64", "clion64", "goland64", "phpstorm64", "rubymine64", "datagrip64",
        "sublime_text", "atom", "zed", "nvim-qt", "neovide", "emacs",
    };

    public static IReadOnlyList<TargetDefinition> Create(Config config)
    {
        var revision = 0L;
        TargetDefinition Stamp(TargetDefinition value) => value with { Revision = ++revision };
        var result = new List<TargetDefinition>
        {
            Stamp(TargetDefinition.Seeded(
                "Claude Desktop", "claude", "Chrome_WidgetWin_1",
                new[] { ProviderBinding.Claude }, TargetPlacementKind.Desktop,
                TargetActivationPolicy.AlwaysWhileOpen, true, 0) with
            {
                WindowTitle = new(TitleMatchKind.Regex, @"(?s).+"),
            }),
            Stamp(TargetDefinition.Seeded(
                "Codex / ChatGPT", "ChatGPT", "Chrome_WidgetWin_1",
                new[] { ProviderBinding.Codex }, TargetPlacementKind.Desktop,
                TargetActivationPolicy.AlwaysWhileOpen, true, 0) with
            {
                WindowTitle = new(TitleMatchKind.Regex, @"(?s).+"),
            }),
        };

        foreach (var className in TerminalClasses)
        {
            result.Add(Stamp(TargetDefinition.Seeded(
                $"Claude terminal ({className})", null, className,
                new[] { ProviderBinding.Claude },
                TargetPlacementKind.Terminal, TargetActivationPolicy.RequireActivityProcess,
                false, 0, new[] { "claude" }) with { Enabled = config.AttachToClaudeCodeTerminals }));
            result.Add(Stamp(TargetDefinition.Seeded(
                $"Codex terminal ({className})", null, className,
                new[] { ProviderBinding.Codex },
                TargetPlacementKind.Terminal, TargetActivationPolicy.RequireActivityProcess,
                false, 0, new[] { "codex" }) with { Enabled = config.AttachToCodexTerminals }));
        }

        foreach (var (process, frame, height) in new[]
        {
            ("rider64", "SunAwtFrame", 26d), ("rider", "SunAwtFrame", 26d),
            ("code", "Chrome_WidgetWin_1", 22d),
            ("code - insiders", "Chrome_WidgetWin_1", 22d),
        })
        {
            result.Add(Stamp(TargetDefinition.Seeded(
                $"{process} IDE", process, frame,
                new[] { ProviderBinding.Claude, ProviderBinding.Codex },
                TargetPlacementKind.Ide,
                config.AlwaysShowOnIdes
                    ? TargetActivationPolicy.AlwaysWhileOpen
                    : TargetActivationPolicy.RequireActivityProcess,
                false, height,
                config.AlwaysShowOnIdes ? null : new[] { "claude", "codex" })));
        }

        foreach (var process in GenericEditors)
            result.Add(Stamp(TargetDefinition.Seeded(
                $"{process} editor", process, null,
                new[] { ProviderBinding.Claude, ProviderBinding.Codex },
                TargetPlacementKind.Terminal, TargetActivationPolicy.RequireActivityProcess,
                true, 0, new[] { "claude", "codex" })));

        return result;
    }
}
