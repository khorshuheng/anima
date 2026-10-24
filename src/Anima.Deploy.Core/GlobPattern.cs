using System.Text.RegularExpressions;

namespace Anima.Deploy;

/// <summary>
/// Repository and ref matching for deploy rules. <c>*</c> stays within one path
/// segment, <c>**</c> crosses segments, <c>?</c> matches one character.
/// </summary>
public static class GlobPattern
{
    public static bool IsMatch(string pattern, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentNullException.ThrowIfNull(value);

        if (pattern == "*")
        {
            return true;
        }

        var regex = "^" + Regex.Escape(pattern)
            .Replace(@"\*\*", ".*", StringComparison.Ordinal)
            .Replace(@"\*", "[^/]*", StringComparison.Ordinal)
            .Replace(@"\?", ".", StringComparison.Ordinal) + "$";

        return Regex.IsMatch(
            value,
            regex,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
    }
}
