using Anima.Bus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Anima.Agent.Core.Tests;

public sealed class AgentRuntimeTests
{
    private static readonly AgentDescriptor Descriptor = new(
        "tester",
        "Tester",
        ["echo", "delegate"]);

    [Fact]
    public async Task Tool_call_invokes_registered_tool()
    {
        await using var provider = BuildProvider();
        var runtime = provider.GetRequiredService<IAgentRuntime>();

        var result = await runtime.RunTurnAsync(Descriptor, """ /echo {"value":42} """);

        Assert.Equal(AgentActionKind.ToolCall, result.Kind);
        Assert.Equal("echo", result.ToolName);
        Assert.Contains("\"value\":42", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_tool_is_reported_as_a_failed_turn()
    {
        await using var provider = BuildProvider();
        var runtime = provider.GetRequiredService<IAgentRuntime>();

        var result = await runtime.RunTurnAsync(Descriptor, "/missing {}");

        Assert.StartsWith("failed:", result.Summary, StringComparison.Ordinal);
        Assert.Contains("not registered", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plain_input_responds_without_a_tool()
    {
        await using var provider = BuildProvider();
        var runtime = provider.GetRequiredService<IAgentRuntime>();

        var result = await runtime.RunTurnAsync(Descriptor, "summarize the open tasks");

        Assert.Equal(AgentActionKind.Respond, result.Kind);
        Assert.Equal("summarize the open tasks", result.Summary);
    }

    [Fact]
    public async Task Delegation_lands_in_the_recipient_mailbox()
    {
        await using var provider = BuildProvider();
        var runtime = provider.GetRequiredService<IAgentRuntime>();
        var factory = provider.GetRequiredService<IAgentMailboxFactory>();
        await using var worker = await factory.CreateAsync("worker");

        var result = await runtime.RunTurnAsync(Descriptor, "delegate:worker: run the smoke script");
        Assert.Equal(AgentActionKind.Delegate, result.Kind);
        Assert.Equal("worker", result.Recipient);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        MessageEnvelope? received = null;
        await foreach (var envelope in worker.ReadAllAsync(timeout.Token))
        {
            received = envelope;
            break;
        }

        Assert.NotNull(received);
        Assert.Equal("tester", received.Sender);
        Assert.Contains("run the smoke script", System.Text.Encoding.UTF8.GetString(received.Payload.Span), StringComparison.Ordinal);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddInProcBus();
        services.AddSingleton<IAgentPlanner, DefaultAgentPlanner>();
        services.AddSingleton<IToolRegistry>(new ToolRegistry([new EchoTool()]));
        services.AddSingleton<IAgentRuntime, AgentRuntime>();
        return services.BuildServiceProvider();
    }

    private sealed class EchoTool : IAgentTool
    {
        public string Name => "echo";

        public string Description => "Returns its arguments.";

        public Task<string> InvokeAsync(
            string argumentsJson,
            AgentContext context,
            CancellationToken cancellationToken = default) => Task.FromResult(argumentsJson);
    }
}
