using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anima.Bus;
using Anima.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Anima.Workflow;

/// <summary>
/// Sends a role task to a running external agent and awaits its reply. Anima does
/// not start, stop or supervise that agent.
/// </summary>
public sealed class BusWorkflowDispatcher : IWorkflowDispatcher
{
    private readonly IBusRequester _requester;
    private readonly WorkflowOptions _options;
    private readonly ILogger<BusWorkflowDispatcher> _logger;

    public BusWorkflowDispatcher(
        IBusRequester requester,
        IOptions<WorkflowOptions> options,
        ILogger<BusWorkflowDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(requester);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _requester = requester;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<DispatchResult> DispatchAsync(
        long workflowId,
        DispatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.AgentId))
        {
            throw new WorkflowValidationException("agentId is required");
        }

        if (string.IsNullOrWhiteSpace(request.Role))
        {
            throw new WorkflowValidationException("role is required");
        }

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

        var messageId = string.IsNullOrWhiteSpace(request.MessageId)
            ? Guid.NewGuid().ToString("n")
            : request.MessageId.Trim();
        var envelope = MessageEnvelope.Create(
            BusTopics.AgentInbox(request.AgentId),
            "anima.workflow.v1.WorkflowRoleTask",
            Encoding.UTF8.GetBytes(payload.ToJsonString()),
            ContractContentTypes.ProtobufJson,
            sender: "anima-workflow",
            correlationId: messageId) with
        {
            MessageId = messageId,
        };

        var timeout = request.TimeoutSeconds > 0
            ? TimeSpan.FromSeconds(request.TimeoutSeconds)
            : _options.DispatchTimeout;

        try
        {
            var reply = await _requester.AskAsync(
                envelope,
                new BusRequestOptions { Timeout = timeout },
                cancellationToken);
            return ParseReply(reply, envelope.MessageId);
        }
        catch (BusRequestTimeoutException exception)
        {
            _logger.LogWarning(
                exception,
                "Dispatch to {AgentId} for workflow {WorkflowId} timed out.",
                request.AgentId,
                workflowId);
            return new DispatchResult(false, exception.Message, null, exception.Message, envelope.MessageId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Dispatch to {AgentId} for workflow {WorkflowId} failed.",
                request.AgentId,
                workflowId);
            return new DispatchResult(false, exception.Message, null, exception.Message, envelope.MessageId);
        }
    }

    private static DispatchResult ParseReply(MessageEnvelope reply, string messageId)
    {
        var text = Encoding.UTF8.GetString(reply.Payload.Span);
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var success = root.TryGetProperty("success", out var successElement) &&
                          successElement.ValueKind == JsonValueKind.True;
            var summary = root.TryGetProperty("summary", out var summaryElement) &&
                          summaryElement.ValueKind == JsonValueKind.String
                ? summaryElement.GetString() ?? string.Empty
                : string.Empty;
            var data = root.TryGetProperty("data", out var dataElement)
                ? dataElement.GetRawText()
                : null;

            return new DispatchResult(success, summary, data, success ? null : summary, messageId);
        }
        catch (JsonException exception)
        {
            return new DispatchResult(false, "agent reply was not valid JSON", null, exception.Message, messageId);
        }
    }
}
