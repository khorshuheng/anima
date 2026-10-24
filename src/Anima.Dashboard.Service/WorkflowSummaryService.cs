using System.Net;
using System.Text.Json;

namespace Anima.Dashboard.Service;

public sealed record SummaryTaskBreakdown(
    int Total,
    int Implementation,
    int Merge,
    int Review,
    int Verification,
    int Documentation);

public sealed record SummaryAgents(
    int RecordedAgents,
    int RecordedRuns,
    IReadOnlyList<string> AgentIds);

public sealed record SummaryResults(
    int Total,
    int Implementation,
    int Merge,
    int Review,
    int Verification,
    int Findings,
    int Errors,
    int Warnings);

public sealed record SummaryEvidence(
    int ImplementationResults,
    int ImplementationWithHeadSha,
    int ImplementationWithTests,
    int ReviewResults,
    int ReviewWithHeadSha,
    int ReviewWithTests);

public sealed record SummaryReview(
    int Index,
    long ResultId,
    DateTimeOffset CreatedAt,
    bool Success,
    string? AgentId,
    string? HeadSha,
    IReadOnlyList<string> TestCommands,
    int FindingCount,
    string Summary);

public sealed record SummaryAssignment(
    long Id,
    string TaskKey,
    string AgentId,
    string Status,
    string? MessageId,
    DateTimeOffset? AcknowledgedAt,
    string? RunId,
    DateTimeOffset UpdatedAt);

public sealed record SummaryPhase(
    long Id,
    string Name,
    string Status,
    string? Summary,
    DateTimeOffset CreatedAt);

public sealed record WorkflowRunSummary(
    long Id,
    string Name,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long DurationMs,
    int? SpecVersion,
    string? SpecHash,
    SummaryTaskBreakdown Tasks,
    SummaryAgents Agents,
    SummaryResults Results,
    SummaryEvidence Evidence,
    IReadOnlyList<SummaryReview> ReviewIterations,
    IReadOnlyList<SummaryAssignment> Assignments,
    IReadOnlyList<SummaryPhase> Phases);

/// <summary>
/// Read-only aggregation for the Workflows tab. The workflow service remains the
/// source of truth; this only joins its existing read endpoints.
/// </summary>
public sealed class WorkflowSummaryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;

    public WorkflowSummaryService(IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _httpClientFactory = httpClientFactory;
    }

    public async Task<WorkflowRunSummary?> GetAsync(long workflowId, CancellationToken cancellationToken = default)
    {
        var client = _httpClientFactory.CreateClient(DashboardUpstreams.Workflow);
        var state = await GetOptionalAsync<WorkflowStateDto>(
            client,
            $"api/workflows/{workflowId}",
            cancellationToken);
        if (state is null)
        {
            return null;
        }

        var specTask = GetOptionalAsync<SpecDto>(client, $"api/workflows/{workflowId}/spec", cancellationToken);
        var assignmentTask = GetAsync<ItemEnvelope<AssignmentDto>>(
            client,
            $"api/workflows/{workflowId}/assignments",
            cancellationToken);
        var resultTask = GetAsync<ItemEnvelope<ResultDto>>(
            client,
            $"api/workflows/{workflowId}/results",
            cancellationToken);
        var timelineTask = GetAsync<TimelineDto>(
            client,
            $"api/workflows/{workflowId}/timeline",
            cancellationToken);

        await Task.WhenAll(specTask, assignmentTask, resultTask, timelineTask);
        return WorkflowSummaryBuilder.Build(
            state,
            specTask.Result,
            assignmentTask.Result.Items,
            resultTask.Result.Items,
            timelineTask.Result.Phases);
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string path, CancellationToken cancellationToken)
        where T : notnull
    {
        var value = await client.GetFromJsonAsync<T>(path, JsonOptions, cancellationToken);
        return value ?? throw new JsonException($"empty response from {path}");
    }

    private static async Task<T?> GetOptionalAsync<T>(HttpClient client, string path, CancellationToken cancellationToken)
        where T : class
    {
        using var response = await client.GetAsync(path, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
    }

    public sealed record WorkflowStateDto(
        long Id,
        string Name,
        string Status,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        int? SpecVersion,
        string? SpecHash);

    public sealed record SpecTaskDto(string Key, string Title, string? Role);

    public sealed record SpecDto(IReadOnlyList<SpecTaskDto> Tasks);

    public sealed record AssignmentDto(
        long Id,
        string TaskKey,
        string AgentId,
        string Status,
        string? MessageId,
        DateTimeOffset? AcknowledgedAt,
        string? RunId,
        DateTimeOffset UpdatedAt);

    public sealed record ResultDto(
        long Id,
        string TaskKey,
        string Kind,
        bool Success,
        string Summary,
        string? AgentId,
        string? HeadSha,
        string? RunId,
        IReadOnlyList<string> TestCommands,
        IReadOnlyList<FindingDto> Findings,
        DateTimeOffset CreatedAt);

    public sealed record FindingDto(string Severity, string Message);

    public sealed record PhaseDto(long Id, string Name, string Status, string? Summary, DateTimeOffset CreatedAt);

    public sealed record TimelineDto(IReadOnlyList<PhaseDto> Phases);

    public sealed record ItemEnvelope<T>(IReadOnlyList<T> Items);
}

