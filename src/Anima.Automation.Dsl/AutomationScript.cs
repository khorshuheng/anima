namespace Anima.Automation.Dsl;

/// <summary>One declarative step in an agent-derived UI script.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Major Code Smell",
    "S2094:Classes should not be empty",
    Justification = "Marker base type for the step hierarchy; steps are matched by concrete type.")]
public abstract record AutomationStep;

public sealed record NavigateStep(string Url) : AutomationStep;

/// <summary>
/// Clicks a target. <paramref name="Modifiers"/> are held during the click, which
/// is how grids multi-select (Ctrl-click) without dropping the selection.
/// </summary>
public sealed record ClickStep(string Target, IReadOnlyList<string>? Modifiers = null) : AutomationStep;

public sealed record FillStep(string Target, string Value) : AutomationStep;

public sealed record PressStep(string Target, string Key) : AutomationStep;

/// <summary>Selects one option, or several when <paramref name="Values"/> is set.</summary>
public sealed record SelectStep(string Target, string? Value, IReadOnlyList<string>? Values = null) : AutomationStep;

/// <summary>Presses a key on the page itself, with no target.</summary>
public sealed record PressKeyStep(string Key) : AutomationStep;

public sealed record WaitStep(string Target, WaitState State, int? TimeoutMs = null) : AutomationStep;

public sealed record WaitForTextStep(string Target, string Text, int? TimeoutMs = null) : AutomationStep;

public sealed record ExpectStep(
    string? Url = null,
    string? Target = null,
    string? Text = null,
    string? Value = null) : AutomationStep;

/// <summary>
/// Waits for a network response, which is how a script confirms that a write
/// actually reached the server. Any field left null matches anything;
/// <paramref name="Extract"/> stores the response body in a variable.
/// </summary>
public sealed record WaitForResponseStep(
    string? Method = null,
    string? Url = null,
    int? Status = null,
    int? TimeoutMs = null,
    string? Extract = null) : AutomationStep;

/// <summary>Double click, for cells whose editor opens on dblclick.</summary>
public sealed record DoubleClickStep(string Target, IReadOnlyList<string>? Modifiers = null) : AutomationStep;

/// <summary>Types character by character, for widgets that filter per keystroke.</summary>
public sealed record TypeStep(
    string Target,
    string Value,
    int? DelayMs = null,
    bool ClearFirst = false) : AutomationStep;

public sealed record ClearStep(string Target) : AutomationStep;

public sealed record HoverStep(string Target) : AutomationStep;

public sealed record CheckStep(string Target, bool IsChecked = true) : AutomationStep;

/// <summary>Wheel scroll; when <paramref name="Target"/> is set the wheel goes over it.</summary>
public sealed record ScrollStep(int DeltaX, int DeltaY, string? Target = null) : AutomationStep;

public sealed record ScrollIntoViewStep(string Target) : AutomationStep;

public sealed record DragToStep(string Source, string Target) : AutomationStep;

/// <summary>
/// Fills several fields at once. The parser infers each value's kind: scalars are
/// text, YAML booleans are toggles, sequences are choices.
/// </summary>
public sealed record FillFormStep(IReadOnlyDictionary<string, FormFieldValue> Fields) : AutomationStep;

public sealed record ExtractStep(string Name, string Target, string? Attribute = null) : AutomationStep;

public sealed record EvaluateStep(string Expression) : AutomationStep;

public sealed record ScreenshotStep(string? Path = null) : AutomationStep;

public sealed record AutomationScript(
    string Name,
    IReadOnlyDictionary<string, string> Variables,
    IReadOnlyList<AutomationStep> Steps,
    string Source);

public sealed record ScriptParseResult(
    AutomationScript? Script,
    IReadOnlyList<ScriptDiagnostic> Diagnostics)
{
    public bool IsValid => Script is not null && Diagnostics.All(diagnostic => !diagnostic.IsError);
}
