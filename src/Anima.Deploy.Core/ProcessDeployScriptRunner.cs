using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Anima.Deploy;

/// <summary>
/// Runs a repo-owned script with push metadata in the environment. The script
/// content is never taken from the hook payload, so a push cannot inject code.
/// </summary>
public sealed class ProcessDeployScriptRunner : IDeployScriptRunner
{
    private readonly DeployOptions _options;
    private readonly ILogger<ProcessDeployScriptRunner> _logger;

    public ProcessDeployScriptRunner(
        IOptions<DeployOptions> options,
        ILogger<ProcessDeployScriptRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _logger = logger;
    }

    public async Task<DeployRunResult> RunAsync(
        string script,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);

        string scriptRoot = Path.IsPathRooted(_options.ScriptRoot)
            ? _options.ScriptRoot
            : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), _options.ScriptRoot));

        if (!Directory.Exists(scriptRoot))
        {
            throw new DeployValidationException($"deploy script root '{scriptRoot}' does not exist");
        }

        var interpreter = DeployScriptInterpreter.Resolve(
            script,
            _options,
            OperatingSystem.IsWindows());

        var startInfo = new ProcessStartInfo
        {
            FileName = interpreter.ScriptIsExecutable ? script : interpreter.FileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = scriptRoot,
        };

        foreach (string argument in interpreter.PrefixArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (!interpreter.ScriptIsExecutable)
        {
            startInfo.ArgumentList.Add(script);
        }
        foreach (var pair in environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        var output = new StringBuilder();
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) => Append(output, args.Data);
        process.ErrorDataReceived += (_, args) => Append(output, args.Data);

        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation(
            "Running deploy script {Script} in {ScriptRoot} via {Interpreter}.",
            script,
            scriptRoot,
            interpreter.ScriptIsExecutable ? "<direct>" : interpreter.FileName);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_options.TimeoutSeconds > 0)
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        }

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        stopwatch.Stop();
        string snapshot;
        lock (output)
        {
            snapshot = output.ToString();
        }

        return new DeployRunResult(process.ExitCode, snapshot, stopwatch.Elapsed);
    }

    private static void Append(StringBuilder output, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (output)
        {
            output.AppendLine(line);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited.
        }
    }
}
