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
        string[] terms = [.. text
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => term.Any(char.IsLetterOrDigit))];
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

        string? edgeType = null;
        var direction = query.Direction;
        if (query.EdgeType is not null)
        {
            if (!_vocabulary.TryResolveEdgeType(query.EdgeType, out var resolution))
            {
                return [];
            }

            edgeType = resolution.Definition.Name;
            direction = EffectiveDirection(resolution, direction);
        }

        var edges = new List<(long Id, long Source, long Destination, string Type, DateTimeOffset Created)>();
        await using (var command = connection.CreateCommand())
        {
            string typeFilter = edgeType is null
                ? string.Empty
                : " AND t.name = $type COLLATE NOCASE";
            command.CommandText = $"""
                SELECT e.id, e.source, e.destination, t.name, e.created_at
                FROM edge e JOIN edge_type t ON t.id = e.edge_type_id
                WHERE (e.source = $id OR e.destination = $id){typeFilter}
                ORDER BY e.id;
                """;
            command.Parameters.AddWithValue("$id", query.NodeId);
            if (edgeType is not null)
            {
                command.Parameters.AddWithValue("$type", edgeType);
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
                direction is EdgeDirection.Outgoing or EdgeDirection.Both)
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
                direction is EdgeDirection.Incoming or EdgeDirection.Both)
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
        command.CommandText = """
            SELECT id, name, description, required_props, traits, source, deprecated
            FROM node_kind WHERE active = 1 AND deprecated = 0 ORDER BY id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var kinds = new List<NodeKindDefinition>();
        while (await reader.ReadAsync(cancellationToken))
        {
            kinds.Add(new NodeKindDefinition(
                (int)reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2))
            {
                RequiredProps = DeserializeNames(reader.GetString(3)),
                Traits = DeserializeNames(reader.GetString(4)),
                Source = reader.GetString(5),
                Deprecated = reader.GetInt64(6) != 0,
            });
        }

        return kinds;
    }

    public async Task<IReadOnlyList<EdgeTypeDefinition>> GetEdgeTypesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, description, resolves, single_outgoing, source_kinds, destination_kinds,
                   pairs, inverse_name, symmetric, acyclic, traits, source, deprecated
            FROM edge_type WHERE active = 1 AND deprecated = 0 ORDER BY id;
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
                reader.GetInt64(4) != 0)
            {
                Pairs = DeserializePairs(reader.GetString(7)),
                InverseName = await reader.IsDBNullAsync(8, cancellationToken) ? null : reader.GetString(8),
                Symmetric = reader.GetInt64(9) != 0,
                Acyclic = reader.GetInt64(10) != 0,
                Traits = DeserializeNames(reader.GetString(11)),
                Source = reader.GetString(12),
                Deprecated = reader.GetInt64(13) != 0,
            });
        }

        return edgeTypes;
    }

    public async Task<GraphHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        int nodeCount = (int)ScalarLong(connection, "SELECT COUNT(*) FROM node;");
        int edgeCount = (int)ScalarLong(connection, "SELECT COUNT(*) FROM edge;");
        int isolated = (int)ScalarLong(connection, """
            SELECT COUNT(*) FROM node n
            WHERE NOT EXISTS (SELECT 1 FROM edge e WHERE e.source = n.id OR e.destination = n.id);
            """);
        int duplicates = (int)ScalarLong(connection, """
            SELECT COUNT(*) FROM (
                SELECT source, destination, edge_type_id
                FROM edge GROUP BY source, destination, edge_type_id HAVING COUNT(*) > 1
            );
            """);
        int unanchored = await CountUnanchoredAsync(connection, cancellationToken);

        return new GraphHealth(nodeCount, edgeCount, isolated, unanchored, duplicates);
    }

    public async Task<GraphSnapshot> GetGraphAsync(
        GraphSnapshotQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        int maxNodes = query.EffectiveMaxNodes;
        int maxEdges = query.EffectiveMaxEdges;
        string direction = query.Newest ? "DESC" : "ASC";
        string sinceFilter = query.SinceId is null ? string.Empty : " WHERE id > $sinceId";
        string nodeFilter = query.SinceId is null ? string.Empty : " WHERE n.id > $sinceId";
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var nodes = new List<GraphSnapshotNode>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT n.id, k.name, n.name, n.description,
                       CASE WHEN {ResolvedExists} THEN 1 ELSE 0 END AS resolved
                FROM node n JOIN node_kind k ON k.id = n.kind_id
                {nodeFilter}
                ORDER BY n.id {direction}
                LIMIT $maxNodes;
                """;
            command.Parameters.AddWithValue("$maxNodes", maxNodes);
            if (query.SinceId is { } sinceId)
            {
                command.Parameters.AddWithValue("$sinceId", sinceId);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                nodes.Add(new GraphSnapshotNode(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt64(4) != 0));
            }
        }

        // Both endpoints must be inside the node window, so the subqueries repeat the
        // node filter, ordering and cap instead of materializing an id list client-side.
        var edges = new List<GraphSnapshotEdge>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT e.id, e.source, e.destination, t.name
                FROM edge e JOIN edge_type t ON t.id = e.edge_type_id
                WHERE e.source IN (SELECT id FROM node{sinceFilter} ORDER BY id {direction} LIMIT $maxNodes)
                  AND e.destination IN (SELECT id FROM node{sinceFilter} ORDER BY id {direction} LIMIT $maxNodes)
                ORDER BY e.id {direction}
                LIMIT $maxEdges;
                """;
            command.Parameters.AddWithValue("$maxNodes", maxNodes);
            command.Parameters.AddWithValue("$maxEdges", maxEdges);
            if (query.SinceId is { } sinceId)
            {
                command.Parameters.AddWithValue("$sinceId", sinceId);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                edges.Add(new GraphSnapshotEdge(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetString(3)));
            }
        }

        int nodeCount;
        int edgeCount;
        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.Transaction = transaction;
            countCommand.CommandText = "SELECT (SELECT COUNT(*) FROM node), (SELECT COUNT(*) FROM edge);";
            await using var reader = await countCommand.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            nodeCount = (int)reader.GetInt64(0);
            edgeCount = (int)reader.GetInt64(1);
        }

        await transaction.CommitAsync(cancellationToken);
        return new GraphSnapshot(
            nodes,
            edges,
            nodeCount,
            edgeCount,
            nodeCount > nodes.Count || edgeCount > edges.Count);
    }

    private async Task<NodePage> QueryAsync(
        NodeQuery query,
        string[]? searchText,
        CancellationToken cancellationToken)
    {
        int limit = Math.Clamp(query.Limit <= 0 ? 100 : query.Limit, 1, _knowledgeOptions.MaxPageSize);
        int offset = Math.Max(query.Offset, 0);
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
        string contentColumn = query.SummaryOnly ? "''" : "n.content";
        string rankColumn = useFts ? ", bm25(node_fts) AS rank" : string.Empty;
        string from = useFts
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
            for (int index = 0; index < searchText.Length; index++)
            {
                string parameter = $"$like{index}";
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
            int index = 0;
            foreach (var pair in properties)
            {
                string alias = $"je{index}";
                string keyParameter = $"$pk{index}";
                string valueParameter = $"$pv{index}";
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

        string whereSql = " WHERE " + string.Join(" AND ", conditions);
        string selectSql =
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
        int total = Convert.ToInt32(
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

        string? viaType = CanonicalEdgeType(via);
        var hop = new HashSet<long>();
        foreach (var edge in edges)
        {
            if (viaType is not null &&
                !string.Equals(edge.Type, viaType, StringComparison.OrdinalIgnoreCase))
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
        if (descend is not null && _vocabulary.TryResolveEdgeType(descend, out var descendResolution))
        {
            // descend narrows away from the hop nodes. Children are always the edge's
            // source side (they point at their parent), so naming the relation by its
            // inverse changes which edges match, never the walk direction.
            string descendType = descendResolution.Definition.Name;
            var queue = new Queue<long>(hop);
            while (queue.Count > 0)
            {
                long current = queue.Dequeue();
                foreach (var edge in edges)
                {
                    if (!string.Equals(edge.Type, descendType, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (edge.Destination != current || !reachable.Add(edge.Source))
                    {
                        continue;
                    }

                    queue.Enqueue(edge.Source);
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
        foreach (long id in reachable)
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
        // One recursive walk instead of loading every plannable node and every
        // hierarchy/anchor edge into memory: a node is anchored when an ancestor
        // reached through hierarchy edges carries an anchor edge.
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE
                plannable(id) AS (
                    SELECT n.id
                    FROM node n JOIN node_kind k ON k.id = n.kind_id
                    WHERE k.name IN (SELECT value FROM json_each($plannable))
                ),
                ancestor(node, current) AS (
                    SELECT id, id FROM plannable
                    UNION
                    SELECT ancestor.node, e.destination
                    FROM ancestor
                    JOIN edge e ON e.source = ancestor.current
                    JOIN edge_type t ON t.id = e.edge_type_id
                    WHERE t.name IN (SELECT value FROM json_each($hierarchy))
                ),
                anchored(id) AS (
                    SELECT DISTINCT ancestor.node
                    FROM ancestor
                    JOIN edge e ON e.source = ancestor.current
                    JOIN edge_type t ON t.id = e.edge_type_id
                    WHERE t.name IN (SELECT value FROM json_each($anchor))
                )
            SELECT (SELECT COUNT(*) FROM plannable) - (SELECT COUNT(*) FROM anchored);
            """;
        command.Parameters.AddWithValue("$plannable", _plannableKindsJson);
        command.Parameters.AddWithValue("$hierarchy", _hierarchyEdgesJson);
        command.Parameters.AddWithValue("$anchor", _anchorEdgesJson);
        object? unanchored = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(unanchored, CultureInfo.InvariantCulture);
    }
}
