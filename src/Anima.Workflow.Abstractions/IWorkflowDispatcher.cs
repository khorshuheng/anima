namespace Anima.Workflow;

/// <summary>
/// Publishes a role task to an external agent's bus inbox as a best-effort
/// wake-up. It does not wait for a reply and does not create, start or stop the
/// agent. Durable results belong in the workflow API.
/// </summary>
public interface IWorkflowDispatcher
{
    Task<DispatchResult> DispatchAsync(
        long workflowId,
        DispatchRequest request,
        CancellationToken cancellationToken = default);
}

public interface IWorkflowEventPublisher
{
    Task PublishAsync(WorkflowEvent workflowEvent, CancellationToken cancellationToken = default);
}
