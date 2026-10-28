using System.Diagnostics;
using System.Globalization;
using Anima.Automation;
using Anima.Automation.Dsl;
using Anima.Automation.Playwright;
using Anima.Cli;
using Anima.Contracts;
using Anima.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

string command = args.Length == 0 ? "help" : args[0];
string[] rest = [.. args.Skip(1)];

try
{
    return command switch
    {
        "contracts" => Contracts(rest),
        "scripts" => await ScriptsAsync(rest),
        "browser" => Browser(rest),
        "bus" => await BusCommands.RunAsync(
            rest,
            Environment.GetEnvironmentVariable("ANIMA_BUS_URL") ?? "http://127.0.0.1:7412"),
        "workflow" => await WorkflowCommands.RunAsync(
            rest,
            Environment.GetEnvironmentVariable("ANIMA_WORKFLOW_URL") ?? "http://127.0.0.1:7415"),
        "knowledge" => await KnowledgeCommands.RunAsync(
            rest,
            Environment.GetEnvironmentVariable("ANIMA_KNOWLEDGE_URL") ?? "http://127.0.0.1:7411"),
        "help" or "--help" or "-h" => Help(),
        _ => Unknown(command),
    };
}
catch (Exception exception)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    return 1;
}

static int Help()
{
    Console.WriteLine("""
        anima - Anima monorepo helper

        usage:
          anima contracts compile [--dir <descriptor-dir>]
          anima contracts list [--dir <descriptor-dir>]
          anima contracts describe <fully.qualified.Message>
          anima contracts validate <fully.qualified.Message> <json-file>
          anima scripts list [--dir <specs-dir>]
          anima scripts validate <script.yaml>
          anima bus publish|ask|watch ...
          anima workflow start|show|ask|answer|spec|assign|result ...
          anima knowledge kinds|edge-types|vocabulary|search|get|create-node|edge|plan ...
          anima browser check [--browser <name>] [--mode launch|profile|connect] [--profile-dir <path>] [--connect <url>]
          anima scripts run <script.yaml> [--browser <name>] [--mode launch|profile|connect] [--profile-dir <path>] [--connect <url>] [--base-url <url>] [--headless] [--events|--events-errors] [--var key=value]

        --browser (or ANIMA_BROWSER) is required: chromium, chrome, msedge, firefox or webkit.
        Chrome opens a visible window by default; pass --headless (or set
        Anima:Automation:Playwright:Headless) where no display exists, such as CI.
        --events / --events-errors print the captured console, network, navigation,
        dialog and page-error log after the run; --capture-headers / --capture-bodies
        extend network events (secrets are redacted).
        --mode (or ANIMA_BROWSER_MODE) launches a throwaway Chrome (launch, default), a
        Chrome with a persistent profile (profile, dir from --profile-dir or
        ANIMA_CHROME_PROFILE), or attaches over CDP to a running Chrome (connect, url from
        --connect or ANIMA_CDP_ENDPOINT, default http://127.0.0.1:9222).
        """);
    return 0;
}

static int Browser(string[] args)
{
    if (args.Length == 0 || !string.Equals(args[0], "check", StringComparison.Ordinal))
    {
        return Help();
    }

    if (ValuedOptionWithoutValue(args) is { } missingOption)
    {
        Console.Error.WriteLine($"option {missingOption} requires a value");
        return 2;
    }

    string name = OptionValue(args, "--browser")
        ?? Environment.GetEnvironmentVariable("ANIMA_BROWSER")
        ?? "chrome";

    BrowserLaunchPlan plan;
    try
    {
        plan = BrowserLaunchPlanResolver.Resolve(CreateBrowserOptions(args, name, headless: false));
    }
    catch (InvalidOperationException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 3;
    }

    Console.WriteLine($"mode: {plan.Mode}");
    if (plan.UserDataDirectory is not null)
    {
        Console.WriteLine($"profile: {plan.UserDataDirectory}");
    }

    if (plan.ConnectEndpoint is not null)
    {
        Console.WriteLine($"endpoint: {plan.ConnectEndpoint}");
    }

    string? path = plan.ExecutablePath;
    if (path is null)
    {
        // Firefox and WebKit never use a system executable; they need the
        // Playwright-managed build.
        string hint = plan.BrowserName is "firefox" or "webkit"
            ? $" (scripts/install-playwright.sh {plan.BrowserName})"
            : string.Empty;

        Console.WriteLine($"{plan.BrowserName}: Playwright-managed build{hint}");
        return 0;
    }

    Console.WriteLine($"{plan.BrowserName}: {path}");

    string? version = OperatingSystem.IsWindows()
        ? WindowsBrowserVersion(path)
        : RunBrowserVersion(path);

    if (!string.IsNullOrWhiteSpace(version))
    {
        Console.WriteLine(version);
    }

    return 0;
}

