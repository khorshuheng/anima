namespace Anima.Agent;

public sealed class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, IAgentTool> _tools;

    public ToolRegistry(IEnumerable<IAgentTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _tools = tools.ToDictionary(tool => tool.Name, StringComparer.OrdinalIgnoreCase);
        Tools = _tools.Values.OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyCollection<IAgentTool> Tools { get; }

    public IAgentTool? Find(string name)
        => string.IsNullOrWhiteSpace(name) ? null : _tools.GetValueOrDefault(name);
}
