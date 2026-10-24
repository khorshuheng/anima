namespace Anima.Deploy.Core.Tests;

public sealed class DeployInterpreterTests
{
    [Fact]
    public void Windows_batch_scripts_use_cmd()
    {
        var interpreter = DeployScriptInterpreter.Resolve(
            "deploy.cmd",
            new DeployOptions(),
            isWindows: true);

        Assert.Equal("cmd.exe", interpreter.FileName);
        Assert.Equal(["/c"], interpreter.PrefixArguments);
        Assert.False(interpreter.ScriptIsExecutable);
    }

    [Fact]
    public void Executables_run_directly()
    {
        var interpreter = DeployScriptInterpreter.Resolve(
            "deploy.exe",
            new DeployOptions(),
            isWindows: true);

        Assert.Equal("deploy.exe", interpreter.FileName);
        Assert.Empty(interpreter.PrefixArguments);
        Assert.True(interpreter.ScriptIsExecutable);
    }

    [Fact]
    public void PowerShell_is_not_assumed_on_windows()
    {
        var exception = Assert.Throws<DeployValidationException>(
            () => DeployScriptInterpreter.Resolve("deploy.ps1", new DeployOptions(), isWindows: true));

        Assert.Contains("does not assume PowerShell", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PowerShell_runs_only_when_explicitly_configured()
    {
        var interpreter = DeployScriptInterpreter.Resolve(
            "deploy.ps1",
            new DeployOptions { PowerShellExecutable = "pwsh" },
            isWindows: false);

        Assert.Equal("pwsh", interpreter.FileName);
        Assert.Contains("-File", interpreter.PrefixArguments);
    }

    [Fact]
    public void Shell_override_wins()
    {
        var interpreter = DeployScriptInterpreter.Resolve(
            "deploy.anything",
            new DeployOptions { Shell = "custom-shell" },
            isWindows: true);

        Assert.Equal("custom-shell", interpreter.FileName);
        Assert.Empty(interpreter.PrefixArguments);
    }
}
