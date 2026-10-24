namespace Anima.Workflow;

public sealed class WorkflowOptions
{
    /// <summary>Maximum characters stored/returned for summaries and output tails.</summary>
    public int ContentLimit { get; set; } = 4000;

    /// <summary>Publish workflow events on the bus. Failures never fail the API call.</summary>
    public bool PublishEvents { get; set; } = true;

    /// <summary>Maximum page size for workflow queries.</summary>
    public int MaxPageSize { get; set; } = 200;
}
