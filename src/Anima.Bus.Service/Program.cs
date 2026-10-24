using System.Text;
using System.Text.Json;
using Anima.Bus;
using Anima.Bus.Service.Contracts;
using Anima.Contracts;
using Anima.Hosting;
using Google.Protobuf;

var builder = WebApplication.CreateBuilder(args);
builder.AddAnimaServiceDefaults("bus", AnimaPorts.Bus);
builder.Services.AddRepoContracts();
builder.Services.AddInProcBus();
builder.Services.Configure<BusOptions>(builder.Configuration.GetSection("Anima:Bus"));

var app = builder.Build();
app.UseAnimaServiceDefaults("bus");

app.MapPost("/api/bus/publish", async (
    PublishRequest request,
    IMessageBus bus,
    IMessageCodec codec,
    CancellationToken cancellationToken) =>
{
    var contentType = request.ContentType ?? ContractContentTypes.ProtobufJson;
    byte[] payload;

    if (request.Payload is not { } json)
    {
        payload = [];
    }
    else if (contentType == ContractContentTypes.Json)
    {
        payload = Encoding.UTF8.GetBytes(json.GetRawText());
    }
    else
    {
        if (!codec.IsKnown(request.TypeName))
        {
            return Results.UnprocessableEntity(new
            {
                message = $"contract message '{request.TypeName}' is not loaded; run 'make contracts'",
            });
        }

        try
        {
            payload = codec.Encode(codec.Parse(request.TypeName, json.GetRawText()));
        }
        catch (ContractValidationException exception)
        {
            return Results.UnprocessableEntity(new { message = exception.Message });
        }
    }

    var envelope = MessageEnvelope.Create(
        request.Topic,
        request.TypeName,
        payload,
        contentType,
        request.Sender,
        request.Recipient,
        request.CorrelationId,
        request.CausationId,
        request.Headers);

    await bus.PublishAsync(envelope, cancellationToken);
    return Results.Accepted($"/api/bus/messages/{envelope.MessageId}", new
    {
        envelope.MessageId,
        envelope.Topic,
        envelope.TypeName,
        Bytes = payload.Length,
    });
});

app.MapGet("/api/bus/stream", async (
    HttpContext context,
    IMessageCodec codec,
    IMessageBus bus,
    CancellationToken cancellationToken) =>
{
    var topics = context.Request.Query["topics"].FirstOrDefault() ?? BusTopics.All;
    context.Response.Headers.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache";

    await using var subscription = await bus.SubscribeAsync(
        topics,
        async (envelope, token) =>
        {
            var json = JsonSerializer.Serialize(Describe(envelope, codec), BusJson.Sse);
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

app.MapGet("/api/bus/contracts", (ISchemaCatalog catalog) => Results.Ok(new
{
    count = catalog.Messages.Count,
    items = catalog.Messages
        .OrderBy(message => message.FullName, StringComparer.Ordinal)
        .Select(message => new
        {
            message.FullName,
            message.Name,
            Fields = message.Fields.InDeclarationOrder().Select(field => new
            {
                field.Name,
                field.FieldNumber,
                Type = field.FieldType.ToString(),
                field.IsRepeated,
            }),
        }),
}));

app.MapPost("/api/bus/validate", (ValidateRequest request, IMessageCodec codec) =>
{
    try
    {
        var message = codec.Parse(request.TypeName, request.Json);
        return Results.Ok(new
        {
            valid = true,
            typeName = request.TypeName,
            normalized = codec.ToJson(message),
        });
    }
    catch (Exception exception) when (
        exception is KeyNotFoundException or ContractValidationException or InvalidProtocolBufferException or InvalidJsonException)
    {
        return Results.Ok(new
        {
            valid = false,
            typeName = request.TypeName,
            message = exception.Message,
        });
    }
});

app.MapGet("/api/bus/info", (Microsoft.Extensions.Options.IOptions<BusOptions> options) => Results.Ok(options.Value));

app.Run();

static object Describe(MessageEnvelope envelope, IMessageCodec codec)
{
    string? payloadJson = null;
    if (envelope.ContentType == ContractContentTypes.ProtobufJson &&
        codec.IsKnown(envelope.TypeName))
    {
        try
        {
            payloadJson = codec.ToJson(codec.Decode(envelope.TypeName, envelope.Payload));
        }
        catch (Exception exception) when (
            exception is ContractValidationException or InvalidProtocolBufferException)
        {
            payloadJson = null;
        }
    }

    return new
    {
        envelope.MessageId,
        envelope.Topic,
        envelope.TypeName,
        envelope.ContentType,
        envelope.Timestamp,
        envelope.Sender,
        envelope.Recipient,
        envelope.CorrelationId,
        envelope.CausationId,
        envelope.Headers,
        PayloadBase64 = Convert.ToBase64String(envelope.Payload.Span),
        PayloadJson = payloadJson,
    };
}
