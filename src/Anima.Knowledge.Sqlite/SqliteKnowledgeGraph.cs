using System.Globalization;
using System.Text.Json;
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

    public SqliteKnowledgeGraph(
        IOptions<SqliteKnowledgeOptions> sqliteOptions,
        IOptions<KnowledgeOptions> knowledgeOptions,
        ILogger<SqliteKnowledgeGraph> logger)
    {
        ArgumentNullException.ThrowIfNull(sqliteOptions);
        ArgumentNullException.ThrowIfNull(knowledgeOptions);
        ArgumentNullException.ThrowIfNull(logger);
        _sqliteOptions = sqliteOptions.Value;
        _knowledgeOptions = knowledgeOptions.Value;
        _logger = logger;

        _databasePath = string.IsNullOrWhiteSpace(_sqliteOptions.DatabasePath)
            ? SqliteKnowledgeOptions.ResolveDefaultPath()
            : Path.GetFullPath(_sqliteOptions.DatabasePath);

        var directory = Path.GetDirectoryName(_databasePath);
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
            var kindId = await ResolveKindAsync(connection, transaction, draft.Kind, token);
            var id = await InsertNodeAsync(
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
            var name = patch.Name is null ? existing.Name : RequireName(patch.Name);
            var description = patch.Description ?? existing.Description;
            var content = patch.Content ?? existing.Content;
            var properties = patch.Properties ?? existing.Properties;
            var now = Now();

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
                var goalKindId = await ResolveKindAsync(connection, transaction, KnowledgeKinds.Goal, token);
                var goalNodeId = await InsertNodeAsync(
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
                var kindId = await ResolveKindAsync(connection, transaction, KnowledgeKinds.Task, token);
                var taskId = await InsertNodeAsync(
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
                var parentId = task.Parent is { } parentKey ? created[parentKey].Id : goal.Id;
                await AddEdgeCoreAsync(
                    connection,
                    transaction,
                    node.Id,
                    parentId,
                    KnowledgeEdgeTypes.PartOf,
                    token);

                foreach (var dependency in task.DependsOn ?? [])
                {
                    await AddEdgeCoreAsync(
                        connection,
                        transaction,
                        node.Id,
                        created[dependency].Id,
                        KnowledgeEdgeTypes.DependsOn,
                        token);
                }

                foreach (var affect in task.Affects ?? [])
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
                tasks.Select(task => new PlanTaskResult(
                    task.Key,
                    created[task.Key],
                    task.DependsOn ?? [])).ToArray());
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
            if (parent.Kind is not (KnowledgeKinds.Task or KnowledgeKinds.Goal))
            {
                throw new KnowledgeValidationException(
                    $"task parent must be a task or goal, got '{parent.Kind}'");
            }

            if (!await IsAnchoredAsync(connection, transaction, parentId, token))
            {
                throw new KnowledgeValidationException(
                    $"part_of parent references node {parentId}, which is not attached to a repo via belongs_to");
            }

            var kindId = await ResolveKindAsync(connection, transaction, KnowledgeKinds.Task, token);
            var nodeId = await InsertNodeAsync(
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

            foreach (var dependency in draft.DependsOn ?? [])
            {
                await AddEdgeCoreAsync(
                    connection,
                    transaction,
                    nodeId,
                    dependency,
                    KnowledgeEdgeTypes.DependsOn,
                    token);
            }

            foreach (var affect in draft.Affects ?? [])
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

            var name = patch.Name is null ? node.Name : RequireName(patch.Name);
            var description = patch.Description ?? node.Description;
            var content = patch.Content ?? node.Content;
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
        using var connection = OpenConnection();
        if (_sqliteOptions.EnableWal)
        {
            Execute(connection, "PRAGMA journal_mode = WAL;");
        }

        Migrate(connection);
        Seed(connection);
        _logger.LogInformation(
            "SQLite knowledge graph ready at {DatabasePath} ({NodeCount} nodes).",
            _databasePath,
            ScalarLong(connection, "SELECT COUNT(*) FROM node;"));
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
            using var transaction = connection.BeginTransaction();
            var result = await action(connection, transaction, cancellationToken);
            transaction.Commit();
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

        if (definition.SourceKinds.Count > 0 && !definition.SourceKinds.Contains(source.Kind))
        {
            throw new KnowledgeValidationException(
                $"edge type '{definition.Name}' source references node {source.Id} of kind '{source.Kind}', " +
                $"expected one of {string.Join(", ", definition.SourceKinds)}");
        }

        if (definition.DestinationKinds.Count > 0 && !definition.DestinationKinds.Contains(destination.Kind))
        {
            throw new KnowledgeValidationException(
                $"edge type '{definition.Name}' destination references node {destination.Id} of kind '{destination.Kind}', " +
                $"expected one of {string.Join(", ", definition.DestinationKinds)}");
        }

        await using (var existingCommand = connection.CreateCommand())
        {
            existingCommand.Transaction = transaction;
            existingCommand.CommandText = """
                SELECT id, created_at FROM edge
                WHERE source = $source AND destination = $destination AND edge_type_id = $type;
                """;
            existingCommand.Parameters.AddWithValue("$source", sourceId);
            existingCommand.Parameters.AddWithValue("$destination", destinationId);
            existingCommand.Parameters.AddWithValue("$type", definition.Id);
            await using var reader = await existingCommand.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return new KnowledgeEdge
                {
                    Id = reader.GetInt64(0),
                    Source = sourceId,
                    Destination = destinationId,
                    EdgeType = definition.Name,
                    CreatedAt = ParseTime(reader.GetString(1)),
                };
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
            var count = Convert.ToInt64(
                await countCommand.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
            if (count > 0)
            {
                throw new KnowledgeValidationException(
                    $"edge type '{definition.Name}' allows one outgoing edge, and node {sourceId} already has one");
            }
        }

        var createdAt = Now();
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
        var edgeId = Convert.ToInt64(
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
        var now = Now();
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
        var normalized = Require(kind, "kind").ToLowerInvariant();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT id FROM node_kind WHERE name = $name COLLATE NOCASE;";
            select.Parameters.AddWithValue("$name", normalized);
            var value = await select.ExecuteScalarAsync(cancellationToken);
            if (value is not null and not DBNull)
            {
                return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }
        }

        if (!_knowledgeOptions.AllowAdHocKinds)
        {
            throw new KnowledgeValidationException($"unknown node kind '{kind}'");
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO node_kind(name, description, updated_at) VALUES($name, $description, $updated)
            RETURNING id;
            """;
        insert.Parameters.AddWithValue("$name", normalized);
        insert.Parameters.AddWithValue("$description", "Ad-hoc kind.");
        insert.Parameters.AddWithValue("$updated", Now());
        var id = Convert.ToInt64(
            await insert.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
        _logger.LogInformation("Registered ad-hoc node kind {Kind}.", normalized);
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
            SELECT id, name, description, resolves, single_outgoing, source_kinds, destination_kinds
            FROM edge_type WHERE name = $name COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$name", Require(edgeType, "edgeType"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new KnowledgeValidationException($"unknown edge type '{edgeType}'");
        }

        return new EdgeTypeDefinition(
            (int)reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            ParseKinds(reader.GetString(5)),
            ParseKinds(reader.GetString(6)),
            reader.GetInt64(3) != 0,
            reader.GetInt64(4) != 0);
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
        var currentId = nodeId;

        while (seen.Add(currentId))
        {
            await using var nodeCommand = connection.CreateCommand();
            nodeCommand.Transaction = transaction;
            nodeCommand.CommandText = """
                SELECT k.name FROM node n JOIN node_kind k ON k.id = n.kind_id WHERE n.id = $id;
                """;
            nodeCommand.Parameters.AddWithValue("$id", currentId);
            var kind = await nodeCommand.ExecuteScalarAsync(cancellationToken) as string;
            if (kind is null)
            {
                return false;
            }

            if (kind == KnowledgeKinds.Goal)
            {
                await using var anchorCommand = connection.CreateCommand();
                anchorCommand.Transaction = transaction;
                anchorCommand.CommandText = """
                    SELECT COUNT(*) FROM edge e JOIN edge_type t ON t.id = e.edge_type_id
                    WHERE e.source = $id AND t.name = 'belongs_to';
                    """;
                anchorCommand.Parameters.AddWithValue("$id", currentId);
                var anchored = Convert.ToInt64(
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
                WHERE e.source = $id AND t.name = 'part_of';
                """;
            parentCommand.Parameters.AddWithValue("$id", currentId);
            var parent = await parentCommand.ExecuteScalarAsync(cancellationToken);
            if (parent is null or DBNull)
            {
                return false;
            }

            currentId = Convert.ToInt64(parent, CultureInfo.InvariantCulture);
        }

        return false;
    }

    private void Migrate(SqliteConnection connection)
    {
        var version = ScalarLong(connection, "PRAGMA user_version;");
        if (version >= 1)
        {
            return;
        }

        var assembly = typeof(SqliteKnowledgeGraph).Assembly;
        var resource = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith("0001_init.sql", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"missing migration resource {resource}");
        using var reader = new StreamReader(stream);
        var sql = reader.ReadToEnd();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        Execute(connection, "PRAGMA user_version = 1;");
        _logger.LogInformation("Applied SQLite knowledge migration 1.");
    }

    private void Seed(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        var now = Now();

        foreach (var kind in KnowledgeKinds.Definitions)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO node_kind(id, name, description, updated_at)
                VALUES($id, $name, $description, $updated);
                UPDATE node_kind SET description = $description, updated_at = $updated WHERE name = $name;
                """;
            command.Parameters.AddWithValue("$id", kind.Id);
            command.Parameters.AddWithValue("$name", kind.Name);
            command.Parameters.AddWithValue("$description", kind.Description);
            command.Parameters.AddWithValue("$updated", now);
            command.ExecuteNonQuery();
        }

        foreach (var edgeType in KnowledgeEdgeTypes.Definitions)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO edge_type(
                    id, name, description, resolves, single_outgoing, source_kinds, destination_kinds, updated_at)
                VALUES($id, $name, $description, $resolves, $single, $sources, $destinations, $updated);
                UPDATE edge_type SET description = $description, resolves = $resolves,
                    single_outgoing = $single, source_kinds = $sources,
                    destination_kinds = $destinations, updated_at = $updated
                WHERE name = $name;
                """;
            command.Parameters.AddWithValue("$id", edgeType.Id);
            command.Parameters.AddWithValue("$name", edgeType.Name);
            command.Parameters.AddWithValue("$description", edgeType.Description);
            command.Parameters.AddWithValue("$resolves", edgeType.Resolves ? 1 : 0);
            command.Parameters.AddWithValue("$single", edgeType.SingleOutgoing ? 1 : 0);
            command.Parameters.AddWithValue("$sources", JoinKinds(edgeType.SourceKinds));
            command.Parameters.AddWithValue("$destinations", JoinKinds(edgeType.DestinationKinds));
            command.Parameters.AddWithValue("$updated", now);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static string JoinKinds(IReadOnlySet<string> kinds)
        => string.Join(",", kinds.Order(StringComparer.OrdinalIgnoreCase));

    private static HashSet<string> ParseKinds(string value)
        => new HashSet<string>(
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
        var value = command.ExecuteScalar();
        return value is null or DBNull
            ? 0
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public void Dispose() => _writeGate.Dispose();
}
