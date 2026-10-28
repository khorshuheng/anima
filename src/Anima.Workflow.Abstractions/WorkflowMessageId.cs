namespace Anima.Workflow;

/// <summary>
/// Normalizes caller-supplied dispatch message ids. The id is used in bus
/// topics by some callers, so only a conservative alphabet is accepted.
/// </summary>
public static class WorkflowMessageId
{
    public const int MaxLength = 64;

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Guid.NewGuid().ToString("n");
        }

        string messageId = value.Trim();
        if (messageId.Length > MaxLength ||
            !messageId.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            throw new WorkflowValidationException(
                "messageId must be 1-64 ASCII letters, digits, '-' or '_'");
        }

        return messageId;
    }
}
