using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UsageBars;

/// <summary>
/// Optional tweaks, written next to the exe when possible so the whole thing stays portable.
/// Nothing here is required for a first run.
/// </summary>
internal sealed class Config
{
    /// <summary>Height of Claude's own title bar in logical pixels; the pill is centred in it.</summary>
    public double TitleBarHeight { get; set; } = 40;

    /// <summary>
    /// Show every enabled provider's pill on Rider's main frame for as long as it is open, without
    /// requiring a CLI session to be detected running under it.
    ///
    /// On by default. Rider offers too many routes to a provider — an integrated terminal tab, an
    /// AI assistant, a run configuration, an external tool — for "is a CLI process descended from
    /// the IDE right this instant" to be a useful question; the answer flickers, and it is no at
    /// the moment you go looking most of the time. The budgets are the account's rather than any
    /// session's, so there is nothing to withhold: if Rider is open, both are shown.
    ///
    /// Set false to go back to showing a pill only while a session is actually detected. The
    /// per-provider <see cref="EnableClaude"/> / <see cref="EnableCodex"/> and
    /// <see cref="AttachToClaudeCodeTerminals"/> / <see cref="AttachToCodexTerminals"/> switches
    /// still apply either way, so a provider you have turned off never appears here.
    /// </summary>
    public bool AlwaysShowOnIdes { get; set; } = true;

    /// <summary>
    /// How often to re-scrape Claude Code's /usage screen. Each scrape spins a hidden Claude Code
    /// session for ~20 s, so this is deliberately infrequent; the value itself only moves as you
    /// use Claude.
    /// </summary>
    public double ScrapeIntervalMinutes { get; set; } = 15;

    /// <summary>
    /// Absolute floor: the "+" icon always appears at or below this remaining percentage, even
    /// when the burn rate says there's plenty of time. The primary trigger is the velocity
    /// projection below.
    /// </summary>
    public double LowThresholdPct { get; set; } = 10;

    /// <summary>Warn when the meter is projected to empty within this many hours.</summary>
    public double WarnHoursToEmpty { get; set; } = 2;

    /// <summary>
    /// Safety margin on the measured burn rate. 1.25 assumes you'll consume 25 % faster than
    /// recent history suggests, so the warning lands early rather than late.
    /// </summary>
    public double VelocityPaddingFactor { get; set; } = 1.25;

    /// <summary>How far back to look when measuring the current burn rate.</summary>
    public double VelocityWindowHours { get; set; } = 3;

    /// <summary>Minimum samples before a rate is trusted.</summary>
    public int MinVelocitySamples { get; set; } = 3;

    /// <summary>Minimum time the samples must span before a rate is trusted.</summary>
    public int MinVelocitySpanMinutes { get; set; } = 20;

    /// <summary>
    /// Day the usage window is expected to reset, for display only. Accepts a day name such as
    /// "Friday". This never affects the bar, the burn rate or the warning — an actual reset is
    /// detected from the data itself (a drop in usage).
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DayOfWeek ResetDayOfWeek { get; set; } = DayOfWeek.Friday;

    /// <summary>Local hour of day (0-23) of the expected reset. Display only.</summary>
    public int ResetHour { get; set; }

    /// <summary>
    /// Fall back to <see cref="ResetDayOfWeek"/> when a reset can't be measured from the data.
    ///
    /// Off by default. Reset deadlines are now derived from observed usage, which is correct by
    /// construction; a configured weekday is a guess, and showing a confident wrong date is worse
    /// than showing "unknown". Turn this on only if your plan genuinely has a fixed weekly reset
    /// and your history is too short to observe it.
    /// </summary>
    public bool UseConfiguredResetDay { get; set; }

    /// <summary>
    /// Where the Claude "+" icon points when the meter runs low. Empty by default, for the same
    /// reason as <see cref="CodexRequestCreditUrl"/>: there is no universal "get more credit"
    /// page, so no icon is drawn unless one is configured locally.
    /// </summary>
    public string RequestCreditUrl { get; set; } = "";

