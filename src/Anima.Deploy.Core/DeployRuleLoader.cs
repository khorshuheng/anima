using System.Text.Json;

namespace Anima.Deploy;

public static class DeployRuleLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public static IReadOnlyList<DeployRule> Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new DeployValidationException($"deploy rules file '{path}' does not exist");
        }

        var rules = JsonSerializer.Deserialize<List<DeployRule>>(File.ReadAllText(path), JsonOptions);
        return rules ?? [];
    }
}
