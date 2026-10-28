using System.Globalization;
using System.Text.Json;
using DbUp;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Anima.Knowledge.Sqlite;

/// <summary>
/// SQLite-backed knowledge graph. Schema, FTS5 index and seed vocabulary live in
/// embedded migrations; writes are serialized and transactional, reads use WAL.
/// This is the production store; the in-memory implementation is for tests.
/// </summary>
public sealed partial class SqliteKnowledgeGraph : IKnowledgeGraph, IDisposable
{
    private const string ResolvedExists =
        "EXISTS (SELECT 1 FROM edge e JOIN edge_type et ON et.id = e.edge_type_id " +
        "WHERE e.destination = n.id AND et.resolves = 1)";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SqliteKnowledgeOptions _sqliteOptions;
    private readonly KnowledgeOptions _knowledgeOptions;
    private readonly ILogger<SqliteKnowledgeGraph> _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly string _connectionString;
    private readonly string _databasePath;
    private readonly KnowledgeVocabularyCatalog _vocabulary;
    private readonly HashSet<string> _plannableKinds;
    private readonly HashSet<string> _hierarchyEdges;
    private readonly HashSet<string> _anchorEdges;
    private readonly string _plannableKindsJson;
    private readonly string _hierarchyEdgesJson;
    private readonly string _anchorEdgesJson;

    public SqliteKnowledgeGraph(
        IOptions<SqliteKnowledgeOptions> sqliteOptions,
        IOptions<KnowledgeOptions> knowledgeOptions,
        ILogger<SqliteKnowledgeGraph> logger,
        KnowledgeVocabularyCatalog vocabulary)
    {
        ArgumentNullException.ThrowIfNull(sqliteOptions);
        ArgumentNullException.ThrowIfNull(knowledgeOptions);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(vocabulary);
        _sqliteOptions = sqliteOptions.Value;
        _knowledgeOptions = knowledgeOptions.Value;
        _logger = logger;
        _vocabulary = vocabulary;

        // Trait sets are passed to SQL as JSON arrays and expanded with json_each,
        // so vocabulary names never appear in a query string.
        _plannableKinds = vocabulary.NodeKindsWithTrait(KnowledgeTraits.Plannable)
            .Select(kind => kind.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _plannableKindsJson = NamesJson(_plannableKinds);
        _hierarchyEdges = vocabulary.EdgeTypesWithTrait(KnowledgeTraits.Hierarchy)
            .Select(edge => edge.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _anchorEdges = vocabulary.EdgeTypesWithTrait(KnowledgeTraits.Anchor)
            .Select(edge => edge.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _hierarchyEdgesJson = NamesJson(_hierarchyEdges);
        _anchorEdgesJson = NamesJson(_anchorEdges);

        _databasePath = string.IsNullOrWhiteSpace(_sqliteOptions.DatabasePath)
            ? SqliteKnowledgeOptions.ResolveDefaultPath()
            : Path.GetFullPath(_sqliteOptions.DatabasePath);

        string? directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true,
            DefaultTimeout = Math.Max(1, _sqliteOptions.BusyTimeoutSeconds),
        }.ToString();

        Initialize();
    }

    public string DatabasePath => _databasePath;

    public async Task<KnowledgeNode> CreateNodeAsync(
        NodeDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return await WriteAsync(async (connection, transaction, token) =>
        {
            long kindId = await ResolveKindAsync(connection, transaction, draft.Kind, token);
            if (_vocabulary.TryGetKind(draft.Kind, out var kindDefinition))
            {
                KnowledgePropertyRules.EnsureRequired(kindDefinition, draft.Properties);
            }

            long id = await InsertNodeAsync(
                connection,
                transaction,
                kindId,
                draft.Name,
                draft.Description,
                draft.Content,
                draft.Properties,
                token);
            return await LoadNodeAsync(connection, transaction, id, token);
        }, cancellationToken);
    }

    public async Task<KnowledgeNode> UpdateNodeAsync(
        long id,
        NodePatch patch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (patch.Name is null && patch.Description is null && patch.Content is null && patch.Properties is null)
        {
            throw new KnowledgeValidationException(
                "patch requires at least one of name, description, content or properties");
        }

        return await WriteAsync(async (connection, transaction, token) =>
        {
            var existing = await LoadNodeAsync(connection, transaction, id, token);
            string name = patch.Name is null ? existing.Name : RequireName(patch.Name);
            string description = patch.Description ?? existing.Description;
            string content = patch.Content ?? existing.Content;
            var properties = patch.Properties ?? existing.Properties;
            if (patch.Properties is not null && _vocabulary.TryGetKind(existing.Kind, out var kindDefinition))
            {
                KnowledgePropertyRules.EnsureRequired(kindDefinition, properties);
            }

            string now = Now();

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE node
                SET name = $name, description = $description, content = $content,
                    properties = $properties, updated_at = $updated
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$description", description);
            command.Parameters.AddWithValue("$content", content);
            command.Parameters.AddWithValue("$properties", SerializeProperties(properties));
            command.Parameters.AddWithValue("$updated", now);
            await command.ExecuteNonQueryAsync(token);

            return await LoadNodeAsync(connection, transaction, id, token);
        }, cancellationToken);
    }

    public async Task<bool> DeleteNodeAsync(long id, CancellationToken cancellationToken = default)
    {
        return await WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM node WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            return await command.ExecuteNonQueryAsync(token) > 0;
        }, cancellationToken);
    }

