using Google.Protobuf.Reflection;

namespace Anima.Contracts;

/// <summary>
/// Read-only view of the protobuf contracts available at runtime.
/// Contracts are discovered from compiled descriptor sets, never from C# types,
/// so adding a contract only requires recompiling the <c>.proto</c> files.
/// </summary>
public interface ISchemaCatalog
{
    /// <summary>All message descriptors currently loaded.</summary>
    IReadOnlyCollection<MessageDescriptor> Messages { get; }

    /// <summary>Find a message by its fully-qualified proto name.</summary>
    MessageDescriptor? Find(string fullName);

    /// <summary>Try to find a message by its fully-qualified proto name.</summary>
    bool TryFind(string fullName, out MessageDescriptor descriptor);

    /// <summary>Re-read descriptor sets from disk.</summary>
    Task ReloadAsync(CancellationToken cancellationToken = default);
}
