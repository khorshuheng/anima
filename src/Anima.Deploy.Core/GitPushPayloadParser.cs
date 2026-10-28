using System.Text.Json;

namespace Anima.Deploy;

/// <summary>
/// Normalizes GitHub, Gitea and generic push payloads into <see cref="GitPushEvent"/>.
/// The sink only needs repository, ref and commit messages to choose a rule.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance",
    "S2325:Methods and properties that don't access instance data should be static",
    Justification = "Parsers are injected services; keeping the call site instance-based is deliberate.")]
public sealed class GitPushPayloadParser
{
    public GitPushEvent Parse(string provider, string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new DeployValidationException("push payload must be a JSON object");
        }

        string repository = FirstNonEmpty(
            GetString(root, "repository.full_name"),
            GetString(root, "repository.name"),
            GetString(root, "project.path_with_namespace"),
            GetString(root, "project.name"),
            "unknown");

        string reference = FirstNonEmpty(
            GetString(root, "ref"),
            GetString(root, "branch"),
            GetString(root, "checkout_sha"),
            "refs/heads/unknown");

        string? before = GetString(root, "before") ?? GetString(root, "oldrev");
        string? after = GetString(root, "after") ?? GetString(root, "newrev") ?? GetString(root, "checkout_sha");

        string pusher = FirstNonEmpty(
            GetString(root, "pusher.name"),
            GetString(root, "pusher_name"),
            GetString(root, "user_name"),
            GetString(root, "sender.login"),
            "unknown");

        bool deleted = IsTrue(root, "deleted") || IsZeroSha(after);
        bool created = IsZeroSha(before) || IsTrue(root, "created");

        var commits = ReadCommitMessages(root);
        if (commits.Count == 0)
        {
            string? head = GetString(root, "head_commit.message");
            if (!string.IsNullOrWhiteSpace(head))
            {
                commits.Add(head);
            }
        }

        return new GitPushEvent(
            provider.Trim().ToLowerInvariant(),
            repository,
            reference,
            before,
            after,
            pusher,
            commits,
            deleted,
            created);
    }

    private static List<string> ReadCommitMessages(JsonElement root)
    {
        var messages = new List<string>();
        if (!root.TryGetProperty("commits", out var commits) || commits.ValueKind != JsonValueKind.Array)
        {
            return messages;
        }

        foreach (var commit in commits.EnumerateArray())
        {
            string? message = commit.ValueKind == JsonValueKind.Object
                ? GetString(commit, "message")
                : null;

            if (!string.IsNullOrWhiteSpace(message))
            {
                messages.Add(message);
            }
        }

        return messages;
    }

    private static string? GetString(JsonElement element, string path)
    {
        var current = element;
        foreach (string segment in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            _ => null,
        };
    }

    private static bool IsTrue(JsonElement root, string property)
        => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static bool IsZeroSha(string? sha)
        => !string.IsNullOrWhiteSpace(sha) &&
           sha.All(character => character == '0') &&
           sha.Length >= 7;

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
}
