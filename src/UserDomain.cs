using System;

namespace UsageBars;

/// <summary>
/// Whether the signed-in user belongs to a particular Windows domain.
///
/// Exists for one reason: a configured request-credit link may be an internal page that only
/// resolves inside one company's network. Anyone else running this build — it is a portable exe,
/// so it travels — would get an action icon leading nowhere. Gating the icon on domain membership
/// keeps the workplace behaviour without making the app useless elsewhere.
/// </summary>
internal static class UserDomain
{
    /// <summary>
    /// True when the current user is in <paramref name="expected"/>, or when no domain is
    /// required. An unset expectation means "no restriction", so the common case of someone
    /// pointing the URL at their own page keeps working without extra configuration.
    /// </summary>
    public static bool Matches(string? expected) =>
        Matches(expected, Current(), Environment.GetEnvironmentVariable("USERDNSDOMAIN"));

    /// <summary>Current NetBIOS domain, or the machine name when not domain-joined.</summary>
    public static string Current()
    {
        try { return Environment.UserDomainName ?? ""; }
        catch (Exception) { return ""; }
    }

    /// <summary>The DNS domain, when the machine is joined to one.</summary>
    public static string? CurrentDns()
    {
        try { return Environment.GetEnvironmentVariable("USERDNSDOMAIN"); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Testable core. Both forms of the domain name are considered, because the two are reported
    /// separately and either may be the one that matches: <c>CONTOSO</c> from
    /// <c>UserDomainName</c>, and something like <c>corp.contoso.com</c> from
    /// <c>USERDNSDOMAIN</c>.
    /// </summary>
    internal static bool Matches(string? expected, string? netbios, string? dns)
    {
        if (string.IsNullOrWhiteSpace(expected)) return true;

        var wanted = expected.Trim();

        if (!string.IsNullOrWhiteSpace(netbios)
            && netbios.Trim().Equals(wanted, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(dns)) return false;

        // Compared label by label rather than by substring, so "notcontoso.com" does not match
        // "contoso" while "corp.contoso.com" does.
        foreach (var label in dns.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (label.Trim().Equals(wanted, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>Human-readable current domain, for the diagnostics dump.</summary>
    public static string Describe()
    {
        var netbios = Current();
        var dns = CurrentDns();
        return string.IsNullOrWhiteSpace(dns) ? netbios : $"{netbios} ({dns})";
    }
}
