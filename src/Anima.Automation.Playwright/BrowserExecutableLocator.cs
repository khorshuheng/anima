namespace Anima.Automation.Playwright;

/// <summary>
/// Locates a system-installed Chromium-family browser on Linux or Windows so
/// automation does not depend on Playwright-downloaded binaries. Chrome is the
/// default; ANIMA_CHROME_PATH always wins.
/// </summary>
public static class BrowserExecutableLocator
{
    public static string? Find(string browserName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(browserName);

        var overridePath = Environment.GetEnvironmentVariable("ANIMA_CHROME_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return File.Exists(overridePath) ? overridePath : null;
        }

        return Candidates(browserName.Trim().ToLowerInvariant()).FirstOrDefault(File.Exists);
    }

    public static IReadOnlyList<string> Candidates(string browserName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(browserName);

        var candidates = new List<string>();
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

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

    private static void Add(List<string> candidates, string root, params string[] segments)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        candidates.Add(Path.Combine([root, .. segments]));
    }
}
