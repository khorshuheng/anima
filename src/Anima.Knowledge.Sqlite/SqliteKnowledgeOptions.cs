namespace Anima.Knowledge.Sqlite;

public sealed class SqliteKnowledgeOptions
{
    /// <summary>
    /// SQLite database path. Null means <c>ANIMA_KNOWLEDGE_DB</c> or
    /// <c>%LOCALAPPDATA%/anima/knowledge.db</c> (Linux: ~/.local/share/anima).
    /// </summary>
    public string? DatabasePath { get; set; }

    public int BusyTimeoutSeconds { get; set; } = 5;

    public bool EnableWal { get; set; } = true;

    public static string ResolveDefaultPath()
    {
        var configured = Environment.GetEnvironmentVariable("ANIMA_KNOWLEDGE_DB");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        var data = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(data))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            data = Path.Combine(home, ".local", "share");
        }

        return Path.Combine(data, "anima", "knowledge.db");
    }
}
