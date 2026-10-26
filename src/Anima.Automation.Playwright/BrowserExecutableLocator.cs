namespace Anima.Automation.Playwright;

/// <summary>
/// Locates a system-installed Chromium-family browser on Linux or Windows so
/// automation does not depend on Playwright-downloaded binaries. Chrome is the
/// default; ANIMA_CHROME_PATH always wins.
/// </summary>
public static class BrowserExecutableLocator
{
    /// <summary>
    /// Browser install roots. Injectable so Windows layouts can be asserted on any
    /// build host, which matters because the Windows roots resolve to empty strings
    /// off Windows and would otherwise leave those candidates untested.
    /// </summary>
    public sealed record SearchRoots(string? ProgramFiles, string? ProgramFilesX86, string? LocalAppData)
    {
        public static SearchRoots ForCurrentPlatform() => new(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    }

    /// <summary>Finds the system browser, or null when no candidate exists.</summary>
    public static string? Find(
        string browserName,
        SearchRoots? roots = null,
        Func<string, string?>? environment = null,
        Func<string, bool>? fileExists = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(browserName);

        var readEnvironment = environment ?? (name => Environment.GetEnvironmentVariable(name));
        var exists = fileExists ?? (path => File.Exists(path));

        var overridePath = readEnvironment("ANIMA_CHROME_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return exists(overridePath) ? overridePath : null;
        }

        return Candidates(browserName, roots ?? SearchRoots.ForCurrentPlatform()).FirstOrDefault(exists);
    }

    public static IReadOnlyList<string> Candidates(string browserName)
        => Candidates(browserName, SearchRoots.ForCurrentPlatform());

    public static IReadOnlyList<string> Candidates(string browserName, SearchRoots roots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(browserName);
        ArgumentNullException.ThrowIfNull(roots);

        var candidates = new List<string>();
        var programFiles = roots.ProgramFiles;
        var programFilesX86 = roots.ProgramFilesX86;
        var localAppData = roots.LocalAppData;

        switch (browserName.Trim().ToLowerInvariant())
        {
            case "chrome":
                Add(candidates, programFiles, "Google", "Chrome", "Application", "chrome.exe");
                Add(candidates, programFilesX86, "Google", "Chrome", "Application", "chrome.exe");
                Add(candidates, localAppData, "Google", "Chrome", "Application", "chrome.exe");
                candidates.Add("/usr/bin/google-chrome-stable");
                candidates.Add("/usr/bin/google-chrome");
                candidates.Add("/usr/bin/google-chrome-beta");
                candidates.Add("/opt/google/chrome/chrome");
                break;

            case "chromium":
                Add(candidates, localAppData, "Chromium", "Application", "chrome.exe");
                Add(candidates, programFiles, "Chromium", "Application", "chrome.exe");
                Add(candidates, programFilesX86, "Chromium", "Application", "chrome.exe");
                candidates.Add("/usr/bin/chromium");
                candidates.Add("/usr/bin/chromium-browser");
                break;

            case "msedge":
                Add(candidates, programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe");
                Add(candidates, programFiles, "Microsoft", "Edge", "Application", "msedge.exe");
                candidates.Add("/usr/bin/microsoft-edge-stable");
                candidates.Add("/usr/bin/microsoft-edge");
                break;
        }

        return candidates;
    }

    private static void Add(List<string> candidates, string? root, params string[] segments)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        candidates.Add(Path.Combine([root, .. segments]));
    }
}
