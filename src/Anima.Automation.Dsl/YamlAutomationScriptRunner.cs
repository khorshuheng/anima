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
            string error = string.Join(
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
        for (int index = 0; index < script.Steps.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = script.Steps[index];
            string action = ActionName(step);
            var stopwatch = Stopwatch.StartNew();

            try
            {
                string? output = await ExecuteAsync(
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
        string resolve(string value) => ResolveVariables(value, variables, diagnostics, index);

        switch (step)
        {
            case NavigateStep navigate:
                await session.NavigateAsync(
                    ResolveUrl(resolve(navigate.Url), session.CurrentUrl, options.BaseUrl),
                    cancellationToken);
                return session.CurrentUrl;

            case ClickStep click:
                await session.ClickAsync(
                    resolve(click.Target),
                    click.Modifiers is { } clickModifiers ? new BrowserClickOptions { Modifiers = clickModifiers } : null,
                    cancellationToken);
                return null;

            case DoubleClickStep doubleClick:
                await session.DoubleClickAsync(
                    resolve(doubleClick.Target),
                    doubleClick.Modifiers is { } doubleModifiers
                        ? new BrowserClickOptions { Modifiers = doubleModifiers }
                        : null,
                    cancellationToken);
                return null;

            case TypeStep type:
                await session.TypeAsync(
                    resolve(type.Target),
                    resolve(type.Value),
                    new BrowserTypeOptions { DelayMs = type.DelayMs, ClearFirst = type.ClearFirst },
                    cancellationToken);
                return null;

            case ClearStep clear:
                await session.ClearAsync(resolve(clear.Target), cancellationToken);
                return null;

            case HoverStep hover:
                await session.HoverAsync(resolve(hover.Target), cancellationToken);
                return null;

            case CheckStep check:
                await session.CheckAsync(resolve(check.Target), check.IsChecked, cancellationToken);
                return null;

            case ScrollStep scroll:
                await session.ScrollAsync(
                    scroll.DeltaX,
                    scroll.DeltaY,
                    scroll.Target is null ? null : resolve(scroll.Target),
                    cancellationToken);
                return null;

            case ScrollIntoViewStep scrollIntoView:
                await session.ScrollIntoViewAsync(resolve(scrollIntoView.Target), cancellationToken);
                return null;

            case DragToStep drag:
                await session.DragToAsync(resolve(drag.Source), resolve(drag.Target), cancellationToken);
                return null;

            case FillStep fill:
                await session.FillAsync(resolve(fill.Target), resolve(fill.Value), cancellationToken);
                return null;

            case PressStep press:
                await session.PressAsync(resolve(press.Target), resolve(press.Key), cancellationToken);
                return null;

            case PressKeyStep pressKey:
                await session.PressKeyAsync(resolve(pressKey.Key), cancellationToken);
                return null;

            case SelectStep select:
                if (select.Values is { Count: > 0 })
                {
                    await session.SelectAsync(
                        resolve(select.Target),
                        [.. select.Values.Select(resolve)],
                        cancellationToken);
                    return null;
                }

                if (select.Value is not { } singleValue)
                {
                    throw new InvalidOperationException("select requires either value or values");
                }

                await session.SelectAsync(resolve(select.Target), resolve(singleValue), cancellationToken);
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

            case WaitForResponseStep waitResponse:
                var response = await session.WaitForResponseAsync(
                    new ResponseMatcher
                    {
                        Method = waitResponse.Method is null ? null : resolve(waitResponse.Method),
                        UrlPattern = waitResponse.Url is null ? null : resolve(waitResponse.Url),
                        Status = waitResponse.Status,
                        Timeout = waitResponse.TimeoutMs is { } timeoutMs
                            ? TimeSpan.FromMilliseconds(timeoutMs)
                            : null,
                        IncludeBody = waitResponse.Extract is not null,
                    },
                    cancellationToken);

                if (waitResponse.Extract is not null)
                {
                    if (response.Body is null)
                    {
                        // An empty string here would silently hide a write whose
                        // response was never readable.
                        string reason = response.Detail is { } detail
                            ? $"the body was not retained ({detail})"
                            : "the response was empty, or the page navigated before the body could be read";

                        throw new InvalidOperationException(
                            $"Response {response.Status} from {response.Url} had no readable body: {reason}. " +
                            "Confirm the write with method/url/status instead, or read the body before the " +
                            "page navigates.");
                    }

                    variables[resolve(waitResponse.Extract)] = response.Body;
                }

                return $"{response.Method} {response.Status} {response.Url}";

            case ExpectStep expect:
                await ExpectAsync(expect, session, resolve, cancellationToken);
                return null;

            case FillFormStep fillForm:
                {
                    var resolvedFields = fillForm.Fields.ToDictionary(
                        pair => resolve(pair.Key),
                        pair => ResolveField(pair.Value, resolve),
                        StringComparer.Ordinal);

                    var filled = await session.FillFormAsync(resolvedFields, cancellationToken);
                    return $"{filled.Count} field(s) filled";
                }

            case ExtractStep extract:
                string name = resolve(extract.Name);
                string target = resolve(extract.Target);
                string? value = extract.Attribute is null or "text"
                    ? await session.GetTextAsync(target, cancellationToken)
                    : await session.GetAttributeAsync(target, resolve(extract.Attribute), cancellationToken);
                variables[name] = value ?? string.Empty;
                return value;

            case EvaluateStep evaluate:
                return await session.EvaluateAsync(resolve(evaluate.Expression), cancellationToken);

            case ScreenshotStep screenshot:
                var captured = await session.TakeScreenshotAsync(
                    new BrowserScreenshotOptions
                    {
                        Path = screenshot.Path is null ? null : resolve(screenshot.Path),
                        FullPage = true,
                    },
                    cancellationToken);

                return captured.Path;

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
            string expected = resolve(url);
            if (session.CurrentUrl?.Contains(expected, StringComparison.OrdinalIgnoreCase) != true)
            {
                throw new InvalidOperationException(
                    $"expected url containing '{expected}', current url is '{session.CurrentUrl}'");
            }
        }

        if (expect.Target is { } target && expect.Text is { } text)
        {
            string resolvedTarget = resolve(target);
            string expected = resolve(text);
            string? actual = await session.GetTextAsync(resolvedTarget, cancellationToken);
            if (actual?.Contains(expected, StringComparison.Ordinal) != true)
            {
                throw new InvalidOperationException(
                    $"expected '{resolvedTarget}' to contain '{expected}', got '{actual}'");
            }
        }

        if (expect.Value is { } value)
        {
            string expected = resolve(value);
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
            string key = match.Groups[1].Value;
            if (key.StartsWith("env.", StringComparison.OrdinalIgnoreCase))
            {
                string environmentKey = key[4..];
                string? environmentValue = Environment.GetEnvironmentVariable(environmentKey);
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

            if (variables.TryGetValue(key, out string? resolved))
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
        DoubleClickStep => "dblclick",
        TypeStep => "type",
        ClearStep => "clear",
        HoverStep => "hover",
        CheckStep => "check",
        ScrollStep => "scroll",
        ScrollIntoViewStep => "scrollIntoView",
        DragToStep => "dragTo",
        FillStep => "fill",
        PressStep => "press",
        SelectStep => "select",
        PressKeyStep => "pressKey",
        FillFormStep => "fillForm",
        WaitStep => "waitFor",
        WaitForTextStep => "waitForText",
        WaitForResponseStep => "waitForResponse",
        ExpectStep => "expect",
        ExtractStep => "extract",
        EvaluateStep => "eval",
        ScreenshotStep => "screenshot",
        _ => step.GetType().Name,
    };

    private static FormFieldValue ResolveField(FormFieldValue value, Func<string, string> resolve)
    {
        if (value.Text is { } text)
        {
            return FormFieldValue.FromText(resolve(text));
        }

        return value.Choices is { } choices
            ? FormFieldValue.FromChoices(choices.Select(resolve))
            : value;
    }

}