    public async Task<KnowledgeEdge> AddEdgeAsync(
        EdgeDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return await WriteAsync(
            (connection, transaction, token) => AddEdgeCoreAsync(
                connection,
                transaction,
                draft.Source,
                draft.Destination,
                draft.EdgeType,
                token),
            cancellationToken);
    }

    public async Task<bool> RemoveEdgeAsync(
        long source,
        long destination,
        string edgeType,
        CancellationToken cancellationToken = default)
    {
        Require(edgeType, "edgeType");
        return await WriteAsync(async (connection, transaction, token) =>
        {
            var definition = await LoadEdgeTypeAsync(connection, transaction, edgeType, token);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM edge
                WHERE source = $source AND destination = $destination AND edge_type_id = $type;
                """;
            command.Parameters.AddWithValue("$source", source);
            command.Parameters.AddWithValue("$destination", destination);
            command.Parameters.AddWithValue("$type", definition.Id);
            return await command.ExecuteNonQueryAsync(token) > 0;
        }, cancellationToken);
    }

    public async Task<PlanResult> CreatePlanAsync(
        PlanDraft plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var tasks = plan.Tasks ?? [];
        KnowledgePlanValidator.Validate(tasks);

        return await WriteAsync(async (connection, transaction, token) =>
        {
            KnowledgeNode goal;
            if (plan.GoalId is { } goalId)
            {
                goal = await LoadNodeAsync(connection, transaction, goalId, token);
                if (goal.Kind != KnowledgeKinds.Goal)
                {
                    throw new KnowledgeValidationException(
                        $"node {goalId} is of kind '{goal.Kind}', expected goal");
                }
            }
            else
            {
                long goalKindId = await ResolveKindAsync(connection, transaction, KnowledgeKinds.Goal, token);
                long goalNodeId = await InsertNodeAsync(
                    connection,
                    transaction,
                    goalKindId,
                    plan.GoalName,
                    plan.GoalDescription,
                    plan.GoalContent,
                    null,
                    token);
                goal = await LoadNodeAsync(connection, transaction, goalNodeId, token);

                if (plan.Anchor is { } anchor)
                {
                    await AddEdgeCoreAsync(
                        connection,
                        transaction,
                        goal.Id,
                        anchor.NodeId,
                        anchor.EdgeType,
                        token);
                }
            }

            if (plan.GoalId is not null && plan.Anchor is { } existingAnchor)
            {
                await EnsureAnchorAsync(connection, transaction, goal.Id, existingAnchor, token);
            }

            var created = new Dictionary<string, KnowledgeNode>(StringComparer.Ordinal);
            foreach (var task in tasks)
            {
                long kindId = await ResolveKindAsync(connection, transaction, KnowledgeKinds.Task, token);
                long taskId = await InsertNodeAsync(
                    connection,
                    transaction,
                    kindId,
                    task.Name,
                    task.Description,
                    task.Content,
                    task.Properties,
                    token);
                created[task.Key] = await LoadNodeAsync(connection, transaction, taskId, token);
            }

            foreach (var task in tasks)
            {
                var node = created[task.Key];
                long parentId = task.Parent is { } parentKey ? created[parentKey].Id : goal.Id;
                await AddEdgeCoreAsync(
                    connection,
                    transaction,
                    node.Id,
                    parentId,
                    KnowledgeEdgeTypes.PartOf,
                    token);

                foreach (string dependency in task.DependsOn ?? [])
                {
                    await AddEdgeCoreAsync(
                        connection,
                        transaction,
                        node.Id,
                        created[dependency].Id,
                        KnowledgeEdgeTypes.DependsOn,
                        token);
                }

                foreach (long affect in task.Affects ?? [])
                {
                    await AddEdgeCoreAsync(
                        connection,
                        transaction,
                        node.Id,
                        affect,
                        KnowledgeEdgeTypes.Affects,
                        token);
                }
            }

            return new PlanResult(
                goal,
                [.. tasks.Select(task => new PlanTaskResult(
                    task.Key,
                    created[task.Key],
                    task.DependsOn ?? []))]);
        }, cancellationToken);
    }

    public async Task<KnowledgeNode> CreateTaskAsync(
        TaskDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return await WriteAsync(async (connection, transaction, token) =>
        {
            if (draft.Parent is not { } parentId)
            {
                throw new KnowledgeValidationException("a task requires a parent");
            }

            var parent = await LoadNodeAsync(connection, transaction, parentId, token);
            if (!_plannableKinds.Contains(parent.Kind))
            {
                throw new KnowledgeValidationException(
                    $"task parent must be a plannable kind, got '{parent.Kind}'");
            }

            if (!await IsAnchoredAsync(connection, transaction, parentId, token))
            {
                throw new KnowledgeValidationException(
                    $"parent references node {parentId}, which is not anchored");
            }

            long kindId = await ResolveKindAsync(connection, transaction, KnowledgeKinds.Task, token);
            long nodeId = await InsertNodeAsync(
                connection,
                transaction,
                kindId,
                draft.Name,
                draft.Description,
                draft.Content,
                null,
                token);
            await AddEdgeCoreAsync(
                connection,
                transaction,
                nodeId,
                parentId,
                KnowledgeEdgeTypes.PartOf,
                token);

            foreach (long dependency in draft.DependsOn ?? [])
            {
                await AddEdgeCoreAsync(
                    connection,
                    transaction,
                    nodeId,
                    dependency,
                    KnowledgeEdgeTypes.DependsOn,
                    token);
            }

            foreach (long affect in draft.Affects ?? [])
            {
                await AddEdgeCoreAsync(
                    connection,
                    transaction,
                    nodeId,
                    affect,
                    KnowledgeEdgeTypes.Affects,
                    token);
            }

            return await LoadNodeAsync(connection, transaction, nodeId, token);
        }, cancellationToken);
    }

    public async Task<KnowledgeNode> PatchTaskAsync(
        long id,
        TaskPatch patch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (patch.Name is null && patch.Description is null && patch.Content is null)
        {
            throw new KnowledgeValidationException(
                "patch requires at least one of name, description or content");
        }

        return await WriteAsync(async (connection, transaction, token) =>
        {
            var node = await LoadNodeAsync(connection, transaction, id, token);
            if (node.Kind != KnowledgeKinds.Task)
            {
                throw new KnowledgeValidationException(
                    $"node {id} is of kind '{node.Kind}', expected task");
            }

            string name = patch.Name is null ? node.Name : RequireName(patch.Name);
            string description = patch.Description ?? node.Description;
            string content = patch.Content ?? node.Content;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE node SET name = $name, description = $description, content = $content,
                    updated_at = $updated WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$description", description);
            command.Parameters.AddWithValue("$content", content);
            command.Parameters.AddWithValue("$updated", Now());
            await command.ExecuteNonQueryAsync(token);
            return await LoadNodeAsync(connection, transaction, id, token);
        }, cancellationToken);
    }

    private void Initialize()
    {
        Migrate();

        using var connection = OpenConnection();
        if (_sqliteOptions.EnableWal)
        {
            Execute(connection, "PRAGMA journal_mode = WAL;");
        }

        EnsureSchemaShape(connection);

        Seed(connection);
        _logger.LogInformation(
            "SQLite knowledge graph ready at {DatabasePath} ({NodeCount} nodes).",
            _databasePath,
            ScalarLong(connection, "SELECT COUNT(*) FROM node;"));
    }

    /// <summary>
    /// DbUp journals applied scripts by name only, so editing a script that already
    /// ran is skipped rather than re-applied. During pre-release schema churn that
    /// leaves a database whose shape no longer matches the code; fail at startup with
    /// a reset instruction instead of a SQL error on the first request.
    /// </summary>
    private void EnsureSchemaShape(SqliteConnection connection)
    {
        string[] missing = [.. SchemaContract.Columns
            .SelectMany(group => group.Value
                .Where(column => !ColumnNames(connection, group.Key).Contains(column))
                .Select(column => $"{group.Key}.{column}"))];
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"SQLite knowledge database '{_databasePath}' predates the current schema (missing {string.Join(", ", missing)}). " +
                "Delete the database file and restart; the schema is recreated by the migration scripts. See docs/deployment.md.");
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only a SchemaContract table name is interpolated; no user input reaches the query.")]
    private static HashSet<string> ColumnNames(SqliteConnection connection, string table)
    {
        SchemaContract.EnsureKnownTable(table);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        ConfigureConnection(connection);
        return connection;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        ConfigureConnection(connection);
        return connection;
    }

    private void ConfigureConnection(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = {Math.Max(1, _sqliteOptions.BusyTimeoutSeconds * 1000)};
            """;
        command.ExecuteNonQuery();
    }

    private async Task<T> WriteAsync<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var result = await action(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        finally
        {
            _writeGate.Release();
        }
    }


    private async Task<KnowledgeEdge> AddEdgeCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long sourceId,
        long destinationId,
        string edgeType,
        CancellationToken cancellationToken)
    {
        var source = await LoadNodeAsync(connection, transaction, sourceId, cancellationToken);
        var destination = await LoadNodeAsync(connection, transaction, destinationId, cancellationToken);
        var definition = await LoadEdgeTypeAsync(connection, transaction, edgeType, cancellationToken);

        if (!_vocabulary.AllowsPair(definition.Name, source.Kind, destination.Kind))
        {
            throw new KnowledgeValidationException(
                $"edge type '{definition.Name}' does not allow {source.Kind} -> {destination.Kind}; " +
                $"allowed pairs: {DescribePairs(definition)}");
        }

        if (definition.Acyclic && await ReachesAsync(connection, transaction, definition.Name, destination.Id, source.Id, cancellationToken))
        {
            throw new KnowledgeValidationException(
                $"edge type '{definition.Name}' is acyclic, and adding {source.Id} -> {destination.Id} would close a cycle");
        }

        var existing = await FindEdgeAsync(connection, transaction, sourceId, destinationId, definition, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        // A symmetric relation is undirected: writing the mirror would store the same
        // fact twice, and reads would then report the same neighbour twice.
        if (definition.Symmetric)
        {
            var mirrored = await FindEdgeAsync(connection, transaction, destinationId, sourceId, definition, cancellationToken);
            if (mirrored is not null)
            {
                return mirrored;
            }
        }

        if (definition.SingleOutgoing)
        {
            await using var countCommand = connection.CreateCommand();
            countCommand.Transaction = transaction;
            countCommand.CommandText = """
                SELECT COUNT(*) FROM edge WHERE source = $source AND edge_type_id = $type;
                """;
            countCommand.Parameters.AddWithValue("$source", sourceId);
            countCommand.Parameters.AddWithValue("$type", definition.Id);
            long count = Convert.ToInt64(
                await countCommand.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
            if (count > 0)
            {
                throw new KnowledgeValidationException(
                    $"edge type '{definition.Name}' allows one outgoing edge, and node {sourceId} already has one");
            }
        }

        string createdAt = Now();
        await using var insertCommand = connection.CreateCommand();
        insertCommand.Transaction = transaction;
        insertCommand.CommandText = """
            INSERT INTO edge(source, destination, edge_type_id, created_at)
            VALUES($source, $destination, $type, $created)
            RETURNING id;
            """;
        insertCommand.Parameters.AddWithValue("$source", sourceId);
        insertCommand.Parameters.AddWithValue("$destination", destinationId);
        insertCommand.Parameters.AddWithValue("$type", definition.Id);
        insertCommand.Parameters.AddWithValue("$created", createdAt);
        long edgeId = Convert.ToInt64(
            await insertCommand.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);

        return new KnowledgeEdge
        {
            Id = edgeId,
            Source = sourceId,
            Destination = destinationId,
            EdgeType = definition.Name,
            CreatedAt = ParseTime(createdAt),
        };
    }

    private async Task<long> InsertNodeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long kindId,
        string name,
        string? description,
        string? content,
        IReadOnlyDictionary<string, string>? properties,
        CancellationToken cancellationToken)
    {
        string now = Now();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO node(kind_id, name, description, content, properties, created_at, updated_at)
            VALUES($kind, $name, $description, $content, $properties, $created, $updated)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$kind", kindId);
        command.Parameters.AddWithValue("$name", RequireName(name));
        command.Parameters.AddWithValue("$description", description ?? string.Empty);
        command.Parameters.AddWithValue("$content", content ?? string.Empty);
        command.Parameters.AddWithValue("$properties", SerializeProperties(properties));
        command.Parameters.AddWithValue("$created", now);
        command.Parameters.AddWithValue("$updated", now);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    private async Task<long> ResolveKindAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string kind,
        CancellationToken cancellationToken)
    {
        string normalized = KnowledgeVocabularyLoader.NormalizeName(Require(kind, "kind"));
        var usedIds = new List<int>();
        long? inactiveId = null;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT id, active, deprecated FROM node_kind WHERE name = $name COLLATE NOCASE;";
            select.Parameters.AddWithValue("$name", normalized);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                long existingId = reader.GetInt64(0);
                if (reader.GetInt64(1) != 0)
                {
                    if (reader.GetInt64(2) != 0)
                    {
                        _logger.LogWarning(
                            "Writing node kind {Kind}, which the vocabulary marks deprecated.", normalized);
                    }

                    return existingId;
                }

                inactiveId = existingId;
            }
        }

        if (inactiveId is not null)
        {
            throw new KnowledgeValidationException(
                $"node kind '{normalized}' is inactive because the pack that defined it was removed; restore the pack or use another kind");
        }

        if (!_knowledgeOptions.AllowAdHocKinds)
        {
            throw new KnowledgeValidationException($"unknown node kind '{kind}'");
        }

        // Ad-hoc kinds share the pack id band, so a pack that later formalizes this
        // kind computes the same deterministic id and nothing has to be migrated.
        await using (var idsCommand = connection.CreateCommand())
        {
            idsCommand.Transaction = transaction;
            idsCommand.CommandText = "SELECT id FROM node_kind;";
            await using var reader = await idsCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                usedIds.Add((int)reader.GetInt64(0));
            }
        }

