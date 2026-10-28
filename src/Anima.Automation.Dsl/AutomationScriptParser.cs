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

            string name = Scalar(GetNode(root, "name")) ?? "unnamed";
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
            string? key = Scalar(pair.Key);
            string? value = Scalar(pair.Value);
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

        for (int index = 0; index < sequence.Children.Count; index++)
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
            string? verb = Scalar(entry.Key)?.Trim().ToLowerInvariant();
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
                if (value is YamlMappingNode clickMapping)
                {
                    if (RequireFields(value, index, diagnostics, "target") is not { } clickFields)
                    {
                        return null;
                    }

                    // Accept both `modifiers: [Control, Shift]` and "Control,Shift";
                    // an unquoted comma in flow style would parse as two mapping keys.
                    var modifiersNode = GetNode(clickMapping, "modifiers");
                    IReadOnlyList<string>? modifiers = modifiersNode switch
                    {
                        YamlSequenceNode sequence => [.. sequence.Children
                            .Select(Scalar)
                            .Where(modifier => !string.IsNullOrWhiteSpace(modifier))
                            .Select(modifier => modifier!)],
                        YamlScalarNode => Scalar(modifiersNode)?.Split(
                            ',',
                            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                        _ => null,
                    };

                    return new ClickStep(clickFields["target"], modifiers);
                }

                return RequireScalar(value, verb, index, diagnostics) is { } clickTarget
                    ? new ClickStep(clickTarget)
                    : null;

            case "dblclick":
            case "doubleclick":
                if (value is YamlMappingNode)
                {
                    return RequireFields(value, index, diagnostics, "target") is { } doubleFields
                        ? new DoubleClickStep(doubleFields["target"], Modifiers(value))
                        : null;
                }

                return RequireScalar(value, verb, index, diagnostics) is { } doubleTarget
                    ? new DoubleClickStep(doubleTarget)
                    : null;

            case "type":
                if (RequireFields(value, index, diagnostics, "target", "value") is not { } typeFields)
                {
                    return null;
                }

                if (!TryIntField(value, "delay", "type", index, diagnostics, 0, out int? typeDelay))
                {
                    return null;
                }

                return new TypeStep(
                    typeFields["target"],
                    typeFields["value"],
                    typeDelay,
                    string.Equals(Field(value, "clear"), "true", StringComparison.OrdinalIgnoreCase));

            case "clear":
                return RequireScalar(value, verb, index, diagnostics) is { } clearTarget
                    ? new ClearStep(clearTarget)
                    : null;

            case "hover":
                return RequireScalar(value, verb, index, diagnostics) is { } hoverTarget
                    ? new HoverStep(hoverTarget)
                    : null;

            case "check":
                return RequireFields(value, index, diagnostics, "target") is { } checkFields
                    ? new CheckStep(
                        checkFields["target"],
                        !string.Equals(Field(value, "checked"), "false", StringComparison.OrdinalIgnoreCase))
                    : null;

            case "scroll":
                if (value is not YamlMappingNode)
                {
                    diagnostics.Add(new ScriptDiagnostic(
                        "script.scroll",
                        "scroll requires a mapping with x, y or target",
                        index));
                    return null;
                }

                if (!TryIntField(value, "x", "scroll", index, diagnostics, null, out int? scrollX)
                    || !TryIntField(value, "y", "scroll", index, diagnostics, null, out int? scrollY))
                {
                    return null;
                }

                if (scrollX is null or 0 && scrollY is null or 0)
                {
                    diagnostics.Add(new ScriptDiagnostic(
                        "script.scroll",
                        "scroll requires a non-zero x or y",
                        index));
                    return null;
                }

                return new ScrollStep(scrollX ?? 0, scrollY ?? 0, Field(value, "target"));

            case "scrollintoview":
                return RequireScalar(value, verb, index, diagnostics) is { } scrollTarget
                    ? new ScrollIntoViewStep(scrollTarget)
                    : null;

            case "dragto":
                return RequireFields(value, index, diagnostics, "from", "to") is { } dragFields
                    ? new DragToStep(dragFields["from"], dragFields["to"])
                    : null;

            case "fill":
                return RequireFields(value, index, diagnostics, "target", "value") is { } fill
                    ? new FillStep(fill["target"], fill["value"])
                    : null;

            case "press":
                // GetNode matches field names case-insensitively, so `Key:` works too.
                if (value is YamlMappingNode mapping && GetNode(mapping, "key") is not null)
                {
                    return RequireFields(value, index, diagnostics, "target", "key") is { } press
                        ? new PressStep(press["target"], press["key"])
                        : null;
                }

                // `press: Escape` presses on the page, with no target.
                return RequireScalar(value, verb, index, diagnostics) is { } pageKey
                    ? new PressKeyStep(pageKey)
                    : null;

            case "select":
                if (RequireFields(value, index, diagnostics, "target") is not { } select)
                {
                    return null;
                }

                string[]? selectValues = GetNode((YamlMappingNode)value, "values") is YamlSequenceNode selectList
                    ? [.. selectList.Children
                        .Select(Scalar)
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Select(item => item!)]
                    : null;

                string? singleValue = Field(value, "value");

                if (selectValues is null && singleValue is null)
                {
                    diagnostics.Add(new ScriptDiagnostic(
                        "script.select",
                        "select requires either value or values",
                        index));
                    return null;
                }

                return new SelectStep(select["target"], singleValue, selectValues);

            case "waitfor":
            case "wait":
                if (RequireFields(value, index, diagnostics, "target") is not { } wait)
                {
                    return null;
                }

                var state = ParseWaitState(Field(value, "state"), index, diagnostics);
                if (!TryIntField(value, "timeout", "waitFor", index, diagnostics, 1, out int? timeout))
                {
                    return null;
                }

                return new WaitStep(wait["target"], state, timeout);

            case "waitfortext":
                if (RequireFields(value, index, diagnostics, "target", "text") is not { } waitText)
                {
                    return null;
                }

                if (!TryIntField(value, "timeout", "waitForText", index, diagnostics, 1, out int? textTimeout))
                {
                    return null;
                }

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

            case "waitforresponse":
                if (!TryIntField(value, "status", "waitForResponse", index, diagnostics, null, out int? status)
                    || !TryIntField(value, "timeout", "waitForResponse", index, diagnostics, 1, out int? responseTimeout))
                {
                    return null;
                }

                var waitResponse = new WaitForResponseStep(
                    Method: Field(value, "method"),
                    Url: Field(value, "url"),
                    Status: status,
                    TimeoutMs: responseTimeout,
                    Extract: Field(value, "extract"));

                if (waitResponse.Method is null && waitResponse.Url is null && waitResponse.Status is null)
                {
                    diagnostics.Add(new ScriptDiagnostic(
                        "script.wait_for_response",
                        "waitForResponse requires at least one of method, url or status",
                        index));
                    return null;
                }

                return waitResponse;

            case "fillform":
                if (value is not YamlMappingNode form)
                {
                    diagnostics.Add(new ScriptDiagnostic(
                        "script.fill_form",
                        "fillForm requires a mapping of selector to value",
                        index));
                    return null;
                }

                var fields = new Dictionary<string, FormFieldValue>(StringComparer.Ordinal);
                bool valid = true;

                foreach (var pair in form.Children)
                {
                    string? selector = Scalar(pair.Key);
                    if (string.IsNullOrWhiteSpace(selector))
                    {
                        diagnostics.Add(new ScriptDiagnostic(
                            "script.fill_form",
                            "fillForm selectors must be non-empty strings",
                            index));
                        valid = false;
                        continue;
                    }

                    fields[selector] = pair.Value switch
                    {
                        YamlSequenceNode sequence => FormFieldValue.FromChoices(
                            sequence.Children.Select(Scalar).Where(item => item is not null).Select(item => item!)),
                        // An unquoted true/false is a toggle; a quoted one stays text.
                        YamlScalarNode { Style: ScalarStyle.Plain } plain
                            when bool.TryParse(plain.Value, out bool toggle) => FormFieldValue.FromToggle(toggle),
                        YamlScalarNode scalar => FormFieldValue.FromText(scalar.Value ?? string.Empty),
                        _ => FormFieldValue.FromText(string.Empty),
                    };
                }

                return valid ? new FillFormStep(fields) : null;

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
        foreach (string field in fields)
        {
            string? value = Field(mapping, field);
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

    /// <summary>
    /// Reads an optional integer field. Returns false only when the field is present
    /// but unusable, so callers fail the step instead of silently substituting a
    /// default (a mistyped status used to widen a matcher, a mistyped delay a no-op).
    /// </summary>
    private static bool TryIntField(
        YamlNode value,
        string field,
        string verb,
        int index,
        List<ScriptDiagnostic> diagnostics,
        int? minimum,
        out int? parsed)
    {
        parsed = null;
        string? text = Field(value, field);
        if (text is null)
        {
            return true;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
        {
            diagnostics.Add(new ScriptDiagnostic(
                DiagnosticCode(verb),
                $"{verb} {field} must be an integer, not '{text}'",
                index));
            return false;
        }

        if (minimum is { } min && number < min)
        {
            diagnostics.Add(new ScriptDiagnostic(
                DiagnosticCode(verb),
                min == 1
                    ? $"{verb} {field} must be a positive integer, not '{text}'"
                    : $"{verb} {field} must be at least {min}, not '{text}'",
                index));
            return false;
        }

        parsed = number;
        return true;
    }

    /// <summary>
    /// Reads a modifier list: `[Control, Shift]` or "Control,Shift". A sequence is
    /// preferred because an unquoted comma in flow style parses as two mapping keys.
    /// </summary>
    private static string[]? Modifiers(YamlNode value)
    {
        var node = value is YamlMappingNode mapping ? GetNode(mapping, "modifiers") : null;

        return node switch
        {
            YamlSequenceNode sequence => [.. sequence.Children
                .Select(Scalar)
                .Where(modifier => !string.IsNullOrWhiteSpace(modifier))
                .Select(modifier => modifier!)],
            YamlScalarNode => Scalar(node)?.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            _ => null,
        };
    }

    /// <summary>script.waitForResponse -> script.wait_for_response, matching existing codes.</summary>
    private static string DiagnosticCode(string verb)
    {
        var builder = new System.Text.StringBuilder("script.");

        foreach (char character in verb)
        {
            if (char.IsUpper(character) && builder.Length > "script.".Length)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
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
