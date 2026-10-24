namespace Anima.Automation;

public sealed record ScriptDiagnostic(string Code, string Message, int? StepIndex = null, bool IsError = true);

public sealed record ScriptValidationResult(bool IsValid, IReadOnlyList<ScriptDiagnostic> Diagnostics);

public sealed record ScriptStepResult(
    int Index,
    string Action,
    bool Success,
    long DurationMs,
    string? Output = null,
    string? Error = null);

public sealed record ScriptRunResult(
    bool Success,
    IReadOnlyList<ScriptStepResult> Steps,
    IReadOnlyDictionary<string, string> Variables,
    string? Error = null);

public sealed record ScriptRunOptions
{
    public IReadOnlyDictionary<string, string>? Variables { get; init; }

    public bool StopOnError { get; init; } = true;

    /// <summary>Resolves relative navigations when the session has no base URL.</summary>
    public string? BaseUrl { get; init; }
}

/// <summary>
/// Executes an agent-derived script against a browser session. Formats are
/// pluggable: the built-in one is <c>anima-yaml/v1</c>.
/// </summary>
public interface IScriptRunner
{
    string Format { get; }

    ScriptValidationResult Validate(string source);

    Task<ScriptRunResult> RunAsync(
        string source,
        IBrowserSession session,
        ScriptRunOptions? options = null,
        CancellationToken cancellationToken = default);
}
