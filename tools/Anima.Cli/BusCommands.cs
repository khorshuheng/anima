using System.Text;
using Anima.Bus;
using Anima.Bus.Client;
using Anima.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Anima.Cli;

internal static class BusCommands
{
    public static async Task<int> RunAsync(string[] args, string baseUrl)
    {
        if (args.Length == 0)
        {
            return Help();
        }

        await using var bus = CreateBus(baseUrl);
        return args[0] switch
        {
            "publish" => await PublishAsync(bus, args),
            "ask" => await AskAsync(bus, args),
            "watch" => await WatchAsync(bus, args),
            _ => Help(),
        };
    }

    private static HttpMessageBus CreateBus(string baseUrl) => new(
        new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") },
        Options.Create(new BusClientOptions { BaseUrl = baseUrl }),
        NullLogger<HttpMessageBus>.Instance);

    private static async Task<int> PublishAsync(HttpMessageBus bus, string[] args)
    {
        string? topic = CliJson.Option(args, "--topic");
        string? type = CliJson.Option(args, "--type");
        if (string.IsNullOrWhiteSpace(topic) || string.IsNullOrWhiteSpace(type))
        {
            await Console.Error.WriteLineAsync("usage: anima bus publish --topic <topic> --type <protoMessage> [--payload <json|@file>] [--reply-to <topic>] [--correlation <id>] [--sender <id>] [--recipient <id>]");
            return 2;
        }

        string payload = await CliJson.ReadPayloadAsync(CliJson.Option(args, "--payload"));
        var envelope = MessageEnvelope.Create(
            topic,
            type,
            Encoding.UTF8.GetBytes(payload),
            ContractContentTypes.ProtobufJson,
            sender: CliJson.Option(args, "--sender"),
            recipient: CliJson.Option(args, "--recipient"),
            correlationId: CliJson.Option(args, "--correlation"),
            replyTo: CliJson.Option(args, "--reply-to"));

        await bus.PublishAsync(envelope);
        await Console.Out.WriteLineAsync(CliJson.Serialize(new { envelope.MessageId, envelope.Topic, envelope.TypeName }));
        return 0;
    }

    private static async Task<int> AskAsync(HttpMessageBus bus, string[] args)
    {
        string? topic = CliJson.Option(args, "--topic");
        string? type = CliJson.Option(args, "--type");
        if (string.IsNullOrWhiteSpace(topic) || string.IsNullOrWhiteSpace(type))
        {
            await Console.Error.WriteLineAsync("usage: anima bus ask --topic <agent inbox> --type <protoMessage> [--payload <json|@file>] [--timeout <seconds>] [--sender <id>]");
            return 2;
        }

        int timeout = int.TryParse(CliJson.Option(args, "--timeout"), out int seconds) ? seconds : 30;
        string payload = await CliJson.ReadPayloadAsync(CliJson.Option(args, "--payload"));
        var request = MessageEnvelope.Create(
            topic,
            type,
            Encoding.UTF8.GetBytes(payload),
            ContractContentTypes.ProtobufJson,
            sender: CliJson.Option(args, "--sender") ?? "anima-cli");

        var requester = new BusRequester(bus, bus);
        var reply = await requester.AskAsync(
            request,
            new BusRequestOptions { Timeout = TimeSpan.FromSeconds(timeout) });
        await Console.Out.WriteLineAsync(BusEnvelopeJson.ToJson(reply).ToJsonString());
        return 0;
    }

    private static async Task<int> WatchAsync(HttpMessageBus bus, string[] args)
    {
        string topics = CliJson.Option(args, "--topics") ?? BusTopics.All;
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        await using var subscription = await bus.SubscribeAsync(
            topics,
            async (envelope, deliveryToken) =>
            {
                await Console.Out.WriteLineAsync(BusEnvelopeJson.ToJson(envelope).ToJsonString())
                    .WaitAsync(deliveryToken);
                await Console.Out.FlushAsync(deliveryToken);
            },
            new SubscriptionOptions { Name = "anima-cli-watch" },
            cancellation.Token);

        await Console.Error.WriteLineAsync($"watching {topics} (ctrl-c to stop)");
        try
        {
            await Task.Delay(Timeout.Infinite, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Ctrl-C.
        }

        return 0;
    }

    private static int Help()
    {
        Console.WriteLine("""
            usage:
              anima bus publish --topic <topic> --type <protoMessage> [--payload <json|@file>]
              anima bus ask     --topic <agent inbox> --type <protoMessage> [--payload <json|@file>] [--timeout <s>]
              anima bus watch   [--topics <pattern>]

            payloads are descriptor-validated protobuf JSON; ask prints the reply envelope.
            """);
        return 0;
    }
}
