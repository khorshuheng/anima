namespace Anima.Workflow;

public enum WorkflowStatus
{
    Analysis,
    AwaitingAnswers,
    SpecReady,
    Implementing,
    ImplementationComplete,
    Merging,
    MergeComplete,
    Reviewing,
    Completed,
    Failed,
}

public enum WorkflowResultKind
{
    Implementation,
    Merge,
    Review,
    Verification,
}

public enum FindingSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record WorkflowStart(
    string Name,
    string Requirement,
    string RepoPath,
    string BaseRef = "main",
    IReadOnlyList<SpecTaskInput>? Tasks = null);

public sealed record WorkflowState(
    long Id,
    long GoalId,
    string Name,
    WorkflowStatus Status,
    string RepoPath,
    string BaseRef,
    long? SpecId,
    int OpenQuestionCount,
    int TaskCount,
    int CompletedTaskCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record QuestionInput(string Text, string? Rationale = null);

public sealed record QuestionView(
    long Id,
    long WorkflowId,
    string Text,
    string? Rationale,
    bool Answered,
    DateTimeOffset AskedAt);

public sealed record AnswerInput(long QuestionId, string Text);

public sealed record AnswerView(
    long Id,
    long QuestionId,
    string Text,
    DateTimeOffset AnsweredAt);

public sealed record SpecTaskInput(
    string Key,
    string Title,
    string Description = "",
    IReadOnlyList<string>? AcceptanceCriteria = null,
    IReadOnlyList<string>? VerifyCommands = null);

public sealed record SpecTaskView(
    string Key,
    string Title,
    string Description,
    IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyList<string> VerifyCommands,
    long NodeId);

public sealed record SpecInput(
    string Markdown,
    IReadOnlyList<SpecTaskInput> Tasks,
    string? Name = null);

public sealed record SpecView(
    long Id,
    long WorkflowId,
    string Name,
    string Markdown,
    IReadOnlyList<SpecTaskView> Tasks,
    int Version,
    DateTimeOffset UpdatedAt);

public sealed record AssignmentInput(
    string TaskKey,
    string AgentId,
    string WorktreePath,
    string Branch,
    string? Note = null);

public sealed record AssignmentView(
    long Id,
    string TaskKey,
    string AgentId,
    string WorktreePath,
    string Branch,
    string Status,
    DateTimeOffset UpdatedAt);

public sealed record FindingInput(
    FindingSeverity Severity,
    string Message,
    string? Path = null,
    int? Line = null);

public sealed record FindingView(
    FindingSeverity Severity,
    string Message,
    string? Path,
    int? Line);

public sealed record ResultInput(
    string TaskKey,
    WorkflowResultKind Kind,
    bool Success,
    string Summary,
    IReadOnlyList<string>? ChangedFiles = null,
    IReadOnlyList<string>? Conflicts = null,
    IReadOnlyList<FindingInput>? Findings = null,
    string? AgentId = null,
    string? WorktreePath = null,
    string? OutputTail = null);

public sealed record ResultView(
    long Id,
    string TaskKey,
    WorkflowResultKind Kind,
    bool Success,
    string Summary,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<FindingView> Findings,
    string? AgentId,
    DateTimeOffset CreatedAt);

public sealed record PhaseView(
    long Id,
    string Name,
    WorkflowStatus Status,
    string? Summary,
    DateTimeOffset CreatedAt);

public sealed record WorkflowContext(
    long WorkflowId,
    WorkflowStatus Status,
    string Name,
    string Requirement,
    string? TaskKey,
    string? TaskTitle,
    string? TaskDescription,
    IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyList<string> VerifyCommands,
    long? SpecId,
    AssignmentView? Assignment,
    ResultView? LastResult);

public sealed record TimelineView(
    IReadOnlyList<PhaseView> Phases,
    IReadOnlyList<ResultView> Results);

public sealed record DispatchRequest(
    string AgentId,
    string Role,
    string Instructions,
    string? ContextJson = null,
    int TimeoutSeconds = 0);

public sealed record DispatchResult(
    bool Success,
    string Summary,
    string? DataJson,
    string? Error);

public sealed record WorkflowEvent(
    long WorkflowId,
    string Event,
    WorkflowStatus Status,
    IReadOnlyList<long> NodeIds,
    string Summary,
    DateTimeOffset Timestamp);
