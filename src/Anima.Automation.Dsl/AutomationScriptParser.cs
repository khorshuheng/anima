using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Anima.Automation.Dsl;

/// <summary>
/// Parses the <c>anima-yaml/v1</c> script format:
/// <code>
/// name: login-smoke
/// variables: { user: "${env.UAT_USER}" }
/// steps:
///   - goto: /login
///   - fill: { target: "#username", value: "${user}" }
///   - click: "text=Sign in"
///   - expect: { url: /dashboard }
/// </code>
/// </summary>
public sealed class AutomationScriptParser
{
    public ScriptParseResult Parse(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var diagnostics = new List<ScriptDiagnostic>();

        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(source);
            stream.Load(reader);

            if (stream.Documents.Count != 1)
            {
                diagnostics.Add(new ScriptDiagnostic(
                    "script.document_count",
                    "a script must contain exactly one YAML document"));
                return new ScriptParseResult(null, diagnostics);
            }

            if (stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                diagnostics.Add(new ScriptDiagnostic("script.root", "script root must be a mapping"));
                return new ScriptParseResult(null, diagnostics);
            }

            var name = Scalar(GetNode(root, "name")) ?? "unnamed";
            var variables = ParseVariables(GetNode(root, "variables"), diagnostics);
            var steps = ParseSteps(GetNode(root, "steps"), diagnostics);

            return new ScriptParseResult(
                new AutomationScript(name, variables, steps, source),
                diagnostics);
        }
        catch (YamlException exception)
        {
            diagnostics.Add(new ScriptDiagnostic("script.yaml", exception.Message));
            return new ScriptParseResult(null, diagnostics);
        }
    }

    private static Dictionary<string, string> ParseVariables(
        YamlNode? node,
        List<ScriptDiagnostic> diagnostics)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node is null)
        {
            return variables;
        }

        if (node is not YamlMappingNode mapping)
        {
            diagnostics.Add(new ScriptDiagnostic("script.variables", "variables must be a mapping"));
            return variables;
        }

        foreach (var pair in mapping.Children)
        {
            var key = Scalar(pair.Key);
            var value = Scalar(pair.Value);
            if (string.IsNullOrWhiteSpace(key) || value is null)
            {
                diagnostics.Add(new ScriptDiagnostic(
                    "script.variables",
                    "variable entries must be string keys and scalar values"));
                continue;
            }

            variables[key] = value;
        }

        return variables;
    }

    private static List<AutomationStep> ParseSteps(
        YamlNode? node,
        List<ScriptDiagnostic> diagnostics)
    {
        var steps = new List<AutomationStep>();
        if (node is null)
        {
            diagnostics.Add(new ScriptDiagnostic("script.steps", "steps are required"));
            return steps;
        }

        if (node is not YamlSequenceNode sequence)
        {
            diagnostics.Add(new ScriptDiagnostic("script.steps", "steps must be a sequence"));
            return steps;
        }

        for (var index = 0; index < sequence.Children.Count; index++)
        {
            var child = sequence.Children[index];
            if (child is not YamlMappingNode mapping || mapping.Children.Count != 1)
            {
                diagnostics.Add(new ScriptDiagnostic(
                    "script.step_shape",
                    "each step must be a mapping with exactly one verb",
                    index));
                continue;
            }

            var entry = mapping.Children.First();
            var verb = Scalar(entry.Key)?.Trim().ToLowerInvariant();
            var value = entry.Value;
            var step = ParseStep(verb, value, index, diagnostics);
            if (step is not null)
            {
                steps.Add(step);
            }
        }

        return steps;
    }

    private static AutomationStep? ParseStep(
        string? verb,
        YamlNode value,
        int index,
        List<ScriptDiagnostic> diagnostics)
    {
        switch (verb)
        {
            case "goto":
            case "navigate":
                return RequireScalar(value, verb, index, diagnostics) is { } url
                    ? new NavigateStep(url)
                    : null;

            case "click":
                return RequireScalar(value, verb, index, diagnostics) is { } clickTarget
                    ? new ClickStep(clickTarget)
                    : null;

            case "fill":
                return RequireFields(value, index, diagnostics, "target", "value") is { } fill
                    ? new FillStep(fill["target"], fill["value"])
                    : null;

            case "press":
                return RequireFields(value, index, diagnostics, "target", "key") is { } press
                    ? new PressStep(press["target"], press["key"])
                    : null;

            case "select":
                return RequireFields(value, index, diagnostics, "target", "value") is { } select
                    ? new SelectStep(select["target"], select["value"])
                    : null;

            case "waitfor":
            case "wait":
                if (RequireFields(value, index, diagnostics, "target") is not { } wait)
                {
                    return null;
                }

                var state = ParseWaitState(Field(value, "state"), index, diagnostics);
                int? timeout = int.TryParse(Field(value, "timeout"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTimeout)
                    ? parsedTimeout
                    : null;
                return new WaitStep(wait["target"], state, timeout);

            case "waitfortext":
                if (RequireFields(value, index, diagnostics, "target", "text") is not { } waitText)
                {
                    return null;
                }

                int? textTimeout = int.TryParse(Field(value, "timeout"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTextTimeout)
                    ? parsedTextTimeout
                    : null;
                return new WaitForTextStep(waitText["target"], waitText["text"], textTimeout);

            case "expect":
                var expect = new ExpectStep(
                    Url: Field(value, "url"),
                    Target: Field(value, "target"),
                    Text: Field(value, "text"),
                    Value: Field(value, "value"));

                if (expect.Url is null && expect.Target is null && expect.Text is null && expect.Value is null)
                {
                    diagnostics.Add(new ScriptDiagnostic(
                        "script.expect",
                        "expect requires at least one of url, target, text or value",
                        index));
                    return null;
                }

                if ((expect.Target is null) != (expect.Text is null) && expect.Url is null)
                {
                    diagnostics.Add(new ScriptDiagnostic(
                        "script.expect",
                        "expect target and text must be provided together",
                        index));
                    return null;
                }

                return expect;

            case "extract":
                return RequireFields(value, index, diagnostics, "name", "target") is { } extract
                    ? new ExtractStep(extract["name"], extract["target"], Field(value, "attribute"))
                    : null;

            case "eval":
            case "evaluate":
            case "script":
                return RequireScalar(value, verb, index, diagnostics) is { } expression
                    ? new EvaluateStep(expression)
                    : null;

            case "screenshot":
                return new ScreenshotStep(
                    value is YamlScalarNode ? Scalar(value) : Field(value, "path"));

            default:
                diagnostics.Add(new ScriptDiagnostic(
                    "script.unknown_verb",
                    $"unknown step verb '{verb}'",
                    index));
                return null;
        }
    }

    private static WaitState ParseWaitState(
        string? value,
        int index,
        List<ScriptDiagnostic> diagnostics) => value?.ToLowerInvariant() switch
        {
            null or "" or "visible" => WaitState.Visible,
            "hidden" => WaitState.Hidden,
            "attached" => WaitState.Attached,
            "detached" => WaitState.Detached,
            _ => UnknownState(value, index, diagnostics),
        };

    private static WaitState UnknownState(string value, int index, List<ScriptDiagnostic> diagnostics)
    {
        diagnostics.Add(new ScriptDiagnostic(
            "script.wait_state",
            $"unknown wait state '{value}'",
            index));
        return WaitState.Visible;
    }

    private static Dictionary<string, string>? RequireFields(
        YamlNode node,
        int index,
        List<ScriptDiagnostic> diagnostics,
        params string[] fields)
    {
        if (node is not YamlMappingNode mapping)
        {
            diagnostics.Add(new ScriptDiagnostic(
                "script.step_shape",
                $"step requires a mapping with: {string.Join(", ", fields)}",
                index));
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            var value = Field(mapping, field);
            if (string.IsNullOrWhiteSpace(value))
            {
                diagnostics.Add(new ScriptDiagnostic(
                    "script.missing_field",
                    $"step requires '{field}'",
                    index));
                return null;
            }

            values[field] = value;
        }

        return values;
    }

    private static string? RequireScalar(
        YamlNode node,
        string? verb,
        int index,
        List<ScriptDiagnostic> diagnostics)
    {
        if (node is YamlScalarNode scalar && !string.IsNullOrWhiteSpace(scalar.Value))
        {
            return scalar.Value;
        }

        diagnostics.Add(new ScriptDiagnostic(
            "script.missing_field",
            $"step '{verb}' requires a scalar value",
            index));
        return null;
    }

    private static string? Field(YamlNode node, string name)
        => node is YamlMappingNode mapping ? Scalar(GetNode(mapping, name)) : null;

    private static YamlNode? GetNode(YamlMappingNode mapping, string name)
    {
        foreach (var pair in mapping.Children)
        {
            if (pair.Key is YamlScalarNode key &&
                string.Equals(key.Value, name, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static string? Scalar(YamlNode? node)
        => node is YamlScalarNode scalar ? scalar.Value : null;
}
