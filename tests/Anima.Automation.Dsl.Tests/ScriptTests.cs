
using Microsoft.Extensions.Options;

namespace Anima.Automation.Dsl.Tests;

public sealed class ScriptTests
{
    private const string LoginScript = """
        name: login-smoke
        variables:
          user: alice
        steps:
          - goto: /login
          - fill: { target: "#username", value: "${user}" }
          - click: "text=Sign in"
          - waitFor: { target: "#token" }
          - extract: { name: token, target: "#token" }
          - expect: { target: "#token", text: abc }
        """;

    [Fact]
    public void Parser_produces_steps_and_variables()
    {
        var parser = new AutomationScriptParser();
        var result = parser.Parse(LoginScript);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Script);
        Assert.Equal("login-smoke", result.Script.Name);
        Assert.Equal("alice", result.Script.Variables["user"]);
        Assert.Equal(6, result.Script.Steps.Count);
        Assert.IsType<NavigateStep>(result.Script.Steps[0]);
        Assert.IsType<FillStep>(result.Script.Steps[1]);
        Assert.IsType<ExtractStep>(result.Script.Steps[4]);
    }

    [Theory]
    [InlineData("steps:\n  - dance: now", "script.unknown_verb")]
    [InlineData("steps:\n  - fill: { target: \"#x\" }", "script.missing_field")]
    [InlineData("name: no-steps", "script.steps")]
    public void Parser_reports_actionable_diagnostics(string source, string code)
    {
        var result = new AutomationScriptParser().Parse(source);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code);
    }

    [Fact]
    public void Parser_supports_wait_for_text()
    {
        var result = new AutomationScriptParser().Parse("""
            name: wait-text
            steps:
              - waitForText: { target: "#count", text: "of" }
            """);

        Assert.True(result.IsValid);
        Assert.IsType<WaitForTextStep>(result.Script!.Steps.Single());
    }

    [Fact]
    public void Evaluate_is_an_alias_for_eval()
    {
        var result = new AutomationScriptParser().Parse("""
            name: evaluate-alias
            steps:
              - evaluate: "document.title"
            """);

        Assert.True(result.IsValid);
        Assert.IsType<EvaluateStep>(result.Script!.Steps.Single());
    }

    [Fact]
    public async Task Runner_executes_steps_against_the_session()
    {
        var session = new FakeSession();
        var runner = new YamlAutomationScriptRunner(new AutomationScriptParser());

        var result = await runner.RunAsync(LoginScript, session);

        Assert.True(result.Success, result.Error);
        Assert.Equal(6, result.Steps.Count);
        Assert.Equal("abc123", result.Variables["token"]);
        Assert.Contains("fill:#username=alice", session.Calls);
        Assert.Contains("click:text=Sign in", session.Calls);
    }

    [Fact]
    public void Catalog_loads_repo_style_yaml_from_a_directory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "anima-specs-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "login-smoke.yaml"), LoginScript);

        try
        {
            var catalog = new ScriptCatalog(
                Options.Create(new ScriptCatalogOptions { Directory = directory }),
                new AutomationScriptParser());

            var scripts = catalog.LoadAll();

            Assert.Single(scripts);
            Assert.Equal("login-smoke", scripts[0].Name);
            Assert.True(scripts[0].IsValid);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Parser_reads_wait_for_response_fields()
    {
        var result = new AutomationScriptParser().Parse(
            """
            steps:
              - waitForResponse: { method: POST, url: "*/api/orders", status: 201, timeout: 5000, extract: created }
            """);

        Assert.True(result.IsValid);
        var step = Assert.IsType<WaitForResponseStep>(result.Script!.Steps[0]);
        Assert.Equal("POST", step.Method);
        Assert.Equal("*/api/orders", step.Url);
        Assert.Equal(201, step.Status);
        Assert.Equal(5000, step.TimeoutMs);
        Assert.Equal("created", step.Extract);
    }

    [Theory]
    [InlineData("status: nope", "status must be an integer")]
    [InlineData("timesout: 500", "")]                       // unknown field, but method keeps it valid
    [InlineData("timeout: -5", "timeout must be a positive integer")]
    [InlineData("timeout: 0", "timeout must be a positive integer")]
    [InlineData("timeout: soon", "timeout must be a positive integer")]
    public void Parser_rejects_unparseable_wait_for_response_fields(string field, string expected)
    {
        var result = new AutomationScriptParser().Parse(
            $$"""
            steps:
              - waitForResponse: { method: POST, {{field}} }
            """);

        if (expected.Length == 0)
        {
            Assert.True(result.IsValid);
            return;
        }

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "script.wait_for_response"
            && diagnostic.Message.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Parser_rejects_a_wait_for_response_without_a_selector()
    {
        var result = new AutomationScriptParser().Parse("steps:\n  - waitForResponse: { timeout: 500 }\n");

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "script.wait_for_response");
    }

    [Fact]
    public async Task Wait_for_response_confirms_the_write_and_extracts_the_body()
    {
        var session = new FakeSession();
        var runner = new YamlAutomationScriptRunner(new AutomationScriptParser());

        var result = await runner.RunAsync(
            """
            steps:
              - waitForResponse: { method: POST, status: 201, extract: created }
              - expect: { target: "#token", text: abc }
            """,
            session);

        Assert.True(result.Success);
        Assert.Equal("waitForResponse", result.Steps[0].Action);
        Assert.Equal("POST 201 http://uat.local/api/orders", result.Steps[0].Output);
        Assert.Equal("{\"id\":42}", result.Variables["created"]);
    }

    [Fact]
    public async Task Extracting_an_unreadable_body_fails_instead_of_storing_nothing()
    {
        var session = new FakeSession { ResponseBody = null };
        var runner = new YamlAutomationScriptRunner(new AutomationScriptParser());

        var result = await runner.RunAsync(
            """
            steps:
              - waitForResponse: { method: POST, status: 201, extract: created }
            """,
            session);

        Assert.False(result.Success);
        Assert.Contains("no readable body", result.Error, StringComparison.Ordinal);
        Assert.False(result.Variables.ContainsKey("created"));
    }

    [Fact]
    public async Task Expect_mismatch_fails_the_run()
    {
        var session = new FakeSession();
        var runner = new YamlAutomationScriptRunner(new AutomationScriptParser());

        var result = await runner.RunAsync(
            """
            steps:
              - goto: /login
              - expect: { target: "#token", text: zzz }
            """,
            session);

        Assert.False(result.Success);
        Assert.Contains("zzz", result.Error, StringComparison.Ordinal);
        Assert.Equal(2, result.Steps.Count);
        Assert.False(result.Steps[1].Success);
    }

    private sealed class FakeSession : IBrowserSession
    {
        private readonly Dictionary<string, string> _text = new(StringComparer.Ordinal)
        {
            ["#token"] = "abc123",
        };

        public string Id => "fake";

        public string? CurrentUrl { get; private set; } = "http://uat.local/login";

        public List<string> Calls { get; } = [];

        public ICdpTransport? Cdp => null;

        public long DroppedEventCount => 0;

        public IReadOnlyList<BrowserEvent> ReadEvents(long afterSequence = 0, int limit = 200) => [];

        public string? ResponseBody { get; init; } = "{\"id\":42}";

        public Task<BrowserEvent> WaitForResponseAsync(
            ResponseMatcher matcher,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new BrowserEvent
            {
                Kind = BrowserEventKind.Response,
                Sequence = 1,
                Method = matcher.Method ?? "POST",
                Status = matcher.Status ?? 201,
                Url = matcher.UrlPattern ?? "http://uat.local/api/orders",
                Body = ResponseBody,
            });

        public IAsyncEnumerable<BrowserEvent> SubscribeEventsAsync(
            long afterSequence = 0,
            CancellationToken cancellationToken = default) => EmptyEvents();

        private static async IAsyncEnumerable<BrowserEvent> EmptyEvents()
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task NavigateAsync(string url, CancellationToken cancellationToken = default)
        {
            Calls.Add($"goto:{url}");
            CurrentUrl = url;
            return Task.CompletedTask;
        }

        public Task ClickAsync(string target, CancellationToken cancellationToken = default)
        {
            Calls.Add($"click:{target}");
            return Task.CompletedTask;
        }

        public Task FillAsync(string target, string value, CancellationToken cancellationToken = default)
        {
            Calls.Add($"fill:{target}={value}");
            return Task.CompletedTask;
        }

        public Task PressAsync(string target, string key, CancellationToken cancellationToken = default)
        {
            Calls.Add($"press:{target}={key}");
            return Task.CompletedTask;
        }

        public Task SelectAsync(string target, string value, CancellationToken cancellationToken = default)
        {
            Calls.Add($"select:{target}={value}");
            return Task.CompletedTask;
        }

        public Task WaitForAsync(
            string target,
            WaitState state = WaitState.Visible,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"wait:{target}");
            return Task.CompletedTask;
        }

        public Task<string?> GetTextAsync(string target, CancellationToken cancellationToken = default)
            => Task.FromResult(_text.GetValueOrDefault(target));

        public Task<string?> GetAttributeAsync(
            string target,
            string attribute,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_text.GetValueOrDefault(target));

        public Task<IReadOnlyList<string>> QueryAllTextAsync(
            string target,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([_text.GetValueOrDefault(target) ?? string.Empty]);

        public Task<string> EvaluateAsync(string expression, CancellationToken cancellationToken = default)
            => Task.FromResult(expression);

        public Task<string> TakeScreenshotAsync(
            string? path = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(path ?? "screenshot.png");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
