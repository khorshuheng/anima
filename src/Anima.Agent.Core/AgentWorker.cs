using System.Text;
using System.Text.Json;
using Anima.Bus;
using Anima.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Anima.Agent;

/// <summary>
/// Consumes the agent's inbox and runs turns. One worker per process keeps the
/// scaffold honest: scale out by running more processes with distinct agent ids.
/// </summary>
public sealed class AgentWorker : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IAgentRuntime _runtime;
    private readonly IAgentMailboxFactory _mailboxes;
    private readonly AgentOptions _options;
    private readonly ILogger<AgentWorker> _logger;

    public AgentWorker(
        IAgentRuntime runtime,
        IAgentMailboxFactory mailboxes,
        IOptions<AgentOptions> options,
        ILogger<AgentWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(mailboxes);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _runtime = runtime;
        _mailboxes = mailboxes;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var descriptor = _options.ToDescriptor();
        _logger.LogInformation("Agent {AgentId} listening on {Topic}.",
            descriptor.Id, BusTopics.AgentInbox(descriptor.Id));

        await using var mailbox = await _mailboxes.CreateAsync(descriptor.Id, stoppingToken);
        try
        {
            await foreach (var envelope in mailbox.ReadAllAsync(stoppingToken))
            {
                var input = Encoding.UTF8.GetString(envelope.Payload.Span);
                if (envelope.ContentType == ContractContentTypes.Json)
                {
                    input = ExtractInput(input);
                }

                var result = await _runtime.RunTurnAsync(
                    descriptor,
                    input,
                    variables: null,
                    stoppingToken);

                if (!string.IsNullOrWhiteSpace(envelope.Sender))
                {
                    var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result, JsonOptions));
                    await mailbox.SendAsync(
                        envelope.Sender,
                        MessageEnvelope.Create(
                            BusTopics.TaskResult,
                            "anima.agent.v1.AgentTurnResult",
                            payload,
                            ContractContentTypes.Json,
                            sender: descriptor.Id,
                            recipient: envelope.Sender,
                            correlationId: envelope.CorrelationId),
                        stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    private static string ExtractInput(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in new[] { "instructions", "input", "message", "body" })
                {
                    if (document.RootElement.TryGetProperty(property, out var value) &&
                        value.ValueKind == JsonValueKind.String)
                    {
                        return value.GetString() ?? json;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON after all; use the raw payload.
        }

        return json;
    }
}
