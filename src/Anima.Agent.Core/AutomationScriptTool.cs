using System.Text.Json;
using Anima.Automation;

namespace Anima.Agent;

/// <summary>
/// Runs an agent-derived UI script in a fresh browser session. Arguments:
/// <c>{ script, baseUrl?, variables?, headless? }</c>.
/// </summary>
public sealed class AutomationScriptTool : IAgentTool
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IBrowserDriver _driver;
    private readonly IScriptRunner _runner;

    public AutomationScriptTool(IBrowserDriver driver, IScriptRunner runner)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(runner);
        _driver = driver;
        _runner = runner;
    }

    public string Name => "automation.run_script";

    public string Description =>
        "Run an anima-yaml/v1 UI script. Arguments: { script, baseUrl?, variables?, headless? }.";

    public async Task<string> InvokeAsync(
        string argumentsJson,
        AgentContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var arguments = JsonSerializer.Deserialize<RunScriptArguments>(argumentsJson, JsonOptions)
            ?? throw new InvalidOperationException("automation.run_script requires a JSON object");

        if (string.IsNullOrWhiteSpace(arguments.Script))
        {
            throw new InvalidOperationException("script is required");
        }

        var baseUrl = arguments.BaseUrl
            ?? context.Variables.GetValueOrDefault("automationBaseUrl");

        await using var session = await _driver.StartSessionAsync(
            new SessionOptions
            {
                BaseUrl = baseUrl,
                Headless = arguments.Headless ?? true,
            },
            cancellationToken);

        var result = await _runner.RunAsync(
            arguments.Script,
            session,
            new ScriptRunOptions
            {
                Variables = arguments.Variables,
                BaseUrl = baseUrl,
            },
            cancellationToken);

        return JsonSerializer.Serialize(
            new
            {
                result.Success,
                result.Variables,
                result.Error,
                steps = result.Steps.Select(step => new
                {
                    step.Index,
                    step.Action,
                    step.Success,
                    step.DurationMs,
                    step.Output,
                    step.Error,
                }),
            },
            JsonOptions);
    }

    private sealed record RunScriptArguments(
        string? Script,
        string? BaseUrl,
        Dictionary<string, string>? Variables,
        bool? Headless);
}
