using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Anima.Deploy;

/// <summary>
/// Verifies, normalizes and enqueues a push event. One push can match several
/// rules; each match becomes its own run.
/// </summary>
public sealed class DeployHookSink : IDeployHookSink
{
    private readonly DeployOptions _options;
    private readonly GitPushPayloadParser _parser;
    private readonly DeployDispatcher _queue;
    private readonly IDeployRunStore _store;
    private readonly ILogger<DeployHookSink> _logger;

    public DeployHookSink(
        IOptions<DeployOptions> options,
        GitPushPayloadParser parser,
        DeployDispatcher queue,
        IDeployRunStore store,
        ILogger<DeployHookSink> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _parser = parser;
        _queue = queue;
        _store = store;
        _logger = logger;
    }

    public async Task<DeployAcceptance> AcceptAsync(
        string provider,
        string payload,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        ArgumentNullException.ThrowIfNull(headers);

        if (_options.VerifySignature && !string.IsNullOrEmpty(_options.Secret))
        {
            string signatureHeader = provider.ToLowerInvariant() switch
            {
                "github" => "X-Hub-Signature-256",
                "gitea" => "X-Gitea-Signature",
                _ => "X-Anima-Signature",
            };

            string? signature = GetHeader(headers, signatureHeader);
            if (!HookSignatureValidator.Verify(_options.Secret, payload, signature))
            {
                throw new DeploySignatureException($"invalid or missing {signatureHeader} header");
            }
        }

        var push = _parser.Parse(provider, payload);
        if (push.Deleted)
        {
            return new DeployAcceptance(string.Empty, false, "branch deletion is ignored", []);
        }

        var rules = LoadRules();
        var matched = rules
            .Where(rule => rule.Enabled)
            .Where(rule => GlobPattern.IsMatch(rule.RepositoryPattern, push.Repository))
            .Where(rule => GlobPattern.IsMatch(rule.RefPattern, push.Ref))
            .ToArray();

        if (matched.Length == 0)
        {
            _logger.LogInformation(
                "Push to {Repository} {Ref} matched no deploy rule.",
                push.Repository,
                push.Ref);
            return new DeployAcceptance(string.Empty, false, "no matching deploy rule", []);
        }

        var runIds = new List<string>(matched.Length);
        foreach (var rule in matched)
        {
            var run = new DeployRun
            {
                Id = Guid.NewGuid().ToString("n"),
                RuleName = rule.Name,
                Repository = push.Repository,
                Ref = push.Ref,
                Environment = rule.Environment ?? "dev",
            };

            _store.Add(run);
            await _queue.EnqueueAsync(new DeployWorkItem(run, rule, push), cancellationToken);
            runIds.Add(run.Id);
        }

        _logger.LogInformation(
            "Queued {Count} deploy run(s) for {Repository} {Ref}.",
            runIds.Count,
            push.Repository,
            push.Ref);

        return new DeployAcceptance(
            runIds[0],
            true,
            $"{runIds.Count} deploy run(s) queued",
            runIds);
    }

    private List<DeployRule> LoadRules()
    {
        var rules = new List<DeployRule>(_options.Rules);
        if (!string.IsNullOrWhiteSpace(_options.RulesPath))
        {
            rules.AddRange(DeployRuleLoader.Load(_options.RulesPath));
        }

        return rules;
    }

    private static string? GetHeader(IReadOnlyDictionary<string, string> headers, string name)
    {
        foreach (var pair in headers)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }
}
