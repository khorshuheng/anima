
namespace Anima.Workflow.Service.Contracts;

public sealed record QuestionsRequest(List<QuestionInput> Questions);

public sealed record AnswersRequest(List<AnswerInput> Answers);

public sealed record QuestionsResponse(
    IReadOnlyList<QuestionView> Added,
    IReadOnlyList<QuestionView> Open,
    WorkflowStatus Status);

public sealed record AnswersResponse(
    IReadOnlyList<AnswerView> Added,
    IReadOnlyList<QuestionView> Open,
    WorkflowStatus Status);

public sealed record AckAssignmentRequest(string? RunId = null);
