# Anima

A .NET 10 monorepo for multi-agent workflows. Five runnable services, shared
libraries with narrow seams, and repo-owned contracts that agents can change
without rebuilding C#.

## Services

| Service | Port | Responsibility |
| --- | --- | --- |
| `Anima.Agent.Service` | 7410 | Agent turns, planner/tool loop, mailbox worker |
| `Anima.Knowledge.Service` | 7411 | Task planning **and** long-term memory over one node/edge graph, memgraph-compatible HTTP |
| `Anima.Bus.Service` | 7412 | Lightweight agent message bus (topics, SSE stream, contract validation) |
| `Anima.Automation.Service` | 7413 | CDP-capable UI automation sessions and YAML script execution |
| `Anima.Deploy.Service` | 7414 | Git-push sink that runs repo-owned deploy scripts |

Ports are defined once in `src/Anima.Hosting/AnimaPorts.cs`.

## Layout

```
contracts/proto/            user-editable .proto contracts
contracts/descriptors/      compiled descriptor set (runtime schema)
specs/                      agent-derived UI scripts (anima-yaml/v1)
scripts/                    contract compilation, dev runner, deploy scripts, git hooks
config/                     deploy rule examples
src/
  Anima.Contracts           descriptor catalog + descriptor-driven JSON codec
  Anima.Bus.*               abstractions, in-proc bus, HTTP host
  Anima.Knowledge.*         abstractions, in-memory graph, memgraph-compatible client, host
  Anima.Automation.*        driver/session abstractions, Playwright driver, YAML DSL, host
  Anima.Deploy.*            hook normalization/validation, script runner, host
  Anima.Agent.*             planner/tool/runtime abstractions, runtime, host
  Anima.Hosting             shared host defaults, repo layout, ports
  Anima.<Area>.Service      thin HTTP hosts
tools/Anima.Cli             agent-facing CLI (contracts, scripts, browser check)
tests/                      one xUnit project per component
Anima.sln + Anima.*.slnf    solution filters per component for parallel work
```

## Quick start (Linux/macOS)

Prerequisites: .NET 10 SDK, `protoc` (only needed when contracts change), Chrome
for UI automation.

```bash
make contracts      # .proto -> contracts/descriptors/anima.pb
make build
make test
make dev            # all five services; ports above
make browser-check  # shows the Chrome executable Playwright will launch
```

Windows (including the deployment target): `make` is optional. Use the same
commands directly:

```bat
dotnet run --project tools/Anima.Cli -- contracts compile
dotnet build Anima.sln
dotnet test Anima.sln
dotnet run --project src\Anima.Knowledge.Service
```

## Contracts

Protobuf is the IDL, but **no C# is generated**. `contracts/descriptors/anima.pb`
is loaded at runtime and payloads are validated against the descriptors.

- Add or edit a `.proto` under `contracts/proto/anima/<area>/v1/`.
- Run `make contracts` (or the CLI `contracts compile`).
- Publish JSON that matches the message. Unknown fields, wrong scalar types and
  invalid timestamps are rejected at the bus boundary.
- `int64` is a string, `bytes` are base64, enums are names.
- Validate first: `anima contracts validate <Message> payload.json`, or
  `POST /api/bus/validate`.

See `contracts/README.md` and `docs/adr/0002-contracts.md`.

## UI automation

`Anima.Automation` drives a browser behind `IBrowserDriver`/`IBrowserSession`.
Playwright is an implementation detail; raw CDP is exposed when the engine
supports it.

- **Chrome is assumed and launched from the system install** (no browser
  download): Linux and Windows locations are probed, `ANIMA_CHROME_PATH`
  overrides. `make browser-check` prints the resolved executable.
- Override the engine with `--browser chromium|msedge|firefox|webkit` or
  `ANIMA_BROWSER`. Firefox/WebKit require Playwright-managed builds
  (`make playwright-install BROWSERS='firefox'`); Chromium-family engines use the
  installed executable.
- CDP is available only on Chromium-family engines; `IBrowserSession.Cdp` is null
  otherwise. No stealth patches are applied — these are dev/UAT pages we own.

Scripts are declarative and reusable without an agent in the loop:

