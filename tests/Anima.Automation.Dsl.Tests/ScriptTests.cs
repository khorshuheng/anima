
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
        string directory = Path.Combine(Path.GetTempPath(), "anima-specs-" + Guid.NewGuid().ToString("n"));
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
            && (diagnostic.Message.Contains(expected, StringComparison.Ordinal)
                // A non-numeric value reports "must be an integer" instead.
                || diagnostic.Message.Contains("must be an integer", StringComparison.Ordinal)));
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
    public void Parser_reads_the_interaction_verbs()
    {
        var result = new AutomationScriptParser().Parse(
            """
            steps:
              - dblclick: "#cell-1-1"
              - type: { target: "#search", value: "aba", delay: 25 }
              - clear: "#search"
              - hover: "#menu"
              - check: { target: "#agree" }
              - check: { target: "#news", checked: false }
              - scroll: { target: "#grid", y: 400 }
              - scrollIntoView: "#row-40"
              - dragTo: { from: "#card-1", to: "#done" }
              - click: { target: "#row-2", modifiers: [Control, Shift] }
            """);

        Assert.True(result.IsValid);

        var steps = result.Script!.Steps;
        Assert.Equal("#cell-1-1", Assert.IsType<DoubleClickStep>(steps[0]).Target);

        var type = Assert.IsType<TypeStep>(steps[1]);
        Assert.Equal(("#search", "aba", 25), (type.Target, type.Value, type.DelayMs));

        Assert.IsType<ClearStep>(steps[2]);
        Assert.IsType<HoverStep>(steps[3]);
        Assert.True(Assert.IsType<CheckStep>(steps[4]).IsChecked);
        Assert.False(Assert.IsType<CheckStep>(steps[5]).IsChecked);

        var scroll = Assert.IsType<ScrollStep>(steps[6]);
        Assert.Equal(("#grid", 0, 400), (scroll.Target, scroll.DeltaX, scroll.DeltaY));

        Assert.IsType<ScrollIntoViewStep>(steps[7]);
        Assert.Equal(("#card-1", "#done"), (Assert.IsType<DragToStep>(steps[8]).Source, ((DragToStep)steps[8]).Target));

        var click = Assert.IsType<ClickStep>(steps[9]);
        Assert.Equal(["Control", "Shift"], click.Modifiers);
    }

    [Theory]
    [InlineData("- type: { target: \"#x\" }")]
    [InlineData("- dragTo: { from: \"#a\" }")]
    [InlineData("- scroll: 400")]
    [InlineData("- scroll: { target: \"#grid\" }")]
    [InlineData("- scroll: { y: 0 }")]
    [InlineData("- scroll: { target: \"#grid\", y: abc }")]
    [InlineData("- type: { target: \"#q\", value: \"a\", delay: soon }")]
    [InlineData("- type: { target: \"#q\", value: \"a\", delay: -5 }")]
    [InlineData("- waitFor: { target: \"#q\", timeout: later }")]
    public void Parser_rejects_malformed_interaction_steps(string step)
    {
        var result = new AutomationScriptParser().Parse($"steps:\n  {step}\n");

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Parser_reads_multi_select_page_press_and_clear_first()
    {
        var result = new AutomationScriptParser().Parse(
            """
            steps:
              - select: { target: "#roles", values: [admin, editor] }
              - select: { target: "#role", value: admin }
              - press: Escape
              - type: { target: "#q", value: "ab", clear: true }
              - dblclick: { target: "#cell", modifiers: [Control] }
            """);

        Assert.True(result.IsValid);

        var steps = result.Script!.Steps;
        var multi = Assert.IsType<SelectStep>(steps[0]);
        Assert.Equal(["admin", "editor"], multi.Values);

        var single = Assert.IsType<SelectStep>(steps[1]);
        Assert.Equal("admin", single.Value);
        Assert.Null(single.Values);

        Assert.Equal("Escape", Assert.IsType<PressKeyStep>(steps[2]).Key);
        Assert.True(Assert.IsType<TypeStep>(steps[3]).ClearFirst);
        Assert.Equal(["Control"], Assert.IsType<DoubleClickStep>(steps[4]).Modifiers);
    }

    [Fact]
    public async Task Multi_select_and_page_press_reach_the_session()
    {
        var session = new FakeSession();
        var runner = new YamlAutomationScriptRunner(new AutomationScriptParser());

        var result = await runner.RunAsync(
            """
            steps:
              - select: { target: "#roles", values: [admin, editor] }
              - press: Escape
            """,
            session);

        Assert.True(result.Success);
        Assert.Equal(["select:#roles=[admin,editor]", "pressKey:Escape"], session.Calls);
    }

    [Theory]
    [InlineData("press: { key: Escape }")]
    [InlineData("press: { Key: Escape }")]
    public void Parser_reports_the_missing_target_the_same_way_regardless_of_case(string step)
    {
        var result = new AutomationScriptParser().Parse($"steps:\n  - {step}\n");

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("target", StringComparison.Ordinal));
    }

    [Fact]
    public void Parser_infers_form_value_kinds()
    {
        var result = new AutomationScriptParser().Parse(
            """
            steps:
              - fillForm:
                  "#name": alice
                  "#agree": true
                  "#roles": [admin, editor]
            """);

        Assert.True(result.IsValid);

        var fields = Assert.IsType<FillFormStep>(result.Script!.Steps[0]).Fields;
        Assert.Equal("alice", fields["#name"].Text);
        Assert.True(fields["#agree"].Toggle);
        Assert.Equal(["admin", "editor"], fields["#roles"].Choices);
    }

    [Fact]
    public async Task Fill_form_reaches_the_session_with_typed_values()
    {
        var session = new FakeSession();
        var runner = new YamlAutomationScriptRunner(new AutomationScriptParser());

        var result = await runner.RunAsync(
            """
            steps:
              - fillForm:
                  "#name": alice
                  "#agree": false
            """,
            session);

        Assert.True(result.Success);
        Assert.Equal(["fillForm:#name=alice,#agree=false"], session.Calls);
    }

    [Fact]
    public void Parser_documents_which_field_is_wrong()
    {
        var result = new AutomationScriptParser().Parse("steps:\n  - scroll: { target: \"#grid\", y: abc }\n");

        Assert.False(result.IsValid);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("script.scroll", diagnostic.Code);
        Assert.Contains("y must be an integer", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Interaction_verbs_reach_the_session_in_order()
    {
        var session = new FakeSession();
        var runner = new YamlAutomationScriptRunner(new AutomationScriptParser());

        var result = await runner.RunAsync(
            """
            steps:
              - dblclick: "#cell"
              - type: { target: "#search", value: "ab" }
              - hover: "#menu"
              - check: { target: "#agree" }
              - scroll: { target: "#grid", y: 300 }
              - click: { target: "#row", modifiers: "Control" }
            """,
            session);

        Assert.True(result.Success);
        Assert.Equal(
            ["dblclick:#cell", "type:#search=ab", "hover:#menu", "check:#agree=True", "scroll:#grid:0,300", "click:#row+Control"],
            session.Calls.Where(call => !call.StartsWith("goto:", StringComparison.Ordinal)));
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

        // Interaction verbs record their call so scripts can be asserted.
        public Task ClickAsync(string target, BrowserClickOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls.Add($"click:{target}" + (options?.Modifiers is { } mods ? $"+{string.Join("+", mods)}" : string.Empty));
            return Task.CompletedTask;
        }

        public double DefaultTimeoutMs { get; set; } = 30_000;

        public double? TimeoutOverrideMs { get; set; }

        public Task<IReadOnlyList<FormFieldResult>> FillFormAsync(
            IReadOnlyDictionary<string, FormFieldValue> fields,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"fillForm:{string.Join(",", fields.Select(pair => $"{pair.Key}={pair.Value}"))}");
            return Task.FromResult<IReadOnlyList<FormFieldResult>>(
                [.. fields.Select(pair => new FormFieldResult(pair.Key, "text", pair.Value.ToString(), pair.Value.ToString()))]);
        }

        public Task<IReadOnlyList<FormFieldState>> GetFormStateAsync(
            string? root = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<FormFieldState>>([]);

        public Task DoubleClickAsync(
            string target,
            BrowserClickOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"dblclick:{target}");
            return Task.CompletedTask;
        }

        public Task TypeAsync(
            string target,
            string value,
            BrowserTypeOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"type:{target}={value}");
            return Task.CompletedTask;
        }

        public Task PressKeyAsync(string key, CancellationToken cancellationToken = default)
        {
            Calls.Add($"pressKey:{key}");
            return Task.CompletedTask;
        }

        public Task ClearAsync(string target, CancellationToken cancellationToken = default)
        {
            Calls.Add($"clear:{target}");
            return Task.CompletedTask;
        }

        public Task HoverAsync(string target, CancellationToken cancellationToken = default)
        {
            Calls.Add($"hover:{target}");
            return Task.CompletedTask;
        }

        public Task CheckAsync(string target, bool isChecked = true, CancellationToken cancellationToken = default)
        {
            Calls.Add($"check:{target}={isChecked}");
            return Task.CompletedTask;
        }

        public Task ScrollAsync(int deltaX, int deltaY, string? target = null, CancellationToken cancellationToken = default)
        {
            Calls.Add($"scroll:{target ?? "-"}:{deltaX},{deltaY}");
            return Task.CompletedTask;
        }

        public Task ScrollIntoViewAsync(string target, CancellationToken cancellationToken = default)
        {
            Calls.Add($"scrollintoview:{target}");
            return Task.CompletedTask;
        }

        public Task DragToAsync(string source, string target, CancellationToken cancellationToken = default)
        {
            Calls.Add($"dragto:{source}->{target}");
            return Task.CompletedTask;
        }

        public Task SetInputFilesAsync(string target, string path, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task GoBackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetViewportAsync(int width, int height, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string?> GetValueAsync(string target, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<int> CountAsync(string target, CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task<string?> GetHtmlAsync(string target, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<string> AriaSnapshotAsync(string? target = null, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Empty);

        public Task<bool> IsVisibleAsync(string target, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> IsEnabledAsync(string target, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> IsCheckedAsync(string target, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<BrowserBox?> GetBoxAsync(string target, CancellationToken cancellationToken = default)
            => Task.FromResult<BrowserBox?>(null);

        public Task WaitForUrlAsync(string urlPattern, int? timeoutMs = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task WaitForCountAsync(string target, int count, int? timeoutMs = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task WaitForLoadStateAsync(BrowserLoadState state = BrowserLoadState.Load, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<BrowserScreenshot> TakeScreenshotAsync(
            BrowserScreenshotOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new BrowserScreenshot
            {
                Path = options?.Path ?? "/tmp/shot.png",
                Width = 1280,
                Height = 720,
                Bytes = 1024,
                FullPage = options?.FullPage ?? false,
            });

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

        public Task SelectAsync(
            string target,
            IReadOnlyList<string> values,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"select:{target}=[{string.Join(",", values)}]");
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

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