// chrome.exe is a GUI-subsystem binary on Windows and has no console to write
// --version to, so read the file version resource instead.
static string? WindowsBrowserVersion(string path)
{
    try
    {
        return FileVersionInfo.GetVersionInfo(path).FileVersion;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
    {
        Console.Error.WriteLine($"could not read version: {exception.Message}");
        return null;
    }
}

static string? RunBrowserVersion(string path)
{
    try
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = path,
            ArgumentList = { "--version" },
            RedirectStandardOutput = true,
            UseShellExecute = false,
        });

        if (process is null)
        {
            return null;
        }

        string version = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return version.Length > 0 ? version : null;
    }
    catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
    {
        Console.Error.WriteLine($"could not query version: {exception.Message}");
        return null;
    }
}

static int Unknown(string command)
{
    Console.Error.WriteLine($"unknown command '{command}'. Run 'anima help'.");
    return 2;
}

static int Contracts(string[] args)
{
    if (args.Length == 0)
    {
        return Help();
    }

    var catalog = CreateCatalog(OptionValue(args, "--dir"));

    switch (args[0])
    {
        case "compile":
            return CompileContracts(OptionValue(args, "--dir"));

        case "list":
            foreach (var message in catalog.Messages.OrderBy(item => item.FullName, StringComparer.Ordinal))
            {
                Console.WriteLine($"{message.FullName}  ({message.Fields.InDeclarationOrder().Count} fields)");
            }

            return 0;

        case "describe":
            if (args.Length < 2)
            {
                Console.Error.WriteLine("usage: anima contracts describe <fully.qualified.Message>");
                return 2;
            }

            var descriptor = catalog.Find(args[1]);
            if (descriptor is null)
            {
                Console.Error.WriteLine($"contract '{args[1]}' is not loaded");
                return 3;
            }

            Console.WriteLine($"{descriptor.FullName} (package {descriptor.File.Package})");
            foreach (var field in descriptor.Fields.InDeclarationOrder())
            {
                Console.WriteLine($"  {field.FieldNumber,3}  {field.FieldType,-12} {field.Name}{(field.IsRepeated ? " (repeated)" : string.Empty)}");
            }

            return 0;

        case "validate":
            if (args.Length < 3)
            {
                Console.Error.WriteLine("usage: anima contracts validate <fully.qualified.Message> <json-file>");
                return 2;
            }

            var codec = new DescriptorJsonCodec(catalog);
            string json = File.ReadAllText(args[2]);
            try
            {
                Console.WriteLine(codec.ToJson(codec.Parse(args[1], json)));
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"invalid: {exception.Message}");
                return 4;
            }

        default:
            return Help();
    }
}

