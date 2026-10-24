using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Anima.Deploy.Core.Tests;

public sealed class ProcessDeployScriptRunnerTests
{
    [Fact]
    public async Task Script_receives_push_metadata_and_output()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"anima-deploy-{Guid.NewGuid():n}");
        Directory.CreateDirectory(root);
        var script = Path.Combine(root, "deploy.sh");
        await File.WriteAllTextAsync(script, """
            #!/usr/bin/env bash
            set -eu
            printf 'repo=%s ref=%s env=%s\n' \
              "$ANIMA_DEPLOY_REPO" "$ANIMA_DEPLOY_REF" "$ANIMA_DEPLOY_ENVIRONMENT"
            """);
        File.SetUnixFileMode(
            script,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try
        {
            var runner = CreateRunner(new DeployOptions { ScriptRoot = root, TimeoutSeconds = 5 });
            var result = await runner.RunAsync(
                "deploy.sh",
                new Dictionary<string, string>
                {
                    ["ANIMA_DEPLOY_REPO"] = "acme/anima",
                    ["ANIMA_DEPLOY_REF"] = "refs/heads/main",
                    ["ANIMA_DEPLOY_ENVIRONMENT"] = "uat",
                });

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("repo=acme/anima", result.Output, StringComparison.Ordinal);
            Assert.Contains("ref=refs/heads/main", result.Output, StringComparison.Ordinal);
            Assert.Contains("env=uat", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Non_zero_exit_is_returned_to_the_worker()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"anima-deploy-{Guid.NewGuid():n}");
        Directory.CreateDirectory(root);
        var script = Path.Combine(root, "fail.sh");
        await File.WriteAllTextAsync(script, "#!/usr/bin/env bash\nexit 7\n");
        File.SetUnixFileMode(
            script,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try
        {
            var runner = CreateRunner(new DeployOptions { ScriptRoot = root, TimeoutSeconds = 5 });
            var result = await runner.RunAsync("fail.sh", new Dictionary<string, string>());

            Assert.Equal(7, result.ExitCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ProcessDeployScriptRunner CreateRunner(DeployOptions options)
        => new(
            Options.Create(options),
            NullLogger<ProcessDeployScriptRunner>.Instance);
}
