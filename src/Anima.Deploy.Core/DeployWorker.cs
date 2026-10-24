using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Anima.Deploy;

/// <summary>Drains queued deploy runs one at a time so a push never blocks the HTTP hook.</summary>
public sealed class DeployWorker : BackgroundService
{
    private readonly DeployDispatcher _queue;
    private readonly IDeployScriptRunner _runner;
    private readonly IDeployRunStore _store;
    private readonly DeployOptions _options;
    private readonly ILogger<DeployWorker> _logger;

    public DeployWorker(
        DeployDispatcher queue,
        IDeployScriptRunner runner,
        IDeployRunStore store,
        IOptions<DeployOptions> options,
        ILogger<DeployWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _queue = queue;
        _runner = runner;
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _queue.ReadAllAsync(stoppingToken))
        {
            await RunAsync(item, stoppingToken);
        }
    }

    private async Task RunAsync(DeployWorkItem item, CancellationToken cancellationToken)
    {
        var run = item.Run with
        {
            Status = DeployStatus.Running,
            StartedAt = DateTimeOffset.UtcNow,
        };
        _store.Update(run);
        _store.AppendLog(run.Id, $"deploy {run.RuleName} for {run.Repository} {run.Ref}");

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ANIMA_DEPLOY_RUN_ID"] = run.Id,
            ["ANIMA_DEPLOY_RULE"] = item.Rule.Name,
            ["ANIMA_DEPLOY_REPO"] = item.Push.Repository,
            ["ANIMA_DEPLOY_REF"] = item.Push.Ref,
            ["ANIMA_DEPLOY_BEFORE"] = item.Push.Before ?? string.Empty,
            ["ANIMA_DEPLOY_AFTER"] = item.Push.After ?? string.Empty,
            ["ANIMA_DEPLOY_PUSHER"] = item.Push.Pusher ?? string.Empty,
            ["ANIMA_DEPLOY_ENVIRONMENT"] = run.Environment,
            ["ANIMA_DEPLOY_SCRIPT_ROOT"] = _options.ScriptRoot,
        };

        try
        {
            var result = await _runner.RunAsync(item.Rule.Script, environment, cancellationToken);
            _store.AppendLog(run.Id, result.Output);
            run = run with
            {
                Status = result.ExitCode == 0 ? DeployStatus.Succeeded : DeployStatus.Failed,
                ExitCode = result.ExitCode,
                CompletedAt = DateTimeOffset.UtcNow,
                Error = result.ExitCode == 0 ? null : $"script exited with {result.ExitCode}",
            };

            _logger.LogInformation(
                "Deploy run {RunId} finished with status {Status} (exit {ExitCode}).",
                run.Id,
                run.Status,
                run.ExitCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            run = run with
            {
                Status = DeployStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                Error = "deploy cancelled",
            };
        }
        catch (Exception exception)
        {
            _store.AppendLog(run.Id, exception.ToString());
            run = run with
            {
                Status = DeployStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                Error = exception.Message,
            };
            _logger.LogError(exception, "Deploy run {RunId} failed.", run.Id);
        }

        _store.Update(run);
    }
}
