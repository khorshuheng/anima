using System.Text.Json;

namespace Anima.Dashboard.Service;

public sealed record GoalExplorerItem(
    long Id,
    string Name,
    string Description,
    bool Resolved,
    int TaskCount,
    int ResolvedTaskCount,
    string? WorkflowStatus,
    string CompletionSource);

public sealed record GoalExplorerResponse(IReadOnlyList<GoalExplorerItem> Items, int Total);

/// <summary>
/// Joins goals, their repository scope, linked workflows and child tasks. This is
/// intentionally a read-only BFF query: the knowledge and workflow services stay
/// the systems of record.
/// </summary>
public sealed class GoalExplorer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;

    public GoalExplorer(IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _httpClientFactory = httpClientFactory;
    }

    public async Task<GoalExplorerResponse> GetGoalsAsync(
        long? repoId,
        bool? resolved,
        CancellationToken cancellationToken = default)
    {
        var knowledge = _httpClientFactory.CreateClient(DashboardUpstreams.Knowledge);
        var workflow = _httpClientFactory.CreateClient(DashboardUpstreams.Workflow);

        var goalPath = repoId is { } scope
            ? $"api/nodes?within={scope}&via=belongs_to&descend=part_of&kind=goal&scope=all&summary=true&limit=500"
            : "api/nodes?kind=goal&scope=all&summary=true&limit=500";
        var goals = await knowledge.GetFromJsonAsync<NodeEnvelope>(goalPath, JsonOptions, cancellationToken)
            ?? new NodeEnvelope([]);
        var workflows = await workflow.GetFromJsonAsync<WorkflowEnvelope>("api/workflows", JsonOptions, cancellationToken)
            ?? new WorkflowEnvelope([]);

        var workflowByGoal = workflows.Items
            .Where(item => item.GoalId is not null)
            .GroupBy(item => item.GoalId!.Value)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.Id).First().Status);

        var goalNodes = goals.Items;
        var taskLists = await Task.WhenAll(goalNodes.Select(async goal =>
        {
            var taskPath =
                $"api/nodes?within={goal.Id}&via=part_of&scope=all&summary=true&limit=500";
            var tasks = await knowledge.GetFromJsonAsync<NodeEnvelope>(taskPath, JsonOptions, cancellationToken)
                ?? new NodeEnvelope([]);
            return tasks.Items;
        }));

        var items = goalNodes.Select((goal, index) =>
        {
            var tasks = taskLists[index];
            var resolvedTasks = tasks.Count(task => task.Resolved);
            var workflowStatus = workflowByGoal.GetValueOrDefault(goal.Id);
            var completion = GoalCompletion.Compute(
                goal.Resolved,
                workflowStatus,
                tasks.Count,
                resolvedTasks);

            return new GoalExplorerItem(
                goal.Id,
                goal.Name,
                goal.Description,
                completion.Resolved,
                tasks.Count,
                resolvedTasks,
                workflowStatus,
                completion.Source);
        });

        if (resolved is { } completed)
        {
            items = items.Where(item => item.Resolved == completed);
        }

        var rows = items.ToArray();
        return new GoalExplorerResponse(rows, rows.Length);
    }

    private sealed record NodeDto(
        long Id,
        string Name,
        string Description,
        bool Resolved);

    private sealed record NodeEnvelope(List<NodeDto> Items);

    private sealed record WorkflowDto(long Id, long? GoalId, string Status);

    private sealed record WorkflowEnvelope(List<WorkflowDto> Items);
}

public static class GoalCompletion
{
    public const string Direct = "goal";
    public const string Workflow = "workflow";
    public const string Tasks = "tasks";
    public const string Open = "open";

    public static GoalCompletionResult Compute(
        bool directlyResolved,
        string? workflowStatus,
        int taskCount,
        int resolvedTaskCount)
    {
        if (directlyResolved)
        {
            return new GoalCompletionResult(true, Direct);
        }

        if (string.Equals(workflowStatus, "completed", StringComparison.OrdinalIgnoreCase))
        {
            return new GoalCompletionResult(true, Workflow);
        }

        if (taskCount > 0 && resolvedTaskCount == taskCount)
        {
            return new GoalCompletionResult(true, Tasks);
        }

        return new GoalCompletionResult(false, Open);
    }
}

public sealed record GoalCompletionResult(bool Resolved, string Source);