        int adHocId = KnowledgeVocabularyLoader.AllocatePackId(normalized, usedIds);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO node_kind(id, name, description, required_props, traits, source, active, deprecated, updated_at)
            VALUES($id, $name, 'Ad-hoc kind.', '[]', '[]', 'ad-hoc', 1, 0, $updated)
            RETURNING id;
            """;
        insert.Parameters.AddWithValue("$id", adHocId);
        insert.Parameters.AddWithValue("$name", normalized);
        insert.Parameters.AddWithValue("$updated", Now());
        long id = Convert.ToInt64(
            await insert.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
        _logger.LogInformation("Registered ad-hoc node kind {Kind} as id {Id}.", normalized, id);
        return id;
    }

    private async Task<EdgeTypeDefinition> LoadEdgeTypeAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string edgeType,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, name, description, resolves, single_outgoing, source_kinds, destination_kinds,
                   pairs, inverse_name, symmetric, acyclic, traits, source, deprecated
            FROM edge_type WHERE name = $name COLLATE NOCASE AND active = 1;
            """;
        command.Parameters.AddWithValue("$name", Require(edgeType, "edgeType"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            if (await ExistsInactiveEdgeTypeAsync(connection, transaction, edgeType, cancellationToken))
            {
                throw new KnowledgeValidationException(
                    $"edge type '{edgeType}' is inactive because the pack that defined it was removed; restore the pack or use another edge type");
            }

            throw InverseEdgeTypeException(edgeType);
        }

        if (reader.GetInt64(13) != 0)
        {
            _logger.LogWarning("Writing edge type {EdgeType}, which the vocabulary marks deprecated.", edgeType);
        }

        // The write path needs the same view of the definition as the read path: a
        // partial projection silently loses resolves/single_outgoing/acyclic.
        return new EdgeTypeDefinition(
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
        };
    }