    /// <summary>
    /// Windows domain whose members see the "+" request-credit icon at all. Empty = no restriction.
    ///
    /// For a <see cref="RequestCreditUrl"/> that only resolves inside a company network: this is a
    /// portable exe that travels, and off-domain users still get the red bar and the warning —
    /// they just aren't offered a link that would go nowhere for them.
    /// </summary>
    public string RequestCreditDomain { get; set; } = "";

    /// <summary>Set false to rely only on plan-usage-history.json and never launch Claude Code.</summary>
    public bool UseClaudeCodeFallback { get; set; } = true;

    // --- Bar colours ------------------------------------------------------------

    /// <summary>Remaining percentage at or below which a bar turns amber.</summary>
    public double AmberAtPercent { get; set; } = 50;

    /// <summary>Remaining percentage below which a bar turns red. Clamped to never exceed amber.</summary>
    public double RedAtPercent { get; set; } = 20;

    // --- Providers --------------------------------------------------------------

    /// <summary>Show the Claude pill (Claude Desktop and Claude Code terminals).</summary>
    public bool EnableClaude { get; set; } = true;

    /// <summary>
    /// Show the Codex pill on the Codex/ChatGPT desktop window and Codex CLI terminals. Reads the
    /// signed-in Codex app-server, so there's nothing to configure beyond having Codex installed.
    /// </summary>
    public bool EnableCodex { get; set; } = true;

    /// <summary>
    /// How often to poll the Codex app-server. Cheap — it's a local JSON-RPC call over an already
    /// running process — and the server also pushes updates in between.
    /// </summary>
    public double CodexRefreshMinutes { get; set; } = 2;

    /// <summary>Also show the Codex pill on terminals running a Codex CLI session.</summary>
    public bool AttachToCodexTerminals { get; set; } = true;

    /// <summary>
    /// Title regex identifying a Codex CLI terminal, applied only to terminal classes.
    ///
    /// As specific as the Claude pattern, and for the same reason: a bare <c>\bcodex\b</c> matches
    /// any terminal whose title merely contains the word — a shell sitting in a directory called
    /// "codex", a build log, a git branch — and makes the pill jump to an unrelated window.
    /// </summary>
    public string CodexTitlePattern { get; set; } = @"(?i)^\s*codex\b|\bcodex\s+(?:cli|session)\b";

    /// <summary>
    /// Where the Codex pill's action icon points when the meter runs low. Empty by default: there
    /// is no universal "get more Codex quota" page, and an icon that leads nowhere is worse than
    /// no icon, so none is drawn unless you set this.
    /// </summary>
    public string CodexRequestCreditUrl { get; set; } = "";

    // --- Freshness --------------------------------------------------------------

    /// <summary>plan-usage-history.json older than this is drawn dimmed.</summary>
    public double FileStaleMinutes { get; set; } = 15;

    /// <summary>A scraped or API reading older than this is drawn dimmed.</summary>
    public double ReadingStaleHours { get; set; } = 2;

    /// <summary>
    /// Also show the pill on terminal windows that have a Claude Code CLI session running under
    /// them (Windows Terminal, PowerShell, cmd, mintty, and so on).
    /// </summary>
    public bool AttachToClaudeCodeTerminals { get; set; } = true;

    /// <summary>
    /// Keep the pill on screen even when its host window isn't focused, so usage stays visible
    /// while you work elsewhere. Set false to only show it while the host is the active window.
    /// </summary>
    public bool AlwaysOn { get; set; } = true;

    /// <summary>
    /// In always-on mode, hide the pill when another window covers that patch of screen, so it
    /// never floats over unrelated apps. Turning this off makes it a true always-on-top badge.
    /// </summary>
    public bool HideWhenCovered { get; set; } = true;

    /// <summary>
    /// Regex matched against a terminal window's title to spot a Claude Code session. Claude Code
    /// sets the title to "✳ Claude Code", and for Windows Terminal the title is the only usable
    /// signal — the terminal process never parents the CLI. Only applied to known terminal window
    /// classes, and only when a Claude Code process is actually running.
    ///
    /// Deliberately specific: a bare "claude" match also hits any terminal whose title merely
    /// contains the word, such as a shell sitting in a directory called "claude" — which happened
    /// in testing and made the pill jump to an unrelated window.
    ///
    /// The leading character class covers the braille block as well as "✳", because Claude Code
    /// animates the title with a braille spinner while it is working — measured on a live session:
    /// <c>"⠐ Access previous session from different folder"</c> (U+2810). Matching only "✳" made
    /// detection flap between title and ancestry for the entire time a task was running.
    /// </summary>
    public string TerminalTitlePattern { get; set; } =
        "(?i)^\\s*[✳⠀-⣿]|\\bclaude code\\b|^\\s*claude\\s*$";

