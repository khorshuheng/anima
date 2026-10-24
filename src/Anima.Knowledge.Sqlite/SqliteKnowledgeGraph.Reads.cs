using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Anima.Knowledge.Sqlite;

public sealed partial class SqliteKnowledgeGraph
{
    public async Task<KnowledgeNode?> GetNodeAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT n.id, k.name, n.name, n.description, n.content, n.properties,
                   n.created_at, n.updated_at,
                   CASE WHEN {ResolvedExists} THEN 1 ELSE 0 END AS resolved
            FROM node n JOIN node_kind k ON k.id = n.kind_id
            WHERE n.id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadNode(reader, includeContent: true)
            : null;
    }

    public async Task<NodePage> QueryNodesAsync(
        NodeQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await QueryAsync(query, searchText: null, cancellationToken);
    }

    public async Task<NodePage> SearchAsync(
        string text,
        NodeQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var terms = text
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => term.Any(char.IsLetterOrDigit))
            .ToArray();
        if (terms.Length == 0)
        {
            return new NodePage([], 0, 0, 0);
        }

        return await QueryAsync(query ?? new NodeQuery(), terms, cancellationToken);
    }

    public async Task<IReadOnlyList<NeighborResult>> GetNeighborsAsync(
        NeighborQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsureNodeAsync(connection, query.NodeId, cancellationToken);

        var edges = new List<(long Id, long Source, long Destination, string Type, DateTimeOffset Created)>();
        await using (var command = connection.CreateCommand())
        {
            var typeFilter = query.EdgeType is null
                ? string.Empty
                : " AND t.name = $type COLLATE NOCASE";
            command.CommandText = $"""
                SELECT e.id, e.source, e.destination, t.name, e.created_at
                FROM edge e JOIN edge_type t ON t.id = e.edge_type_id
                WHERE (e.source = $id OR e.destination = $id){typeFilter}
                ORDER BY e.id;
                """;
            command.Parameters.AddWithValue("$id", query.NodeId);
            if (query.EdgeType is not null)
            {
                command.Parameters.AddWithValue("$type", query.EdgeType);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                edges.Add((
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetString(3),
                    ParseTime(reader.GetString(4))));
            }
        }

        var results = new List<NeighborResult>();
        foreach (var edge in edges)
        {
            if (edge.Source == query.NodeId &&
                query.Direction is EdgeDirection.Outgoing or EdgeDirection.Both)
            {
                var node = await LoadNodeAsync(connection, null, edge.Destination, cancellationToken);
                results.Add(new NeighborResult(
                    node,
                    new KnowledgeEdge
                    {
                        Id = edge.Id,
                        Source = edge.Source,
                        Destination = edge.Destination,
                        EdgeType = edge.Type,
                        CreatedAt = edge.Created,
                    },
                    EdgeDirection.Outgoing));
            }

            if (edge.Destination == query.NodeId &&
                query.Direction is EdgeDirection.Incoming or EdgeDirection.Both)
            {
                var node = await LoadNodeAsync(connection, null, edge.Source, cancellationToken);
                results.Add(new NeighborResult(
                    node,
                    new KnowledgeEdge
                    {
                        Id = edge.Id,
                        Source = edge.Source,
                        Destination = edge.Destination,
                        EdgeType = edge.Type,
                        CreatedAt = edge.Created,
                    },
                    EdgeDirection.Incoming));
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<NodeKindDefinition>> GetKindsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, description FROM node_kind ORDER BY id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var kinds = new List<NodeKindDefinition>();
        while (await reader.ReadAsync(cancellationToken))
        {
            kinds.Add(new NodeKindDefinition(
                (int)reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2)));
        }

        return kinds;
    }

    public async Task<IReadOnlyList<EdgeTypeDefinition>> GetEdgeTypesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, description, resolves, single_outgoing, source_kinds, destination_kinds
            FROM edge_type ORDER BY id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var edgeTypes = new List<EdgeTypeDefinition>();
        while (await reader.ReadAsync(cancellationToken))
        {
            edgeTypes.Add(new EdgeTypeDefinition(
                (int)reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                ParseKinds(reader.GetString(5)),
                ParseKinds(reader.GetString(6)),
                reader.GetInt64(3) != 0,
                reader.GetInt64(4) != 0));
        }

        return edgeTypes;
    }

    public async Task<GraphHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var nodeCount = (int)ScalarLong(connection, "SELECT COUNT(*) FROM node;");
        var edgeCount = (int)ScalarLong(connection, "SELECT COUNT(*) FROM edge;");
        var isolated = (int)ScalarLong(connection, """
            SELECT COUNT(*) FROM node n
            WHERE NOT EXISTS (SELECT 1 FROM edge e WHERE e.source = n.id OR e.destination = n.id);
            """);
        var duplicates = (int)ScalarLong(connection, """
            SELECT COUNT(*) FROM (
                SELECT source, destination, edge_type_id
                FROM edge GROUP BY source, destination, edge_type_id HAVING COUNT(*) > 1
            );
            """);
        var unanchored = await CountUnanchoredAsync(connection, cancellationToken);

        return new GraphHealth(nodeCount, edgeCount, isolated, unanchored, duplicates);
    }

    private async Task<NodePage> QueryAsync(
        NodeQuery query,
        string[]? searchText,
        CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(query.Limit <= 0 ? 100 : query.Limit, 1, _knowledgeOptions.MaxPageSize);
        var offset = Math.Max(query.Offset, 0);
        await using var connection = await OpenConnectionAsync(cancellationToken);

        if (query.Within is { } within)
        {
            await EnsureNodeAsync(connection, within, cancellationToken);
            await FillReachableAsync(connection, within, query.Via, query.Descend, cancellationToken);
        }

        if (searchText is not null)
        {
            try
            {
                return await ExecuteQueryAsync(
                    connection,
                    query,
                    searchText,
                    useFts: true,
                    limit,
                    offset,
                    cancellationToken);
            }
            catch (SqliteException)
            {
                // Malformed FTS terms fall back to a substring scan.
                return await ExecuteQueryAsync(
                    connection,
                    query,
                    searchText,
                    useFts: false,
                    limit,
                    offset,
                    cancellationToken);
            }
        }

        return await ExecuteQueryAsync(
            connection,
            query,
            searchText,
            useFts: false,
            limit,
            offset,
            cancellationToken);
    }

    private async Task<NodePage> ExecuteQueryAsync(
        SqliteConnection connection,
        NodeQuery query,
        string[]? searchText,
        bool useFts,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        var parameters = new List<(string Name, object Value)>();
        var contentColumn = query.SummaryOnly ? "''" : "n.content";
        var rankColumn = useFts ? ", bm25(node_fts) AS rank" : string.Empty;
        var from = useFts
            ? " FROM node_fts JOIN node n ON n.id = node_fts.rowid JOIN node_kind k ON k.id = n.kind_id"
            : " FROM node n JOIN node_kind k ON k.id = n.kind_id";

        var conditions = new List<string>();
        if (useFts)
        {
            conditions.Add("node_fts MATCH $match");
            parameters.Add(("$match", BuildFtsQuery(searchText!)));
        }
        else if (searchText is not null)
        {
            var parts = new List<string>();
            for (var index = 0; index < searchText.Length; index++)
            {
                var parameter = $"$like{index}";
                parameters.Add((parameter, $"%{searchText[index]}%"));
                parts.Add($"(n.name LIKE {parameter} OR n.description LIKE {parameter} " +
                          $"OR n.content LIKE {parameter} OR n.properties LIKE {parameter})");
            }

            conditions.Add("(" + string.Join(" OR ", parts) + ")");
        }

        if (!string.IsNullOrWhiteSpace(query.Kind))
        {
            conditions.Add("k.name = $kind COLLATE NOCASE");
            parameters.Add(("$kind", query.Kind.Trim()));
        }

        if (query.Properties is { Count: > 0 } properties)
        {
            var index = 0;
            foreach (var pair in properties)
            {
                var alias = $"je{index}";
                var keyParameter = $"$pk{index}";
                var valueParameter = $"$pv{index}";
                conditions.Add(
                    $"EXISTS (SELECT 1 FROM json_each(n.properties) AS {alias} " +
                    $"WHERE {alias}.key = {keyParameter} AND {alias}.value = {valueParameter})");
                parameters.Add((keyParameter, pair.Key));
                parameters.Add((valueParameter, pair.Value));
                index++;
            }
        }

        conditions.Add(query.Scope switch
        {
            KnowledgeScope.Active => $"NOT ({ResolvedExists})",
            KnowledgeScope.Resolved => ResolvedExists,
            _ => "1 = 1",
        });

        if (query.Within is not null)
        {
            conditions.Add("n.id IN (SELECT id FROM reachable)");
        }

        var whereSql = " WHERE " + string.Join(" AND ", conditions);
        var selectSql =
            $"SELECT n.id, k.name, n.name, n.description, {contentColumn} AS content, n.properties, " +
            $"n.created_at, n.updated_at, CASE WHEN {ResolvedExists} THEN 1 ELSE 0 END AS resolved" +
            $"{rankColumn}{from}{whereSql}";
        selectSql += useFts ? " ORDER BY rank ASC, n.id ASC" : " ORDER BY n.id ASC";
        selectSql += " LIMIT $limit OFFSET $offset;";

        await using var command = connection.CreateCommand();
        command.CommandText = selectSql;
        ApplyParameters(command);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);

        var items = new List<KnowledgeNode>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(ReadNode(reader, includeContent: !query.SummaryOnly));
            }
        }

        // COUNT(*) OVER() is lost when the page is empty; run a dedicated count so
        // total stays correct even when offset >= total.
        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = $"SELECT COUNT(*){from}{whereSql};";
        ApplyParameters(countCommand);
        var total = Convert.ToInt32(
            await countCommand.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);

        return new NodePage(items, total, limit, offset);

        void ApplyParameters(SqliteCommand target)
        {
            foreach (var parameter in parameters)
            {
                target.Parameters.AddWithValue(parameter.Name, parameter.Value);
            }
        }
    }

    private static string BuildFtsQuery(string[] terms)
        => string.Join(
            " OR ",
            terms.Select(term => "\"" + term.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"*"));

    private async Task FillReachableAsync(
        SqliteConnection connection,
        long within,
        string? via,
        string? descend,
        CancellationToken cancellationToken)
    {
        var edges = new List<(long Source, long Destination, string Type)>();
        await using (var edgeCommand = connection.CreateCommand())
        {
            edgeCommand.CommandText = """
                SELECT e.source, e.destination, t.name
                FROM edge e JOIN edge_type t ON t.id = e.edge_type_id;
                """;
            await using var reader = await edgeCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                edges.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2)));
            }
        }

        var hop = new HashSet<long>();
        foreach (var edge in edges)
        {
            if (via is not null &&
                !string.Equals(edge.Type, via, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (edge.Source == within)
            {
                hop.Add(edge.Destination);
            }

            if (edge.Destination == within)
            {
                hop.Add(edge.Source);
            }
        }

        var reachable = new HashSet<long>(hop);
        if (descend is not null)
        {
            var queue = new Queue<long>(hop);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var edge in edges)
                {
                    if (!string.Equals(edge.Type, descend, StringComparison.OrdinalIgnoreCase) ||
                        edge.Destination != current)
                    {
                        continue;
                    }

                    if (reachable.Add(edge.Source))
                    {
                        queue.Enqueue(edge.Source);
                    }
                }
            }
        }

        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                CREATE TEMP TABLE IF NOT EXISTS reachable(id INTEGER PRIMARY KEY);
                DELETE FROM reachable;
                """;
            await setup.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT OR IGNORE INTO reachable(id) VALUES($id);";
        var parameter = insert.Parameters.AddWithValue("$id", 0);
        foreach (var id in reachable)
        {
            parameter.Value = id;
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task EnsureNodeAsync(
        SqliteConnection connection,
        long id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM node WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw new KnowledgeNotFoundException($"node {id} does not exist");
        }
    }

    private async Task<int> CountUnanchoredAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var nodes = new List<(long Id, string Kind)>();
        await using (var nodeCommand = connection.CreateCommand())
        {
            nodeCommand.CommandText = """
                SELECT n.id, k.name FROM node n JOIN node_kind k ON k.id = n.kind_id
                WHERE k.name IN ('task', 'goal');
                """;
            await using var reader = await nodeCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                nodes.Add((reader.GetInt64(0), reader.GetString(1)));
            }
        }

        var edges = new List<(long Source, long Destination, string Type)>();
        await using (var edgeCommand = connection.CreateCommand())
        {
            edgeCommand.CommandText = """
                SELECT e.source, e.destination, t.name
                FROM edge e JOIN edge_type t ON t.id = e.edge_type_id
                WHERE t.name IN ('part_of', 'belongs_to');
                """;
            await using var reader = await edgeCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                edges.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2)));
            }
        }

        var kinds = nodes.ToDictionary(node => node.Id, node => node.Kind);
        var parent = edges
            .Where(edge => edge.Type == "part_of")
            .GroupBy(edge => edge.Source)
            .ToDictionary(group => group.Key, group => group.First().Destination);
        var anchored = edges
            .Where(edge => edge.Type == "belongs_to")
            .Select(edge => edge.Source)
            .ToHashSet();

        return nodes.Count(node => !IsAnchored(node.Id));

        bool IsAnchored(long id)
        {
            var seen = new HashSet<long>();
            var current = id;
            while (seen.Add(current))
            {
                if (kinds.TryGetValue(current, out var kind) &&
                    kind == KnowledgeKinds.Goal &&
                    anchored.Contains(current))
                {
                    return true;
                }

                if (!parent.TryGetValue(current, out current))
                {
                    return false;
                }
            }

            return false;
        }
    }
}
