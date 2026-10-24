
namespace Anima.Bus.Core.Tests;

public sealed class TopicMatcherTests
{
    [Theory]
    [InlineData("anima.agent.*.inbox", "anima.agent.worker.inbox", true)]
    [InlineData("anima.agent.*.inbox", "anima.agent.worker.outbox", false)]
    [InlineData("anima.**", "anima.agent.worker.inbox", true)]
    [InlineData("anima.agent.worker.inbox", "anima.agent.worker.inbox", true)]
    [InlineData("anima.agent", "anima.agent.worker", false)]
    [InlineData("*", "anima", true)]
    [InlineData("**", "anything.at.all", true)]
    public void IsMatch_handles_segment_wildcards(string pattern, string topic, bool expected)
    {
        Assert.Equal(expected, TopicMatcher.IsMatch(pattern, topic));
    }
}
