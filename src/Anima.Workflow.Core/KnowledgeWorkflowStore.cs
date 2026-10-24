using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Anima.Knowledge;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Anima.Workflow;

/// <summary>
/// Workflow state persisted as knowledge-graph nodes. External harnesses post
/// facts; this class derives status from them. It never starts agents and never
/// touches git worktrees.
/// </summary>
public sealed class KnowledgeWorkflowStore : IWorkflowStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IKnowledgeGraph _graph;
    private readonly IWorkflowEventPublisher _events;
    private readonly WorkflowOptions _options;
    private readonly ILogger<KnowledgeWorkflowStore> _logger;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    public KnowledgeWorkflowStore(
        IKnowledgeGraph graph,
        IWorkflowEventPublisher events,
        IOptions<WorkflowOptions> options,
        ILogger<KnowledgeWorkflowStore> logger)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _graph = graph;
        _events = events;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<WorkflowState> CreateAsync(
        WorkflowStart start,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        Require(start.Name, "name");
        Require(start.Requirement, "requirement");
        Require(start.RepoPath, "repoPath");

        if (!Directory.Exists(start.RepoPath))
        {
            throw new WorkflowValidationException($"repoPath '{start.RepoPath}' does not exist");
        }

        var repo = await FindOrCreateRepoAsync(start.RepoPath, cancellationToken);

        var goal = await _graph.CreateNodeAsync(
            new NodeDraft(
                KnowledgeKinds.Goal,
                start.Name.Trim(),
                Clip(start.Requirement),
                start.Requirement,
                new Dictionary<string, string> { ["repoPath"] = start.RepoPath }),
            cancellationToken);
        await _graph.AddEdgeAsync(
            new EdgeDraft(goal.Id, repo.Id, KnowledgeEdgeTypes.BelongsTo),
            cancellationToken);

        var properties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["status"] = Wire(WorkflowStatus.Analysis),
            ["requirement"] = start.Requirement,
            ["repoPath"] = start.RepoPath,
            ["baseRef"] = string.IsNullOrWhiteSpace(start.BaseRef) ? "main" : start.BaseRef,
            ["goalId"] = goal.Id.ToString(CultureInfo.InvariantCulture),
        };

        if (start.Tasks is { Count: > 0 })
        {
            properties["plannedTasks"] = JsonSerializer.Serialize(start.Tasks, JsonOptions);
        }

        var workflow = await _graph.CreateNodeAsync(
            new NodeDraft(
                KnowledgeKinds.Workflow,
                start.Name.Trim(),
                "Harness-driven workflow",
                string.Empty,
                properties),
            cancellationToken);

        await _graph.AddEdgeAsync(
            new EdgeDraft(workflow.Id, goal.Id, KnowledgeEdgeTypes.Tracks),
            cancellationToken);
        await AddPhaseAsync(workflow.Id, WorkflowStatus.Analysis, "workflow created", cancellationToken);
        await PublishEventAsync(
            workflow.Id,
            "workflow.created",
            WorkflowStatus.Analysis,
            [workflow.Id],
            start.Name.Trim(),
            cancellationToken);

        return await GetRequiredStateAsync(workflow.Id, cancellationToken);
    }

    public async Task<WorkflowState?> GetAsync(long workflowId, CancellationToken cancellationToken = default)
    {
        var node = await _graph.GetNodeAsync(workflowId, cancellationToken);
        if (node is null || node.Kind != KnowledgeKinds.Workflow)
        {
            return null;
        }

        return await BuildStateAsync(node, cancellationToken);
    }

    public async Task<IReadOnlyList<WorkflowState>> ListAsync(CancellationToken cancellationToken = default)
    {
        var page = await _graph.QueryNodesAsync(
            new NodeQuery
            {
                Kind = KnowledgeKinds.Workflow,
                Scope = KnowledgeScope.All,
                SummaryOnly = true,
                Limit = _options.MaxPageSize,
            },
            cancellationToken);

        var states = new List<WorkflowState>(page.Items.Count);
        foreach (var node in page.Items.OrderByDescending(item => item.UpdatedAt))
        {
            states.Add(await BuildStateAsync(node, cancellationToken));
        }

        return states;
    }

    public async Task<IReadOnlyList<QuestionView>> GetQuestionsAsync(
        long workflowId,
        bool openOnly,
        CancellationToken cancellationToken = default)
    {
        await RequireWorkflowAsync(workflowId, cancellationToken);
        var page = await _graph.QueryNodesAsync(
            new NodeQuery
            {
                Kind = KnowledgeKinds.Question,
                Scope = openOnly ? KnowledgeScope.Active : KnowledgeScope.All,
                Within = workflowId,
                Via = KnowledgeEdgeTypes.Asks,
                SummaryOnly = true,
                Limit = _options.MaxPageSize,
            },
            cancellationToken);

        return page.Items.OrderBy(node => node.Id).Select(MapQuestion).ToArray();
    }

    public async Task<IReadOnlyList<QuestionView>> AddQuestionsAsync(
        long workflowId,
        IReadOnlyList<QuestionInput> questions,
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            return await AddQuestionsCoreAsync(workflowId, questions, cancellationToken);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task<IReadOnlyList<QuestionView>> AddQuestionsCoreAsync(
        long workflowId,
        IReadOnlyList<QuestionInput> questions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(questions);
        await RequireWorkflowAsync(workflowId, cancellationToken);

        var existing = await GetQuestionsAsync(workflowId, openOnly: false, cancellationToken);
        var known = existing.Select(question => question.Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toCreate = new List<QuestionInput>();
        foreach (var input in questions)
        {
            Require(input.Text, "question text");
            if (known.Add(input.Text.Trim()))
            {
                toCreate.Add(input);
            }
        }

        var added = new List<QuestionView>();
        foreach (var input in toCreate)
        {
            var text = input.Text.Trim();
            var node = await _graph.CreateNodeAsync(
                new NodeDraft(
                    KnowledgeKinds.Question,
                    text,
                    Clip(input.Rationale),
                    string.Empty,
                    new Dictionary<string, string> { ["workflowId"] = Id(workflowId) }),
                cancellationToken);
            await _graph.AddEdgeAsync(
                new EdgeDraft(node.Id, workflowId, KnowledgeEdgeTypes.Asks),
                cancellationToken);
            added.Add(MapQuestion(node));
        }

        if (added.Count > 0)
        {
            await PublishEventAsync(
                workflowId,
                "question.added",
                ParseStatus((await RequireWorkflowAsync(workflowId, cancellationToken)).Properties),
                added.Select(question => question.Id).ToArray(),
                $"{added.Count} question(s) added",
                cancellationToken);
            await RefreshStatusAsync(workflowId, "questions added", cancellationToken);
        }

        return added;
    }

    public async Task<IReadOnlyList<AnswerView>> AddAnswersAsync(
        long workflowId,
        IReadOnlyList<AnswerInput> answers,
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            return await AddAnswersCoreAsync(workflowId, answers, cancellationToken);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task<IReadOnlyList<AnswerView>> AddAnswersCoreAsync(
        long workflowId,
        IReadOnlyList<AnswerInput> answers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(answers);
        await RequireWorkflowAsync(workflowId, cancellationToken);

        var validated = new List<(KnowledgeNode Question, string Text)>();
        var seen = new HashSet<long>();
        foreach (var input in answers)
        {
            Require(input.Text, "answer text");
            var question = await _graph.GetNodeAsync(input.QuestionId, cancellationToken)
                ?? throw new WorkflowValidationException($"question {input.QuestionId} does not exist");
            if (question.Kind != KnowledgeKinds.Question)
            {
                throw new WorkflowValidationException($"node {question.Id} is not a question");
            }

            if (!question.Properties.TryGetValue("workflowId", out var owner) ||
                owner != Id(workflowId))
            {
                throw new WorkflowValidationException(
                    $"question {question.Id} does not belong to workflow {workflowId}");
            }

            if (question.Resolved)
            {
                throw new WorkflowValidationException($"question {question.Id} is already answered");
            }

            if (!seen.Add(question.Id))
            {
                throw new WorkflowValidationException(
                    $"question {question.Id} appears more than once in the answer batch");
            }

            validated.Add((question, input.Text.Trim()));
        }

        var created = new List<AnswerView>();
        foreach (var (question, text) in validated)
        {
            var node = await _graph.CreateNodeAsync(
                new NodeDraft(
                    KnowledgeKinds.Answer,
                    question.Name,
                    string.Empty,
                    text,
                    new Dictionary<string, string>
                    {
                        ["workflowId"] = Id(workflowId),
                        ["questionId"] = Id(question.Id),
                    }),
                cancellationToken);
            await _graph.AddEdgeAsync(
                new EdgeDraft(node.Id, question.Id, KnowledgeEdgeTypes.Answers),
                cancellationToken);

            created.Add(new AnswerView(node.Id, question.Id, text, node.CreatedAt));
        }

        if (created.Count > 0)
        {
            await PublishEventAsync(
                workflowId,
                "answer.added",
                ParseStatus((await RequireWorkflowAsync(workflowId, cancellationToken)).Properties),
                created.Select(answer => answer.Id).ToArray(),
                $"{created.Count} answer(s) added",
                cancellationToken);
            await RefreshStatusAsync(workflowId, "answers added", cancellationToken);
        }

        return created;
    }

    public async Task<IReadOnlyList<AnswerView>> GetAnswersAsync(
        long workflowId,
        long sinceId,
        CancellationToken cancellationToken = default)
    {
        await RequireWorkflowAsync(workflowId, cancellationToken);
        var page = await _graph.QueryNodesAsync(
            new NodeQuery
            {
                Kind = KnowledgeKinds.Answer,
                Scope = KnowledgeScope.All,
                Within = workflowId,
                Via = KnowledgeEdgeTypes.Asks,
                Descend = KnowledgeEdgeTypes.Answers,
                Limit = _options.MaxPageSize,
            },
            cancellationToken);

        return page.Items
            .Where(node => node.Id > sinceId)
            .OrderBy(node => node.Id)
            .Select(node => new AnswerView(
                node.Id,
                ParseLong(node.Properties.GetValueOrDefault("questionId")),
                node.Content,
                node.CreatedAt))
            .ToArray();
    }

    public async Task<SpecView?> GetSpecAsync(long workflowId, CancellationToken cancellationToken = default)
    {
        var workflow = await RequireWorkflowAsync(workflowId, cancellationToken);
        if (!workflow.Properties.TryGetValue("specId", out var specIdText) ||
            !long.TryParse(specIdText, CultureInfo.InvariantCulture, out var specId))
        {
            return null;
        }

        var spec = await _graph.GetNodeAsync(specId, cancellationToken);
        return spec is null || spec.Kind != KnowledgeKinds.Spec
            ? null
            : MapSpec(spec);
    }

    public async Task<SpecView> WriteSpecAsync(
        long workflowId,
        SpecInput spec,
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            return await WriteSpecCoreAsync(workflowId, spec, cancellationToken);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task<SpecView> WriteSpecCoreAsync(
        long workflowId,
        SpecInput spec,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        Require(spec.Markdown, "spec markdown");
        var workflow = await RequireWorkflowAsync(workflowId, cancellationToken);
        var tasks = spec.Tasks ?? [];
        if (tasks.Count == 0 &&
            workflow.Properties.TryGetValue("plannedTasks", out var plannedTasks))
        {
            try
            {
                tasks = JsonSerializer.Deserialize<List<SpecTaskInput>>(plannedTasks, JsonOptions) ?? [];
            }
            catch (JsonException)
            {
                tasks = [];
            }
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            Require(task.Key, "task key");
            Require(task.Title, "task title");
            if (!keys.Add(task.Key))
            {
                throw new WorkflowValidationException($"duplicate task key '{task.Key}'");
            }
        }

        var previous = await GetSpecAsync(workflowId, cancellationToken);
        var version = (previous?.Version ?? 0) + 1;
        var hash = ComputeSpecHash(spec.Markdown, tasks);
        var specNode = await _graph.CreateNodeAsync(
            new NodeDraft(
                KnowledgeKinds.Spec,
                string.IsNullOrWhiteSpace(spec.Name) ? $"{workflow.Name} spec" : spec.Name.Trim(),
                "Refined specification",
                spec.Markdown,
                new Dictionary<string, string>
                {
                    ["workflowId"] = Id(workflowId),
                    ["version"] = version.ToString(CultureInfo.InvariantCulture),
                    ["hash"] = hash,
                }),
            cancellationToken);

        await _graph.AddEdgeAsync(
            new EdgeDraft(specNode.Id, workflowId, KnowledgeEdgeTypes.Specifies),
            cancellationToken);
        if (previous is not null)
        {
            await _graph.AddEdgeAsync(
                new EdgeDraft(specNode.Id, previous.Id, KnowledgeEdgeTypes.Supersedes),
                cancellationToken);
        }

        var goalId = ParseLong(workflow.Properties.GetValueOrDefault("goalId"));
        var taskViews = new List<SpecTaskView>(tasks.Count);
        foreach (var task in tasks)
        {
            var acceptance = task.AcceptanceCriteria ?? [];
            var verify = task.VerifyCommands ?? [];
            var properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["workflowId"] = Id(workflowId),
                ["specTaskKey"] = task.Key,
                ["specId"] = Id(specNode.Id),
                ["acceptance"] = JsonSerializer.Serialize(acceptance, JsonOptions),
                ["verify"] = JsonSerializer.Serialize(verify, JsonOptions),
                ["role"] = Wire(task.Role),
            };

            var existing = await FindTaskNodeAsync(workflowId, task.Key, cancellationToken);
            var taskNode = existing is null
                ? await _graph.CreateNodeAsync(
                    new NodeDraft(KnowledgeKinds.Task, task.Title, task.Description, task.Description, properties),
                    cancellationToken)
                : await _graph.UpdateNodeAsync(
                    existing.Id,
                    new NodePatch(task.Title, task.Description, task.Description, properties),
                    cancellationToken);

            if (existing is null)
            {
                await _graph.AddEdgeAsync(
                    new EdgeDraft(taskNode.Id, goalId, KnowledgeEdgeTypes.PartOf),
                    cancellationToken);
            }

            await _graph.AddEdgeAsync(
                new EdgeDraft(taskNode.Id, specNode.Id, KnowledgeEdgeTypes.DerivedFrom),
                cancellationToken);

            taskViews.Add(new SpecTaskView(
                task.Key,
                task.Title,
                task.Description,
                acceptance,
                verify,
                taskNode.Id,
                task.Role));
        }

        specNode = await _graph.UpdateNodeAsync(
            specNode.Id,
            new NodePatch(
                Properties: new Dictionary<string, string>(specNode.Properties, StringComparer.Ordinal)
                {
                    ["tasks"] = JsonSerializer.Serialize(taskViews, JsonOptions),
                }),
            cancellationToken);

        await UpdateWorkflowPropertiesAsync(
            workflow,
            new Dictionary<string, string>(workflow.Properties, StringComparer.Ordinal)
            {
                ["specId"] = Id(specNode.Id),
            },
            cancellationToken);

        await PublishEventAsync(
            workflowId,
            "spec.updated",
            WorkflowStatus.SpecReady,
            [specNode.Id, .. taskViews.Select(task => task.NodeId)],
            $"spec v{version} with {taskViews.Count} task(s)",
            cancellationToken);
        await RefreshStatusAsync(workflowId, "spec written", cancellationToken);
        return MapSpec(specNode);
    }

    public async Task<IReadOnlyList<AssignmentView>> GetAssignmentsAsync(
        long workflowId,
        CancellationToken cancellationToken = default)
    {
        await RequireWorkflowAsync(workflowId, cancellationToken);
        var page = await _graph.QueryNodesAsync(
            new NodeQuery
            {
                Kind = KnowledgeKinds.Worktree,
                Scope = KnowledgeScope.All,
                Within = workflowId,
                Via = KnowledgeEdgeTypes.WorktreeOf,
                SummaryOnly = true,
                Limit = _options.MaxPageSize,
            },
            cancellationToken);

        return page.Items.OrderBy(node => node.Id).Select(MapAssignment).ToArray();
    }

    public async Task<AssignmentView> RegisterAssignmentAsync(
        long workflowId,
        AssignmentInput assignment,
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            return await RegisterAssignmentCoreAsync(workflowId, assignment, cancellationToken);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task<AssignmentView> RegisterAssignmentCoreAsync(
        long workflowId,
        AssignmentInput assignment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        Require(assignment.TaskKey, "taskKey");
        Require(assignment.AgentId, "agentId");
        Require(assignment.WorktreePath, "worktreePath");
        Require(assignment.Branch, "branch");
        await RequireWorkflowAsync(workflowId, cancellationToken);

        var task = await FindTaskNodeAsync(workflowId, assignment.TaskKey, cancellationToken)
            ?? throw new WorkflowValidationException(
                $"task '{assignment.TaskKey}' is not part of workflow {workflowId}; post the spec first");

        var properties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["workflowId"] = Id(workflowId),
            ["taskKey"] = assignment.TaskKey,
            ["agentId"] = assignment.AgentId,
            ["path"] = assignment.WorktreePath,
            ["branch"] = assignment.Branch,
            ["status"] = "assigned",
        };
        if (!string.IsNullOrWhiteSpace(assignment.MessageId))
        {
            properties["messageId"] = assignment.MessageId;
        }

        var existing = await FindAssignmentNodeAsync(workflowId, assignment.TaskKey, cancellationToken);
        var node = existing is null
            ? await _graph.CreateNodeAsync(
                new NodeDraft(
                    KnowledgeKinds.Worktree,
                    assignment.TaskKey,
                    Clip(assignment.Note) ?? $"Worktree for {assignment.TaskKey}",
                    assignment.WorktreePath,
                    properties),
                cancellationToken)
            : await _graph.UpdateNodeAsync(
                existing.Id,
                new NodePatch(
                    assignment.TaskKey,
                    Clip(assignment.Note) ?? $"Worktree for {assignment.TaskKey}",
                    assignment.WorktreePath,
                    properties),
                cancellationToken);

        if (existing is null)
        {
            await _graph.AddEdgeAsync(
                new EdgeDraft(node.Id, workflowId, KnowledgeEdgeTypes.WorktreeOf),
                cancellationToken);
            await _graph.AddEdgeAsync(
                new EdgeDraft(node.Id, task.Id, KnowledgeEdgeTypes.DerivedFrom),
                cancellationToken);
        }

        var agent = await FindOrCreateAgentAsync(assignment.AgentId, cancellationToken);
        var previousAgents = await _graph.GetNeighborsAsync(
            new NeighborQuery(node.Id, EdgeDirection.Outgoing, KnowledgeEdgeTypes.AssignedTo),
            cancellationToken);
        foreach (var previous in previousAgents.Where(item => item.Edge.Destination != agent.Id))
        {
            await _graph.RemoveEdgeAsync(
                node.Id,
                previous.Edge.Destination,
                KnowledgeEdgeTypes.AssignedTo,
                cancellationToken);
        }

        await _graph.AddEdgeAsync(
            new EdgeDraft(node.Id, agent.Id, KnowledgeEdgeTypes.AssignedTo),
            cancellationToken);

        await PublishEventAsync(
            workflowId,
            "assignment.registered",
            ParseStatus((await RequireWorkflowAsync(workflowId, cancellationToken)).Properties),
            [node.Id],
            $"{assignment.TaskKey} -> {assignment.AgentId}",
            cancellationToken);
        await RefreshStatusAsync(workflowId, "assignment registered", cancellationToken);
        return MapAssignment(node);
    }

    public async Task<AssignmentView> AcknowledgeAssignmentAsync(
        long workflowId,
        string taskKey,
        string? runId = null,
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            return await AcknowledgeAssignmentCoreAsync(workflowId, taskKey, runId, cancellationToken);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task<AssignmentView> AcknowledgeAssignmentCoreAsync(
        long workflowId,
        string taskKey,
        string? runId,
        CancellationToken cancellationToken = default)
    {
        Require(taskKey, "taskKey");
        await RequireWorkflowAsync(workflowId, cancellationToken);
        var node = await FindAssignmentNodeAsync(workflowId, taskKey, cancellationToken)
            ?? throw new WorkflowNotFoundException($"no assignment for task '{taskKey}'");

        var properties = new Dictionary<string, string>(node.Properties, StringComparer.Ordinal)
        {
            ["acknowledgedAt"] = (ParseNullableDate(node.Properties.GetValueOrDefault("acknowledgedAt"))
                ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrWhiteSpace(runId))
        {
            properties["runId"] = runId;
        }

        var updated = await _graph.UpdateNodeAsync(
            node.Id,
            new NodePatch(Properties: properties),
            cancellationToken);
        await PublishEventAsync(
            workflowId,
            "assignment.acknowledged",
            ParseStatus((await RequireWorkflowAsync(workflowId, cancellationToken)).Properties),
            [node.Id],
            $"{taskKey} acknowledged",
            cancellationToken);
        return MapAssignment(updated);
    }

    public async Task<bool> RemoveAssignmentAsync(
        long workflowId,
        string taskKey,
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            return await RemoveAssignmentCoreAsync(workflowId, taskKey, cancellationToken);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task<bool> RemoveAssignmentCoreAsync(
        long workflowId,
        string taskKey,
        CancellationToken cancellationToken = default)
    {
        Require(taskKey, "taskKey");
        await RequireWorkflowAsync(workflowId, cancellationToken);
        var node = await FindAssignmentNodeAsync(workflowId, taskKey, cancellationToken);
        if (node is null)
        {
            return false;
        }

        await _graph.DeleteNodeAsync(node.Id, cancellationToken);
        await PublishEventAsync(
            workflowId,
            "assignment.removed",
            ParseStatus((await RequireWorkflowAsync(workflowId, cancellationToken)).Properties),
            [node.Id],
            $"{taskKey} released",
            cancellationToken);
        await RefreshStatusAsync(workflowId, "assignment removed", cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<ResultView>> GetResultsAsync(
        long workflowId,
        CancellationToken cancellationToken = default)
    {
        await RequireWorkflowAsync(workflowId, cancellationToken);
        var results = await QueryAllAsync(
            new NodeQuery
            {
                Kind = KnowledgeKinds.Result,
                Scope = KnowledgeScope.All,
                Within = workflowId,
                Via = KnowledgeEdgeTypes.ResultOf,
                SummaryOnly = true,
            },
            cancellationToken);

        var findings = await QueryAllAsync(
            new NodeQuery
            {
                Kind = KnowledgeKinds.Finding,
                Scope = KnowledgeScope.All,
                SummaryOnly = true,
                Properties = new Dictionary<string, string> { ["workflowId"] = Id(workflowId) },
            },
            cancellationToken);

        var findingsByResult = findings
            .GroupBy(node => node.Properties.GetValueOrDefault("resultId") ?? string.Empty)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        return results
            .OrderBy(node => node.Id)
            .Select(node => MapResult(node, findingsByResult.GetValueOrDefault(Id(node.Id)) ?? []))
            .ToArray();
    }

    public async Task<ResultView> AddResultAsync(
        long workflowId,
        ResultInput result,
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            return await AddResultCoreAsync(workflowId, result, cancellationToken);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task<ResultView> AddResultCoreAsync(
        long workflowId,
        ResultInput result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        Require(result.Summary, "summary");
        var workflow = await RequireWorkflowAsync(workflowId, cancellationToken);
        var taskKey = result.TaskKey.Trim();
        var taskNode = taskKey.Length == 0
            ? null
            : await FindTaskNodeAsync(workflowId, taskKey, cancellationToken);

        if (result.Kind is WorkflowResultKind.Implementation && taskNode is null)
        {
            throw new WorkflowValidationException(
                $"implementation result requires a known taskKey; '{taskKey}' is not in workflow {workflowId}");
        }

        if (taskKey.Length > 0 && taskNode is null)
        {
            throw new WorkflowValidationException(
                $"task '{taskKey}' is not part of workflow {workflowId}");
        }

        foreach (var finding in result.Findings ?? [])
        {
            Require(finding.Message, "finding message");
        }

        var spec = await GetSpecAsync(workflowId, cancellationToken);
        var existingResults = await GetResultsAsync(workflowId, cancellationToken);
        var latestByTask = LatestImplementationByTask(existingResults);
        var implementationTasks = spec?.Tasks
            .Where(task => task.Role == WorkflowTaskRole.Implementation)
            .ToArray() ?? [];
        var allImplemented = implementationTasks.Length > 0 &&
                             implementationTasks.All(task =>
                                 latestByTask.TryGetValue(task.Key, out var item) && item.Success);
        var latestMerge = Latest(existingResults, WorkflowResultKind.Merge);

        if (result.Kind is WorkflowResultKind.Merge or WorkflowResultKind.Review)
        {
            if (!allImplemented)
            {
                throw new WorkflowValidationException(
                    $"{Wire(result.Kind)} requires every spec task to have a successful implementation result");
            }
        }

        if (result.Kind == WorkflowResultKind.Review &&
            (latestMerge is null || !latestMerge.Success))
        {
            throw new WorkflowValidationException(
                "review requires a successful merge result");
        }

        if (result.Kind == WorkflowResultKind.Review &&
            result.Success &&
            (result.Findings ?? []).Any(finding => finding.Severity == FindingSeverity.Error))
        {
            throw new WorkflowValidationException(
                "a review with error findings cannot be marked successful");
        }

        var properties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["workflowId"] = Id(workflowId),
            ["taskKey"] = taskKey,
            ["kind"] = Wire(result.Kind),
            ["success"] = result.Success ? "true" : "false",
            ["changedFiles"] = JsonSerializer.Serialize(result.ChangedFiles ?? [], JsonOptions),
            ["conflicts"] = JsonSerializer.Serialize(result.Conflicts ?? [], JsonOptions),
        };
        if (!string.IsNullOrWhiteSpace(result.AgentId))
        {
            properties["agentId"] = result.AgentId;
        }

        if (!string.IsNullOrWhiteSpace(result.WorktreePath))
        {
            properties["worktreePath"] = result.WorktreePath;
        }

        if (!string.IsNullOrWhiteSpace(result.HeadSha))
        {
            properties["headSha"] = result.HeadSha;
        }

        if (!string.IsNullOrWhiteSpace(result.Branch))
        {
            properties["branch"] = result.Branch;
        }

        if (!string.IsNullOrWhiteSpace(result.BaseRef))
        {
            properties["baseRef"] = result.BaseRef;
        }

        if (!string.IsNullOrWhiteSpace(result.RunId))
        {
            properties["runId"] = result.RunId;
        }

        properties["testCommands"] = JsonSerializer.Serialize(result.TestCommands ?? [], JsonOptions);
        properties["artifactPaths"] = JsonSerializer.Serialize(result.ArtifactPaths ?? [], JsonOptions);

        var node = await _graph.CreateNodeAsync(
            new NodeDraft(
                KnowledgeKinds.Result,
                taskKey.Length == 0 ? Wire(result.Kind) : $"{Wire(result.Kind)}:{taskKey}",
                Clip(result.Summary),
                Clip(result.OutputTail) ?? string.Empty,
                properties),
            cancellationToken);

        await _graph.AddEdgeAsync(
            new EdgeDraft(node.Id, workflowId, KnowledgeEdgeTypes.ResultOf),
            cancellationToken);
        if (taskNode is not null)
        {
            await _graph.AddEdgeAsync(
                new EdgeDraft(node.Id, taskNode.Id, KnowledgeEdgeTypes.DerivedFrom),
                cancellationToken);
        }

        if (spec is not null && result.Kind == WorkflowResultKind.Implementation)
        {
            await _graph.AddEdgeAsync(
                new EdgeDraft(node.Id, spec.Id, KnowledgeEdgeTypes.ImplementsSpec),
                cancellationToken);
        }

        foreach (var finding in result.Findings ?? [])
        {
            var findingNode = await _graph.CreateNodeAsync(
                new NodeDraft(
                    KnowledgeKinds.Finding,
                    finding.Message.Trim(),
                    Clip(finding.Message),
                    string.Empty,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["workflowId"] = Id(workflowId),
                        ["resultId"] = Id(node.Id),
                        ["severity"] = Wire(finding.Severity),
                        ["path"] = finding.Path ?? string.Empty,
                        ["line"] = finding.Line?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    }),
                cancellationToken);
            await _graph.AddEdgeAsync(
                new EdgeDraft(findingNode.Id, node.Id, KnowledgeEdgeTypes.FoundIn),
                cancellationToken);
        }

        var status = await ComputeStatusAsync(workflowId, cancellationToken);
        await SetStatusAsync(workflow, status, $"{Wire(result.Kind)} result posted", cancellationToken);
        await PublishEventAsync(
            workflowId,
            "result.posted",
            status,
            [node.Id],
            $"{Wire(result.Kind)}: {(result.Success ? "success" : "failure")}",
            cancellationToken);
        return MapResult(node, []);
    }

    public async Task<TimelineView> GetTimelineAsync(
        long workflowId,
        CancellationToken cancellationToken = default)
    {
        await RequireWorkflowAsync(workflowId, cancellationToken);
        var phases = await _graph.QueryNodesAsync(
            new NodeQuery
            {
                Kind = KnowledgeKinds.Phase,
                Scope = KnowledgeScope.All,
                Within = workflowId,
                Via = KnowledgeEdgeTypes.PhaseOf,
                SummaryOnly = true,
                Limit = _options.MaxPageSize,
            },
            cancellationToken);
        var results = await GetResultsAsync(workflowId, cancellationToken);

        return new TimelineView(
            phases.Items
                .OrderBy(node => node.CreatedAt)
                .Select(node => new PhaseView(
                    node.Id,
                    node.Name,
                    ParseStatus(node.Properties),
                    node.Description,
                    node.CreatedAt))
                .ToArray(),
            results);
    }

    public async Task<WorkflowContext> GetContextAsync(
        long workflowId,
        string? taskKey,
        CancellationToken cancellationToken = default)
    {
        var workflow = await RequireWorkflowAsync(workflowId, cancellationToken);
        var spec = await GetSpecAsync(workflowId, cancellationToken);
        SpecTaskView? task = null;
        if (!string.IsNullOrWhiteSpace(taskKey))
        {
            task = spec?.Tasks.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, taskKey, StringComparison.Ordinal))
                ?? throw new WorkflowValidationException(
                    $"task '{taskKey}' is not in the current spec");
        }

        var assignments = await GetAssignmentsAsync(workflowId, cancellationToken);
        var results = await GetResultsAsync(workflowId, cancellationToken);
        var assignment = task is null
            ? null
            : assignments.FirstOrDefault(candidate =>
                string.Equals(candidate.TaskKey, task.Key, StringComparison.Ordinal));
        var lastResult = task is null
            ? null
            : results.Where(candidate =>
                    string.Equals(candidate.TaskKey, task.Key, StringComparison.Ordinal))
                .OrderByDescending(candidate => candidate.Id)
                .FirstOrDefault();

        return new WorkflowContext(
            workflowId,
            ParseStatus(workflow.Properties),
            workflow.Name,
            Clip(workflow.Properties.GetValueOrDefault("requirement") ?? string.Empty) ?? string.Empty,
            task?.Key,
            task?.Title,
            Clip(task?.Description),
            task?.AcceptanceCriteria ?? [],
            task?.VerifyCommands ?? [],
            spec?.Id,
            spec?.Version,
            spec?.Hash,
            assignment,
            lastResult);
    }

    private async Task<List<KnowledgeNode>> QueryAllAsync(
        NodeQuery query,
        CancellationToken cancellationToken)
    {
        var pageSize = Math.Max(1, _options.MaxPageSize);
        var items = new List<KnowledgeNode>();
        var offset = 0;
        while (true)
        {
            var page = await _graph.QueryNodesAsync(
                query with { Limit = pageSize, Offset = offset },
                cancellationToken);
            items.AddRange(page.Items);
            if (page.Items.Count < pageSize)
            {
                return items;
            }

            offset += page.Items.Count;
        }
    }

    private static Dictionary<string, ResultView> LatestImplementationByTask(
        IEnumerable<ResultView> results)
        => results
            .Where(result => result.Kind == WorkflowResultKind.Implementation)
            .GroupBy(result => result.TaskKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(result => result.Id).First(),
                StringComparer.Ordinal);

    private async Task<WorkflowState> GetRequiredStateAsync(long workflowId, CancellationToken cancellationToken)
        => await GetAsync(workflowId, cancellationToken)
           ?? throw new WorkflowNotFoundException($"workflow {workflowId} does not exist");

    private async Task<WorkflowState> BuildStateAsync(KnowledgeNode workflow, CancellationToken cancellationToken)
    {
        var questions = await GetQuestionsAsync(workflow.Id, openOnly: true, cancellationToken);
        var spec = await GetSpecAsync(workflow.Id, cancellationToken);
        var results = await GetResultsAsync(workflow.Id, cancellationToken);
        var completed = results
            .Where(result => result.Kind == WorkflowResultKind.Implementation && result.Success)
            .Select(result => result.TaskKey)
            .Distinct(StringComparer.Ordinal)
            .Count();

        return new WorkflowState(
            workflow.Id,
            ParseLong(workflow.Properties.GetValueOrDefault("goalId")),
            workflow.Name,
            Clip(workflow.Properties.GetValueOrDefault("requirement") ?? string.Empty) ?? string.Empty,
            ParseStatus(workflow.Properties),
            workflow.Properties.GetValueOrDefault("repoPath") ?? string.Empty,
            workflow.Properties.GetValueOrDefault("baseRef") ?? "main",
            spec?.Id,
            spec?.Version,
            spec?.Hash,
            questions.Count,
            spec?.Tasks.Count(task => task.Role == WorkflowTaskRole.Implementation) ?? 0,
            completed,
            workflow.CreatedAt,
            workflow.UpdatedAt);
    }

    private async Task RefreshStatusAsync(long workflowId, string summary, CancellationToken cancellationToken)
    {
        var workflow = await RequireWorkflowAsync(workflowId, cancellationToken);
        var status = await ComputeStatusAsync(workflowId, cancellationToken);
        await SetStatusAsync(workflow, status, summary, cancellationToken);
    }

    private async Task<WorkflowStatus> ComputeStatusAsync(long workflowId, CancellationToken cancellationToken)
    {
        var open = await GetQuestionsAsync(workflowId, openOnly: true, cancellationToken);
        if (open.Count > 0)
        {
            return WorkflowStatus.AwaitingAnswers;
        }

        var spec = await GetSpecAsync(workflowId, cancellationToken);
        if (spec is null)
        {
            return WorkflowStatus.Analysis;
        }

        var results = await GetResultsAsync(workflowId, cancellationToken);
        var latestByTask = LatestImplementationByTask(results);
        var implementationTasks = spec.Tasks
            .Where(task => task.Role == WorkflowTaskRole.Implementation)
            .ToArray();
        var allImplemented = implementationTasks.Length > 0 &&
                             implementationTasks.All(task =>
                                 latestByTask.TryGetValue(task.Key, out var item) && item.Success);
        var review = Latest(results, WorkflowResultKind.Review);
        if (review is not null)
        {
            if (!review.Success || review.Findings.Any(finding => finding.Severity == FindingSeverity.Error))
            {
                return WorkflowStatus.Failed;
            }

            var reviewedMerge = Latest(results, WorkflowResultKind.Merge);
            return allImplemented && reviewedMerge is { Success: true }
                ? WorkflowStatus.Completed
                : WorkflowStatus.Failed;
        }

        var merge = Latest(results, WorkflowResultKind.Merge);
        if (merge is not null)
        {
            return merge.Success && allImplemented
                ? WorkflowStatus.MergeComplete
                : WorkflowStatus.Failed;
        }

        if (latestByTask.Values.Any(result => !result.Success))
        {
            return WorkflowStatus.Failed;
        }

        if (allImplemented)
        {
            return WorkflowStatus.ImplementationComplete;
        }

        if (latestByTask.Count > 0 ||
            (await GetAssignmentsAsync(workflowId, cancellationToken)).Count > 0)
        {
            return WorkflowStatus.Implementing;
        }

        return WorkflowStatus.SpecReady;
    }

    private async Task SetStatusAsync(
        KnowledgeNode workflow,
        WorkflowStatus status,
        string summary,
        CancellationToken cancellationToken)
    {
        var current = ParseStatus(workflow.Properties);
        if (current == status)
        {
            return;
        }

        await UpdateWorkflowPropertiesAsync(
            workflow,
            new Dictionary<string, string>(workflow.Properties, StringComparer.Ordinal)
            {
                ["status"] = Wire(status),
                ["statusSummary"] = Clip(summary) ?? string.Empty,
            },
            cancellationToken);
        await AddPhaseAsync(workflow.Id, status, summary, cancellationToken);
        await PublishEventAsync(
            workflow.Id,
            "status.changed",
            status,
            [workflow.Id],
            $"{current} -> {status}: {summary}",
            cancellationToken);
    }

    private async Task AddPhaseAsync(
        long workflowId,
        WorkflowStatus status,
        string summary,
        CancellationToken cancellationToken)
    {
        var phases = await _graph.QueryNodesAsync(
            new NodeQuery
            {
                Kind = KnowledgeKinds.Phase,
                Scope = KnowledgeScope.All,
                Within = workflowId,
                Via = KnowledgeEdgeTypes.PhaseOf,
                SummaryOnly = true,
                Limit = _options.MaxPageSize,
            },
            cancellationToken);
        var previous = phases.Items.OrderBy(node => node.CreatedAt).LastOrDefault();

        var phase = await _graph.CreateNodeAsync(
            new NodeDraft(
                KnowledgeKinds.Phase,
                Wire(status),
                Clip(summary),
                string.Empty,
                new Dictionary<string, string>
                {
                    ["workflowId"] = Id(workflowId),
                    ["status"] = Wire(status),
                }),
            cancellationToken);
        await _graph.AddEdgeAsync(
            new EdgeDraft(phase.Id, workflowId, KnowledgeEdgeTypes.PhaseOf),
            cancellationToken);
        if (previous is not null)
        {
            await _graph.AddEdgeAsync(
                new EdgeDraft(previous.Id, phase.Id, KnowledgeEdgeTypes.Precedes),
                cancellationToken);
        }
    }

    private async Task UpdateWorkflowPropertiesAsync(
        KnowledgeNode workflow,
        Dictionary<string, string> properties,
        CancellationToken cancellationToken)
        => await _graph.UpdateNodeAsync(
            workflow.Id,
            new NodePatch(Properties: properties),
            cancellationToken);

    private async Task<KnowledgeNode> RequireWorkflowAsync(long workflowId, CancellationToken cancellationToken)
    {
        var node = await _graph.GetNodeAsync(workflowId, cancellationToken);
        if (node is null || node.Kind != KnowledgeKinds.Workflow)
        {
            throw new WorkflowNotFoundException($"workflow {workflowId} does not exist");
        }

        return node;
    }

    private async Task<KnowledgeNode?> FindTaskNodeAsync(
        long workflowId,
        string taskKey,
        CancellationToken cancellationToken)
    {
        var page = await _graph.QueryNodesAsync(
            new NodeQuery
            {
                Kind = KnowledgeKinds.Task,
                Scope = KnowledgeScope.All,
                SummaryOnly = true,
                Limit = 1,
                Properties = new Dictionary<string, string>
                {
                    ["workflowId"] = Id(workflowId),
                    ["specTaskKey"] = taskKey,
                },
            },
            cancellationToken);
        return page.Items.Count > 0 ? page.Items[0] : null;
    }

    private async Task<KnowledgeNode?> FindAssignmentNodeAsync(
        long workflowId,
        string taskKey,
        CancellationToken cancellationToken)
    {
        var page = await _graph.QueryNodesAsync(
            new NodeQuery
            {
                Kind = KnowledgeKinds.Worktree,
                Scope = KnowledgeScope.All,
                SummaryOnly = true,
                Limit = 1,
                Properties = new Dictionary<string, string>
                {
                    ["workflowId"] = Id(workflowId),
                    ["taskKey"] = taskKey,
                },
            },
            cancellationToken);
        return page.Items.Count > 0 ? page.Items[0] : null;
    }

    private async Task<KnowledgeNode> FindOrCreateAgentAsync(
        string agentId,
        CancellationToken cancellationToken)
    {
        var page = await _graph.QueryNodesAsync(
            new NodeQuery
            {
                Kind = KnowledgeKinds.Agent,
                Scope = KnowledgeScope.All,
                SummaryOnly = true,
                Limit = 1,
                Properties = new Dictionary<string, string> { ["agentId"] = agentId },
            },
            cancellationToken);
        if (page.Items.Count > 0)
        {
            return page.Items[0];
        }

        return await _graph.CreateNodeAsync(
            new NodeDraft(
                KnowledgeKinds.Agent,
                agentId,
                "External harness agent",
                string.Empty,
                new Dictionary<string, string> { ["agentId"] = agentId }),
            cancellationToken);
    }

    private async Task<KnowledgeNode> FindOrCreateRepoAsync(
        string repoPath,
        CancellationToken cancellationToken)
    {
        var page = await _graph.QueryNodesAsync(
            new NodeQuery
            {
                Kind = KnowledgeKinds.Repo,
                Scope = KnowledgeScope.All,
                SummaryOnly = true,
                Limit = 1,
                Properties = new Dictionary<string, string> { ["path"] = repoPath },
            },
            cancellationToken);
        if (page.Items.Count > 0)
        {
            return page.Items[0];
        }

        var name = Path.GetFileName(repoPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return await _graph.CreateNodeAsync(
            new NodeDraft(
                KnowledgeKinds.Repo,
                string.IsNullOrWhiteSpace(name) ? repoPath : name,
                "Repository",
                repoPath,
                new Dictionary<string, string> { ["path"] = repoPath }),
            cancellationToken);
    }

    private async Task PublishEventAsync(
        long workflowId,
        string eventName,
        WorkflowStatus status,
        IReadOnlyList<long> nodeIds,
        string summary,
        CancellationToken cancellationToken)
    {
        if (!_options.PublishEvents)
        {
            return;
        }

        try
        {
            await _events.PublishAsync(
                new WorkflowEvent(
                    workflowId,
                    eventName,
                    status,
                    nodeIds,
                    Clip(summary) ?? string.Empty,
                    DateTimeOffset.UtcNow),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Events are wake-ups; state is already durable in the graph.
            _logger.LogWarning(exception, "Failed to publish workflow event {Event}.", eventName);
        }
    }

    private static ResultView? Latest(IEnumerable<ResultView> results, WorkflowResultKind kind)
        => results.Where(result => result.Kind == kind).OrderByDescending(result => result.Id).FirstOrDefault();

    private static QuestionView MapQuestion(KnowledgeNode node) => new(
        node.Id,
        ParseLong(node.Properties.GetValueOrDefault("workflowId")),
        node.Name,
        node.Description,
        node.Resolved,
        node.CreatedAt);

    private static AssignmentView MapAssignment(KnowledgeNode node) => new(
        node.Id,
        node.Properties.GetValueOrDefault("taskKey") ?? string.Empty,
        node.Properties.GetValueOrDefault("agentId") ?? string.Empty,
        node.Properties.GetValueOrDefault("path") ?? node.Content,
        node.Properties.GetValueOrDefault("branch") ?? string.Empty,
        node.Properties.GetValueOrDefault("status") ?? "assigned",
        node.UpdatedAt,
        NullIfEmpty(node.Properties.GetValueOrDefault("messageId")),
        ParseNullableDate(node.Properties.GetValueOrDefault("acknowledgedAt")),
        NullIfEmpty(node.Properties.GetValueOrDefault("runId")));

    private static ResultView MapResult(KnowledgeNode node, IReadOnlyList<KnowledgeNode> findings)
    {
        return new ResultView(
            node.Id,
            node.Properties.GetValueOrDefault("taskKey") ?? string.Empty,
            ParseResultKind(node.Properties.GetValueOrDefault("kind")),
            string.Equals(node.Properties.GetValueOrDefault("success"), "true", StringComparison.OrdinalIgnoreCase),
            node.Description,
            DeserializeList(node.Properties.GetValueOrDefault("changedFiles")),
            DeserializeList(node.Properties.GetValueOrDefault("conflicts")),
            findings.Select(finding => new FindingView(
                ParseSeverity(finding.Properties.GetValueOrDefault("severity")),
                finding.Name,
                NullIfEmpty(finding.Properties.GetValueOrDefault("path")),
                ParseNullableInt(finding.Properties.GetValueOrDefault("line")))).ToArray(),
            NullIfEmpty(node.Properties.GetValueOrDefault("agentId")),
            NullIfEmpty(node.Properties.GetValueOrDefault("headSha")),
            NullIfEmpty(node.Properties.GetValueOrDefault("branch")),
            NullIfEmpty(node.Properties.GetValueOrDefault("baseRef")),
            NullIfEmpty(node.Properties.GetValueOrDefault("runId")),
            DeserializeList(node.Properties.GetValueOrDefault("testCommands")),
            DeserializeList(node.Properties.GetValueOrDefault("artifactPaths")),
            node.CreatedAt);
    }

    private static SpecView MapSpec(KnowledgeNode node)
    {
        var tasks = DeserializeTasks(node.Properties.GetValueOrDefault("tasks"));
        return new SpecView(
            node.Id,
            ParseLong(node.Properties.GetValueOrDefault("workflowId")),
            node.Name,
            node.Content,
            tasks,
            ParseNullableInt(node.Properties.GetValueOrDefault("version")) ?? 1,
            node.UpdatedAt,
            node.Properties.GetValueOrDefault("hash") ?? string.Empty);
    }

    private static List<SpecTaskView> DeserializeTasks(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<SpecTaskView>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static List<string> DeserializeList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static DateTimeOffset? ParseNullableDate(string? value)
        => DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : null;

    private static WorkflowStatus ParseStatus(IReadOnlyDictionary<string, string> properties)
        => ParseStatus(properties.GetValueOrDefault("status"));

    private static WorkflowStatus ParseStatus(string? value)
        => Enum.TryParse<WorkflowStatus>(value, ignoreCase: true, out var status)
            ? status
            : WorkflowStatus.Analysis;

    private static WorkflowResultKind ParseResultKind(string? value)
        => Enum.TryParse<WorkflowResultKind>(value, ignoreCase: true, out var kind)
            ? kind
            : WorkflowResultKind.Verification;

    private static FindingSeverity ParseSeverity(string? value)
        => Enum.TryParse<FindingSeverity>(value, ignoreCase: true, out var severity)
            ? severity
            : FindingSeverity.Info;

    private static long ParseLong(string? value)
        => long.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static int? ParseNullableInt(string? value)
        => int.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrEmpty(value) ? null : value;

    private static string Wire(WorkflowStatus status) => status.ToString().ToLowerInvariant();

    private static string Wire(WorkflowResultKind kind) => kind.ToString().ToLowerInvariant();

    private static string Wire(WorkflowTaskRole role) => role.ToString().ToLowerInvariant();

    private static string Wire(FindingSeverity severity) => severity.ToString().ToLowerInvariant();

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    private static string ComputeSpecHash(string markdown, IReadOnlyList<SpecTaskInput> tasks)
    {
        var canonical = new StringBuilder();
        AppendHashField(
            canonical,
            markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd());
        foreach (var task in tasks)
        {
            AppendHashField(canonical, task.Key);
            AppendHashField(canonical, task.Title);
            AppendHashField(canonical, task.Description ?? string.Empty);
            AppendHashField(canonical, JsonSerializer.Serialize(task.AcceptanceCriteria ?? [], JsonOptions));
            AppendHashField(canonical, JsonSerializer.Serialize(task.VerifyCommands ?? [], JsonOptions));
            AppendHashField(canonical, Wire(task.Role));
        }

        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void AppendHashField(StringBuilder builder, string value)
        => builder
            .Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append('\n');

    private string? Clip(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= _options.ContentLimit)
        {
            return value;
        }

        return value[.._options.ContentLimit] + "…";
    }

    public void Dispose() => _mutationGate.Dispose();

    private static string Require(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new WorkflowValidationException($"{field} is required");
        }

        return value.Trim();
    }
}