    private async Task<KnowledgeNode> LoadNodeAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT n.id, k.name, n.name, n.description, n.content, n.properties,
                   n.created_at, n.updated_at,
                   CASE WHEN {ResolvedExists} THEN 1 ELSE 0 END AS resolved
            FROM node n JOIN node_kind k ON k.id = n.kind_id
            WHERE n.id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new KnowledgeNotFoundException($"node {id} does not exist");
        }

        return ReadNode(reader, includeContent: true);
    }

    private async Task EnsureAnchorAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long goalId,
        PlanAnchor anchor,
        CancellationToken cancellationToken)
    {
        var existing = new List<long>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT e.destination FROM edge e JOIN edge_type t ON t.id = e.edge_type_id
                WHERE e.source = $goal AND t.name = $type COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue("$goal", goalId);
            command.Parameters.AddWithValue("$type", anchor.EdgeType);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existing.Add(reader.GetInt64(0));
            }
        }

        if (existing.Count == 0)
        {
            await AddEdgeCoreAsync(
                connection,
                transaction,
                goalId,
                anchor.NodeId,
                anchor.EdgeType,
                cancellationToken);
            return;
        }

        if (existing.Contains(anchor.NodeId))
        {
            return;
        }

        throw new KnowledgeValidationException(
            $"goal {goalId} is already anchored via '{anchor.EdgeType}' to node {existing[0]}; " +
            $"it cannot be re-anchored to {anchor.NodeId}");
    }

    private async Task<bool> IsAnchoredAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long nodeId,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<long>();
        long currentId = nodeId;

        while (seen.Add(currentId))
        {
            await using (var nodeCommand = connection.CreateCommand())
            {
                nodeCommand.Transaction = transaction;
                nodeCommand.CommandText = "SELECT 1 FROM node WHERE id = $id;";
                nodeCommand.Parameters.AddWithValue("$id", currentId);
                if (await nodeCommand.ExecuteScalarAsync(cancellationToken) is null)
                {
                    return false;
                }
            }

            await using (var anchorCommand = connection.CreateCommand())
            {
                anchorCommand.Transaction = transaction;
                anchorCommand.CommandText = """
                    SELECT COUNT(*) FROM edge e JOIN edge_type t ON t.id = e.edge_type_id
                    WHERE e.source = $id AND t.name IN (SELECT value FROM json_each($anchor));
                    """;
                anchorCommand.Parameters.AddWithValue("$id", currentId);
                anchorCommand.Parameters.AddWithValue("$anchor", _anchorEdgesJson);
                long anchored = Convert.ToInt64(
                    await anchorCommand.ExecuteScalarAsync(cancellationToken),
                    CultureInfo.InvariantCulture);
                if (anchored > 0)
                {
                    return true;
                }
            }

            await using var parentCommand = connection.CreateCommand();
            parentCommand.Transaction = transaction;
            parentCommand.CommandText = """
                SELECT e.destination FROM edge e JOIN edge_type t ON t.id = e.edge_type_id
                WHERE e.source = $id AND t.name IN (SELECT value FROM json_each($hierarchy));
                """;
            parentCommand.Parameters.AddWithValue("$id", currentId);
            parentCommand.Parameters.AddWithValue("$hierarchy", _hierarchyEdgesJson);
            object? parent = await parentCommand.ExecuteScalarAsync(cancellationToken);
            if (parent is null or DBNull)
            {
                return false;
            }

            currentId = Convert.ToInt64(parent, CultureInfo.InvariantCulture);
        }

        return false;
    }

    /// <summary>
    /// Applies embedded migration scripts in filename order. DbUp journals applied
    /// scripts in <c>SchemaVersions</c>, so a restart is a no-op and an already
    /// applied script is never re-run.
    /// </summary>
    private void Migrate()
    {
        var upgrader = DeployChanges.To
            .SqliteDatabase(_connectionString)
            .WithScriptsEmbeddedInAssembly(
                typeof(SqliteKnowledgeGraph).Assembly,
                resourceName => resourceName.Contains(".Migrations.", StringComparison.Ordinal))
            .WithTransactionPerScript()
            .LogTo(_logger)
            .Build();

        var result = upgrader.PerformUpgrade();
        if (!result.Successful)
        {
            var failed = result.Error ?? new InvalidOperationException("unknown DbUp failure");
            throw new InvalidOperationException(
                $"SQLite knowledge migration failed at {_databasePath}: {failed.Message}",
                failed);
        }

        int applied = result.Scripts.Count();
        if (applied == 0)
        {
            _logger.LogInformation("SQLite knowledge schema is up to date at {DatabasePath}.", _databasePath);
        }
        else
        {
            _logger.LogInformation(
                "Applied {Count} SQLite knowledge migration script(s) to {DatabasePath}.",
                applied,
                _databasePath);
        }
    }

    private void Seed(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        string now = Now();

        foreach (var kind in _vocabulary.NodeKinds)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO node_kind(
                    id, name, description, required_props, traits, source, active, deprecated, updated_at)
                VALUES($id, $name, $description, $required, $traits, $source, 1, $deprecated, $updated);
                UPDATE node_kind SET description = $description, required_props = $required,
                    traits = $traits, source = $source, active = 1, deprecated = $deprecated,
                    updated_at = $updated
                WHERE name = $name;
                """;
            command.Parameters.AddWithValue("$id", kind.Id);
            command.Parameters.AddWithValue("$name", kind.Name);
            command.Parameters.AddWithValue("$description", kind.Description);
            command.Parameters.AddWithValue("$required", JsonSerializer.Serialize(kind.RequiredProps));
            command.Parameters.AddWithValue("$traits", JsonSerializer.Serialize(kind.Traits));
            command.Parameters.AddWithValue("$source", kind.Source);
            command.Parameters.AddWithValue("$deprecated", kind.Deprecated ? 1 : 0);
            command.Parameters.AddWithValue("$updated", now);
            command.ExecuteNonQuery();
            EnsureIdMatches(connection, transaction, "node_kind", kind.Name, kind.Id);
        }

        foreach (var edgeType in _vocabulary.EdgeTypes)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO edge_type(
                    id, name, description, resolves, single_outgoing, source_kinds, destination_kinds,
                    pairs, inverse_name, symmetric, acyclic, traits, source, active, deprecated, updated_at)
                VALUES($id, $name, $description, $resolves, $single, $sources, $destinations,
                    $pairs, $inverse, $symmetric, $acyclic, $traits, $source, 1, $deprecated, $updated);
                UPDATE edge_type SET description = $description, resolves = $resolves,
                    single_outgoing = $single, source_kinds = $sources,
                    destination_kinds = $destinations, pairs = $pairs, inverse_name = $inverse,
                    symmetric = $symmetric, acyclic = $acyclic, traits = $traits,
                    source = $source, active = 1, deprecated = $deprecated, updated_at = $updated
                WHERE name = $name;
                """;
            command.Parameters.AddWithValue("$id", edgeType.Id);
            command.Parameters.AddWithValue("$name", edgeType.Name);
            command.Parameters.AddWithValue("$description", edgeType.Description);
            command.Parameters.AddWithValue("$resolves", edgeType.Resolves ? 1 : 0);
            command.Parameters.AddWithValue("$single", edgeType.SingleOutgoing ? 1 : 0);
            command.Parameters.AddWithValue("$sources", JoinKinds(edgeType.SourceKinds));
            command.Parameters.AddWithValue("$destinations", JoinKinds(edgeType.DestinationKinds));
            command.Parameters.AddWithValue("$pairs", SerializePairs(edgeType.Pairs));
            command.Parameters.AddWithValue("$inverse", (object?)edgeType.InverseName ?? DBNull.Value);
            command.Parameters.AddWithValue("$symmetric", edgeType.Symmetric ? 1 : 0);
            command.Parameters.AddWithValue("$acyclic", edgeType.Acyclic ? 1 : 0);
            command.Parameters.AddWithValue("$traits", JsonSerializer.Serialize(edgeType.Traits));
            command.Parameters.AddWithValue("$source", edgeType.Source);
            command.Parameters.AddWithValue("$deprecated", edgeType.Deprecated ? 1 : 0);
            command.Parameters.AddWithValue("$updated", now);
            command.ExecuteNonQuery();
            EnsureIdMatches(connection, transaction, "edge_type", edgeType.Name, edgeType.Id);
        }

        Reconcile(connection, transaction, now);
        transaction.Commit();
    }

    /// <summary>
    /// A row created before the vocabulary claimed the name (an ad-hoc write) keeps its
    /// old id because the upsert matches on name. The id is part of the wire shape and
    /// must agree with the catalog, so disagreeing rows fail startup instead of diverging
    /// silently between SQLite and the in-memory store.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only a SchemaContract table name is interpolated; every value is parameterized.")]
    private static void EnsureIdMatches(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string name,
        int expected)
    {
        SchemaContract.EnsureKnownTable(table);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT id FROM {table} WHERE name = $name;";
        command.Parameters.AddWithValue("$name", name);
        object? actual = command.ExecuteScalar();
        if (actual is null or DBNull)
        {
            throw new InvalidOperationException($"{table} row '{name}' disappeared during seeding");
        }

        long actualId = Convert.ToInt64(actual, CultureInfo.InvariantCulture);
        if (actualId != expected)
        {
            throw new InvalidOperationException(
                $"{table} row '{name}' has id {actualId} in the database but the vocabulary assigns {expected}. " +
                "This happens when a name was first written as an ad-hoc kind and later claimed by a pack with a " +
                "different id. Delete the database file and restart, or keep the original id in the pack.");
        }
    }

    /// <summary>
    /// Entries that are no longer in the vocabulary are deactivated when nothing uses
    /// them and reported as an error when the graph still references them. Nothing is
    /// deleted, and ad-hoc kinds are owned by writes rather than by packs.
    /// </summary>
    private void Reconcile(SqliteConnection connection, SqliteTransaction transaction, string now)
    {
        Deactivate(
            connection,
            transaction,
            now,
            table: "node_kind",
            inUseSql: "SELECT COUNT(*) FROM node WHERE kind_id = $id;",
            label: "node kind");
        Deactivate(
            connection,
            transaction,
            now,
            table: "edge_type",
            inUseSql: "SELECT COUNT(*) FROM edge WHERE edge_type_id = $id;",
            label: "edge type");
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only a SchemaContract table name is interpolated; every value is parameterized.")]
    private void Deactivate(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string now,
        string table,
        string inUseSql,
        string label)
    {
        SchemaContract.EnsureKnownTable(table);
        var activeNames = table == "node_kind"
            ? _vocabulary.NodeKinds.Select(kind => kind.Name)
            : _vocabulary.EdgeTypes.Select(edge => edge.Name);

        var candidates = new List<(long Id, string Name)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT id, name FROM {table}
                WHERE active = 1 AND source <> 'ad-hoc'
                  AND name NOT IN (SELECT value FROM json_each($names));
                """;
            command.Parameters.AddWithValue("$names", NamesJson(activeNames));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                candidates.Add((reader.GetInt64(0), reader.GetString(1)));
            }
        }

        var inUse = new List<string>();
        foreach (var (id, name) in candidates)
        {
            using var usage = connection.CreateCommand();
            usage.Transaction = transaction;
            usage.CommandText = inUseSql;
            usage.Parameters.AddWithValue("$id", id);
            long count = Convert.ToInt64(
                usage.ExecuteScalar(),
                CultureInfo.InvariantCulture);
            if (count > 0)
            {
                inUse.Add($"{name} ({count})");
                continue;
            }

            using var deactivate = connection.CreateCommand();
            deactivate.Transaction = transaction;
            deactivate.CommandText = $"UPDATE {table} SET active = 0, updated_at = $now WHERE id = $id;";
            deactivate.Parameters.AddWithValue("$id", id);
            deactivate.Parameters.AddWithValue("$now", now);
            deactivate.ExecuteNonQuery();
            _logger.LogInformation("Deactivated {Label} {Name}: no longer in the vocabulary.", label, name);
        }

        if (inUse.Count > 0)
        {
            throw new KnowledgeValidationException(
                $"the vocabulary no longer defines {label}(s) {string.Join(", ", inUse)}, which the graph still uses. " +
                "Restore the pack that defined them, or remove those nodes/edges first.");
        }
    }

    private static string SerializePairs(IReadOnlyList<EdgeKindPair> pairs)
        => JsonSerializer.Serialize(pairs.Select(pair => new[] { pair.Source, pair.Destination }).ToArray());

    private static List<EdgeKindPair> DeserializePairs(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]")
        {
            return [];
        }

        string[][] pairs = JsonSerializer.Deserialize<string[][]>(json) ?? [];
        return [.. pairs
            .Where(pair => pair.Length == 2)
            .Select(pair => new EdgeKindPair(pair[0], pair[1]))];
    }

    private static List<string> DeserializeNames(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]")
        {
            return [];
        }

        return JsonSerializer.Deserialize<string[]>(json)?.ToList() ?? [];
    }

    private static async Task<bool> ExistsInactiveEdgeTypeAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string edgeType,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1 FROM edge_type WHERE name = $name COLLATE NOCASE AND active = 0 LIMIT 1;
            """;
        command.Parameters.AddWithValue("$name", edgeType);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static string DescribePairs(EdgeTypeDefinition definition)
        => definition.Pairs.Count == 0
            ? "any"
            : string.Join(", ", definition.Pairs.Select(pair => $"{pair.Source}->{pair.Destination}"));

    private static async Task<KnowledgeEdge?> FindEdgeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long source,
        long destination,
        EdgeTypeDefinition definition,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, created_at FROM edge
            WHERE source = $source AND destination = $destination AND edge_type_id = $type;
            """;
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$destination", destination);
        command.Parameters.AddWithValue("$type", definition.Id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new KnowledgeEdge
        {
            Id = reader.GetInt64(0),
            Source = source,
            Destination = destination,
            EdgeType = definition.Name,
            CreatedAt = ParseTime(reader.GetString(1)),
        };
    }

    /// <summary>True when <paramref name="to"/> is reachable from <paramref name="from"/> along the edge type.</summary>
    private static async Task<bool> ReachesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string edgeType,
        long from,
        long to,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH RECURSIVE reach(id) AS (
                SELECT $from
                UNION
                SELECT e.destination
                FROM reach
                JOIN edge e ON e.source = reach.id
                JOIN edge_type t ON t.id = e.edge_type_id
                WHERE t.name = $type COLLATE NOCASE
            )
            SELECT 1 FROM reach WHERE id = $to LIMIT 1;
            """;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        command.Parameters.AddWithValue("$type", edgeType);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    /// <summary>
    /// A symmetric relation is undirected however it is stored, and an inverse name
    /// reads the stored edge from the opposite end.
    /// </summary>
    private static EdgeDirection EffectiveDirection(EdgeTypeResolution resolution, EdgeDirection requested)
    {
        if (resolution.Definition.Symmetric)
        {
            return EdgeDirection.Both;
        }

        return resolution.Reversed ? Flip(requested) : requested;
    }

    private static EdgeDirection Flip(EdgeDirection direction) => direction switch
    {
        EdgeDirection.Outgoing => EdgeDirection.Incoming,
        EdgeDirection.Incoming => EdgeDirection.Outgoing,
        _ => EdgeDirection.Both,
    };

    /// <summary>Canonical stored name for a traversal filter, or null when unset.</summary>
    private string? CanonicalEdgeType(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return _vocabulary.TryGetCanonicalEdgeTypeName(name, out string? canonical)
            ? canonical
            : KnowledgeVocabularyLoader.NormalizeName(name);
    }

    /// <summary>
    /// Edges are stored in the canonical direction only. Naming the inverse on a write
    /// is a mistake worth reporting precisely, not a silent swap.
    /// </summary>
    private KnowledgeValidationException InverseEdgeTypeException(string edgeType)
        => _vocabulary.TryResolveEdgeType(edgeType, out var resolution) && resolution.Reversed
            ? new KnowledgeValidationException(
                $"'{edgeType}' is the inverse of '{resolution.Definition.Name}'; add the edge as '{resolution.Definition.Name}' from the opposite endpoint")
            : new KnowledgeValidationException($"unknown edge type '{edgeType}'");

    private static string NamesJson(IEnumerable<string> names)
        => JsonSerializer.Serialize(names.Order(StringComparer.OrdinalIgnoreCase).ToArray());

    private static string JoinKinds(IReadOnlySet<string> kinds)
        => string.Join(",", kinds.Order(StringComparer.OrdinalIgnoreCase));

    private static HashSet<string> ParseKinds(string value)
        => new(
            value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

    private static string SerializeProperties(IReadOnlyDictionary<string, string>? properties)
        => JsonSerializer.Serialize(
            properties ?? new Dictionary<string, string>(StringComparer.Ordinal),
            JsonOptions);

    private static Dictionary<string, string> DeserializeProperties(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static KnowledgeNode ReadNode(SqliteDataReader reader, bool includeContent) => new()
    {
        Id = reader.GetInt64(0),
        Kind = reader.GetString(1),
        Name = reader.GetString(2),
        Description = reader.GetString(3),
        Content = includeContent ? reader.GetString(4) : string.Empty,
        Properties = DeserializeProperties(reader.GetString(5)),
        CreatedAt = ParseTime(reader.GetString(6)),
        UpdatedAt = ParseTime(reader.GetString(7)),
        Resolved = reader.GetInt64(8) != 0,
    };

    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string RequireName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new KnowledgeValidationException("node name is required");
        }

        return name.Trim();
    }

    private static string Require(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new KnowledgeValidationException($"{field} is required");
        }

        return value.Trim();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        object? value = command.ExecuteScalar();
        return value is null or DBNull
            ? 0
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public void Dispose() => _writeGate.Dispose();
}
