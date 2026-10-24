using Microsoft.Extensions.Options;

namespace Anima.Automation.Dsl;

public sealed class ScriptCatalogOptions
{
    /// <summary>Directory agents write derived scripts into.</summary>
    public string Directory { get; set; } = "specs";
}

public sealed record ScriptDescriptor(string Name, string Path, string Source, bool IsValid);

/// <summary>Lists repo scripts so scripts can be reused without regenerating them.</summary>
public sealed class ScriptCatalog
{
    private readonly ScriptCatalogOptions _options;
    private readonly AutomationScriptParser _parser;

    public ScriptCatalog(IOptions<ScriptCatalogOptions> options, AutomationScriptParser parser)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(parser);
        _options = options.Value;
        _parser = parser;
    }

    public IReadOnlyList<ScriptDescriptor> LoadAll()
    {
        var directory = ResolveDirectory();
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(directory, "*.y*ml", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .Select(path =>
            {
                var source = File.ReadAllText(path);
                var parsed = _parser.Parse(source);
                return new ScriptDescriptor(
                    parsed.Script?.Name ?? Path.GetFileNameWithoutExtension(path),
                    path,
                    source,
                    parsed.IsValid);
            })
            .ToArray();
    }

    private string ResolveDirectory()
        => Path.IsPathRooted(_options.Directory)
            ? _options.Directory
            : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), _options.Directory));
}
