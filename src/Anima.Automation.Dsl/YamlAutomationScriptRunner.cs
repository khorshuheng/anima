using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Anima.Automation.Dsl;

/// <summary>
/// Interprets <c>anima-yaml/v1</c> scripts. The agent derives the script once,
/// then the same steps can run unattended without an agent in the loop.
/// </summary>
public sealed class YamlAutomationScriptRunner : IScriptRunner
{
    private static readonly Regex VariablePattern = new(
        @"\$\{([A-Za-z0-9_.-]+)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private readonly AutomationScriptParser _parser;

    public YamlAutomationScriptRunner(AutomationScriptParser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        _parser = parser;
    }

    public string Format => "anima-yaml/v1";

    public ScriptValidationResult Validate(string source)
    {
        var parsed = _parser.Parse(source);
        return new ScriptValidationResult(parsed.IsValid, parsed.Diagnostics);
    }

    public async Task<ScriptRunResult> RunAsync(
        string source,
        IBrowserSession session,
        ScriptRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        options ??= new ScriptRunOptions();

        var parsed = _parser.Parse(source);
        var diagnostics = parsed.Diagnostics.ToList();
        var script = parsed.Script;

        if (script is null || diagnostics.Any(diagnostic => diagnostic.IsError))
        {
            var error = string.Join(
                "; ",
                diagnostics.Where(diagnostic => diagnostic.IsError).Select(diagnostic => diagnostic.Message));
            return new ScriptRunResult(
                false,
                [],
                new Dictionary<string, string>(StringComparer.Ordinal),
                error);
        }

        var variables = new Dictionary<string, string>(script.Variables, StringComparer.Ordinal);
        if (options.Variables is not null)
        {
            foreach (var pair in options.Variables)
            {
                variables[pair.Key] = pair.Value;
            }
        }

        var results = new List<ScriptStepResult>();
        for (var index = 0; index < script.Steps.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = script.Steps[index];
            var action = ActionName(step);
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var output = await ExecuteAsync(
                    step,
                    session,
                    variables,
                    options,
                    diagnostics,
                    index,
                    cancellationToken);
                results.Add(new ScriptStepResult(
                    index,
                    action,
                    true,
                    stopwatch.ElapsedMilliseconds,
                    output));
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                results.Add(new ScriptStepResult(
                    index,
                    action,
                    false,
                    stopwatch.ElapsedMilliseconds,
                    Error: exception.Message));

                if (options.StopOnError)
                {
                    return new ScriptRunResult(false, results, variables, exception.Message);
                }
            }
        }

        return new ScriptRunResult(
            results.All(result => result.Success),
            results,
            variables,
            results.Where(result => !result.Success).Select(result => result.Error).FirstOrDefault());
    }

    private static async Task<string?> ExecuteAsync(
        AutomationStep step,
        IBrowserSession session,
        Dictionary<string, string> variables,
        ScriptRunOptions options,
        List<ScriptDiagnostic> diagnostics,
        int index,
        CancellationToken cancellationToken)
    {
        var resolve = (string value) => ResolveVariables(value, variables, diagnostics, index);

        switch (step)
        {
            case NavigateStep navigate:
                await session.NavigateAsync(
                    ResolveUrl(resolve(navigate.Url), session.CurrentUrl, options.BaseUrl),
                    cancellationToken);
                return session.CurrentUrl;

            case ClickStep click:
                await session.ClickAsync(resolve(click.Target), cancellationToken);
                return null;

            case FillStep fill:
                await session.FillAsync(resolve(fill.Target), resolve(fill.Value), cancellationToken);
                return null;

            case PressStep press:
                await session.PressAsync(resolve(press.Target), resolve(press.Key), cancellationToken);
                return null;

            case SelectStep select:
                await session.SelectAsync(resolve(select.Target), resolve(select.Value), cancellationToken);
                return null;

            case WaitStep wait:
                await session.WaitForAsync(resolve(wait.Target), wait.State, cancellationToken);
                return null;

            case WaitForTextStep waitText:
                await WaitForTextAsync(
                    resolve(waitText.Target),
                    resolve(waitText.Text),
                    waitText.TimeoutMs ?? 10_000,
                    session,
                    cancellationToken);
                return null;

            case ExpectStep expect:
                await ExpectAsync(expect, session, resolve, cancellationToken);
                return null;

            case ExtractStep extract:
                var name = resolve(extract.Name);
                var target = resolve(extract.Target);
                var value = extract.Attribute is null or "text"
                    ? await session.GetTextAsync(target, cancellationToken)
                    : await session.GetAttributeAsync(target, resolve(extract.Attribute), cancellationToken);
                variables[name] = value ?? string.Empty;
                return value;

            case EvaluateStep evaluate:
                return await session.EvaluateAsync(resolve(evaluate.Expression), cancellationToken);

            case ScreenshotStep screenshot:
                return await session.TakeScreenshotAsync(
                    screenshot.Path is null ? null : resolve(screenshot.Path),
                    cancellationToken);

            default:
                throw new InvalidOperationException($"unsupported step '{step.GetType().Name}'");
        }
    }

