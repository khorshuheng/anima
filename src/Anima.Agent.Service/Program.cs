using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Anima.Agent;
using Anima.Automation.Dsl;
using Anima.Automation.Playwright;
using Anima.Bus;
using Anima.Hosting;
using Anima.Knowledge;
using Anima.Knowledge.Client;

var builder = WebApplication.CreateBuilder(args);
builder.AddAnimaServiceDefaults("agent", AnimaPorts.Agent);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    // Enum values are part of the agent-facing API; keep them readable.
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
builder.Services.AddInProcBus();

// In-memory keeps a single process self-contained. Set
// Anima:Knowledge:Transport=Http so multiple agent processes share the
// knowledge service (and its task/memory graph).
var knowledgeTransport = builder.Configuration["Anima:Knowledge:Transport"] ?? "InMemory";
if (string.Equals(knowledgeTransport, "Http", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddKnowledgeClient(options =>
    {
        options.BaseUrl = builder.Configuration["Anima:Knowledge:BaseUrl"] ?? "http://127.0.0.1:7411";
        options.SessionId = builder.Configuration["Anima:Agent:Id"] ?? "orchestrator";
    });
}
else
{
    builder.Services.AddInMemoryKnowledgeGraph();
}
builder.Services.AddYamlAutomationScripts(options => options.Directory = RepoLayout.SpecsDirectory);
builder.Services.AddPlaywrightAutomation();
builder.Services.AddAgentCore();
builder.Services.AddHostedService<AgentWorker>();

builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Anima:Agent"));
builder.Services.Configure<PlaywrightOptions>(builder.Configuration.GetSection("Anima:Automation:Playwright"));

var app = builder.Build();
var agentDescriptor = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOptions>>().Value.ToDescriptor();

app.UseAnimaServiceDefaults("agent");

app.MapGet("/api/agents", () => Results.Ok(new { items = new[] { agentDescriptor } }));

app.MapGet("/api/agents/{id}", (string id) =>
    string.Equals(id, agentDescriptor.Id, StringComparison.OrdinalIgnoreCase)
        ? Results.Ok(agentDescriptor)
        : Results.NotFound(new { message = $"agent '{id}' is not hosted here" }));

app.MapPost("/api/agents/{id}/turns", async (
    string id,
    TurnRequest request,
    IAgentRuntime runtime,
    CancellationToken cancellationToken) =>
{
    if (!string.Equals(id, agentDescriptor.Id, StringComparison.OrdinalIgnoreCase))
    {
        return Results.NotFound(new { message = $"agent '{id}' is not hosted here" });
    }

    var result = await runtime.RunTurnAsync(
        agentDescriptor,
        request.Input,
        request.Variables,
        cancellationToken);
    return Results.Ok(result);
});

app.MapGet("/api/agents/{id}/mailbox/stream", async (
    string id,
    HttpContext context,
    IMessageBus bus,
    CancellationToken cancellationToken) =>
{
    context.Response.Headers.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache";

    await using var subscription = await bus.SubscribeAsync(
        BusTopics.AgentInbox(id),
        async (envelope, token) =>
        {
            var json = JsonSerializer.Serialize(new
            {
                envelope.MessageId,
                envelope.Topic,
                envelope.TypeName,
                envelope.ContentType,
                envelope.Sender,
                envelope.CorrelationId,
                Payload = Encoding.UTF8.GetString(envelope.Payload.Span),
            }, AgentJson.Sse);

            await context.Response.WriteAsync($"event: message\ndata: {json}\n\n", token);
            await context.Response.Body.FlushAsync(token);
        },
        cancellationToken: cancellationToken);

    try
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }
    catch (OperationCanceledException)
    {
        // Client disconnected.
    }
});

app.Run();

internal sealed record TurnRequest(string Input, Dictionary<string, string>? Variables = null);

internal static class AgentJson
{
    public static readonly JsonSerializerOptions Sse = new(JsonSerializerDefaults.Web);
}
