namespace Anima.Bus;

/// <summary>
/// Segment-based topic matching. <c>*</c> matches exactly one segment,
/// <c>**</c> matches the remainder of the topic.
/// </summary>
public static class TopicMatcher
{
    public static bool IsMatch(string pattern, string topic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);

        var patternSegments = pattern.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var topicSegments = topic.Split('.', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < patternSegments.Length; i++)
        {
            var segment = patternSegments[i];
            if (segment == "**")
            {
                return true;
            }

            if (i >= topicSegments.Length)
            {
                return false;
            }

            if (segment != "*" && !string.Equals(segment, topicSegments[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return patternSegments.Length == topicSegments.Length;
    }
}
