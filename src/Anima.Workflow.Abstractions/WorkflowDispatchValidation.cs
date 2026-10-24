using System.Text.Json;

namespace Anima.Workflow;

/// <summary>
/// Validates a dispatch request before any durable assignment is written. The
/// bus publication itself is best-effort, but invalid input must not leave
/// workflow state behind.
/// </summary>
public static class WorkflowDispatchValidation
{
    public static void Validate(DispatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.AgentId))
        {
            throw new WorkflowValidationException("agentId is required");
        }

        if (string.IsNullOrWhiteSpace(request.Role))
        {
            throw new WorkflowValidationException("role is required");
        }

        if (string.IsNullOrWhiteSpace(request.ContextJson))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(request.ContextJson);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                throw new WorkflowValidationException("contextJson must be a JSON object");
            }
        }
        catch (JsonException exception)
        {
            throw new WorkflowValidationException($"contextJson is invalid: {exception.Message}");
        }
    }
}
