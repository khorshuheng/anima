using Anima.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Anima.Bus.Core.Tests;

public sealed class InProcMessageBusTests
{
    [Fact]
    public async Task Publish_delivers_to_matching_subscriptions_only()
    {
        await using var bus = CreateBus();
        var received = new TaskCompletionSource<MessageEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var subscription = await bus.SubscribeAsync(
            "anima.agent.*.inbox",
            (envelope, _) =>
            {
                received.TrySetResult(envelope);
                return Task.CompletedTask;
            });

        await bus.PublishAsync(CreateEnvelope("anima.agent.worker.outbox"));
        await bus.PublishAsync(CreateEnvelope("anima.agent.worker.inbox"));

        var envelope = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("anima.agent.worker.inbox", envelope.Topic);
    }

    [Fact]
    public async Task Handler_failure_is_retried_and_then_dead_lettered()
    {
        await using var bus = CreateBus();
        int attempts = 0;
        var deadLetter = new TaskCompletionSource<MessageEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var failing = await bus.SubscribeAsync(
            "anima.test.topic",
            (_, _) =>
            {
                Interlocked.Increment(ref attempts);
                throw new InvalidOperationException("boom");
            });

        await using var dead = await bus.SubscribeAsync(
            BusTopics.DeadLetter,
            (envelope, _) =>
            {
                deadLetter.TrySetResult(envelope);
                return Task.CompletedTask;
            });

        await bus.PublishAsync(CreateEnvelope("anima.test.topic"));

        var letter = await deadLetter.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, attempts);
        Assert.Equal("boom", letter.Headers["anima-error"]);
    }

    [Fact]
    public async Task Mailbox_routes_between_agents()
    {
        await using var bus = CreateBus();
        var factory = new InProcAgentMailboxFactory(bus, NullLogger<InProcAgentMailboxFactory>.Instance);

        await using var alice = await factory.CreateAsync("alice");
        await using var bob = await factory.CreateAsync("bob");

        await alice.SendAsync("bob", CreateEnvelope("unused"));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        MessageEnvelope? received = null;
        await foreach (var envelope in bob.ReadAllAsync(timeout.Token))
        {
            received = envelope;
            break;
        }

        Assert.NotNull(received);
        Assert.Equal("alice", received.Sender);
        Assert.Equal("bob", received.Recipient);
        Assert.Equal(BusTopics.AgentInbox("bob"), received.Topic);
    }

    private static InProcMessageBus CreateBus() => new(
        Options.Create(new BusOptions { RetryDelay = TimeSpan.Zero }),
        NullLogger<InProcMessageBus>.Instance);

    private static MessageEnvelope CreateEnvelope(string topic) => MessageEnvelope.Create(
        topic,
        "anima.test.v1.Ping",
        new byte[] { 1, 2, 3 },
        ContractContentTypes.ProtobufJson,
        sender: "alice",
        recipient: "bob");
}