```yaml
name: login-smoke
variables:
  user: "${env.UAT_USER}"
steps:
  - goto: /login
  - fill: { target: "#username", value: "${user}" }
  - click: "button[type=submit]"
  - expect: { target: "#dashboard", text: "Welcome" }
  - extract: { name: token, target: "#session-token" }
```

```bash
dotnet run --project tools/Anima.Cli -- scripts validate specs/login-smoke.yaml
dotnet run --project tools/Anima.Cli -- scripts run specs/login-smoke.yaml --base-url http://uat.local
```

Run one through the service: `POST /api/sessions`, then
`POST /api/sessions/{id}/scripts` with `{ "source": "..." }`.

## Deploy sink

`Anima.Deploy` receives a git push hook, normalizes the payload, matches rules,
and runs a repo-owned script. Pushes never block: runs are queued and logged.

```json
[
  {
    "name": "uat-main",
    "repositoryPattern": "*",
    "refPattern": "refs/heads/main",
    "script": "example-deploy.cmd",
    "environment": "uat",
    "enabled": true
  }
]
```

- Linux/macOS: `.sh` scripts run with `bash`.
- Windows deployment: `.cmd`/`.bat` run with `cmd.exe`, `.exe` runs directly.
  **PowerShell is not assumed**; `.ps1` requires an explicit
  `Deploy:PowerShellExecutable`. `Deploy:Shell` overrides inference entirely.
- Start with `RulesPath=config/deploy.rules.json` or
  `config/deploy.rules.windows.json`, or put rules under `Anima:Deploy:Rules`.
- Hook install: `make hooks-install REPO=/path/to/repo` (or copy
  `scripts/git-hooks/post-receive` into `.git/hooks/`). It needs only `curl`,
  which Git for Windows bundles.
- Secret enabled? The hook computes HMAC-SHA256 when `ANIMA_DEPLOY_SECRET` is
  set and `openssl` exists. The sink verifies `X-Anima-Signature`,
  `X-Gitea-Signature` or `X-Hub-Signature-256`.

Inspect runs at `GET /api/runs`, `GET /api/runs/{id}`, `GET /api/runs/{id}/log`.

## Agent workflow

The default planner is deterministic; replace `IAgentPlanner` with an LLM-backed
implementation without touching tools or the runtime.

```bash
# call a tool
curl -X POST localhost:7410/api/agents/orchestrator/turns \
  -H 'content-type: application/json' \
  -d '{"input":"/knowledge.remember {\"kind\":\"memory\",\"name\":\"Login flow\"}"}'

# delegate to another agent
curl -X POST localhost:7410/api/agents/orchestrator/turns \
  -H 'content-type: application/json' \
  -d '{"input":"delegate:worker: run specs/login-smoke.yaml"}'
```

Built-in tools: `knowledge.remember`, `knowledge.search`,
`automation.run_script`. Delegation uses per-agent inboxes on the bus.

Task management and memory are the same graph: goals and tasks are nodes, and a
task is resolved when a `change` node has an `implements` edge to it. See
`docs/adr/0003-knowledge-service.md`.

## Topology

Services are separate projects, but hosts wire them **in-process** by default:
the agent host registers the in-memory bus, graph, DSL and Playwright driver.
Every boundary is an interface, so moving to HTTP or a broker is a
composition-root change.

For more than one agent process, point agents at the shared knowledge service so
task and memory state is common:

```json
{ "Anima": { "Knowledge": { "Transport": "Http", "BaseUrl": "http://127.0.0.1:7411" } } }
```

The default (`InMemory`) keeps a single agent self-contained.

- `IKnowledgeGraph` -> `Anima.Knowledge.Client.AddKnowledgeClient()` talks to a
  memgraph-compatible service.
- `IMessageBus` -> replace `AddInProcBus()` with a broker-backed implementation.
- `IBrowserDriver` -> point at a remote automation service, or keep Playwright
  local.
- `IDeployHookSink` -> keep the sink local, or call it over HTTP.

Each service also exposes its own HTTP host, so the remote topology is already
reachable; only the client-side composition changes.

## Verification

```bash
make check                       # dotnet format check + build + test
dotnet build Anima.Knowledge.slnf
dotnet test  Anima.Automation.slnf
```

Build is warnings-as-errors with the .NET analyzers enabled.
