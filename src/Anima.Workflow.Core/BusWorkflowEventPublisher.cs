using System.Text;
using System.Text.Json.Nodes;
using Anima.Bus;
using Anima.Contracts;
using Microsoft.Extensions.Logging;

namespace Anima.Workflow;

/// <summary>Publishes durable-state change notifications on the bus. Never authoritative.</summary>
public sealed class BusWorkflowEventPublisher : IWorkflowEventPublisher
{
    private readonly IBusPublisher _bus;
    private readonly ILogger<BusWorkflowEventPublisher> _logger;

    public BusWorkflowEventPublisher(IBusPublisher bus, ILogger<BusWorkflowEventPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(logger);
        _bus = bus;
        _logger = logger;
    }

    public async Task PublishAsync(WorkflowEvent workflowEvent, CancellationToken cancellationToken = default)
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

        await _bus.PublishAsync(envelope, cancellationToken);
        _logger.LogDebug(
            "Published workflow event {Event} for {WorkflowId}.",
            workflowEvent.Event,
            workflowEvent.WorkflowId);
    }
}

public sealed class NullWorkflowEventPublisher : IWorkflowEventPublisher
{
    public Task PublishAsync(WorkflowEvent workflowEvent, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
