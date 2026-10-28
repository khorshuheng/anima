using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Anima.Knowledge.Sqlite.Tests;

/// <summary>
/// The schema is applied by DbUp from embedded scripts and journaled in
/// <c>SchemaVersions</c>. These tests cover the first-boot path, the no-op restart
/// path and the shape the stores depend on.
/// </summary>
public sealed class MigrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "anima-migration-" + Guid.NewGuid().ToString("n"));

    public MigrationTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void Fresh_database_gets_tables_triggers_and_one_journal_entry()
    {
        string path = Path.Combine(_directory, "knowledge.db");
        using var graph = CreateGraph(path);
        using var connection = Open(path);

        Assert.Equal(1, ScalarLong(connection,
            "SELECT COUNT(*) FROM SchemaVersions WHERE ScriptName LIKE '%.0001_init.sql';"));
        Assert.Equal(5, ScalarLong(connection, """
            SELECT COUNT(*) FROM sqlite_master
            WHERE name IN ('node', 'edge', 'node_kind', 'edge_type', 'node_fts');
            """));
        Assert.Equal(3, ScalarLong(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name IN ('node_ai', 'node_ad', 'node_au');"));
        Assert.Equal(4, ScalarLong(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name LIKE 'idx_%';"));
    }

    [Fact]
    public async Task Reopening_the_database_does_not_reapply_scripts()
    {
        string path = Path.Combine(_directory, "knowledge.db");
        long nodeId;
        using (var first = CreateGraph(path))
        {
            nodeId = (await first.CreateNodeAsync(new NodeDraft(KnowledgeKinds.Note, "kept"))).Id;
        }

        using var second = CreateGraph(path);
        using var connection = Open(path);

        Assert.Equal(1, ScalarLong(connection, "SELECT COUNT(*) FROM SchemaVersions;"));
        Assert.Equal(1, ScalarLong(connection, "SELECT COUNT(*) FROM node;"));
        Assert.NotNull(await second.GetNodeAsync(nodeId));
    }

    [Fact]
    public void Vocabulary_columns_exist_with_defaults()
    {
        string path = Path.Combine(_directory, "knowledge.db");
        using var graph = CreateGraph(path);
        using var connection = Open(path);

        var kindColumns = ColumnNames(connection, "node_kind");
        Assert.Contains("required_props", kindColumns);
        Assert.Contains("traits", kindColumns);
        Assert.Contains("source", kindColumns);
        Assert.Contains("pack_version", kindColumns);
        Assert.Contains("checksum", kindColumns);
        Assert.Contains("active", kindColumns);
        Assert.Contains("deprecated", kindColumns);

        var edgeColumns = ColumnNames(connection, "edge_type");
        Assert.Contains("pairs", edgeColumns);
        Assert.Contains("inverse_name", edgeColumns);
        Assert.Contains("symmetric", edgeColumns);
        Assert.Contains("acyclic", edgeColumns);
        Assert.Contains("source", edgeColumns);
        Assert.Contains("active", edgeColumns);
        Assert.Contains("deprecated", edgeColumns);

        // Seeded core rows are core-owned, active and not deprecated.
        Assert.Equal(0, ScalarLong(connection,
            "SELECT COUNT(*) FROM node_kind WHERE source <> 'core' OR active <> 1 OR deprecated <> 0 OR required_props <> '[]';"));
        Assert.Equal(0, ScalarLong(connection,
            "SELECT COUNT(*) FROM edge_type WHERE source <> 'core' OR active <> 1 OR deprecated <> 0 OR symmetric <> 0;"));

        // Acyclic relations are declared in the core pack and persisted.
        Assert.Equal(
            new[] { "depends_on", "part_of", "precedes", "supersedes" },
            QueryRows(connection, "SELECT name FROM edge_type WHERE acyclic = 1 ORDER BY name;"));
        Assert.Equal(0, ScalarLong(connection, "SELECT COUNT(*) FROM edge_type WHERE inverse_name IS NOT NULL;"));

        // Endpoint pairs and traits are persisted, not left at their defaults.
        Assert.Equal("[\"plannable\"]", ScalarString(connection,
            "SELECT traits FROM node_kind WHERE name = 'task';"));
        Assert.Equal(2, ScalarLong(connection,
            "SELECT json_array_length(pairs) FROM edge_type WHERE name = 'part_of';"));
        Assert.Equal(1, ScalarLong(connection,
            "SELECT json_array_length(traits) FROM edge_type WHERE name = 'part_of';"));
    }

    [Fact]
    public void DbUp_journal_is_the_schema_version_source()
    {
        string path = Path.Combine(_directory, "knowledge.db");
        using var graph = CreateGraph(path);
        using var connection = Open(path);

        // DbUp does not maintain PRAGMA user_version; keep exactly one source of truth.
        Assert.Equal(0, ScalarLong(connection, "PRAGMA user_version;"));
        Assert.Equal(1, ScalarLong(connection, "SELECT COUNT(*) FROM SchemaVersions;"));
    }

    [Fact]
    public void Database_from_an_earlier_pre_release_schema_fails_at_startup()
    {
        string path = Path.Combine(_directory, "legacy.db");
        SeedLegacyDatabase(path);

        var exception = Assert.Throws<InvalidOperationException>(() => CreateGraph(path));

        Assert.Contains("predates the current schema", exception.Message, StringComparison.Ordinal);
        Assert.Contains("node_kind.required_props", exception.Message, StringComparison.Ordinal);
        Assert.Contains("docs/deployment.md", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reproduces a database created before the vocabulary columns existed: the old
    /// script is journaled as applied, so DbUp will not re-run it.
    /// </summary>
    private static void SeedLegacyDatabase(string path)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE node_kind (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL UNIQUE,
                description TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE edge_type (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL UNIQUE,
                description TEXT NOT NULL,
                resolves INTEGER NOT NULL DEFAULT 0,
                single_outgoing INTEGER NOT NULL DEFAULT 0,
                source_kinds TEXT NOT NULL DEFAULT '',
                destination_kinds TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL
            );
            CREATE TABLE node (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                kind_id INTEGER NOT NULL REFERENCES node_kind(id),
                name TEXT NOT NULL,
                description TEXT NOT NULL DEFAULT '',
                content TEXT NOT NULL DEFAULT '',
                properties TEXT NOT NULL DEFAULT '{}',
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE edge (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source INTEGER NOT NULL,
                destination INTEGER NOT NULL,
                edge_type_id INTEGER NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE TABLE SchemaVersions (
                SchemaVersionID INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
                ScriptName TEXT NOT NULL,
                Applied DATETIME NOT NULL
            );
            INSERT INTO SchemaVersions(ScriptName, Applied)
            VALUES('Anima.Knowledge.Sqlite.Migrations.0001_init.sql', '2026-01-01');
            """;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void Schema_contract_matches_the_migrated_schema()
    {
        string path = Path.Combine(_directory, "knowledge.db");
        using var graph = CreateGraph(path);
        using var connection = Open(path);

        foreach (var (table, expected) in SchemaContract.Columns)
        {
            var actual = ColumnNames(connection, table);
            string[] missing = expected.Where(column => !actual.Contains(column)).ToArray();
            Assert.True(
                missing.Length == 0,
                $"{table} is missing {string.Join(", ", missing)}; update SchemaContract with Migrations/*.sql");
        }
    }

    private static SqliteKnowledgeGraph CreateGraph(string path) => new(
        Options.Create(new SqliteKnowledgeOptions
        {
            DatabasePath = path,
            BusyTimeoutSeconds = 5,
        }),
        Options.Create(new KnowledgeOptions()),
        NullLogger<SqliteKnowledgeGraph>.Instance,
        KnowledgeVocabularyLoader.LoadCore());

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        return connection;
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static List<string> QueryRows(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private static string ScalarString(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static List<string> ColumnNames(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(1));
        }

        return names;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
