namespace Anima.Dashboard.Service.Tests;

public sealed class GoalCompletionTests
{
    [Fact]
    public void Direct_resolution_wins()
    {
        var result = GoalCompletion.Compute(true, "implementing", 0, 0);

        Assert.True(result.Resolved);
        Assert.Equal(GoalCompletion.Direct, result.Source);
    }

    [Fact]
    public void Completed_workflow_resolves_the_goal()
    {
        var result = GoalCompletion.Compute(false, "completed", 2, 0);

        Assert.True(result.Resolved);
        Assert.Equal(GoalCompletion.Workflow, result.Source);
    }

    [Fact]
    public void All_child_tasks_resolved_resolves_the_goal()
    {
        var result = GoalCompletion.Compute(false, null, 3, 3);

        Assert.True(result.Resolved);
        Assert.Equal(GoalCompletion.Tasks, result.Source);
    }

    [Fact]
    public void Partial_or_absent_tasks_stay_open()
    {
        var partial = GoalCompletion.Compute(false, "implementing", 3, 2);
        var noTasks = GoalCompletion.Compute(false, null, 0, 0);

        Assert.False(partial.Resolved);
        Assert.Equal(GoalCompletion.Open, partial.Source);
        Assert.False(noTasks.Resolved);
        Assert.Equal(GoalCompletion.Open, noTasks.Source);
    }
}
