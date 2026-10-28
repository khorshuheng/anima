
namespace Anima.Deploy.Core.Tests;

public sealed class DeployTests
{
    private const string GithubPayload = """
        {
          "ref": "refs/heads/main",
          "before": "0000000000000000000000000000000000000000",
          "after": "abc123",
          "repository": { "full_name": "acme/uat-app", "name": "uat-app" },
          "pusher": { "name": "dev" },
          "commits": [
            { "message": "feat: add login" },
            { "message": "fix: redirect" }
          ]
        }
        """;

    [Fact]
    public void Parser_normalizes_github_push_payload()
    {
        var push = new GitPushPayloadParser().Parse("github", GithubPayload);

        Assert.Equal("acme/uat-app", push.Repository);
        Assert.Equal("refs/heads/main", push.Ref);
        Assert.Equal("dev", push.Pusher);
        Assert.Equal(2, push.CommitMessages.Count);
        Assert.True(push.Created);
        Assert.False(push.Deleted);
    }

    [Fact]
    public void Signature_verification_accepts_github_and_raw_forms()
    {
        const string secret = "s3cret";
        string expected = HookSignatureValidator.Compute(secret, GithubPayload);

        Assert.True(HookSignatureValidator.Verify(secret, GithubPayload, "sha256=" + expected));
        Assert.True(HookSignatureValidator.Verify(secret, GithubPayload, expected));
        Assert.False(HookSignatureValidator.Verify(secret, GithubPayload, "sha256=deadbeef"));
        Assert.False(HookSignatureValidator.Verify(secret, GithubPayload, null));
        Assert.True(HookSignatureValidator.Verify(null, GithubPayload, null));
    }

    [Theory]
    [InlineData("acme/*", "acme/uat-app", true)]
    [InlineData("acme/*", "other/uat-app", false)]
    [InlineData("acme/**", "acme/team/uat-app", true)]
    [InlineData("refs/heads/*", "refs/heads/main", true)]
    [InlineData("refs/heads/*", "refs/heads/feature/login", false)]
    [InlineData("refs/heads/**", "refs/heads/feature/login", true)]
    [InlineData("*", "anything/goes", true)]
    public void Glob_matching_respects_segments(string pattern, string value, bool expected) => Assert.Equal(expected, GlobPattern.IsMatch(pattern, value));

    [Fact]
    public void Deploy_rule_loader_reads_json_rules()
    {
        string path = Path.Combine(Path.GetTempPath(), $"anima-rules-{Guid.NewGuid():n}.json");
        File.WriteAllText(path, """
            [
              { "name": "uat", "repositoryPattern": "acme/*", "refPattern": "refs/heads/main", "script": "deploy-uat.sh", "environment": "uat" }
            ]
            """);

        try
        {
            var rules = DeployRuleLoader.Load(path);
            Assert.Single(rules);
            Assert.Equal("deploy-uat.sh", rules[0].Script);
            Assert.Equal("uat", rules[0].Environment);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
