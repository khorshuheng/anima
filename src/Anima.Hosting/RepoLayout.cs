namespace Anima.Hosting;

/// <summary>
/// Locates repo-owned assets (contracts, specs, scripts) from a running service.
/// Set <c>ANIMA_REPO_ROOT</c> when a published binary runs outside the repo.
/// </summary>
public static class RepoLayout
{
    private static readonly Lazy<string> RootPath = new(FindRoot);

    public static string Root => RootPath.Value;

    public static string ContractsDirectory => Path.Combine(Root, "contracts");

    public static string DescriptorDirectory => Path.Combine(ContractsDirectory, "descriptors");

    public static string SpecsDirectory => Path.Combine(Root, "specs");

    public static string ScriptsDirectory => Path.Combine(Root, "scripts");

    private static string FindRoot()
    {
        string? configured = Environment.GetEnvironmentVariable("ANIMA_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Anima.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}