public static class WorkflowSummaryBuilder
{
    public static WorkflowRunSummary Build(
        WorkflowSummaryService.WorkflowStateDto state,
        WorkflowSummaryService.SpecDto? spec,
        IReadOnlyList<WorkflowSummaryService.AssignmentDto> assignments,
        IReadOnlyList<WorkflowSummaryService.ResultDto> results,
        IReadOnlyList<WorkflowSummaryService.PhaseDto> phases)
    {
        var tasks = spec?.Tasks ?? [];
        var taskBreakdown = new SummaryTaskBreakdown(
            tasks.Count,
            tasks.Count(task => Role(task.Role) == "implementation"),
            tasks.Count(task => Role(task.Role) == "merge"),
            tasks.Count(task => Role(task.Role) == "review"),
            tasks.Count(task => Role(task.Role) == "verification"),
            tasks.Count(task => Role(task.Role) == "documentation"));

        var agentIds = assignments.Select(assignment => assignment.AgentId)
            .Concat(results.Where(result => result.AgentId is not null).Select(result => result.AgentId!))
            .Where(agent => !string.IsNullOrWhiteSpace(agent))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var runIds = assignments.Where(assignment => !string.IsNullOrWhiteSpace(assignment.RunId)).Select(assignment => assignment.RunId!)
            .Concat(results.Where(result => !string.IsNullOrWhiteSpace(result.RunId)).Select(result => result.RunId!))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var allReviews = results
            .Where(result => Kind(result.Kind) == "review")
            .ToArray();
        var reviews = allReviews
            .OrderBy(result => result.CreatedAt)
            .ThenBy(result => result.Id)
            .Select((result, index) => new SummaryReview(
                index + 1,
                result.Id,
                result.CreatedAt,
                result.Success,
                result.AgentId,
                result.HeadSha,
                result.TestCommands,
                result.Findings.Count,
                result.Summary))
            .ToArray();

        var implementation = results
            .Where(result => Kind(result.Kind) == "implementation" && result.Success)
            .ToArray();
        var successfulReviews = allReviews.Where(review => review.Success).ToArray();
        var evidence = new SummaryEvidence(
            implementation.Length,
            implementation.Count(result => !string.IsNullOrWhiteSpace(result.HeadSha)),
            implementation.Count(result => result.TestCommands.Any(command => !string.IsNullOrWhiteSpace(command))),
            successfulReviews.Length,
            successfulReviews.Count(review => !string.IsNullOrWhiteSpace(review.HeadSha)),
            successfulReviews.Count(review => review.TestCommands.Any(command => !string.IsNullOrWhiteSpace(command))));

        var findings = results.SelectMany(result => result.Findings).ToArray();
        var resultSummary = new SummaryResults(
            results.Count,
            results.Count(result => Kind(result.Kind) == "implementation"),
            results.Count(result => Kind(result.Kind) == "merge"),
            results.Count(result => Kind(result.Kind) == "review"),
            results.Count(result => Kind(result.Kind) == "verification"),
            findings.Length,
            findings.Count(finding => Kind(finding.Severity) == "error"),
            findings.Count(finding => Kind(finding.Severity) == "warning"));

        return new WorkflowRunSummary(
            state.Id,
            state.Name,
            state.Status,
            state.CreatedAt,
            state.UpdatedAt,
            Math.Max(0, (long)(state.UpdatedAt - state.CreatedAt).TotalMilliseconds),
            state.SpecVersion,
            state.SpecHash,
            taskBreakdown,
            new SummaryAgents(agentIds.Length, runIds.Length, agentIds),
            resultSummary,
            evidence,
            reviews,
            assignments.Select(assignment => new SummaryAssignment(
                assignment.Id,
                assignment.TaskKey,
                assignment.AgentId,
                assignment.Status,
                assignment.MessageId,
                assignment.AcknowledgedAt,
                assignment.RunId,
                assignment.UpdatedAt)).ToArray(),
            phases.Select(phase => new SummaryPhase(
                phase.Id,
                phase.Name,
                phase.Status,
                phase.Summary,
                phase.CreatedAt)).ToArray());
    }

    private static string Role(string? role) => string.IsNullOrWhiteSpace(role)
        ? "implementation"
        : role.ToLowerInvariant();

    private static string Kind(string? kind) => kind?.ToLowerInvariant() ?? string.Empty;
}
