using System.Text.Json;
using System.Text.Json.Serialization;

namespace Anima.Cli;

internal static class CliJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static async Task<string> ReadPayloadAsync(string? inlineOrFile)
    {
        if (string.IsNullOrWhiteSpace(inlineOrFile))
        {
            return "{}";
        }

        return inlineOrFile.StartsWith('@')
            ? await File.ReadAllTextAsync(inlineOrFile[1..])
            : inlineOrFile;
    }

    public static string? Option(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.Ordinal))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    public static IReadOnlyList<string> OptionValues(string[] args, string name)
    {
        var values = new List<string>();
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.Ordinal))
            {
                values.Add(args[index + 1]);
            }
        }

        return values;
    }

    public static bool HasFlag(string[] args, string name)
        => args.Contains(name, StringComparer.Ordinal);
}
