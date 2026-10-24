using System.Globalization;

namespace Anima.Dashboard.Service.Tests;

public sealed class WorkflowSummaryBuilderTests
{
    [Fact]
    public void Summary_joins_tasks_agents_results_reviews_and_evidence()
    {
        var start = DateTimeOffset.Parse("2026-10-24T09:00:00Z", CultureInfo.InvariantCulture);
        var state = new WorkflowSummaryService.WorkflowStateDto(
            7,
            "Feature",
            "completed",
            start,
            start.AddMinutes(12),
            2,
            "hash");
        var spec = new WorkflowSummaryService.SpecDto(
        [
            new("api", "API", "implementation"),
            new("docs", "Docs", "documentation"),
            new("merge", "Merge", "merge"),
            new("review", "Review", "review"),
        ]);
        var assignments = new[]
        {
            new WorkflowSummaryService.AssignmentDto(
                1, "api", "impl-api", "acknowledged", "msg-1",
                start.AddMinutes(1), "run-api", start.AddMinutes(1)),
            new WorkflowSummaryService.AssignmentDto(
                2, "docs", "impl-docs", "assigned", null, null, null, start.AddMinutes(2)),
        };
        var results = new[]
        {
            new WorkflowSummaryService.ResultDto(
                10, "api", "implementation", true, "ok", "impl-api",
                "abc1234", "run-api", ["dotnet test"], [], start.AddMinutes(3)),
            new WorkflowSummaryService.ResultDto(
                11, "docs", "implementation", true, "ok", "impl-docs",
                null, null, [], [], start.AddMinutes(4)),
            new WorkflowSummaryService.ResultDto(
                12, "", "merge", true, "merged", "merger",
                "abc1234", "run-merge", ["make check"], [], start.AddMinutes(5)),
            new WorkflowSummaryService.ResultDto(
                13, "", "review", true, "reviewed", "reviewer",
                "abc1234", "run-review", ["make check"],
                [
                    new("warning", "minor"),
                    new("error", "blocking"),
                ],
                start.AddMinutes(6)),
        };
        var phases = new[]
        {
            new WorkflowSummaryService.PhaseDto(1, "analysis", "analysis", null, start),
            new WorkflowSummaryService.PhaseDto(2, "completed", "completed", "done", start.AddMinutes(12)),
        };

        var summary = WorkflowSummaryBuilder.Build(state, spec, assignments, results, phases);

        Assert.Equal(4, summary.Tasks.Total);
        Assert.Equal(1, summary.Tasks.Implementation);
        Assert.Equal(1, summary.Tasks.Merge);
        Assert.Equal(1, summary.Tasks.Review);
        Assert.Equal(1, summary.Tasks.Documentation);
        Assert.Equal(4, summary.Agents.RecordedAgents);
        Assert.Equal(3, summary.Agents.RecordedRuns);
        Assert.Equal(4, summary.Results.Total);
        Assert.Equal(2, summary.Results.Findings);
        Assert.Equal(1, summary.Results.Errors);
        Assert.Equal(1, summary.Results.Warnings);
        Assert.Single(summary.ReviewIterations);
        Assert.Equal(1, summary.ReviewIterations[0].Index);
        Assert.Equal("reviewer", summary.ReviewIterations[0].AgentId);
        Assert.Equal(1, summary.Evidence.ImplementationWithHeadSha);
        Assert.Equal(1, summary.Evidence.ImplementationWithTests);
        Assert.Equal(1, summary.Evidence.ReviewWithHeadSha);
        Assert.Equal(1, summary.Evidence.ReviewWithTests);
        Assert.Equal(720_000, summary.DurationMs);
        Assert.Equal(2, summary.Phases.Count);
    }

    [Fact]
    public void Multiple_reviews_are_numbered_in_timestamp_order()
    {
        var start = DateTimeOffset.UtcNow;
        var state = new WorkflowSummaryService.WorkflowStateDto(
            8, "Rereview", "completed", start, start.AddMinutes(3), 1, null);
        var results = new[]
        {
            new WorkflowSummaryService.ResultDto(
                2, "", "review", true, "second", "reviewer", null, null, [], [], start.AddMinutes(2)),
            new WorkflowSummaryService.ResultDto(
                1, "", "review", false, "first", "reviewer", null, null, [], [], start.AddMinutes(1)),
        };

        var summary = WorkflowSummaryBuilder.Build(
            state,
            null,
            [],
            results,
            []);

        Assert.Equal(2, summary.ReviewIterations.Count);
        Assert.Equal("first", summary.ReviewIterations[0].Summary);
        Assert.Equal(1, summary.ReviewIterations[0].Index);
        Assert.Equal("second", summary.ReviewIterations[1].Summary);
        Assert.Equal(2, summary.ReviewIterations[1].Index);
    }
}
