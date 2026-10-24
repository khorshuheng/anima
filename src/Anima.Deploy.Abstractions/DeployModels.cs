namespace Anima.Deploy;

public enum DeployStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Skipped,
}

/// <summary>Normalized push event from a GitHub/Gitea-style local hook payload.</summary>
public sealed record GitPushEvent(
    string Provider,
    string Repository,
    string Ref,
    string? Before,
    string? After,
    string? Pusher,
    IReadOnlyList<string> CommitMessages,
    bool Deleted = false,
    bool Created = false);

public sealed record DeployRule(
    string Name,
    string RepositoryPattern = "*",
    string RefPattern = "refs/heads/*",
    string Script = "",
    string? Environment = null,
    bool Enabled = true);

public sealed class DeployOptions
{
    /// <summary>Directory containing deploy scripts; also the process working directory.</summary>
    public string ScriptRoot { get; set; } = "scripts/deploy";

    /// <summary>Shared secret for HMAC verification. Empty disables verification.</summary>
    public string? Secret { get; set; }

    public bool VerifySignature { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>Optional interpreter override, e.g. bash or pwsh. Wins over extension inference.</summary>
    public string? Shell { get; set; }

    /// <summary>Windows command processor used for .cmd/.bat and extensionless scripts.</summary>
    public string? CommandShell { get; set; }

    /// <summary>
    /// Interpreter for .ps1 scripts. Null means PowerShell is not supported on
    /// this host (the Windows deployment target has none).
    /// </summary>
    public string? PowerShellExecutable { get; set; }

    /// <summary>Rules declared in configuration.</summary>
    public List<DeployRule> Rules { get; set; } = [];

    /// <summary>Optional JSON file containing an array of rules, merged with <see cref="Rules"/>.</summary>
    public string? RulesPath { get; set; }
}

public sealed record DeployRun
{
    public required string Id { get; init; }

    public required string RuleName { get; init; }

    public required string Repository { get; init; }

    public required string Ref { get; init; }

    public required string Environment { get; init; }

    public DeployStatus Status { get; init; } = DeployStatus.Queued;

    public DateTimeOffset QueuedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public int? ExitCode { get; init; }

    public string? Error { get; init; }
}

public sealed record DeployRunResult(int ExitCode, string Output, TimeSpan Duration);

public sealed record DeployAcceptance(
    string RunId,
    bool Matched,
    string Message,
    IReadOnlyList<string> RunIds);

public interface IDeployScriptRunner
{
    Task<DeployRunResult> RunAsync(
        string script,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken = default);
}

public interface IDeployRunStore
{
    void Add(DeployRun run);

    void Update(DeployRun run);

    void AppendLog(string runId, string line);

    DeployRun? Find(string runId);

    IReadOnlyList<DeployRun> List();

    IReadOnlyList<string> GetLog(string runId);
}

public interface IDeployHookSink
{
    Task<DeployAcceptance> AcceptAsync(
        string provider,
        string payload,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken = default);
}
