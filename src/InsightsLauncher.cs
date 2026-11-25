using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Serialization;

namespace UsageBars;

/// <summary>
/// Answers for the "request more usage" form. Lives in the local config only: the shipped default
/// leaves every value empty so nothing about the user or their employer is ever committed.
/// </summary>
internal sealed class RequestUsageSettings
{
    /// <summary>Address of the request form. Empty means "do not prepare a request".</summary>
    public string Url { get; set; } = "";

    /// <summary>Standing answer to "how are you choosing which models for which tasks".</summary>
    public string ModelChoice { get; set; } = "";

    /// <summary>
    /// Which use-of-tokens boxes to tick, by their form code: "3a", "3b", "3c" or "3d".
    /// Deliberately explicit rather than defaulted, because ticking one is an attestation.
    /// </summary>
    public List<string> TokenUse { get; set; } = new();

    /// <summary>Optional free text for the final "anything else" field.</summary>
    public string ExtraNotes { get; set; } = "";

    [JsonIgnore]
    public bool HasUsableUrl =>
        Uri.TryCreate(Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}

/// <summary>
/// Starts the low-credit hand-off in a brand-new terminal window.
///
/// Everything runs in processes this class starts itself. It never sends keystrokes to, attaches
/// to, or reuses an existing terminal or Claude session: /insights gets its own fresh Claude Code
/// instance, and the optional second step is a second fresh instance. That second instance is told
/// to fill the request form and stop, and never to submit it.
/// </summary>
internal static class InsightsLauncher
{
    internal const string FillInstructionsFile = "fill-request.md";
    internal const string ScriptFile = "run-insights.ps1";

