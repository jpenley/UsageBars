using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UsageBars;

internal static class ActivityProcessName
{
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = (value ?? "").Trim();
        if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^4].TrimEnd();
        if (normalized.Length == 0
            || normalized.IndexOfAny(new[] { '\\', '/', ':' }) >= 0
            || normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || normalized is "." or "..")
        {
            normalized = "";
            return false;
        }
        return true;
    }

    public static List<string> NormalizeMany(IEnumerable<string> values) => values
        .Select(value => TryNormalize(value, out var normalized) ? normalized : null)
        .Where(value => value is not null)
        .Select(value => value!.ToLowerInvariant())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
        .ToList();
}
