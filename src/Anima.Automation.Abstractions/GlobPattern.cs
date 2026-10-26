namespace Anima.Automation;

/// <summary>
/// Case-insensitive glob matching for URLs: <c>*</c> matches any run of
/// characters (including <c>/</c>), <c>?</c> matches one. Deliberately not regex,
/// so a pattern from a script cannot become a backtracking hazard.
/// </summary>
public static class GlobPattern
{
    public static bool IsMatch(string? value, string? pattern)
    {
        if (pattern is null)
        {
            return true;
        }

        if (value is null)
        {
            return false;
        }

        int valueIndex = 0, patternIndex = 0, starIndex = -1, retryFrom = 0;

        while (valueIndex < value.Length)
        {
            if (patternIndex < pattern.Length
                && (pattern[patternIndex] == '?'
                    || char.ToUpperInvariant(pattern[patternIndex]) == char.ToUpperInvariant(value[valueIndex])))
            {
                valueIndex++;
                patternIndex++;
                continue;
            }

            if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                starIndex = patternIndex++;
                retryFrom = valueIndex;
                continue;
            }

            if (starIndex < 0)
            {
                return false;
            }

            patternIndex = starIndex + 1;
            valueIndex = ++retryFrom;
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
        {
            patternIndex++;
        }

        return patternIndex == pattern.Length;
    }
}