    /// <summary>
    /// Horizontal space kept clear at the right of the title bar for the host's own window
    /// controls (minimise / maximise / close, and Electron apps' extra buttons). The pill centres
    /// in what's left, and hides entirely when that isn't wide enough.
    /// </summary>
    public double RightReservePx { get; set; } = 160;

    /// <summary>Persist a rotating diagnostics log under the data folder.</summary>
    public bool WriteDiagnosticsLog { get; set; } = true;

    /// <summary>
    /// How the pill is kept above its host: <c>auto</c>, <c>zorder</c> or <c>topmost</c>.
    ///
    /// <c>auto</c> prefers making the pill an owned window of the host, which hands stacking to
    /// the window manager and needs no occlusion detection at all; it falls back to inserting the
    /// pill just above the host, then to topmost-plus-occlusion. The other values pin a mode, for
    /// diagnosing a host that misbehaves under ownership.
    /// </summary>
    public string PlacementMode { get; set; } = "auto";

    [JsonIgnore]
    public string? SavedTo { get; private set; }

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    // Tolerant on read so a hand-edited file with // comments or a trailing comma still loads.
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static IEnumerable<string> CandidatePaths()
    {
        // Beside the exe first, so a portable copy carries its own settings.
        var exeDir = AppPaths.ExeDirectory();
        if (exeDir is not null)
            yield return Path.Combine(exeDir, "UsageBars.config.json");

        yield return AppPaths.FallbackConfigFile;
    }

    public static Config Load()
    {
        foreach (var path in CandidatePaths())
        {
            try
            {
                if (!File.Exists(path)) continue;
                var loaded = JsonSerializer.Deserialize<Config>(File.ReadAllText(path), ReadOptions);
                if (loaded is null) continue;
                loaded.SavedTo = path;
                loaded.Normalize();
                return loaded;
            }
            catch (Exception)
            {
                // Corrupt or unreadable config: fall through to defaults rather than refusing to start.
            }
        }

        return new Config();
    }