    private static async Task WaitForTextAsync(
        string target,
        string expected,
        int timeoutMs,
        IBrowserSession session,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(Math.Max(1, timeoutMs));
        string? actual = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            actual = await session.GetTextAsync(target, cancellationToken);
            if (actual?.Contains(expected, StringComparison.Ordinal) == true)
            {
                return;
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new InvalidOperationException(
            $"timed out waiting for '{target}' to contain '{expected}', last text was '{actual}'");
    }

    private static async Task ExpectAsync(
        ExpectStep expect,
        IBrowserSession session,
        Func<string, string> resolve,
        CancellationToken cancellationToken)
    {
        if (expect.Url is { } url)
        {
            var expected = resolve(url);
            if (session.CurrentUrl is null ||
                !session.CurrentUrl.Contains(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"expected url containing '{expected}', current url is '{session.CurrentUrl}'");
            }
        }

        if (expect.Target is { } target && expect.Text is { } text)
        {
            var resolvedTarget = resolve(target);
            var expected = resolve(text);
            var actual = await session.GetTextAsync(resolvedTarget, cancellationToken);
            if (actual is null || !actual.Contains(expected, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"expected '{resolvedTarget}' to contain '{expected}', got '{actual}'");
            }
        }

        if (expect.Value is { } value)
        {
            var expected = resolve(value);
            if (session.CurrentUrl is null ||
                !string.Equals(session.CurrentUrl, expected, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"expected url '{expected}', current url is '{session.CurrentUrl}'");
            }
        }
    }

    private static string ResolveUrl(string url, string? currentUrl, string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        if (!string.IsNullOrWhiteSpace(baseUrl) &&
            Uri.TryCreate(new Uri(baseUrl.TrimEnd('/') + "/"), url.TrimStart('/'), out var combined))
        {
            return combined.ToString();
        }

        if (!string.IsNullOrWhiteSpace(currentUrl) &&
            Uri.TryCreate(new Uri(currentUrl), url, out var relative))
        {
            return relative.ToString();
        }

        return url;
    }

    private static string ResolveVariables(
        string value,
        Dictionary<string, string> variables,
        List<ScriptDiagnostic> diagnostics,
        int index)
    {
        if (!value.Contains("${", StringComparison.Ordinal))
        {
            return value;
        }

        return VariablePattern.Replace(value, match =>
        {
            var key = match.Groups[1].Value;
            if (key.StartsWith("env.", StringComparison.OrdinalIgnoreCase))
            {
                var environmentKey = key[4..];
                var environmentValue = Environment.GetEnvironmentVariable(environmentKey);
                if (environmentValue is null)
                {
                    diagnostics.Add(new ScriptDiagnostic(
                        "script.missing_variable",
                        $"environment variable '{environmentKey}' is not set",
                        index,
                        IsError: false));
                    return string.Empty;
                }

                return environmentValue;
            }

            if (variables.TryGetValue(key, out var resolved))
            {
                return resolved;
            }

            diagnostics.Add(new ScriptDiagnostic(
                "script.missing_variable",
                $"variable '{key}' is not defined",
                index,
                IsError: false));
            return match.Value;
        });
    }

    private static string ActionName(AutomationStep step) => step switch
    {
        NavigateStep => "goto",
        ClickStep => "click",
        FillStep => "fill",
        PressStep => "press",
        SelectStep => "select",
        WaitStep => "waitFor",
        WaitForTextStep => "waitForText",
        ExpectStep => "expect",
        ExtractStep => "extract",
        EvaluateStep => "eval",
        ScreenshotStep => "screenshot",
        _ => step.GetType().Name,
    };
}
