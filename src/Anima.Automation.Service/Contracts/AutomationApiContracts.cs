namespace Anima.Automation.Service.Contracts;

public sealed record RunScriptRequest(
    string Source,
    Dictionary<string, string>? Variables = null,
    bool? StopOnError = null);

public sealed record ValidateScriptRequest(string Source);

/// <summary>
/// One session action. Not every field applies to every action:
/// <list type="bullet">
/// <item><c>Target</c>: a selector, or a URL glob for <c>waitforurl</c>/<c>waitforresponse</c>.</item>
/// <item><c>Value</c>: text to type/fill, an attribute name, a load state, the mouse
/// button for <c>click</c>, or <c>fullpage</c> for <c>screenshot</c>.</item>
/// <item><c>Modifiers</c>: held during <c>click</c> (<c>["Control","Shift"]</c>).</item>
/// <item><c>Method</c>/<c>Status</c>/<c>IncludeBody</c>: <c>waitforresponse</c> only.</item>
/// <item><c>TimeoutMs</c>: any waiting action.</item>
/// <item><c>X</c>/<c>Y</c>: <c>scroll</c> deltas. <c>Width</c>/<c>Height</c>:
/// <c>setviewport</c>, and <c>Height</c> also caps a <c>screenshot</c>.</item>
/// <item><c>DelayMs</c>: <c>type</c>. <c>Checked</c>: <c>check</c>. <c>Count</c>:
/// <c>waitforcount</c> expected count, or <c>click</c> click count.</item>
/// <item><c>Name</c>: <c>screenshot</c> file name. <c>Values</c>: <c>select</c> options.
/// <c>State</c>: <c>waitfor</c> state (<c>visible</c>/<c>hidden</c>/<c>attached</c>/<c>detached</c>).
/// <c>Position</c>: <c>click</c>/<c>dblclick</c> offset. <c>Clear</c>: <c>type</c>
/// empties the field first.</item>
/// </list>
/// </summary>
public sealed record ActionRequest(
    string Action,
    string? Target = null,
    string? Value = null,
    string? Method = null,
    int? Status = null,
    int? TimeoutMs = null,
    bool? IncludeBody = null,
    int? X = null,
    int? Y = null,
    int? Width = null,
    int? Height = null,
    int? DelayMs = null,
    bool? Checked = null,
    int? Count = null,
    string[]? Modifiers = null,
    string? Name = null,
    string[]? Values = null,
    string? State = null,
    ActionPosition? Position = null,
    bool? Clear = null);

/// <summary>Click offset inside the element, in CSS pixels.</summary>
public sealed record ActionPosition(double X, double Y);

/// <summary>
/// Sequential actions against one session, with a per-step result shaped like a
/// script run. <c>stopOnError</c> defaults to true.
/// </summary>
public sealed record BatchActionRequest(
    IReadOnlyList<ActionRequest> Steps,
    bool? StopOnError = null);

public sealed record CdpRequest(string Method, string? ParametersJson = null);
