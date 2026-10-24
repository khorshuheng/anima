namespace Anima.Workflow;

/// <summary>
/// Sends a role task to an already-running external agent over the bus and waits
/// for its reply. It does not create, start or stop the agent.
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
