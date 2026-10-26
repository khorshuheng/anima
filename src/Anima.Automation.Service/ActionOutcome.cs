namespace Anima.Automation.Service;

/// <summary>
/// Result of one session action: the payload a single-action request returns and
/// the short text a batch reports for the step. Thrown failures carry the message.
/// </summary>
internal readonly record struct ActionOutcome(object? Payload, string? Output);

/// <summary>
/// The session closed while an action was waiting on it. Distinct from a bad
/// request because retrying is pointless, so it maps to 409 rather than 400.
/// </summary>
public sealed class SessionClosedException(string message) : Exception(message);
