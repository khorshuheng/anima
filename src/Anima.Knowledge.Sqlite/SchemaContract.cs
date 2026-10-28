namespace Anima.Knowledge.Sqlite;

/// <summary>
/// Columns the stores read or write. DbUp journals applied scripts by name only, so an
/// edited script that already ran is skipped; comparing the live schema against this
/// list turns that into a startup error with a reset instruction instead of a SQL error
/// on the first request. Keep it in sync with <c>Migrations/*.sql</c>.
/// </summary>
public static class SchemaContract
{
    /// <summary>
    /// Rejects a table name that is not part of the contract, so string-built DDL/DML
    /// can only ever name a known table.
    /// </summary>
    public static void EnsureKnownTable(string table)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        if (!Columns.ContainsKey(table))
        {
            throw new ArgumentOutOfRangeException(
                nameof(table),
                table,
                $"'{table}' is not a known knowledge table");
        }
    }

    public static IReadOnlyDictionary<string, string[]> Columns { get; } =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["node_kind"] =
            [
                "id", "name", "description", "required_props", "traits",
                "source", "pack_version", "checksum", "active", "deprecated", "updated_at",
            ],
            ["edge_type"] =
            [
                "id", "name", "description", "resolves", "single_outgoing",
                "source_kinds", "destination_kinds", "pairs", "inverse_name",
                "symmetric", "acyclic", "traits", "source", "pack_version", "checksum",
                "active", "deprecated", "updated_at",
            ],
            ["node"] = ["id", "kind_id", "name", "description", "content", "properties", "created_at", "updated_at"],
            ["edge"] = ["id", "source", "destination", "edge_type_id", "created_at"],
        };
}
