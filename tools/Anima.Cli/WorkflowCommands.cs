using System.Text;
using Anima.Workflow;

namespace Anima.Cli;

internal static class WorkflowCommands
{
    public static async Task<int> RunAsync(string[] args, string baseUrl)
    {
        if (args.Length == 0)
        {
            return Help();
        }

        using var http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        try
        {
            return args[0] switch
            {
                "start" => await StartAsync(http, args),
                "list" => await GetAsync(http, "api/workflows"),
                "show" => await GetAsync(http, $"api/workflows/{RequireId(args)}"),
                "context" => await ContextAsync(http, args),
                "questions" => await QuestionsAsync(http, args),
                "ask" => await AskAsync(http, args),
                "answer" => await AnswerAsync(http, args),
                "spec" => await SpecAsync(http, args),
                "spec-write" => await SpecWriteAsync(http, args),
                "assignments" => await GetAsync(http, $"api/workflows/{RequireId(args)}/assignments"),
                "assign" => await AssignAsync(http, args),
                "ack" => await AckAsync(http, args),
                "unassign" => await UnassignAsync(http, args),
                "results" => await GetAsync(http, $"api/workflows/{RequireId(args)}/results"),
                "result" => await ResultAsync(http, args),
                "timeline" => await GetAsync(http, $"api/workflows/{RequireId(args)}/timeline"),
                "dispatch" => await DispatchAsync(http, args),
                "api" => await RawAsync(http, args),
                _ => Help(),
            };
        }
        catch (WorkflowValidationException exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 2;
        }
        catch (HttpRequestException exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 3;
        }
    }

    private static async Task<int> StartAsync(HttpClient http, string[] args)
    {
        var name = CliJson.Option(args, "--name") ?? "workflow";
        var requirement = CliJson.Option(args, "--requirement");
        var requirementFile = CliJson.Option(args, "--requirement-file");
        if (requirement is null && requirementFile is not null)
        {
            requirement = await File.ReadAllTextAsync(requirementFile);
        }

        if (string.IsNullOrWhiteSpace(requirement))
        {
            throw new WorkflowValidationException("--requirement or --requirement-file is required");
        }

        var repo = CliJson.Option(args, "--repo") ?? Directory.GetCurrentDirectory();
        var baseRef = CliJson.Option(args, "--base-ref") ?? "main";
        var tasksFile = CliJson.Option(args, "--tasks");
        var tasks = tasksFile is null
            ? null
            : CliJson.Deserialize<List<SpecTaskInput>>(await File.ReadAllTextAsync(tasksFile));

        return await PostAsync(http, "api/workflows", new WorkflowStart(name, requirement, repo, baseRef, tasks));
    }

    private static async Task<int> ContextAsync(HttpClient http, string[] args)
        => await GetAsync(
            http,
            $"api/workflows/{RequireId(args)}/context?taskKey={Uri.EscapeDataString(CliJson.Option(args, "--task") ?? string.Empty)}");

    private static async Task<int> QuestionsAsync(HttpClient http, string[] args)
    {
        var open = !CliJson.HasFlag(args, "--all");
        return await GetAsync(http, $"api/workflows/{RequireId(args)}/questions?open={open.ToString().ToLowerInvariant()}");
    }

    private static async Task<int> AskAsync(HttpClient http, string[] args)
    {
        var file = CliJson.Option(args, "--file");
        List<QuestionInput> questions;
        if (file is not null)
        {
            questions = CliJson.Deserialize<List<QuestionInput>>(await File.ReadAllTextAsync(file)) ?? [];
        }
        else
        {
            var text = CliJson.Option(args, "--text");
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new WorkflowValidationException("--text or --file is required");
            }

            questions = [new QuestionInput(text, CliJson.Option(args, "--rationale"))];
        }

