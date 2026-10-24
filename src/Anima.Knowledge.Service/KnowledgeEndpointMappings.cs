
using System.Text.Json;

namespace Anima.Knowledge.Service;

internal static class KnowledgeEndpointMappings
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static object ToResponse(
        this KnowledgeNode node,
        IReadOnlyDictionary<string, int> kindIds) => new
        {
            Id = node.Id,
            Kind = node.Kind,
            KindId = kindIds.GetValueOrDefault(node.Kind),
            node.Name,
            node.Description,
            node.Content,
            node.Properties,
            node.Resolved,
            node.CreatedAt,
            node.UpdatedAt,
        };

    public static object ToResponse(
        this KnowledgeEdge edge,
        IReadOnlyDictionary<string, int> edgeTypeIds) => new
        {
            edge.Id,
            edge.Source,
            edge.Destination,
            EdgeType = edge.EdgeType,
            EdgeTypeId = edgeTypeIds.GetValueOrDefault(edge.EdgeType),
            edge.CreatedAt,
        };

    public static async Task<(Dictionary<string, int> Kinds, Dictionary<string, int> EdgeTypes)> VocabularyAsync(
        this IKnowledgeGraph graph,
        CancellationToken cancellationToken)
    {
        var kinds = await graph.GetKindsAsync(cancellationToken);
        var edgeTypes = await graph.GetEdgeTypesAsync(cancellationToken);
        return (
            kinds.ToDictionary(kind => kind.Name, kind => kind.Id, StringComparer.OrdinalIgnoreCase),
            edgeTypes.ToDictionary(type => type.Name, type => type.Id, StringComparer.OrdinalIgnoreCase));
    }

    public static KnowledgeScope ParseScope(string? scope) => scope?.ToLowerInvariant() switch
    {
        "all" => KnowledgeScope.All,
        "resolved" => KnowledgeScope.Resolved,
        _ => KnowledgeScope.Active,
    };

    public static IReadOnlyDictionary<string, string>? ParseProperties(string? props)
    {
        if (string.IsNullOrWhiteSpace(props))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(props, JsonOptions);
        }
        catch (JsonException)
        {
            throw new KnowledgeValidationException("props must be a JSON object of string values");
        }
    }

    public static EdgeDirection ParseDirection(string? direction) => direction?.ToLowerInvariant() switch
    {
        "outgoing" => EdgeDirection.Outgoing,
        "incoming" => EdgeDirection.Incoming,
        _ => EdgeDirection.Both,
    };
}
