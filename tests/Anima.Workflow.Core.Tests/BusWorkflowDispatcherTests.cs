using Anima.Bus;
using Microsoft.Extensions.Logging.Abstractions;

namespace Anima.Workflow.Core.Tests;

public sealed class BusWorkflowDispatcherTests
{
    [Fact]
    public async Task Dispatch_publishes_and_returns_the_envelope_message_id()
    {
        var publisher = new CapturingPublisher();
        var dispatcher = CreateDispatcher(publisher);

        var result = await dispatcher.DispatchAsync(
            7,
            new DispatchRequest("worker", "implementation", "do the task"));

        Assert.True(result.Success);
        Assert.Equal("published", result.Summary);
        Assert.Equal(publisher.Envelope!.MessageId, result.MessageId);
        Assert.False(string.IsNullOrWhiteSpace(result.MessageId));
        Assert.Equal("anima.agent.worker.inbox", result.Topic);
        Assert.Equal("anima.agent.worker.inbox", publisher.Envelope.Topic);
        Assert.Null(publisher.Envelope.ReplyTo);
    }

    [Fact]
    public async Task Dispatch_honours_an_explicit_message_id()
    {
        var publisher = new CapturingPublisher();
        var dispatcher = CreateDispatcher(publisher);

        var result = await dispatcher.DispatchAsync(
            7,
            new DispatchRequest("worker", "implementation", "do the task", MessageId: "msg-42"));

        Assert.Equal("msg-42", publisher.Envelope!.MessageId);
        Assert.Equal("msg-42", result.MessageId);
        Assert.Equal("msg-42", publisher.Envelope.CorrelationId);
    }

    [Fact]
    public async Task Dispatch_rejects_an_unsafe_message_id()
    {
        var publisher = new CapturingPublisher();
        var dispatcher = CreateDispatcher(publisher);

        await Assert.ThrowsAsync<WorkflowValidationException>(
            () => dispatcher.DispatchAsync(
                7,
                new DispatchRequest("worker", "implementation", "do the task", MessageId: "**")));

        Assert.Null(publisher.Envelope);
    }

    [Fact]
    public async Task Dispatch_reports_publish_failure_without_throwing()
    {
        var publisher = new CapturingPublisher { Failure = new InvalidOperationException("bus down") };
        var dispatcher = CreateDispatcher(publisher);

        var result = await dispatcher.DispatchAsync(
            7,
            new DispatchRequest("worker", "implementation", "do the task", MessageId: "msg-1"));

        Assert.False(result.Success);
        Assert.Equal("publish failed", result.Summary);
        Assert.Equal("bus down", result.Error);
        Assert.Equal("msg-1", result.MessageId);
    }

    private static BusWorkflowDispatcher CreateDispatcher(IBusPublisher publisher)
        => new(publisher, NullLogger<BusWorkflowDispatcher>.Instance);

    private sealed class CapturingPublisher : IBusPublisher
    {
        public MessageEnvelope? Envelope { get; private set; }

        public Exception? Failure { get; init; }

        public ValueTask PublishAsync(
            MessageEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            Envelope = envelope;
            return Failure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(Failure);
        }
    }
}
