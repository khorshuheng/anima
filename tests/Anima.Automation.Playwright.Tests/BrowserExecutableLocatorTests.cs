namespace Anima.Automation.Playwright.Tests;

public sealed class BrowserExecutableLocatorTests
{
    // Windows install roots. Path segments are joined with the host separator, so
    // these assertions pin the directory layout, not the separator.
    private static readonly BrowserExecutableLocator.SearchRoots WindowsRoots = new(
        @"C:\Program Files",
        @"C:\Program Files (x86)",
        @"C:\Users\tester\AppData\Local");

    private static readonly BrowserExecutableLocator.SearchRoots NoRoots = new(null, null, null);

    /// <summary>Expected install path: the same segment layout the locator probes.</summary>
    private static string Install(string? root, params string[] segments)
        => Path.Combine([root ?? throw new InvalidOperationException("root required"), .. segments]);

    [Fact]
    public void Chrome_candidates_cover_the_standard_windows_installs()
    {
        var candidates = BrowserExecutableLocator.Candidates("chrome", WindowsRoots);

        Assert.Contains(Install(WindowsRoots.ProgramFiles, "Google", "Chrome", "Application", "chrome.exe"), candidates);
        Assert.Contains(Install(WindowsRoots.ProgramFilesX86, "Google", "Chrome", "Application", "chrome.exe"), candidates);
        Assert.Contains(Install(WindowsRoots.LocalAppData, "Google", "Chrome", "Application", "chrome.exe"), candidates);
    }

    [Fact]
    public void All_users_chrome_is_preferred_over_the_per_user_install()
    {
        var candidates = BrowserExecutableLocator.Candidates("chrome", WindowsRoots);

        var system = candidates.ToList().FindIndex(path => path.Contains("Program Files", StringComparison.Ordinal) && !path.Contains("(x86)", StringComparison.Ordinal));
        var perUser = candidates.ToList().FindIndex(path => path.Contains("AppData", StringComparison.Ordinal));

        Assert.True(system >= 0);
        Assert.True(perUser > system);
    }

    [Fact]
    public void Edge_candidates_cover_program_files_and_x86()
    {
        var candidates = BrowserExecutableLocator.Candidates("msedge", WindowsRoots);

        Assert.Contains(Install(WindowsRoots.ProgramFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"), candidates);
        Assert.Contains(Install(WindowsRoots.ProgramFiles, "Microsoft", "Edge", "Application", "msedge.exe"), candidates);
    }

    [Fact]
    public void Chromium_candidates_include_the_per_user_install()
    {
        var candidates = BrowserExecutableLocator.Candidates("chromium", WindowsRoots);

        Assert.Contains(Install(WindowsRoots.LocalAppData, "Chromium", "Application", "chrome.exe"), candidates);
    }

    [Fact]
    public void Unix_candidates_are_still_probed()
    {
        var candidates = BrowserExecutableLocator.Candidates("chrome", NoRoots);

        Assert.Contains("/usr/bin/google-chrome-stable", candidates);
        Assert.Contains("/opt/google/chrome/chrome", candidates);
    }

    [Fact]
    public void Find_returns_the_first_existing_candidate()
    {
        var expected = BrowserExecutableLocator.Candidates("chrome", WindowsRoots)[1];

        var path = BrowserExecutableLocator.Find(
            "chrome",
            WindowsRoots,
            environment: _ => null,
            fileExists: candidate => string.Equals(candidate, expected, StringComparison.Ordinal));

        Assert.Equal(expected, path);
    }

    [Fact]
    public void Find_honours_the_chrome_path_override()
    {
        var path = BrowserExecutableLocator.Find(
            "chrome",
            WindowsRoots,
            environment: name => name == "ANIMA_CHROME_PATH" ? @"D:\chrome\chrome.exe" : null,
            fileExists: _ => true);

        Assert.Equal(@"D:\chrome\chrome.exe", path);
    }

    [Fact]
    public void Find_reports_nothing_when_the_override_is_missing()
    {
        var path = BrowserExecutableLocator.Find(
            "chrome",
            WindowsRoots,
            environment: name => name == "ANIMA_CHROME_PATH" ? @"D:\gone\chrome.exe" : null,
            fileExists: _ => false);

        Assert.Null(path);
    }
}