static int CompileContracts(string? descriptorDirectory)
{
    string protoRoot = Path.Combine(RepoLayout.ContractsDirectory, "proto");
    if (!Directory.Exists(protoRoot))
    {
        Console.Error.WriteLine($"proto root '{protoRoot}' does not exist");
        return 1;
    }

    string outputDirectory = descriptorDirectory ?? RepoLayout.DescriptorDirectory;
    Directory.CreateDirectory(outputDirectory);
    string output = Path.Combine(outputDirectory, "anima.pb");
    string protoc = Environment.GetEnvironmentVariable("PROTOC") ?? "protoc";
    string[] protos = [.. Directory
        .EnumerateFiles(protoRoot, "*.proto", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)];

    var startInfo = new ProcessStartInfo
    {
        FileName = protoc,
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        UseShellExecute = false,
    };
    startInfo.ArgumentList.Add($"--proto_path={protoRoot}");
    startInfo.ArgumentList.Add("--include_imports");
    startInfo.ArgumentList.Add("--include_source_info");
    startInfo.ArgumentList.Add($"--descriptor_set_out={output}");
    foreach (string? proto in protos)
    {
        startInfo.ArgumentList.Add(proto);
    }

    try
    {
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            Console.Error.WriteLine("could not start protoc");
            return 1;
        }

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (stdout.Length > 0)
        {
            Console.WriteLine(stdout.TrimEnd());
        }

        if (process.ExitCode != 0)
        {
            Console.Error.WriteLine(stderr.TrimEnd());
            return process.ExitCode;
        }
    }
    catch (System.ComponentModel.Win32Exception exception)
    {
        Console.Error.WriteLine($"protoc not found: {exception.Message}");
        return 1;
    }

    Console.WriteLine($"wrote {output}");
    return 0;
}

static async Task<int> ScriptsAsync(string[] args)
{
    if (args.Length == 0)
    {
        return Help();
    }

    var parser = new AutomationScriptParser();
    switch (args[0])
    {
        case "list":
            {
                var catalog = new ScriptCatalog(
                    Options.Create(new ScriptCatalogOptions
                    {
                        Directory = OptionValue(args, "--dir") ?? RepoLayout.SpecsDirectory,
                    }),
                    parser);
                foreach (var script in catalog.LoadAll())
                {
                    Console.WriteLine($"{script.Name,-32} {(script.IsValid ? "ok" : "invalid"),-8} {script.Path}");
                }

                return 0;
            }

        case "validate":
            {
                if (args.Length < 2)
                {
                    Console.Error.WriteLine("usage: anima scripts validate <script.yaml>");
                    return 2;
                }

                var result = parser.Parse(File.ReadAllText(args[1]));
                foreach (var diagnostic in result.Diagnostics)
                {
                    Console.WriteLine($"[{(diagnostic.IsError ? "error" : "warn")}] step {diagnostic.StepIndex?.ToString(CultureInfo.InvariantCulture) ?? "-"}: {diagnostic.Message}");
                }

                return result.IsValid ? 0 : 4;
            }

        case "run":
            {
                if (ValuedOptionWithoutValue(args) is { } missingOption)
                {
                    Console.Error.WriteLine($"option {missingOption} requires a value");
                    return 2;
                }

                if (args.Length < 2)
                {
                    Console.Error.WriteLine("usage: anima scripts run <script.yaml> [--browser <name>] [--mode launch|profile|connect] [--profile-dir <path>] [--connect <url>] [--base-url <url>] [--headless] [--events|--events-errors] [--capture-headers] [--capture-bodies] [--var key=value]");
                    return 2;
                }

                string browser = OptionValue(args, "--browser")
                    ?? Environment.GetEnvironmentVariable("ANIMA_BROWSER")
                    ?? "chrome";

                string? baseUrl = OptionValue(args, "--base-url");
                // Headed is the default; --headless opts out. --headed is still
                // accepted so existing invocations keep working.
                bool headless = args.Contains("--headless", StringComparer.Ordinal);
                var variables = ParseVariables(args);

                var driver = new PlaywrightBrowserDriver(
                    Options.Create(CreateBrowserOptions(args, browser, headless)),
                    NullLogger<PlaywrightBrowserDriver>.Instance);
                var runner = new YamlAutomationScriptRunner(parser);

                bool showEvents = args.Contains("--events", StringComparer.Ordinal)
                    || args.Contains("--events-errors", StringComparer.Ordinal);
                bool errorsOnly = args.Contains("--events-errors", StringComparer.Ordinal);

                await using var session = await driver.StartSessionAsync(
                    new SessionOptions
                    {
                        BaseUrl = baseUrl,
                        Headless = headless,
                        CaptureHeaders = args.Contains("--capture-headers", StringComparer.Ordinal),
                        CaptureBodies = args.Contains("--capture-bodies", StringComparer.Ordinal),
                    });

                var result = await runner.RunAsync(
                    File.ReadAllText(args[1]),
                    session,
                    new ScriptRunOptions { Variables = variables, BaseUrl = baseUrl });

                foreach (var step in result.Steps)
                {
                    Console.WriteLine($"{(step.Success ? "ok  " : "FAIL")} {step.Index,3} {step.Action,-12} {step.DurationMs,6}ms {step.Output ?? step.Error}");
                }

                if (showEvents)
                {
                    PrintEvents(session.ReadEvents(limit: int.MaxValue), session.DroppedEventCount, errorsOnly);
                }

                Console.WriteLine(result.Success ? "script succeeded" : $"script failed: {result.Error}");
                return result.Success ? 0 : 5;
            }

        default:
            return Help();
    }
}

