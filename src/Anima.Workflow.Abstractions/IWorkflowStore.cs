namespace Anima.Workflow;

/// <summary>
/// Durable workflow state for an external harness. Anima stores facts; it never
/// creates agents or worktrees and never infers agent results.
/// </summary>
public interface IWorkflowStore
{
    Task<WorkflowState> CreateAsync(WorkflowStart start, CancellationToken cancellationToken = default);

    Task<WorkflowState?> GetAsync(long workflowId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkflowState>> ListAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<QuestionView>> GetQuestionsAsync(
        long workflowId,
        bool openOnly,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<QuestionView>> AddQuestionsAsync(
        long workflowId,
        IReadOnlyList<QuestionInput> questions,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AnswerView>> AddAnswersAsync(
        long workflowId,
        IReadOnlyList<AnswerInput> answers,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AnswerView>> GetAnswersAsync(
        long workflowId,
        long sinceId,
        CancellationToken cancellationToken = default);

    Task<SpecView?> GetSpecAsync(long workflowId, CancellationToken cancellationToken = default);

    Task<SpecView> WriteSpecAsync(
        long workflowId,
        SpecInput spec,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AssignmentView>> GetAssignmentsAsync(
        long workflowId,
        CancellationToken cancellationToken = default);

    Task<AssignmentView> RegisterAssignmentAsync(
        long workflowId,
        AssignmentInput assignment,
        CancellationToken cancellationToken = default);

    Task<bool> RemoveAssignmentAsync(
        long workflowId,
        string taskKey,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ResultView>> GetResultsAsync(
        long workflowId,
        CancellationToken cancellationToken = default);

    Task<ResultView> AddResultAsync(
        long workflowId,
        ResultInput result,
        CancellationToken cancellationToken = default);

    Task<TimelineView> GetTimelineAsync(long workflowId, CancellationToken cancellationToken = default);

    Task<WorkflowContext> GetContextAsync(
        long workflowId,
        string? taskKey,
        CancellationToken cancellationToken = default);
}
