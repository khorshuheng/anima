using System.Diagnostics;
using System.Globalization;
using Anima.Automation;
using Anima.Automation.Dsl;
using Anima.Automation.Playwright;
using Anima.Contracts;
using Anima.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

var command = args.Length == 0 ? "help" : args[0];
var rest = args.Skip(1).ToArray();

try
{
    return command switch
    {
        "contracts" => Contracts(rest),
        "scripts" => await ScriptsAsync(rest),
        "browser" => Browser(rest),
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
          anima browser check [--browser <name>]
          anima scripts run <script.yaml> [--browser <name>] [--base-url <url>] [--headed] [--var key=value]

        --browser (or ANIMA_BROWSER) is required: chromium, chrome, msedge, firefox or webkit.
        """);
    return 0;
}

static int Browser(string[] args)
{
    if (args.Length == 0 || !string.Equals(args[0], "check", StringComparison.Ordinal))
    {
        return Help();
    }

    var name = OptionValue(args, "--browser")
        ?? Environment.GetEnvironmentVariable("ANIMA_BROWSER")
        ?? "chrome";

    if (name is "firefox" or "webkit")
    {
        Console.WriteLine($"{name}: Playwright-managed build (scripts/install-playwright.sh {name})");
        return 0;
    }

    var path = BrowserExecutableLocator.Find(name);
    if (path is null)
    {
        Console.Error.WriteLine($"{name}: executable not found (set ANIMA_CHROME_PATH or Playwright:ExecutablePath)");
        return 3;
    }

    Console.WriteLine($"{name}: {path}");
    try
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = path,
            ArgumentList = { "--version" },
            RedirectStandardOutput = true,
            UseShellExecute = false,
        });

        if (process is not null)
        {
            var version = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            if (version.Length > 0)
            {
                Console.WriteLine(version);
            }
        }
    }
    catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
    {
        Console.Error.WriteLine($"could not query version: {exception.Message}");
    }

    return 0;
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
            var json = File.ReadAllText(args[2]);
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
    var protoRoot = Path.Combine(RepoLayout.ContractsDirectory, "proto");
    if (!Directory.Exists(protoRoot))
    {
        Console.Error.WriteLine($"proto root '{protoRoot}' does not exist");
        return 1;
    }

    var outputDirectory = descriptorDirectory ?? RepoLayout.DescriptorDirectory;
    Directory.CreateDirectory(outputDirectory);
    var output = Path.Combine(outputDirectory, "anima.pb");
    var protoc = Environment.GetEnvironmentVariable("PROTOC") ?? "protoc";
    var protos = Directory
        .EnumerateFiles(protoRoot, "*.proto", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)
        .ToArray();

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
    foreach (var proto in protos)
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

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
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
                if (args.Length < 2)
                {
                    Console.Error.WriteLine("usage: anima scripts run <script.yaml> [--browser <name>] [--base-url <url>] [--headed] [--var key=value]");
                    return 2;
                }

                var browser = OptionValue(args, "--browser")
                    ?? Environment.GetEnvironmentVariable("ANIMA_BROWSER")
                    ?? "chrome";

                var baseUrl = OptionValue(args, "--base-url");
                var headless = !args.Contains("--headed", StringComparer.Ordinal);
                var variables = ParseVariables(args);

                var driver = new PlaywrightBrowserDriver(
                    Options.Create(new PlaywrightOptions { BrowserName = browser, Headless = headless }),
                    NullLogger<PlaywrightBrowserDriver>.Instance);
                var runner = new YamlAutomationScriptRunner(parser);

                await using var session = await driver.StartSessionAsync(
                    new SessionOptions { BaseUrl = baseUrl, Headless = headless });

                var result = await runner.RunAsync(
                    File.ReadAllText(args[1]),
                    session,
                    new ScriptRunOptions { Variables = variables, BaseUrl = baseUrl });

                foreach (var step in result.Steps)
                {
                    Console.WriteLine($"{(step.Success ? "ok  " : "FAIL")} {step.Index,3} {step.Action,-12} {step.DurationMs,6}ms {step.Output ?? step.Error}");
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

static string? OptionValue(string[] args, string name)
{
    for (var index = 0; index < args.Length - 1; index++)
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
    for (var index = 0; index < args.Length - 1; index++)
    {
        if (!string.Equals(args[index], "--var", StringComparison.Ordinal))
        {
            continue;
        }

        var pair = args[index + 1];
        var separator = pair.IndexOf('=', StringComparison.Ordinal);
        if (separator > 0)
        {
            variables[pair[..separator]] = pair[(separator + 1)..];
        }
    }

    return variables;
}