        return await PostAsync(http, $"api/workflows/{RequireId(args)}/questions", new { questions });
    }

    private static async Task<int> AnswerAsync(HttpClient http, string[] args)
    {
        var file = CliJson.Option(args, "--file");
        List<AnswerInput> answers;
        if (file is not null)
        {
            answers = CliJson.Deserialize<List<AnswerInput>>(await File.ReadAllTextAsync(file)) ?? [];
        }
        else
        {
            answers = [];
            foreach (var pair in CliJson.OptionValues(args, "--answer"))
            {
                var separator = pair.IndexOf('=', StringComparison.Ordinal);
                if (separator <= 0 ||
                    !long.TryParse(pair[..separator], out var questionId))
                {
                    throw new WorkflowValidationException("--answer must be <questionId>=<text>");
                }

                answers.Add(new AnswerInput(questionId, pair[(separator + 1)..]));
            }
        }

        if (answers.Count == 0)
        {
            throw new WorkflowValidationException("at least one --answer or --file is required");
        }

        return await PostAsync(http, $"api/workflows/{RequireId(args)}/answers", new { answers });
    }

    private static async Task<int> SpecAsync(HttpClient http, string[] args)
        => await GetAsync(http, $"api/workflows/{RequireId(args)}/spec");

    private static async Task<int> SpecWriteAsync(HttpClient http, string[] args)
    {
        var file = CliJson.Option(args, "--file") ?? throw new WorkflowValidationException("--file is required");
        var markdown = await File.ReadAllTextAsync(file);
        var tasksFile = CliJson.Option(args, "--tasks");
        var tasks = tasksFile is null
            ? []
            : CliJson.Deserialize<List<SpecTaskInput>>(await File.ReadAllTextAsync(tasksFile)) ?? [];

        return await PostAsync(
            http,
            $"api/workflows/{RequireId(args)}/spec",
            new SpecInput(markdown, tasks, CliJson.Option(args, "--name")));
    }

    private static async Task<int> AssignAsync(HttpClient http, string[] args)
    {
        var taskKey = CliJson.Option(args, "--task") ?? throw new WorkflowValidationException("--task is required");
        var agent = CliJson.Option(args, "--agent") ?? throw new WorkflowValidationException("--agent is required");
        var path = CliJson.Option(args, "--path") ?? throw new WorkflowValidationException("--path is required");
        var branch = CliJson.Option(args, "--branch") ?? throw new WorkflowValidationException("--branch is required");

        return await PostAsync(
            http,
            $"api/workflows/{RequireId(args)}/assignments",
            new AssignmentInput(
                taskKey,
                agent,
                path,
                branch,
                CliJson.Option(args, "--note"),
                CliJson.Option(args, "--message-id")));
    }

    private static async Task<int> AckAsync(HttpClient http, string[] args)
    {
        var taskKey = CliJson.Option(args, "--task") ?? throw new WorkflowValidationException("--task is required");
        return await PostAsync(
            http,
            $"api/workflows/{RequireId(args)}/assignments/{Uri.EscapeDataString(taskKey)}/ack",
            new { runId = CliJson.Option(args, "--run") });
    }

    private static async Task<int> UnassignAsync(HttpClient http, string[] args)
    {
        var taskKey = CliJson.Option(args, "--task") ?? throw new WorkflowValidationException("--task is required");
        return await SendAsync(http, HttpMethod.Delete, $"api/workflows/{RequireId(args)}/assignments/{Uri.EscapeDataString(taskKey)}", null);
    }

    private static async Task<int> ResultAsync(HttpClient http, string[] args)
    {
        var summary = CliJson.Option(args, "--summary") ?? throw new WorkflowValidationException("--summary is required");
        var kind = Enum.TryParse<WorkflowResultKind>(CliJson.Option(args, "--kind"), ignoreCase: true, out var parsedKind)
            ? parsedKind
            : WorkflowResultKind.Implementation;
        var success = !CliJson.HasFlag(args, "--failure");
        var findings = new List<FindingInput>();
        foreach (var value in CliJson.OptionValues(args, "--finding"))
        {
            var separator = value.IndexOf(':', StringComparison.Ordinal);
            var severity = separator > 0 &&
                           Enum.TryParse<FindingSeverity>(value[..separator], ignoreCase: true, out var parsedSeverity)
                ? parsedSeverity
                : FindingSeverity.Info;
            findings.Add(new FindingInput(
                severity,
                separator > 0 ? value[(separator + 1)..] : value));
        }

        return await PostAsync(
            http,
            $"api/workflows/{RequireId(args)}/results",
            new ResultInput(
                CliJson.Option(args, "--task") ?? string.Empty,
                kind,
                success,
                summary,
                CliJson.OptionValues(args, "--changed-file"),
                CliJson.OptionValues(args, "--conflict"),
                findings,
                CliJson.Option(args, "--agent"),
                CliJson.Option(args, "--worktree"),
                CliJson.Option(args, "--output-tail"),
                CliJson.Option(args, "--head-sha"),
                CliJson.Option(args, "--branch"),
                CliJson.Option(args, "--base-ref"),
                CliJson.Option(args, "--run-id"),
                CliJson.OptionValues(args, "--test")
                    .Concat(CliJson.OptionValues(args, "--test-command"))
                    .ToArray(),
                CliJson.OptionValues(args, "--artifact")
                    .Concat(CliJson.OptionValues(args, "--artifact-path"))
                    .ToArray()));
    }

    private static async Task<int> DispatchAsync(HttpClient http, string[] args)
    {
        var agent = CliJson.Option(args, "--agent") ?? throw new WorkflowValidationException("--agent is required");
        var role = CliJson.Option(args, "--role") ?? throw new WorkflowValidationException("--role is required");
        var instructions = CliJson.Option(args, "--instructions") ?? throw new WorkflowValidationException("--instructions is required");
        var context = CliJson.Option(args, "--context");
        var contextJson = context is null ? null : await File.ReadAllTextAsync(context);

        return await PostAsync(
            http,
            $"api/workflows/{RequireId(args)}/dispatch",
            new DispatchRequest(
                agent,
                role,
                instructions,
                contextJson,
                CliJson.Option(args, "--task"),
                CliJson.Option(args, "--path"),
                CliJson.Option(args, "--branch"),
                CliJson.Option(args, "--note"),
                CliJson.Option(args, "--message-id")));
    }

    private static async Task<int> RawAsync(HttpClient http, string[] args)
    {
        if (args.Length < 3)
        {
            throw new WorkflowValidationException("usage: anima workflow api <METHOD> <path> [json|@file]");
        }

        var method = new HttpMethod(args[1].ToUpperInvariant());
        var body = args.Length > 3 ? await CliJson.ReadPayloadAsync(args[3]) : null;
        return await SendAsync(http, method, args[2], body);
    }

    private static async Task<int> GetAsync(HttpClient http, string path)
        => await SendAsync(http, HttpMethod.Get, path, null);

    private static async Task<int> PostAsync(HttpClient http, string path, object body)
        => await SendAsync(http, HttpMethod.Post, path, CliJson.Serialize(body));

    private static async Task<int> SendAsync(HttpClient http, HttpMethod method, string path, string? body)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"error {(int)response.StatusCode}: {text}");
            return 3;
        }

        if (text.Length > 0)
        {
            Console.WriteLine(text);
        }

        return 0;
    }

    private static long RequireId(string[] args)
    {
        if (args.Length < 2 || !long.TryParse(args[1], out var id))
        {
            throw new WorkflowValidationException("workflow id is required");
        }

        return id;
    }

    private static int Help()
    {
        Console.WriteLine("""
            usage:
              anima workflow start --requirement <text|--requirement-file <f>> --repo <path> [--name <n>] [--base-ref <ref>] [--tasks <json>]
              anima workflow list
              anima workflow show <id>
              anima workflow context <id> [--task <key>]
              anima workflow questions <id> [--all]
              anima workflow ask <id> --text <question> [--rationale <why>] | --file <questions.json>
              anima workflow answer <id> --answer <questionId>=<text> [--answer ...] | --file <answers.json>
              anima workflow spec <id>
              anima workflow spec-write <id> --file <spec.md> [--tasks <tasks.json>] [--name <n>]
              anima workflow assignments <id>
              anima workflow assign <id> --task <key> --agent <id> --path <worktree> --branch <branch> [--message-id <id>]
              anima workflow ack <id> --task <key> [--run <id>]
              anima workflow unassign <id> --task <key>
              anima workflow results <id>
              anima workflow result <id> [--task <key>] --kind <implementation|merge|review|verification> [--failure] --summary <text> [--changed-file <p>] [--conflict <p>] [--finding <severity:msg>] [--agent <id>] [--head-sha <sha>] [--branch <ref>] [--base-ref <ref>] [--run-id <id>] [--test <cmd>|--test-command <cmd>] [--artifact <path>|--artifact-path <path>]
              anima workflow timeline <id>
              anima workflow dispatch <id> --agent <id> --role <role> --instructions <text> [--context <json-file>] [--task <key> --path <worktree> --branch <branch>] [--note <text>] [--message-id <id>]  (fire-and-forget)
              anima workflow api <METHOD> <path> [json|@file]
            """);
        return 0;
    }
}