    /// <summary>
    /// Clamps hand-edited values into ranges the rest of the app can rely on, in place.
    ///
    /// The file is meant to be edited by hand, so it will contain typos. Every consumer treating
    /// each field defensively would be worse than one pass here: a zero
    /// <see cref="MinVelocitySamples"/> makes the projection trust a single reading, a negative
    /// <see cref="TitleBarHeight"/> puts the pill off-screen, and a <see cref="RedAtPercent"/>
    /// above <see cref="AmberAtPercent"/> makes the colour ramp run backwards. Records what it
    /// changed so the diagnostics dump can explain a value that isn't what the file says.
    /// </summary>
    public void Normalize()
    {
        var notes = new List<string>();

        void ClampDouble(string name, double value, double min, double max, Action<double> set)
        {
            var clamped = double.IsFinite(value) ? Math.Clamp(value, min, max) : min;
            if (Math.Abs(clamped - value) > double.Epsilon || !double.IsFinite(value))
            {
                notes.Add($"{name} {Fmt(value)} -> {Fmt(clamped)}");
                set(clamped);
            }
        }

        void ClampInt(string name, int value, int min, int max, Action<int> set)
        {
            var clamped = Math.Clamp(value, min, max);
            if (clamped != value)
            {
                notes.Add($"{name} {value} -> {clamped}");
                set(clamped);
            }
        }

        ClampDouble(nameof(TitleBarHeight), TitleBarHeight, 8, 200, v => TitleBarHeight = v);

        ClampDouble(nameof(RightReservePx), RightReservePx, 0, 2000, v => RightReservePx = v);
        ClampDouble(nameof(ScrapeIntervalMinutes), ScrapeIntervalMinutes, 1, 1440,
            v => ScrapeIntervalMinutes = v);
        ClampDouble(nameof(CodexRefreshMinutes), CodexRefreshMinutes, 0.25, 1440,
            v => CodexRefreshMinutes = v);
        ClampDouble(nameof(LowThresholdPct), LowThresholdPct, 0, 100, v => LowThresholdPct = v);
        ClampDouble(nameof(WarnHoursToEmpty), WarnHoursToEmpty, 0, 720, v => WarnHoursToEmpty = v);
        ClampDouble(nameof(VelocityPaddingFactor), VelocityPaddingFactor, 1, 10,
            v => VelocityPaddingFactor = v);
        ClampDouble(nameof(VelocityWindowHours), VelocityWindowHours, 0.25, 168,
            v => VelocityWindowHours = v);
        ClampDouble(nameof(FileStaleMinutes), FileStaleMinutes, 1, 1440, v => FileStaleMinutes = v);
        ClampDouble(nameof(ReadingStaleHours), ReadingStaleHours, 0.25, 168,
            v => ReadingStaleHours = v);
        ClampInt(nameof(MinVelocitySamples), MinVelocitySamples, 2, 1000,
            v => MinVelocitySamples = v);
        ClampInt(nameof(MinVelocitySpanMinutes), MinVelocitySpanMinutes, 1, 10080,
            v => MinVelocitySpanMinutes = v);
        ClampInt(nameof(ResetHour), ResetHour, 0, 23, v => ResetHour = v);

        ClampDouble(nameof(AmberAtPercent), AmberAtPercent, 0, 100, v => AmberAtPercent = v);
        ClampDouble(nameof(RedAtPercent), RedAtPercent, 0, 100, v => RedAtPercent = v);

        // Red is the more severe band, so it has to sit at or below amber. Lowering red rather
        // than raising amber keeps the user's "warn me early" intent if they only set one.
        if (RedAtPercent > AmberAtPercent)
        {
            notes.Add($"{nameof(RedAtPercent)} {Fmt(RedAtPercent)} -> {Fmt(AmberAtPercent)} (cannot exceed {nameof(AmberAtPercent)})");
            RedAtPercent = AmberAtPercent;
        }

        if (!IsValidRegex(TerminalTitlePattern))
        {
            notes.Add($"{nameof(TerminalTitlePattern)} is not a valid regex; using the default");
            TerminalTitlePattern = new Config().TerminalTitlePattern;
        }

        if (!IsValidRegex(CodexTitlePattern))
        {
            notes.Add($"{nameof(CodexTitlePattern)} is not a valid regex; using the default");
            CodexTitlePattern = new Config().CodexTitlePattern;
        }

        var placement = PlacementMode?.Trim().ToLowerInvariant();
        if (placement is not ("auto" or "owned" or "zorder" or "topmost"))
        {
            notes.Add($"{nameof(PlacementMode)} '{PlacementMode}' is not recognised; using auto");
            PlacementMode = "auto";
        }
        else
        {
            PlacementMode = placement;
        }

        if (!string.IsNullOrWhiteSpace(RequestCreditUrl) && !IsHttpUrl(RequestCreditUrl))
        {
            notes.Add($"{nameof(RequestCreditUrl)} is not an http(s) URL; no icon will be offered");
            RequestCreditUrl = "";
        }

        Adjustments = notes;

        static string Fmt(double v) =>
            double.IsFinite(v) ? v.ToString("0.###", CultureInfo.InvariantCulture) : "not-a-number";
    }

    /// <summary>What <see cref="Normalize"/> had to correct, for the diagnostics dump.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Adjustments { get; private set; } = Array.Empty<string>();

    private static bool IsValidRegex(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        try
        {
            _ = System.Text.RegularExpressions.Regex.Match("", pattern);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// The action icon opens this with UseShellExecute, so anything other than http(s) would hand
    /// an arbitrary scheme to the shell from a file on disk.
    /// </summary>
    private static bool IsHttpUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public void Save()
    {
        // Prefer the exe directory; fall back to LocalAppData when it isn't writable
        // (Program Files, a read-only share, a locked-down USB stick).
        foreach (var path in CandidatePaths())
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonSerializer.Serialize(this, WriteOptions));
                SavedTo = path;
                return;
            }
            catch (Exception)
            {
                // Try the next location.
            }
        }
    }
}
