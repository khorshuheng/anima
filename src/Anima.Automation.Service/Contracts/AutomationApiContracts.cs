namespace Anima.Automation.Service.Contracts;

public sealed record RunScriptRequest(
    string Source,
    Dictionary<string, string>? Variables = null,
    bool? StopOnError = null);

public sealed record ValidateScriptRequest(string Source);

/// <summary>
/// One session action. Not every field applies to every action:
/// <c>Target</c> is a selector except for <c>waitforresponse</c>, where it is a URL
/// glob; <c>Value</c> carries text/keys; <c>Method</c>, <c>Status</c>,
/// <c>TimeoutMs</c> and <c>IncludeBody</c> are only read by <c>waitforresponse</c>.
/// </summary>
public sealed record ActionRequest(
    string Action,
    string? Target = null,
    string? Value = null,
    string? Method = null,
    int? Status = null,
    int? TimeoutMs = null,
    bool? IncludeBody = null);

public sealed record CdpRequest(string Method, string? ParametersJson = null);
