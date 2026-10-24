namespace Anima.Automation.Service.Contracts;

public sealed record RunScriptRequest(
    string Source,
    Dictionary<string, string>? Variables = null,
    bool? StopOnError = null);

public sealed record ValidateScriptRequest(string Source);

public sealed record ActionRequest(string Action, string? Target = null, string? Value = null);

public sealed record CdpRequest(string Method, string? ParametersJson = null);
