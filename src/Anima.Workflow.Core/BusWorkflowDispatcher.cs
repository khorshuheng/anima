using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anima.Bus;
using Anima.Contracts;
using Microsoft.Extensions.Logging;

namespace Anima.Workflow;

/// <summary>
/// Publishes a role task to an external agent's bus inbox. This is a best-effort
/// wake-up: it does not wait for a reply, and the bus does not promise offline
/// delivery. The agent posts its durable result to the workflow API.
/// </summary>
public sealed class BusWorkflowDispatcher : IWorkflowDispatcher
{
    private readonly IBusPublisher _publisher;
    private readonly ILogger<BusWorkflowDispatcher> _logger;

    public BusWorkflowDispatcher(
        IBusPublisher publisher,
        ILogger<BusWorkflowDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(logger);
        _publisher = publisher;
        _logger = logger;
    }

    public async Task<DispatchResult> DispatchAsync(
        long workflowId,
        DispatchRequest request,
        CancellationToken cancellationToken = default)
    {
        WorkflowDispatchValidation.Validate(request);

        JsonObject context;
        try
        {
            context = string.IsNullOrWhiteSpace(request.ContextJson)
                ? []
                : JsonNode.Parse(request.ContextJson) as JsonObject
                  ?? throw new WorkflowValidationException("contextJson must be a JSON object");
        }
        catch (JsonException exception)
        {
            throw new WorkflowValidationException($"contextJson is invalid: {exception.Message}");
        }

        var payload = new JsonObject
        {
            ["workflowId"] = workflowId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["role"] = request.Role,
            ["instructions"] = request.Instructions,
            ["context"] = context.DeepClone(),
        };

        string messageId = WorkflowMessageId.Normalize(request.MessageId);
        string topic = BusTopics.AgentInbox(request.AgentId);
        var envelope = MessageEnvelope.Create(
            topic,
            "anima.workflow.v1.WorkflowRoleTask",
            Encoding.UTF8.GetBytes(payload.ToJsonString()),
            ContractContentTypes.ProtobufJson,
            sender: "anima-workflow",
            correlationId: messageId) with
        {
            MessageId = messageId,
        };

        try
        {
            await _publisher.PublishAsync(envelope, cancellationToken);
            return new DispatchResult(true, "published", null, null, messageId, topic);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Publishing role task to {AgentId} for workflow {WorkflowId} failed.",
                request.AgentId,
                workflowId);
            return new DispatchResult(
                false,
                "publish failed",
                null,
                exception.Message,
                messageId,
                topic);
        }
    }
}
