using System.Text;
using Anima.Bus;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Anima.Workflow.Core.Tests;

public sealed class BusWorkflowDispatcherTests
{
    [Fact]
    public async Task Dispatch_returns_the_envelope_message_id()
    {
        var requester = new CapturingRequester();
        var dispatcher = CreateDispatcher(requester);

        var result = await dispatcher.DispatchAsync(
            7,
            new DispatchRequest("worker", "implementation", "do the task"));

        Assert.True(result.Success);
        Assert.Equal("done", result.Summary);
        Assert.Equal(requester.Request!.MessageId, result.MessageId);
        Assert.False(string.IsNullOrWhiteSpace(result.MessageId));
        Assert.Equal("anima.agent.worker.inbox", requester.Request.Topic);
    }

    [Fact]
    public async Task Dispatch_honours_an_explicit_message_id()
    {
        var requester = new CapturingRequester();
        var dispatcher = CreateDispatcher(requester);

        var result = await dispatcher.DispatchAsync(
            7,
            new DispatchRequest("worker", "implementation", "do the task", MessageId: "msg-42"));

        Assert.Equal("msg-42", requester.Request!.MessageId);
        Assert.Equal("msg-42", result.MessageId);
        Assert.Equal("msg-42", requester.Request.CorrelationId);
    }

    private static BusWorkflowDispatcher CreateDispatcher(IBusRequester requester)
        => new(
            requester,
            Options.Create(new WorkflowOptions()),
            NullLogger<BusWorkflowDispatcher>.Instance);

    private sealed class CapturingRequester : IBusRequester
    {
        public MessageEnvelope? Request { get; private set; }

        public Task<MessageEnvelope> AskAsync(
            MessageEnvelope request,
            BusRequestOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            var reply = MessageEnvelope.Create(
                "anima.reply",
                "anima.workflow.v1.WorkflowRoleResult",
                Encoding.UTF8.GetBytes("""{"success":true,"summary":"done"}"""));
            return Task.FromResult(reply);
        }
    }
}
