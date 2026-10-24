using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Anima.Bus;
using Anima.Contracts;
using Microsoft.Extensions.Logging;

namespace Anima.Workflow;

/// <summary>Publishes durable-state change notifications on the bus. Never authoritative.</summary>
public sealed class BusWorkflowEventPublisher : IWorkflowEventPublisher, IAsyncDisposable
{
    private readonly IBusPublisher _bus;
    private readonly ILogger<BusWorkflowEventPublisher> _logger;
    private readonly Channel<MessageEnvelope> _queue = Channel.CreateBounded<MessageEnvelope>(
        new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly Task _pump;

    public BusWorkflowEventPublisher(IBusPublisher bus, ILogger<BusWorkflowEventPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(logger);
        _bus = bus;
        _logger = logger;
        _pump = Task.Run(PumpAsync);
    }

    public Task PublishAsync(WorkflowEvent workflowEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflowEvent);

        var payload = new JsonObject
        {
            ["workflowId"] = workflowEvent.WorkflowId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["event"] = workflowEvent.Event,
            ["status"] = workflowEvent.Status.ToString().ToLowerInvariant(),
            ["nodeIds"] = new JsonArray(workflowEvent.NodeIds
                .Select(id => (JsonNode)id.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .ToArray()),
            ["summary"] = workflowEvent.Summary,
            ["timestamp"] = workflowEvent.Timestamp,
        };

        var envelope = MessageEnvelope.Create(
            BusTopics.WorkflowEvents,
            "anima.workflow.v1.WorkflowEvent",
            Encoding.UTF8.GetBytes(payload.ToJsonString()),
            ContractContentTypes.ProtobufJson,
            sender: "anima-workflow");

        if (!_queue.Writer.TryWrite(envelope))
        {
            _logger.LogWarning(
                "Workflow event queue full; dropped {Event} for {WorkflowId}.",
                workflowEvent.Event,
                workflowEvent.WorkflowId);
        }

        return Task.CompletedTask;
    }

    private async Task PumpAsync()
    {
        await foreach (var envelope in _queue.Reader.ReadAllAsync())
        {
            try
            {
                await _bus.PublishAsync(envelope, CancellationToken.None);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to publish workflow event {Topic}.", envelope.Topic);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _pump;
    }
}

public sealed class NullWorkflowEventPublisher : IWorkflowEventPublisher
{
    public Task PublishAsync(WorkflowEvent workflowEvent, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
