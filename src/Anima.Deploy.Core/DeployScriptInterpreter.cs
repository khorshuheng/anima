namespace Anima.Deploy;

/// <summary>How a deploy script is launched on the current host.</summary>
public sealed record DeployInterpreter(
    string FileName,
    IReadOnlyList<string> PrefixArguments,
    bool ScriptIsExecutable);

/// <summary>
/// Resolves the interpreter from the script extension and explicit configuration.
/// Windows deploy hosts are assumed to have <c>cmd.exe</c> but not PowerShell, so
/// PowerShell scripts require an explicitly configured executable.
/// </summary>
public static class DeployScriptInterpreter
{
    public static DeployInterpreter Resolve(string script, DeployOptions options, bool isWindows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrWhiteSpace(options.Shell))
        {
            return new DeployInterpreter(options.Shell, [], ScriptIsExecutable: false);
        }

        var extension = Path.GetExtension(script).ToLowerInvariant();
        switch (extension)
        {
            case ".exe":
                return new DeployInterpreter(script, [], ScriptIsExecutable: true);

            case ".cmd":
            case ".bat":
                return new DeployInterpreter(
                    string.IsNullOrWhiteSpace(options.CommandShell) ? "cmd.exe" : options.CommandShell,
                    ["/c"],
                    ScriptIsExecutable: false);

            case ".ps1":
                if (string.IsNullOrWhiteSpace(options.PowerShellExecutable))
                {
                    throw new DeployValidationException(
                        "PowerShell deploy scripts require Deploy:PowerShellExecutable to be configured; " +
                        "the deployment host does not assume PowerShell. Use a .cmd/.bat or .exe script instead.");
                }

                return isWindows
                    ? new DeployInterpreter(
                        options.PowerShellExecutable,
                        ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File"],
                        ScriptIsExecutable: false)
                    : new DeployInterpreter(
                        options.PowerShellExecutable,
                        ["-NoProfile", "-File"],
                        ScriptIsExecutable: false);

            case ".sh":
                return new DeployInterpreter(
                    string.IsNullOrWhiteSpace(options.Shell) ? "bash" : options.Shell,
                    [],
                    ScriptIsExecutable: false);

            default:
                return isWindows
                    ? new DeployInterpreter(
                        string.IsNullOrWhiteSpace(options.CommandShell) ? "cmd.exe" : options.CommandShell,
                        ["/c"],
                        ScriptIsExecutable: false)
                    : new DeployInterpreter(
                        string.IsNullOrWhiteSpace(options.Shell) ? "bash" : options.Shell,
                        [],
                        ScriptIsExecutable: false);
        }
    }
}