    /// <summary>Where /insights writes the report the form's first three answers come from.</summary>
    public static string ReportPath
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".claude", "usage-data", "report.html");
        }
    }

    /// <summary>Single-quote a value for PowerShell source.</summary>
    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>
    /// The PowerShell the new window runs. The probe's CLAUDE_CODE_CHILD_SESSION marker is removed
    /// first: UsageBars sets it on its own process for the hidden /usage reader, and the new
    /// instance must be an ordinary session, not one flagged as a child probe.
    /// </summary>
    internal static string BuildScript(string claudePath, bool withRequest)
    {
        var claude = Quote(claudePath);
        var text = new StringBuilder();
        text.AppendLine("$ErrorActionPreference = 'Continue'");
        text.AppendLine("$Host.UI.RawUI.WindowTitle = 'UsageBars: Claude insights'");
        text.AppendLine("Remove-Item Env:CLAUDE_CODE_CHILD_SESSION -ErrorAction SilentlyContinue");
        text.AppendLine("Write-Host 'UsageBars: your Claude credit is projected to run out soon.'");
        text.AppendLine("Write-Host 'Starting a separate Claude Code session to run /insights. Your other sessions are untouched.'");
        text.AppendLine(withRequest
            ? "Write-Host 'When the report is done, type /exit to continue to the request form.'"
            : "Write-Host 'When the report is done, type /exit to close this session.'");
        text.AppendLine($"& {claude} '/insights'");
        if (withRequest)
        {
            text.AppendLine("Write-Host ''");
            text.AppendLine("Write-Host 'Next: a second new Claude Code session will prepare the request form. It will NOT submit it.'");
            text.AppendLine("Write-Host 'Press Enter to continue, or close this window to skip.'");
            text.AppendLine("[void](Read-Host)");
            text.AppendLine($"& {claude} 'Follow the instructions in {FillInstructionsFile} in the current directory.'");
        }
        return text.ToString();
    }

    /// <summary>
    /// What the second Claude instance is told to do. Written next to the script in the data
    /// folder, which is untracked, so the user's answers are never part of the repository.
    /// </summary>
    internal static string BuildFillInstructions(RequestUsageSettings request, string reportPath)
    {
        var ticks = new List<string>();
        foreach (var code in request.TokenUse)
            if (!string.IsNullOrWhiteSpace(code)) ticks.Add(code.Trim().ToLowerInvariant());

        var text = new StringBuilder();
        text.AppendLine("# Prepare the request-more-usage form (do not submit)");
        text.AppendLine();
        text.AppendLine("You were started by UsageBars because the user's Claude credit is nearly used up.");
        text.AppendLine("Your only job is to open the request form and fill it in. The user reviews and submits it.");
        text.AppendLine();
        text.AppendLine("## Hard rules");
        text.AppendLine();
        text.AppendLine("- Use the Playwright MCP browser tools only.");
        text.AppendLine("- **Never click Run, Submit, Confirm-run or any button that starts the runbook.** Do not press Enter in a way that submits the form. Stop with the form filled and unsubmitted.");
        text.AppendLine("- Never type a username or password. Never read, copy or store credentials.");
        text.AppendLine("- If a field cannot be found, say so and leave it empty. Do not guess.");
        text.AppendLine();
        text.AppendLine("## Steps");
        text.AppendLine();
        text.AppendLine("1. Check that this file exists and was modified in the last 30 minutes, otherwise tell the user /insights did not finish and stop:");
        text.AppendLine($"   `{reportPath}`");
        text.AppendLine($"2. Navigate the Playwright browser to: {request.Url}");
        text.AppendLine("3. The browser may not be signed in. If you land on a sign-in page (URL contains `sign-in`, or the heading says to sign in), tell the user to sign in in the Playwright browser window themselves, then wait: take a snapshot about every 10 seconds for up to 10 minutes until the runbook form is showing. If you ended up somewhere other than the form after sign-in, navigate to the URL above once more.");
        text.AppendLine("4. Fill the form fields:");
        text.AppendLine();
        text.AppendLine("| Field | Value |");
        text.AppendLine("|---|---|");
        text.AppendLine("| 1a (What you work on) | Plain text of the **What You Work On** section of the report |");
        text.AppendLine("| 1b (How you use Claude Code) | Plain text of the **How You Use Claude Code** section of the report |");
        text.AppendLine("| 1c (Impressive things you did) | Plain text of the **Impressive Things You Did** section of the report |");
        text.AppendLine("| 2 (model choice) | The text under \"Field 2 answer\" below, exactly |");
        text.AppendLine("| 3 (confirmation dropdown) | Select **Confirm** |");
        text.AppendLine($"| 3a / 3b / 3c / 3d (checkboxes) | Tick only: {(ticks.Count == 0 ? "(none)" : string.Join(", ", ticks))}. Leave the others unticked |");
        text.AppendLine($"| 4 (anything else) | {(string.IsNullOrWhiteSpace(request.ExtraNotes) ? "Leave empty" : "The text under \"Field 4 answer\" below, exactly")} |");
        text.AppendLine();
        text.AppendLine("   Take the section text from the report's HTML as plain text, without markup and without summarising it.");
        text.AppendLine("5. Re-read the form to confirm each value landed. Then stop, tell the user the form is filled and waiting for their review, and remind them you did not submit it.");
        text.AppendLine();
        text.AppendLine("## Field 2 answer");
        text.AppendLine();
        text.AppendLine(request.ModelChoice);
        if (!string.IsNullOrWhiteSpace(request.ExtraNotes))
        {
            text.AppendLine();
            text.AppendLine("## Field 4 answer");
            text.AppendLine();
            text.AppendLine(request.ExtraNotes);
        }
        return text.ToString();
    }

    /// <summary>Opens the new terminal. Returns false, with the reason, when it could not.</summary>
    public static bool Launch(Config config, out string detail)
    {
        try
        {
            var claude = ClaudeCli.Find();
            if (claude is null)
            {
                detail = "Claude Code CLI not found";
                return false;
            }

            var dir = AppPaths.InsightsDir;
            Directory.CreateDirectory(dir);

            var request = config.RequestUsage;
            var withRequest = request is { HasUsableUrl: true };
            var instructionsPath = Path.Combine(dir, FillInstructionsFile);
            if (withRequest)
                File.WriteAllText(instructionsPath, BuildFillInstructions(request!, ReportPath));
            else if (File.Exists(instructionsPath))
                File.Delete(instructionsPath);

            var scriptPath = Path.Combine(dir, ScriptFile);
            File.WriteAllText(scriptPath, BuildScript(claude, withRequest));

            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var powershell = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
            var info = new ProcessStartInfo
            {
                FileName = File.Exists(powershell) ? powershell : "powershell.exe",
                Arguments = $"-NoLogo -NoExit -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                WorkingDirectory = dir,
                UseShellExecute = false,
                CreateNoWindow = false,
            };
            info.Environment.Remove("CLAUDE_CODE_CHILD_SESSION");

            using var process = Process.Start(info);
            detail = withRequest
                ? "started /insights in a new terminal; request form instructions prepared"
                : "started /insights in a new terminal";
            return process is not null;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
    }
}