static ISchemaCatalog CreateCatalog(string? directory) => new FileDescriptorSetCatalog(
    Options.Create(new SchemaCatalogOptions
    {
        DescriptorDirectory = directory ?? RepoLayout.DescriptorDirectory,
        WatchForChanges = false,
    }),
    NullLogger<FileDescriptorSetCatalog>.Instance);

static string? ValuedOptionWithoutValue(string[] args)
{
    string[] valuedOptions =
    [
        "--browser", "--mode", "--profile-dir", "--connect", "--base-url", "--dir", "--var",
    ];

    for (int index = 0; index < args.Length; index++)
    {
        if (!valuedOptions.Contains(args[index], StringComparer.Ordinal))
        {
            continue;
        }

        if (index == args.Length - 1 || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            return args[index];
        }
    }

    return null;
}

static void PrintEvents(IReadOnlyList<BrowserEvent> events, long dropped, bool errorsOnly)
{
    var shown = errorsOnly ? [.. events.Where(IsProblem)] : events;

    Console.WriteLine($"events: {shown.Count} shown of {events.Count} captured ({dropped} dropped)");

    foreach (var item in shown)
    {
        Console.WriteLine(
            $"{item.Sequence,5} {item.Kind,-13} {DescribeEvent(item)}");
    }
}

static bool IsProblem(BrowserEvent item)
    => item.Kind switch
    {
        BrowserEventKind.PageError or BrowserEventKind.RequestFailed or BrowserEventKind.Dialog => true,
        BrowserEventKind.Console => item.Level is "error" or "warning",
        BrowserEventKind.Response => item.Status >= 400,
        _ => false,
    };

static string DescribeEvent(BrowserEvent item)
{
    string? detail = item.Kind switch
    {
        BrowserEventKind.Console or BrowserEventKind.PageError => item.Text,
        BrowserEventKind.Navigation => item.Url,
        BrowserEventKind.Dialog => $"{item.Detail}: {item.Text}",
        BrowserEventKind.Response => $"{item.Method} {item.Status} {item.Url} ({item.DurationMs?.ToString(CultureInfo.InvariantCulture) ?? "-"}ms)",
        BrowserEventKind.RequestFailed => $"{item.Method} {item.Url} {item.Detail}",
        _ => $"{item.Method} {item.Url}",
    };

    return detail ?? string.Empty;
}

static PlaywrightOptions CreateBrowserOptions(string[] args, string browserName, bool headless)
    => new()
    {
        BrowserName = browserName,
        Mode = OptionValue(args, "--mode"),
        UserDataDirectory = OptionValue(args, "--profile-dir"),
        ConnectEndpoint = OptionValue(args, "--connect"),
        Headless = headless,
    };

static string? OptionValue(string[] args, string name)
{
    for (int index = 0; index < args.Length - 1; index++)
    {
        if (string.Equals(args[index], name, StringComparison.Ordinal))
        {
            return args[index + 1];
        }
    }

    return null;
}

static Dictionary<string, string> ParseVariables(string[] args)
{
    var variables = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int index = 0; index < args.Length - 1; index++)
    {
        if (!string.Equals(args[index], "--var", StringComparison.Ordinal))
        {
            continue;
        }

        string pair = args[index + 1];
        int separator = pair.IndexOf('=', StringComparison.Ordinal);
        if (separator > 0)
        {
            variables[pair[..separator]] = pair[(separator + 1)..];
        }
    }

    return variables;
}
