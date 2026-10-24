using Anima.Knowledge;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Anima.Workflow.Core.Tests;

public sealed class KnowledgeWorkflowStoreTests : IDisposable
{
    private readonly string _repoPath = Path.Combine(
        Path.GetTempPath(),
        "anima-workflow-tests-" + Guid.NewGuid().ToString("n"));

    public KnowledgeWorkflowStoreTests()
    {
        Directory.CreateDirectory(_repoPath);
    }

    [Fact]
    public async Task Lifecycle_is_derived_from_posted_facts()
    {
        var store = CreateStore();
        var workflow = await store.CreateAsync(new WorkflowStart(
            "Feature X",
            "Build the thing",
            _repoPath,
            "main"));

        Assert.Equal(WorkflowStatus.Analysis, workflow.Status);

        var questions = await store.AddQuestionsAsync(workflow.Id,
        [
            new QuestionInput("What is the scope?"),
            new QuestionInput("What is acceptance?", "Needed for review"),
        ]);
        Assert.Equal(2, questions.Count);
        Assert.Equal(WorkflowStatus.AwaitingAnswers, (await store.GetAsync(workflow.Id))!.Status);

        var duplicate = await store.AddQuestionsAsync(workflow.Id, [new QuestionInput("What is the scope?")]);
        Assert.Empty(duplicate);

        var answers = await store.AddAnswersAsync(workflow.Id,
            questions.Select(question => new AnswerInput(question.Id, "answer " + question.Id)).ToArray());
        Assert.Equal(2, answers.Count);
        Assert.Equal(WorkflowStatus.Analysis, (await store.GetAsync(workflow.Id))!.Status);

        var spec = await store.WriteSpecAsync(workflow.Id, new SpecInput(
            "# Spec\n\nRefined.",
            [
                new SpecTaskInput("api", "Add endpoint", "POST /x", ["201"], ["dotnet test"]),
                new SpecTaskInput("docs", "Update docs", "Explain /x"),
            ]));
        Assert.Equal(2, spec.Tasks.Count);
        Assert.Equal(WorkflowStatus.SpecReady, (await store.GetAsync(workflow.Id))!.Status);

        var assignment = await store.RegisterAssignmentAsync(workflow.Id, new AssignmentInput(
            "api",
            "impl-api",
            "/tmp/worktrees/api",
            "anima/feature-api"));
        Assert.Equal("impl-api", assignment.AgentId);
        Assert.Equal(WorkflowStatus.Implementing, (await store.GetAsync(workflow.Id))!.Status);

        await store.AddResultAsync(workflow.Id, new ResultInput(
            "api",
            WorkflowResultKind.Implementation,
            true,
            "endpoint implemented",
            ChangedFiles: ["src/Api.cs"],
            AgentId: "impl-api"));
        Assert.Equal(WorkflowStatus.Implementing, (await store.GetAsync(workflow.Id))!.Status);

        await store.AddResultAsync(workflow.Id, new ResultInput(
            "docs",
            WorkflowResultKind.Implementation,
            true,
            "docs updated",
            AgentId: "impl-docs"));
        Assert.Equal(WorkflowStatus.ImplementationComplete, (await store.GetAsync(workflow.Id))!.Status);

        await store.AddResultAsync(workflow.Id, new ResultInput(
            string.Empty,
            WorkflowResultKind.Merge,
            true,
            "merged two branches",
            AgentId: "merger"));
        Assert.Equal(WorkflowStatus.MergeComplete, (await store.GetAsync(workflow.Id))!.Status);

        await store.AddResultAsync(workflow.Id, new ResultInput(
            string.Empty,
            WorkflowResultKind.Review,
            true,
            "review passed",
            Findings: [new FindingInput(FindingSeverity.Info, "consider caching", "src/Api.cs", 12)],
            AgentId: "reviewer"));
        Assert.Equal(WorkflowStatus.Completed, (await store.GetAsync(workflow.Id))!.Status);

        var results = await store.GetResultsAsync(workflow.Id);
        Assert.Equal(4, results.Count);
        Assert.Single(results.Single(result => result.Kind == WorkflowResultKind.Review).Findings);

        var context = await store.GetContextAsync(workflow.Id, "api");
        Assert.Equal("Add endpoint", context.TaskTitle);
        Assert.NotNull(context.Assignment);
        Assert.Equal("impl-api", context.Assignment.AgentId);
        Assert.NotNull(context.LastResult);
        Assert.DoesNotContain("# Spec", context.Requirement, StringComparison.Ordinal);

        var timeline = await store.GetTimelineAsync(workflow.Id);
        Assert.True(timeline.Phases.Count >= 6);
        Assert.Equal(4, timeline.Results.Count);
    }

    [Fact]
    public async Task Answers_are_single_use_and_incremental_reads_work()
    {
        var store = CreateStore();
        var workflow = await store.CreateAsync(new WorkflowStart("W", "R", _repoPath));
        var questions = await store.AddQuestionsAsync(
            workflow.Id,
            [new("Q1"), new("Q2")]);

        var answers = await store.AddAnswersAsync(workflow.Id,
        [
            new AnswerInput(questions[0].Id, "A1"),
            new AnswerInput(questions[1].Id, "A2"),
        ]);

        var all = await store.GetAnswersAsync(workflow.Id, 0);
        Assert.Equal(2, all.Count);

        var incremental = await store.GetAnswersAsync(workflow.Id, answers[0].Id);
        Assert.Single(incremental);
        Assert.Equal("A2", incremental[0].Text);

        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => store.AddAnswersAsync(workflow.Id, [new AnswerInput(questions[0].Id, "again")]));
    }

    [Fact]
    public async Task Assignment_requires_a_spec_task()
    {
        var store = CreateStore();
        var workflow = await store.CreateAsync(new WorkflowStart("W", "R", _repoPath));

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => store.RegisterAssignmentAsync(workflow.Id, new AssignmentInput(
                "missing",
                "agent",
                "/tmp/wt",
                "anima/missing")));

        Assert.Contains("post the spec first", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_review_marks_the_workflow_failed()
    {
        var store = CreateStore();
        var workflow = await store.CreateAsync(new WorkflowStart("W", "R", _repoPath));
        await store.WriteSpecAsync(workflow.Id, new SpecInput("spec", [new SpecTaskInput("one", "One")]));
        await store.AddResultAsync(workflow.Id, new ResultInput("one", WorkflowResultKind.Implementation, true, "ok"));
        await store.AddResultAsync(workflow.Id, new ResultInput("", WorkflowResultKind.Merge, true, "merged"));
        await store.AddResultAsync(workflow.Id, new ResultInput(
            "",
            WorkflowResultKind.Review,
            false,
            "missing test",
            Findings: [new FindingInput(FindingSeverity.Error, "no test for /x")]));

        Assert.Equal(WorkflowStatus.Failed, (await store.GetAsync(workflow.Id))!.Status);
    }

    private KnowledgeWorkflowStore CreateStore()
    {
        var graph = new InMemoryKnowledgeGraph(
            Options.Create(new KnowledgeOptions()),
            NullLogger<InMemoryKnowledgeGraph>.Instance);
        return new KnowledgeWorkflowStore(
            graph,
            new NullWorkflowEventPublisher(),
            Options.Create(new WorkflowOptions()),
            NullLogger<KnowledgeWorkflowStore>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_repoPath))
        {
            Directory.Delete(_repoPath, recursive: true);
        }
    }
}
