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

        string goalPath = repoId is { } scope
            ? $"api/nodes?within={scope}&via=belongs_to&descend=part_of&kind=goal&scope=all&summary=true"
            : "api/nodes?kind=goal&scope=all&summary=true";
        var (goalNodes, _) = await QueryAllAsync(knowledge, goalPath, cancellationToken);
        var workflows = await workflow.GetFromJsonAsync<WorkflowEnvelope>("api/workflows", JsonOptions, cancellationToken)
            ?? new WorkflowEnvelope([]);

        var workflowByGoal = workflows.Items
            .Where(item => item.GoalId is not null)
            .GroupBy(item => item.GoalId!.Value)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.Id).First().Status);

        using var gate = new SemaphoreSlim(8);
        var taskLists = await Task.WhenAll(goalNodes.Select(async goal =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                string taskPath =
                    $"api/nodes?within={goal.Id}&via=part_of&descend=part_of&kind=task&scope=all&summary=true";
                return await QueryAllAsync(knowledge, taskPath, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }));

        var items = goalNodes.Select((goal, index) =>
        {
            var (tasks, tasksComplete) = taskLists[index];
            int resolvedTasks = tasks.Count(task => task.Resolved);
            string? workflowStatus = workflowByGoal.GetValueOrDefault(goal.Id);
            var completion = GoalCompletion.Compute(
                goal.Resolved,
                workflowStatus,
                tasks.Count,
                resolvedTasks,
                tasksComplete);

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

    private static async Task<(IReadOnlyList<NodeDto> Items, bool Complete)> QueryAllAsync(
        HttpClient client,
        string path,
        CancellationToken cancellationToken)
    {
        const int pageSize = 500;
        var items = new List<NodeDto>();
        for (int offset = 0; offset < 10_000; offset += pageSize)
        {
            string separator = path.Contains('?') ? "&" : "?";
            var page = await client.GetFromJsonAsync<NodeEnvelope>(
                $"{path}{separator}limit={pageSize}&offset={offset}",
                JsonOptions,
                cancellationToken) ?? new NodeEnvelope([], 0);
            items.AddRange(page.Items);
            if (items.Count >= page.Total || page.Items.Count == 0)
            {
                return (items, true);
            }
        }

        return (items, false);
    }

    private sealed record NodeDto(
        long Id,
        string Name,
        string Description,
        bool Resolved);

    private sealed record NodeEnvelope(List<NodeDto> Items, int Total = 0);

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
        int resolvedTaskCount,
        bool tasksComplete = true)
    {
        if (directlyResolved)
        {
            return new GoalCompletionResult(true, Direct);
        }

        if (string.Equals(workflowStatus, "completed", StringComparison.OrdinalIgnoreCase))
        {
            return new GoalCompletionResult(true, Workflow);
        }

        // A workflow is the system of record for its goal. If one exists but is
        // not completed, task-level completion must not override it.
        if (!string.IsNullOrWhiteSpace(workflowStatus))
        {
            return new GoalCompletionResult(false, Open);
        }

        if (tasksComplete && taskCount > 0 && resolvedTaskCount == taskCount)
        {
            return new GoalCompletionResult(true, Tasks);
        }

        return new GoalCompletionResult(false, Open);
    }
}

public sealed record GoalCompletionResult(bool Resolved, string Source);
