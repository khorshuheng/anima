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
        Assert.Equal(WorkflowStatus.Assigned, (await store.GetAsync(workflow.Id))!.Status);

        await store.AcknowledgeAssignmentAsync(workflow.Id, "api", "run-api");
        Assert.Equal(WorkflowStatus.Implementing, (await store.GetAsync(workflow.Id))!.Status);

        await store.AddResultAsync(workflow.Id, Result(
            "api",
            WorkflowResultKind.Implementation,
            true,
            "endpoint implemented",
            ChangedFiles: ["src/Api.cs"],
            AgentId: "impl-api"));
        Assert.Equal(WorkflowStatus.Implementing, (await store.GetAsync(workflow.Id))!.Status);

        await store.AddResultAsync(workflow.Id, Result(
            "docs",
            WorkflowResultKind.Implementation,
            true,
            "docs updated",
            AgentId: "impl-docs"));
        Assert.Equal(WorkflowStatus.ImplementationComplete, (await store.GetAsync(workflow.Id))!.Status);

        await store.AddResultAsync(workflow.Id, Result(
            string.Empty,
            WorkflowResultKind.Merge,
            true,
            "merged two branches",
            AgentId: "merger"));
        Assert.Equal(WorkflowStatus.MergeComplete, (await store.GetAsync(workflow.Id))!.Status);

        await store.AddResultAsync(workflow.Id, Result(
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
    public async Task Planned_tasks_are_used_when_the_spec_posts_none()
    {
        var store = CreateStore();
        var workflow = await store.CreateAsync(new WorkflowStart(
            "W",
            "R",
            _repoPath,
            Tasks: [new SpecTaskInput("one", "One")]));

        var spec = await store.WriteSpecAsync(workflow.Id, new SpecInput("spec", []));

        Assert.Single(spec.Tasks);
        Assert.Equal("one", spec.Tasks[0].Key);
    }

    [Fact]
    public async Task Failed_review_marks_the_workflow_failed()
    {
        var store = CreateStore();
        var workflow = await store.CreateAsync(new WorkflowStart("W", "R", _repoPath));
        await store.WriteSpecAsync(workflow.Id, new SpecInput("spec", [new SpecTaskInput("one", "One")]));
        await store.AddResultAsync(workflow.Id, Result("one", WorkflowResultKind.Implementation, true, "ok"));
        await store.AddResultAsync(workflow.Id, Result("", WorkflowResultKind.Merge, true, "merged"));
        await store.AddResultAsync(workflow.Id, Result(
            "",
            WorkflowResultKind.Review,
            false,
            "missing test",
            Findings: [new FindingInput(FindingSeverity.Error, "no test for /x")]));

        Assert.Equal(WorkflowStatus.Failed, (await store.GetAsync(workflow.Id))!.Status);
    }

    [Fact]
    public async Task Process_role_tasks_do_not_block_merge_or_count_as_implementation()
    {
        var store = CreateStore();
        var workflow = await store.CreateAsync(new WorkflowStart("W", "R", _repoPath));
        await store.WriteSpecAsync(workflow.Id, new SpecInput("spec",
        [
            new SpecTaskInput("one", "One"),
            new SpecTaskInput("merge", "Merge", Role: WorkflowTaskRole.Merge),
            new SpecTaskInput("review", "Review", Role: WorkflowTaskRole.Review),
            new SpecTaskInput("verify", "Verify", Role: WorkflowTaskRole.Verification),
        ]));

        await store.AddResultAsync(workflow.Id, Result("one", WorkflowResultKind.Implementation, true, "one"));
        var implemented = await store.GetAsync(workflow.Id);
        Assert.Equal(WorkflowStatus.ImplementationComplete, implemented!.Status);
        Assert.Equal(1, implemented.TaskCount);
        Assert.Equal(1, implemented.CompletedTaskCount);

        await store.AddResultAsync(workflow.Id, Result("", WorkflowResultKind.Merge, true, "merged"));
        Assert.Equal(WorkflowStatus.MergeComplete, (await store.GetAsync(workflow.Id))!.Status);

        await store.AddResultAsync(workflow.Id, Result("", WorkflowResultKind.Review, true, "reviewed"));
        Assert.Equal(WorkflowStatus.Completed, (await store.GetAsync(workflow.Id))!.Status);
    }

    [Fact]
    public async Task Review_before_implementation_is_rejected()
    {
        var store = CreateStore();
        var workflow = await store.CreateAsync(new WorkflowStart("W", "R", _repoPath));
        await store.WriteSpecAsync(workflow.Id, new SpecInput("spec", [new SpecTaskInput("one", "One")]));

        var exception = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => store.AddResultAsync(workflow.Id, Result(
                string.Empty,
                WorkflowResultKind.Review,
                true,
                "looks good")));

        Assert.Contains("every spec task", exception.Message, StringComparison.Ordinal);
        Assert.NotEqual(WorkflowStatus.Completed, (await store.GetAsync(workflow.Id))!.Status);
    }

    [Fact]
    public async Task Merge_and_review_require_all_implementations_and_clean_review()
    {
        var store = CreateStore();
        var workflow = await store.CreateAsync(new WorkflowStart("W", "R", _repoPath));
        await store.WriteSpecAsync(workflow.Id, new SpecInput("spec",
        [
            new SpecTaskInput("one", "One"),
            new SpecTaskInput("two", "Two"),
        ]));
        await store.AddResultAsync(workflow.Id, Result("one", WorkflowResultKind.Implementation, true, "one"));

        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => store.AddResultAsync(workflow.Id, Result("", WorkflowResultKind.Merge, true, "too early")));
        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => store.AddResultAsync(workflow.Id, Result("", WorkflowResultKind.Review, true, "too early")));

        await store.AddResultAsync(workflow.Id, Result("two", WorkflowResultKind.Implementation, true, "two"));
        await store.AddResultAsync(workflow.Id, Result("", WorkflowResultKind.Merge, true, "merged"));

        var contradictory = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => store.AddResultAsync(workflow.Id, Result(
                string.Empty,
                WorkflowResultKind.Review,
                true,
                "passed with errors",
                Findings: [new FindingInput(FindingSeverity.Error, "missing test")])));
        Assert.Contains("error findings", contradictory.Message, StringComparison.Ordinal);

        await store.AddResultAsync(workflow.Id, Result(
            string.Empty,
            WorkflowResultKind.Review,
            true,
            "clean review"));
        Assert.Equal(WorkflowStatus.Completed, (await store.GetAsync(workflow.Id))!.Status);
    }

    [Fact]
    public async Task Results_beyond_the_page_size_still_complete_the_workflow()
    {
        var store = CreateStore(new WorkflowOptions { MaxPageSize = 2 });
        var workflow = await store.CreateAsync(new WorkflowStart("W", "R", _repoPath));
        await store.WriteSpecAsync(workflow.Id, new SpecInput("spec",
        [
            new SpecTaskInput("one", "One"),
            new SpecTaskInput("two", "Two"),
            new SpecTaskInput("three", "Three"),
        ]));

        foreach (var key in new[] { "one", "two", "three" })
        {
            await store.AddResultAsync(workflow.Id, Result(
                key,
                WorkflowResultKind.Implementation,
                true,
                $"done {key}"));
        }

        var state = await store.GetAsync(workflow.Id);
        Assert.Equal(WorkflowStatus.ImplementationComplete, state!.Status);
        Assert.Equal(3, state.TaskCount);
        Assert.Equal(3, state.CompletedTaskCount);
        Assert.Equal(3, (await store.GetResultsAsync(workflow.Id)).Count);
    }

    [Fact]
    public async Task Batch_answers_are_atomic()
    {
        var store = CreateStore();
        var workflow = await store.CreateAsync(new WorkflowStart("W", "R", _repoPath));
        var questions = await store.AddQuestionsAsync(workflow.Id, [new("Q1"), new("Q2")]);

        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => store.AddAnswersAsync(workflow.Id,
            [
                new AnswerInput(questions[0].Id, "valid"),
                new AnswerInput(999_999, "missing question"),
            ]));

        Assert.Empty(await store.GetAnswersAsync(workflow.Id, 0));
        Assert.All(
            await store.GetQuestionsAsync(workflow.Id, openOnly: false),
            question => Assert.False(question.Answered));
    }

    [Fact]
    public async Task Spec_hash_is_deterministic_and_exposed_on_state_and_context()
    {
        var store = CreateStore();
        var first = await store.CreateAsync(new WorkflowStart("W", "R", _repoPath));
        var second = await store.CreateAsync(new WorkflowStart("W2", "R2", _repoPath));

        var spec = await store.WriteSpecAsync(first.Id, new SpecInput(
            "# Spec\r\n\r\nDo the thing.\r\n",
            [
                new SpecTaskInput("api", "Add endpoint", "POST /x", ["201"], ["dotnet test"]),
                new SpecTaskInput("docs", "Update docs", "Explain /x"),
            ]));
        var sameSpec = await store.WriteSpecAsync(second.Id, new SpecInput(
            "# Spec\n\nDo the thing.\n",
            [
                new SpecTaskInput("api", "Add endpoint", "POST /x", ["201"], ["dotnet test"]),
                new SpecTaskInput("docs", "Update docs", "Explain /x"),
            ]));

        Assert.Equal(64, spec.Hash.Length);
        Assert.Equal(spec.Hash, sameSpec.Hash);

        var state = await store.GetAsync(first.Id);
        Assert.Equal(1, state!.SpecVersion);
        Assert.Equal(spec.Hash, state.SpecHash);

        var context = await store.GetContextAsync(first.Id, "api");
        Assert.Equal(1, context.SpecVersion);
        Assert.Equal(spec.Hash, context.SpecHash);

        var revised = await store.WriteSpecAsync(first.Id, new SpecInput(
            "# Spec\n\nDo the thing.\n",
            [
                new SpecTaskInput("api", "Add endpoint", "POST /x", ["201"], ["dotnet test"]),
                new SpecTaskInput("docs", "Update docs", "Explain /x and /y"),
            ]));
        Assert.Equal(2, revised.Version);
        Assert.NotEqual(spec.Hash, revised.Hash);
        Assert.Equal(revised.Hash, (await store.GetAsync(first.Id))!.SpecHash);
    }

    [Fact]
    public async Task Assignment_ack_persists_message_correlation()
    {
        var store = CreateStore();
        var workflow = await store.CreateAsync(new WorkflowStart("W", "R", _repoPath));
        await store.WriteSpecAsync(workflow.Id, new SpecInput("spec", [new SpecTaskInput("api", "API")]));

        var assignment = await store.RegisterAssignmentAsync(workflow.Id, new AssignmentInput(
            "api",
            "impl-api",
            "/tmp/worktrees/api",
            "anima/api",
            MessageId: "msg-42"));
        Assert.Equal("msg-42", assignment.MessageId);
        Assert.Null(assignment.AcknowledgedAt);
        Assert.Null(assignment.RunId);
        Assert.Equal(WorkflowStatus.Assigned, (await store.GetAsync(workflow.Id))!.Status);

        var acknowledged = await store.AcknowledgeAssignmentAsync(workflow.Id, "api", "run-7");

        Assert.Equal("msg-42", acknowledged.MessageId);
        Assert.NotNull(acknowledged.AcknowledgedAt);
        Assert.Equal("run-7", acknowledged.RunId);
        Assert.Equal("acknowledged", acknowledged.Status);
        Assert.Equal(WorkflowStatus.Implementing, (await store.GetAsync(workflow.Id))!.Status);
        var persisted = Assert.Single(await store.GetAssignmentsAsync(workflow.Id));
        Assert.Equal(acknowledged.AcknowledgedAt, persisted.AcknowledgedAt);
        Assert.Equal("run-7", persisted.RunId);

        var context = await store.GetContextAsync(workflow.Id, "api");
        Assert.Equal("msg-42", context.Assignment!.MessageId);
        Assert.Equal("run-7", context.Assignment.RunId);

        await Assert.ThrowsAsync<WorkflowNotFoundException>(
            () => store.AcknowledgeAssignmentAsync(workflow.Id, "missing"));
    }

    [Fact]
    public async Task Reassigning_the_same_packet_preserves_ack_but_a_new_packet_or_agent_clears_it()
    {
        var store = CreateStore();
        var workflow = await store.CreateAsync(new WorkflowStart("W", "R", _repoPath));
        await store.WriteSpecAsync(workflow.Id, new SpecInput("spec", [new SpecTaskInput("api", "API")]));

        await store.RegisterAssignmentAsync(workflow.Id, new AssignmentInput(
            "api", "impl-api", "/tmp/worktrees/api", "anima/api", MessageId: "msg-1"));
        await store.AcknowledgeAssignmentAsync(workflow.Id, "api", "run-1");

        var redelivered = await store.RegisterAssignmentAsync(workflow.Id, new AssignmentInput(
            "api", "impl-api", "/tmp/worktrees/api", "anima/api", MessageId: "msg-1"));
        Assert.NotNull(redelivered.AcknowledgedAt);
        Assert.Equal("run-1", redelivered.RunId);
        Assert.Equal("acknowledged", redelivered.Status);
        Assert.Equal(WorkflowStatus.Implementing, (await store.GetAsync(workflow.Id))!.Status);

        var newPacket = await store.RegisterAssignmentAsync(workflow.Id, new AssignmentInput(
            "api", "impl-api", "/tmp/worktrees/api", "anima/api", MessageId: "msg-2"));
        Assert.Equal("msg-2", newPacket.MessageId);
        Assert.Null(newPacket.AcknowledgedAt);
        Assert.Null(newPacket.RunId);
        Assert.Equal("assigned", newPacket.Status);
        Assert.Equal(WorkflowStatus.Assigned, (await store.GetAsync(workflow.Id))!.Status);

        var replaced = await store.RegisterAssignmentAsync(workflow.Id, new AssignmentInput(
            "api", "impl-other", "/tmp/worktrees/other", "anima/other", MessageId: "msg-3"));
        Assert.Null(replaced.AcknowledgedAt);
        Assert.Null(replaced.RunId);
        Assert.Equal("assigned", replaced.Status);
        Assert.Equal(WorkflowStatus.Assigned, (await store.GetAsync(workflow.Id))!.Status);
    }

    [Fact]
    public async Task Result_evidence_round_trips_and_defaults_are_empty()
    {
        var store = CreateStore();
        var workflow = await store.CreateAsync(new WorkflowStart("W", "R", _repoPath));
        await store.WriteSpecAsync(workflow.Id, new SpecInput("spec",
        [
            new SpecTaskInput("api", "API"),
            new SpecTaskInput("docs", "Docs"),
        ]));

        await store.AddResultAsync(workflow.Id, Result(
            "api",
            WorkflowResultKind.Implementation,
            true,
            "implemented",
            ChangedFiles: ["src/Api.cs"],
            AgentId: "impl-api",
            HeadSha: "abc1234",
            Branch: "anima/api",
            BaseRef: "main",
            RunId: "run-7",
            TestCommands: ["dotnet test Anima.Workflow.slnf"],
            ArtifactPaths: ["artifacts/api.trx"]));

        var evidence = (await store.GetResultsAsync(workflow.Id)).Single();
        Assert.Equal("abc1234", evidence.HeadSha);
        Assert.Equal("anima/api", evidence.Branch);
        Assert.Equal("main", evidence.BaseRef);
        Assert.Equal("run-7", evidence.RunId);
        Assert.Equal(["dotnet test Anima.Workflow.slnf"], evidence.TestCommands.ToArray());
        Assert.Equal(["artifacts/api.trx"], evidence.ArtifactPaths.ToArray());

        var context = await store.GetContextAsync(workflow.Id, "api");
        Assert.Equal("abc1234", context.LastResult!.HeadSha);
        Assert.Equal(["artifacts/api.trx"], context.LastResult.ArtifactPaths.ToArray());

        await store.AddResultAsync(workflow.Id, new ResultInput(
            "docs",
            WorkflowResultKind.Verification,
            true,
            "docs"));
        var defaults = (await store.GetResultsAsync(workflow.Id)).Single(result => result.TaskKey == "docs");
        Assert.Null(defaults.HeadSha);
        Assert.Null(defaults.Branch);
        Assert.Null(defaults.BaseRef);
        Assert.Null(defaults.RunId);
        Assert.Empty(defaults.TestCommands);
        Assert.Empty(defaults.ArtifactPaths);
    }

    [Fact]
    public async Task Successful_gated_results_require_structured_evidence()
    {
        var store = CreateStore();
        var workflow = await store.CreateAsync(new WorkflowStart("W", "R", _repoPath));
        await store.WriteSpecAsync(workflow.Id, new SpecInput("spec", [new SpecTaskInput("one", "One")]));

        var implementation = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => store.AddResultAsync(
                workflow.Id,
                new ResultInput("one", WorkflowResultKind.Implementation, true, "done")));
        Assert.Contains("headSha", implementation.Message, StringComparison.Ordinal);

        var malformed = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => store.AddResultAsync(
                workflow.Id,
                Result("one", WorkflowResultKind.Implementation, true, "done", HeadSha: "not-a-sha")));
        Assert.Contains("hexadecimal", malformed.Message, StringComparison.Ordinal);

        await store.AddResultAsync(
            workflow.Id,
            Result("one", WorkflowResultKind.Implementation, true, "done"));

        var merge = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => store.AddResultAsync(
                workflow.Id,
                new ResultInput(string.Empty, WorkflowResultKind.Merge, true, "merged")));
        Assert.Contains("headSha", merge.Message, StringComparison.Ordinal);

        await store.AddResultAsync(
            workflow.Id,
            Result(string.Empty, WorkflowResultKind.Merge, true, "merged"));

        var review = await Assert.ThrowsAsync<WorkflowValidationException>(
            () => store.AddResultAsync(
                workflow.Id,
                new ResultInput(string.Empty, WorkflowResultKind.Review, true, "reviewed")));
        Assert.Contains("headSha", review.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_results_may_omit_evidence()
    {
        var store = CreateStore();
        var workflow = await store.CreateAsync(new WorkflowStart("W", "R", _repoPath));
        await store.WriteSpecAsync(workflow.Id, new SpecInput("spec", [new SpecTaskInput("one", "One")]));

        var failed = await store.AddResultAsync(
            workflow.Id,
            new ResultInput("one", WorkflowResultKind.Implementation, false, "build failed"));

        Assert.False(failed.Success);
        Assert.Null(failed.HeadSha);
    }

    private static ResultInput Result(
        string taskKey,
        WorkflowResultKind kind,
        bool success,
        string summary,
        IReadOnlyList<string>? ChangedFiles = null,
        IReadOnlyList<string>? Conflicts = null,
        IReadOnlyList<FindingInput>? Findings = null,
        string? AgentId = null,
        string? WorktreePath = null,
        string? OutputTail = null,
        string? HeadSha = null,
        string? Branch = null,
        string? BaseRef = null,
        string? RunId = null,
        IReadOnlyList<string>? TestCommands = null,
        IReadOnlyList<string>? ArtifactPaths = null)
        => new(
            taskKey,
            kind,
            success,
            summary,
            ChangedFiles,
            Conflicts,
            Findings,
            AgentId,
            WorktreePath,
            OutputTail,
            HeadSha ?? (success ? "abc1234" : null),
            Branch ?? (success ? "test-branch" : null),
            BaseRef ?? (success ? "main" : null),
            RunId,
            TestCommands ?? (success ? ["dotnet test"] : []),
            ArtifactPaths);

    private KnowledgeWorkflowStore CreateStore(WorkflowOptions? options = null)
    {
        var graph = new InMemoryKnowledgeGraph(
            Options.Create(new KnowledgeOptions()),
            NullLogger<InMemoryKnowledgeGraph>.Instance);
        return new KnowledgeWorkflowStore(
            graph,
            new NullWorkflowEventPublisher(),
            Options.Create(options ?? new WorkflowOptions()),
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
