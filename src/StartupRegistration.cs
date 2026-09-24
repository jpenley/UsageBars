using System;
using System.IO;
using Microsoft.Win32;

namespace UsageBars;

/// <summary>Abstracts the registry so the toggle logic is testable without touching HKCU.</summary>
internal interface IStartupStore
{
    string? Read();
    void Write(string value);
    void Delete();
}

/// <summary>
/// What the Run entry will actually do at the next logon.
///
/// The distinction that matters is between the last two. "An entry exists" was the only question
/// asked before, and it is the wrong one: a Run value naming an executable that isn't there fails
/// completely silently — no error, no notification, no event log — while the menu goes on showing
/// a tick. Measured three times on this machine, across two renamed build directories.
/// </summary>
internal enum StartupState
{
    /// <summary>No entry. Nothing starts, which is what the unticked menu item means.</summary>
    Off,

    /// <summary>An entry naming this executable. This copy starts.</summary>
    On,

    /// <summary>
    /// An entry naming a different executable that does exist. Something starts, but not this
    /// copy — so the tick would be a lie about which one.
    /// </summary>
    OtherCopy,

    /// <summary>
    /// An entry whose target is gone. Nothing starts, and nothing says so. The failure this
    /// whole type exists to make visible.
    /// </summary>
    Dangling,
}

/// <summary>
/// "Start with Windows", as a per-user Run entry.
///
/// Current-user only: no admin rights, no service, no scheduled task, and removing it is a single
/// registry value. That keeps the portable story intact — this is the one thing UsageBars writes
/// outside its own folder, and only when explicitly asked.
/// </summary>
internal sealed class StartupRegistration
{
    /// <summary>The exact value a user would need to delete by hand, quoted in the README.</summary>
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "UsageBars";

    private readonly IStartupStore _store;
    private readonly Func<string?> _exePath;
    private readonly Func<string, bool> _exists;

    public StartupRegistration()
        : this(new RegistryStartupStore(), () => Environment.ProcessPath, File.Exists)
    {
    }

    internal StartupRegistration(
        IStartupStore store, Func<string?> exePath, Func<string, bool>? exists = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _exePath = exePath ?? throw new ArgumentNullException(nameof(exePath));

        // Defaulted rather than required so the existing tests that only care about the toggle
        // don't all have to grow a filesystem stub.
        _exists = exists ?? File.Exists;
    }

    /// <summary>
    /// What the current entry will do at the next logon. The single question everything else here
    /// is derived from — see <see cref="StartupState"/> for why "does an entry exist" isn't it.
    /// </summary>
    public StartupState Current()
    {
        try
        {
            var registered = _store.Read();
            if (string.IsNullOrWhiteSpace(registered)) return StartupState.Off;

            // Expanded before use: the value is read raw so diagnostics can quote it verbatim, but
            // another writer may have stored a REG_EXPAND_SZ, and a literal "%USERPROFILE%\..."
            // exists on no filesystem — it would look dangling and get needlessly rewritten.
            var target = Environment.ExpandEnvironmentVariables(TargetOf(registered));
            if (target.Length == 0) return StartupState.Dangling;

            var exe = _exePath();
            if (!string.IsNullOrWhiteSpace(exe)
                && target.Equals(exe, StringComparison.OrdinalIgnoreCase))
            {
                return StartupState.On;
            }

            return _exists(target) ? StartupState.OtherCopy : StartupState.Dangling;
        }
        catch (Exception)
        {
            // Unreadable registry: report the state that claims the least.
            return StartupState.Off;
        }
    }

    /// <summary>
    /// The executable a Run value names, without its quotes or any arguments following it.
    ///
    /// A Run value is a command line rather than a path. Ours is only ever a quoted exe, but this
    /// also reads entries written by an older build or edited by hand, and treating " --minimize"
    /// as part of the filename would make every such entry look dangling.
    /// </summary>
    internal static string TargetOf(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith('"')) return trimmed;

        var end = trimmed.IndexOf('"', 1);
        return end > 1 ? trimmed[1..end] : trimmed.Trim('"');
    }

    /// <summary>
    /// Repoints a dangling entry at this executable, returning true when it rewrote one.
    ///
    /// Deliberately only the dangling case. Repointing an entry that names a different copy which
    /// still <em>works</em> would mean any build you launch once quietly takes over what starts at
    /// logon — run the Debug build to test something and it inherits your startup. That case is
    /// left for the user to resolve, and the menu now shows it plainly enough to act on.
    /// </summary>
    public bool TryRepair()
    {
        if (Current() != StartupState.Dangling) return false;

        SetEnabled(true);
        return true;
    }

    /// <summary>
    /// Registers or removes the entry. Throws on failure so the caller can roll the menu item back
    /// rather than showing a tick that doesn't reflect the registry.
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            _store.Delete();
            return;
        }

        var exe = _exePath();
        if (string.IsNullOrWhiteSpace(exe))
            throw new InvalidOperationException("could not determine the UsageBars executable path");

        // Quoted: the path routinely contains spaces, and an unquoted Run value would be parsed
        // as a command plus arguments.
        _store.Write($"\"{exe}\"");
    }

    /// <summary>Current registered command, for diagnostics.</summary>
    public string? RegisteredCommand()
    {
        try { return _store.Read(); }
        catch (Exception) { return null; }
    }
}

internal sealed class RegistryStartupStore : IStartupStore
{
    public string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            StartupRegistration.RunKeyPath, writable: false);
        return key?.GetValue(
            StartupRegistration.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
            as string;
    }

    public void Write(string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(
            StartupRegistration.RunKeyPath, writable: true);
        key.SetValue(StartupRegistration.ValueName, value, RegistryValueKind.String);
    }

    public void Delete()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            StartupRegistration.RunKeyPath, writable: true);
        key?.DeleteValue(StartupRegistration.ValueName, throwOnMissingValue: false);
    }
}
