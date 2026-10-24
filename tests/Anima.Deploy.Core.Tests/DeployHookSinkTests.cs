using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Anima.Deploy.Core.Tests;

public sealed class DeployHookSinkTests
{
    private const string Payload = """
        {
          "ref": "refs/heads/main",
          "before": "old",
          "after": "new",
          "repository": { "full_name": "acme/anima" },
          "pusher": { "name": "dev" },
          "commits": []
        }
        """;

    [Fact]
    public async Task Matching_rule_is_queued_for_the_worker()
    {
        var store = new InMemoryDeployRunStore();
        var queue = new DeployDispatcher();
        var sink = CreateSink(
            store,
            queue,
            new DeployOptions
            {
                VerifySignature = false,
                Rules =
                [
                    new DeployRule(
                        "dev",
                        "acme/anima",
                        "refs/heads/main",
                        "update-compose.sh",
                        "dev"),
                ],
            });

        var acceptance = await sink.AcceptAsync("github", Payload, new Dictionary<string, string>());

        Assert.True(acceptance.Matched);
        Assert.Single(store.List());
        Assert.Equal("update-compose.sh", await ReadScriptAsync(queue));
    }

    [Fact]
    public async Task External_rules_path_is_merged_and_ref_mismatch_is_skipped()
    {
        var path = Path.Combine(Path.GetTempPath(), $"anima-rules-{Guid.NewGuid():n}.json");
        await File.WriteAllTextAsync(path, """
            [
              {
                "name": "release",
                "repositoryPattern": "acme/anima",
                "refPattern": "refs/heads/release/**",
                "script": "release.sh",
                "environment": "uat"
              }
            ]
            """);

        try
        {
            var store = new InMemoryDeployRunStore();
            var queue = new DeployDispatcher();
            var sink = CreateSink(
                store,
                queue,
                new DeployOptions { VerifySignature = false, RulesPath = path });

            var acceptance = await sink.AcceptAsync("github", Payload, new Dictionary<string, string>());

            Assert.False(acceptance.Matched);
            Assert.Empty(store.List());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Invalid_signature_is_rejected_before_rules_are_queued()
    {
        var store = new InMemoryDeployRunStore();
        var sink = CreateSink(
            store,
            new DeployDispatcher(),
            new DeployOptions
            {
                VerifySignature = true,
                Secret = "s3cret",
                Rules = [new DeployRule("dev", "*", "*", "deploy.sh")],
            });

        await Assert.ThrowsAsync<DeploySignatureException>(
            () => sink.AcceptAsync("github", Payload, new Dictionary<string, string>()));
        Assert.Empty(store.List());
    }

    private static DeployHookSink CreateSink(
        InMemoryDeployRunStore store,
        DeployDispatcher queue,
        DeployOptions options)
        => new(
            Options.Create(options),
            new GitPushPayloadParser(),
            queue,
            store,
            NullLogger<DeployHookSink>.Instance);

    private static async Task<string> ReadScriptAsync(DeployDispatcher queue)
    {
        await foreach (var item in queue.ReadAllAsync())
        {
            return item.Rule.Script;
        }

        throw new InvalidOperationException("no deploy item was queued");
    }
}
