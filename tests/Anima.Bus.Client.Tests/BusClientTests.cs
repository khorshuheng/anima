using System.Net;
using System.Text;
using Anima.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Anima.Bus.Client.Tests;

public sealed class BusClientTests
{
    [Fact]
    public void Envelope_json_round_trips_json_payloads_and_headers()
    {
        var envelope = MessageEnvelope.Create(
            "anima.test.topic",
            "anima.test.v1.Ping",
            Encoding.UTF8.GetBytes("""{"text":"hi"}"""),
            ContractContentTypes.ProtobufJson,
            sender: "alice",
            recipient: "bob",
            correlationId: "c1",
            replyTo: "anima.reply.r1",
            headers: new Dictionary<string, string> { ["k"] = "v" });

        var json = BusEnvelopeJson.ToJson(envelope);
        using var document = System.Text.Json.JsonDocument.Parse(json.ToJsonString());
        var roundTrip = BusEnvelopeJson.FromJson(document.RootElement);

        Assert.Equal(envelope.Topic, roundTrip.Topic);
        Assert.Equal(envelope.TypeName, roundTrip.TypeName);
        Assert.Equal("alice", roundTrip.Sender);
        Assert.Equal("bob", roundTrip.Recipient);
        Assert.Equal("c1", roundTrip.CorrelationId);
        Assert.Equal("anima.reply.r1", roundTrip.ReplyTo);
        Assert.Equal("v", roundTrip.Headers["k"]);
        Assert.Equal("""{"text":"hi"}""", Encoding.UTF8.GetString(roundTrip.Payload.Span));
    }

    [Fact]
    public async Task Sse_reader_parses_frames()
    {
        var envelope = MessageEnvelope.Create(
            "anima.test.topic",
            "anima.test.v1.Ping",
            Encoding.UTF8.GetBytes("""{"text":"hi"}"""),
            ContractContentTypes.ProtobufJson);
        var frame = "event: message\ndata: " + BusEnvelopeJson.ToJson(envelope).ToJsonString() + "\n\n";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(frame));

        var items = new List<MessageEnvelope>();
        await foreach (var item in SseEnvelopeReader.ReadAsync(stream))
        {
            items.Add(item);
        }

        Assert.Single(items);
        Assert.Equal("anima.test.v1.Ping", items[0].TypeName);
    }

    [Fact]
    public async Task Sse_reader_signals_the_ready_frame_before_messages()
    {
        var envelope = MessageEnvelope.Create(
            "anima.test.topic",
            "anima.test.v1.Ping",
            Encoding.UTF8.GetBytes("{}"),
            ContractContentTypes.ProtobufJson);
        var frame = "event: ready\ndata: {}\n\n" +
                    "event: message\ndata: " + BusEnvelopeJson.ToJson(envelope).ToJsonString() + "\n\n";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(frame));
        var ready = false;

        var items = new List<MessageEnvelope>();
        await foreach (var item in SseEnvelopeReader.ReadAsync(
                           stream,
                           () =>
                           {
                               ready = true;
                               return Task.CompletedTask;
                           }))
        {
            items.Add(item);
        }

        Assert.True(ready);
        Assert.Single(items);
    }

    [Fact]
    public async Task Http_bus_publish_posts_the_envelope()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://bus.local/") };
        await using var bus = new HttpMessageBus(
            http,
            Options.Create(new BusClientOptions()),
            NullLogger<HttpMessageBus>.Instance);

        await bus.PublishAsync(
            MessageEnvelope.Create(
                "anima.test.topic",
                "anima.test.v1.Ping",
                Encoding.UTF8.GetBytes("{}"),
                ContractContentTypes.ProtobufJson,
                replyTo: "anima.reply.r1"));

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("/api/bus/publish", handler.Path);
        Assert.Contains("\"topic\":\"anima.test.topic\"", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"replyTo\":\"anima.reply.r1\"", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http_bus_publish_times_out_when_the_server_stalls()
    {
        var handler = new SlowHandler(TimeSpan.FromMilliseconds(250));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://bus.local/") };
        await using var bus = new HttpMessageBus(
            http,
            Options.Create(new BusClientOptions { PublishTimeout = TimeSpan.FromMilliseconds(25) }),
            NullLogger<HttpMessageBus>.Instance);

        var exception = await Assert.ThrowsAsync<TimeoutException>(
            () => bus.PublishAsync(
                MessageEnvelope.Create(
                    "anima.test.topic",
                    "anima.test.v1.Ping",
                    ReadOnlyMemory<byte>.Empty,
                    ContractContentTypes.ProtobufJson)).AsTask());

        Assert.Contains("timed out", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Requester_correlates_a_reply_on_a_private_topic()
    {
        await using var bus = new InProcMessageBus(
            Options.Create(new BusOptions { RetryDelay = TimeSpan.Zero }),
            NullLogger<InProcMessageBus>.Instance);
        var requester = new BusRequester(bus, bus);

        await using var responder = await bus.SubscribeAsync(
            "anima.test.ask",
            async (request, cancellationToken) =>
            {
                await bus.PublishAsync(
                    MessageEnvelope.Create(
                        request.ReplyTo!,
                        "anima.test.v1.Pong",
                        Encoding.UTF8.GetBytes("""{"ok":true}"""),
                        ContractContentTypes.ProtobufJson,
                        correlationId: request.MessageId),
                    cancellationToken);
            });

        var reply = await requester.AskAsync(
            MessageEnvelope.Create(
                "anima.test.ask",
                "anima.test.v1.Ping",
                Encoding.UTF8.GetBytes("{}"),
                ContractContentTypes.ProtobufJson),
            new BusRequestOptions { Timeout = TimeSpan.FromSeconds(5) });

        Assert.Equal("anima.test.v1.Pong", reply.TypeName);
        Assert.StartsWith(BusTopics.ReplyPrefix, reply.Topic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Requester_times_out_without_a_reply()
    {
        await using var bus = new InProcMessageBus(
            Options.Create(new BusOptions { RetryDelay = TimeSpan.Zero }),
            NullLogger<InProcMessageBus>.Instance);
        var requester = new BusRequester(bus, bus);

        var exception = await Assert.ThrowsAsync<BusRequestTimeoutException>(
            () => requester.AskAsync(
                MessageEnvelope.Create(
                    "anima.test.none",
                    "anima.test.v1.Ping",
                    ReadOnlyMemory<byte>.Empty,
                    ContractContentTypes.ProtobufJson),
                new BusRequestOptions { Timeout = TimeSpan.FromMilliseconds(100) }));

        Assert.Contains("no reply", exception.Message, StringComparison.Ordinal);
    }

    private sealed class SlowHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public string? Path { get; private set; }

        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            Path = request.RequestUri?.AbsolutePath;
            Body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }
}
