using System.Text;
using System.Text.Json.Nodes;

namespace Anima.Cli;

internal static class KnowledgeCommands
{
    public static async Task<int> RunAsync(string[] args, string baseUrl)
    {
        if (args.Length == 0)
        {
            return Help();
        }

        using var http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        try
        {
            return args[0] switch
            {
                "kinds" => await GetAsync(http, "api/node-kinds"),
                "edge-types" => await GetAsync(http, "api/edge-types"),
                "vocabulary" => await VocabularyAsync(http, args),
                "health" => await GetAsync(http, "api/graph-health"),
                "get" => await GetAsync(http, $"api/nodes/{RequireId(args)}"),
                "search" => await SearchAsync(http, args),
                "create-node" => await CreateNodeAsync(http, args),
                "edge" => await EdgeAsync(http, args),
                "plan" => await PlanAsync(http, args),
                "api" => await RawAsync(http, args),
                _ => Help(),
            };
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 2;
        }
    }

    private static async Task<int> SearchAsync(HttpClient http, string[] args)
    {
        if (args.Length < 2)
        {
            throw new InvalidOperationException("usage: anima knowledge search <text> [--kind <kind>] [--scope <active|all|resolved>] [--limit <n>]");
        }

        var query = new List<string>
        {
            $"q={Uri.EscapeDataString(args[1])}",
            "summary=true",
            $"limit={CliJson.Option(args, "--limit") ?? "20"}",
            $"scope={Uri.EscapeDataString(CliJson.Option(args, "--scope") ?? "all")}",
        };
        if (CliJson.Option(args, "--kind") is { } kind)
        {
            query.Add($"kind={Uri.EscapeDataString(kind)}");
        }

        return await GetAsync(http, "api/search?" + string.Join("&", query));
    }

    private static async Task<int> CreateNodeAsync(HttpClient http, string[] args)
    {
        var kind = CliJson.Option(args, "--kind") ?? throw new InvalidOperationException("--kind is required");
        var name = CliJson.Option(args, "--name") ?? throw new InvalidOperationException("--name is required");
        var properties = CliJson.Option(args, "--properties");
        var body = new
        {
            kind,
            name,
            description = CliJson.Option(args, "--description") ?? string.Empty,
            content = CliJson.Option(args, "--content") ?? string.Empty,
            properties = properties is null
                ? null
                : JsonNode.Parse(properties)?.AsObject(),
        };

        return await PostAsync(http, "api/nodes", body);
    }

    private static async Task<int> EdgeAsync(HttpClient http, string[] args)
    {
        if (args.Length < 4 ||
            !long.TryParse(args[1], out var source) ||
            !long.TryParse(args[2], out var destination))
        {
            throw new InvalidOperationException("usage: anima knowledge edge <source> <destination> <edgeType>");
        }

        return await PostAsync(http, "api/edges", new { source, destination, edge_type = args[3] });
    }

    private static async Task<int> PlanAsync(HttpClient http, string[] args)
    {
        var file = CliJson.Option(args, "--file")
            ?? throw new InvalidOperationException("--file is required (memgraph-style plan JSON)");
        var json = await CliJson.ReadPayloadAsync($"@{file}");
        return await PostAsync(http, "api/plans", json);
    }

    private static async Task<int> RawAsync(HttpClient http, string[] args)
    {
        if (args.Length < 3)
        {
            throw new InvalidOperationException("usage: anima knowledge api <METHOD> <path> [json|@file]");
        }

        var method = new HttpMethod(args[1].ToUpperInvariant());
        var body = args.Length > 3 ? await CliJson.ReadPayloadAsync(args[3]) : null;
        return await SendAsync(http, method, args[2], body);
    }

    private static async Task<int> VocabularyAsync(HttpClient http, string[] args)
    {
        if (args.Length < 2)
        {
            throw new InvalidOperationException(
                "usage: anima knowledge vocabulary show | validate <pack.json>");
        }

        return args[1] switch
        {
            "show" => await GetAsync(http, "api/vocabulary"),
            "validate" => await ValidateVocabularyAsync(http, args),
            _ => throw new InvalidOperationException(
                "usage: anima knowledge vocabulary show | validate <pack.json>"),
        };
    }

    private static async Task<int> ValidateVocabularyAsync(HttpClient http, string[] args)
    {
        if (args.Length < 3)
        {
            throw new InvalidOperationException("usage: anima knowledge vocabulary validate <pack.json>");
        }

        var path = args[2];
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"vocabulary pack '{path}' does not exist");
        }

        return await PostAsync(http, "api/vocabulary/validate", await File.ReadAllTextAsync(path));
    }

    private static async Task<int> GetAsync(HttpClient http, string path)
        => await SendAsync(http, HttpMethod.Get, path, null);

    private static async Task<int> PostAsync(HttpClient http, string path, object body)
        => await SendAsync(http, HttpMethod.Post, path, CliJson.Serialize(body));

    private static async Task<int> PostAsync(HttpClient http, string path, string body)
        => await SendAsync(http, HttpMethod.Post, path, body);

    private static async Task<int> SendAsync(HttpClient http, HttpMethod method, string path, string? body)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"error {(int)response.StatusCode}: {text}");
            return 3;
        }

        if (text.Length > 0)
        {
            Console.WriteLine(text);
        }

        return 0;
    }

    private static long RequireId(string[] args)
        => args.Length >= 2 && long.TryParse(args[1], out var id)
            ? id
            : throw new InvalidOperationException("node id is required");

    private static int Help()
    {
        Console.WriteLine("""
            usage:
              anima knowledge kinds
              anima knowledge edge-types
              anima knowledge vocabulary show|validate <pack.json>
              anima knowledge health
              anima knowledge get <id>
              anima knowledge search <text> [--kind <kind>] [--scope <active|all|resolved>] [--limit <n>]
              anima knowledge create-node --kind <kind> --name <name> [--description <text>] [--content <text>] [--properties <json>]
              anima knowledge edge <source> <destination> <edgeType>
              anima knowledge plan --file <plan.json>
              anima knowledge api <METHOD> <path> [json|@file]
            """);
        return 0;
    }
}
