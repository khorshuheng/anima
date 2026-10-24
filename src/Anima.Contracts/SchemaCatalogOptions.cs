namespace Anima.Contracts;

/// <summary>Where the runtime contract descriptors live.</summary>
public sealed class SchemaCatalogOptions
{
    /// <summary>Directory containing compiled <c>*.pb</c> descriptor sets.</summary>
    public string DescriptorDirectory { get; set; } = "contracts/descriptors";

    /// <summary>Descriptor set file pattern.</summary>
    public string FilePattern { get; set; } = "*.pb";

    /// <summary>Reload the catalog when descriptor files change on disk.</summary>
    public bool WatchForChanges { get; set; }
}
