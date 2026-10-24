# Anima

Infrastructure for multi-agent workflows. Anima owns **state, contracts,
transport, UI automation and deploy execution**. A dedicated agent harness
(pi, or anything else) owns the agents: it creates them, runs them, and manages
their git worktrees. Anima never spawns an agent, never calls an LLM, and never
creates a worktree.

## Services

| Service | Port | Responsibility |
| --- | --- | --- |
| `Anima.Knowledge.Service` | 7411 | Task planning and long-term memory over one **SQLite** node/edge graph, memgraph-compatible HTTP |
| `Anima.Bus.Service` | 7412 | Agent message transport: publish, SSE subscribe, contract validation |
| `Anima.Automation.Service` | 7413 | CDP-capable UI automation sessions and YAML script execution |
| `Anima.Deploy.Service` | 7414 | Git-push sink that runs repo-owned deploy scripts |
| `Anima.Workflow.Service` | 7415 | Durable workflow state: questions, spec, assignments, results, timeline; serves the UI at `/ui` |

Ports live in `src/Anima.Hosting/AnimaPorts.cs`. `make dev` starts the five
services; agent harnesses are not started by Anima.

## Layout

```
contracts/proto/            user-editable .proto contracts (incl. workflow roles/events)
contracts/descriptors/      compiled descriptor set (runtime schema)
specs/                      agent-derived UI scripts (anima-yaml/v1)
scripts/                    runbook scripts, git hooks, deploy samples
config/                     deploy rule examples
src/
  Anima.Contracts           descriptor catalog + descriptor-driven JSON codec
  Anima.Bus.*               abstractions, in-proc bus, HTTP client, host
  Anima.Knowledge.*         abstractions, SQLite graph, in-memory test graph, memgraph-compatible client, host
  Anima.Automation.*        driver/session abstractions, Playwright driver, YAML DSL, host
  Anima.Deploy.*            hook normalization/validation, script runner, host
  Anima.Workflow.*          state models, graph-backed store, bus dispatcher, host
  Anima.Hosting             shared host defaults, repo layout, ports
ui/                         React/Vite read-only workflow visualizer (served at /ui)
tools/Anima.Cli             harness-facing CLI: contracts, scripts, browser, bus, knowledge, workflow
tests/                      one xUnit project per component
.agents/skills/             project skill: anima-orchestration (harness runbook)
Anima.sln + Anima.*.slnf    solution filters per component
```

## Quick start

Prerequisites: .NET 10 SDK, `protoc` (only when contracts change), Chrome for UI
automation, Node 20+ for the React visualizer.

```bash
make contracts      # .proto -> contracts/descriptors/anima.pb
make build
make test
make ui-build       # React UI -> ui/dist (make dev does this too)
make dev            # knowledge, bus, automation, deploy, workflow
make browser-check  # Chrome executable Playwright will launch
```

UI: `http://127.0.0.1:7415/ui/`. Vite dev server: `make ui-dev`.
Windows: `make` is optional; run the underlying `dotnet`/`npm` commands. The
deployment target has **no PowerShell assumption** (see Deploy below).

## Persistence and events

- SQLite is the production knowledge store (`Anima.Knowledge.Sqlite`): versioned
  migrations, WAL, foreign keys, JSON properties, and an FTS5 index with sync
  triggers.
- Default path: `$XDG_DATA_HOME/anima/knowledge.db` (Linux) or
  `%LOCALAPPDATA%\anima\knowledge.db` (Windows). Override with
  `Anima:Knowledge:DatabasePath` or `ANIMA_KNOWLEDGE_DB`.
- In-memory is test-only; `Provider=InMemory` exists for isolated tests, not
  production. Unknown provider/transport values fail startup instead of silently
  falling back.
- Graph writes enqueue `anima.knowledge.v1.GraphEvent` on
  `anima.knowledge.changed`; workflow mutations enqueue
  `anima.workflow.v1.WorkflowEvent` on `anima.workflow.events`. Events are
  best-effort wake-ups; readers fetch state from HTTP. Publication is bounded
  (`BusClientOptions.PublishTimeout`, default 5s) and runs off the write path.
- The UI subscribes to both topics live and falls back to polling.

## Harness workflow

The full runbook is in `.agents/skills/anima-orchestration/SKILL.md`; the design
is in `docs/workflow-design.md`.

```bash
anima workflow start --name "Feature X" --requirement "..." --repo "$PWD"
anima workflow show 1
```

State machine:

```
analysis -> awaiting_answers -> spec_ready -> implementing ->
implementation_complete -> merge_complete -> completed
                                      \-> failed
```

Facts, not guesses, drive the state:

1. Analyst agent posts questions (`anima workflow ask`).
2. Main agent reads open questions (`anima workflow questions`), asks the user,
   posts answers (`anima workflow answer`).
3. Analyst posts the refined spec and task list (`anima workflow spec-write`).
4. Harness creates worktrees, spawns implementer agents, registers assignments
   (`anima workflow assign`), and posts results (`anima workflow result`).
5. Merger and reviewer do the same; review findings are stored.
6. Status becomes `completed` only when every spec task has a successful
   implementation, a successful merge exists, and a review passes without error
   findings.

Inspect it at `http://127.0.0.1:7415/ui/` or with `anima workflow show <id>`.
Knowledge plans can be created in Anima's own graph with
`anima knowledge create-node` / `anima knowledge plan --file plan.json`.

Anima stores the assignment metadata (path, branch, agent id) but does not create
the worktree. See `docs/adr/0005-workflow-boundary.md`.

## Q&A and the bus

The workflow API is the system of record for questions and answers; the bus is
for wake-ups and agent-to-agent messages. A bus-only Q&A loop has no durability,
replay or state transition, and the workflow blocking on a human gate would be
wrong.

```bash
# watch workflow events (question added, answer added, spec updated, result posted)
anima bus watch --topics anima.workflow.events

# request/response between two running agents
anima bus ask --topic anima.agent.analyst.inbox \
  --type anima.workflow.v1.WorkflowRoleTask \
  --payload @role-task.json --timeout 120

`PublishAsync` is bounded by `BusClientOptions.PublishTimeout`; a stalled bus
cannot hang a graph/workflow mutation. Events are queued with drop-oldest
backpressure.
```

`anima bus ask` subscribes to a private reply topic, publishes `ReplyTo`, and
prints the reply envelope. `MessageEnvelope` carries `ReplyTo`; the workflow
service uses the same mechanism for `POST /api/workflows/{id}/dispatch`.

## Contracts

Protobuf is the IDL; no C# is generated. Payloads are validated against the
loaded descriptors.

```bash
make contracts
anima contracts list
anima contracts validate anima.workflow.v1.WorkflowRoleTask payload.json
```

- Unknown fields and wrong scalar types are rejected.
- `int64` is a string, `bytes` are base64, enums are names.
- Adding a contract needs a `.proto` edit and `make contracts`, not a rebuild.
- Content type is `application/x-protobuf+json`.

## UI automation

Playwright sits behind `IBrowserDriver`/`IBrowserSession`; raw CDP is exposed only
on Chromium-family engines.

- Chrome is launched from the system install (Linux and Windows paths probed,
  `ANIMA_CHROME_PATH` overrides). No browser download is required.
- `--browser chromium|msedge|firefox|webkit` switches engines; Firefox/WebKit need
  `make playwright-install BROWSERS='firefox'`.
- Scripts are `anima-yaml/v1` data in `specs/`; validate then run:

```bash
anima scripts validate specs/login-smoke.yaml
anima scripts run specs/login-smoke.yaml --base-url http://uat.local
```

## Deploy sink

Push hooks post to `POST /hooks/git/{provider}`; rules match repository/ref and
run a repo-owned script. Runs are queued and logged.

- Linux/macOS: `.sh` runs with `bash`.
- Windows deployment: `.cmd`/`.bat` run with `cmd.exe`, `.exe` directly.
  **PowerShell is not assumed**; `.ps1` requires `Deploy:PowerShellExecutable`.
  `Deploy:Shell` overrides inference.
- Hook install: `make hooks-install REPO=/path/to/repo` (or copy
  `scripts/git-hooks/post-receive`); it needs only `curl`.

Inspect runs: `GET /api/runs`, `GET /api/runs/{id}`,
`GET /api/runs/{id}/log`.

## Context discipline

- Workflow list/context endpoints are compact; the spec is a separate read.
- Knowledge reads support `summary=true` and exact property filters.
- Role tasks carry a task key, acceptance criteria and a spec id, not the spec.
- Command output is tail-truncated in results.
- `GET /api/workflows/{id}/context?taskKey=` is the agent brief: worktree path,
  branch, acceptance criteria, verify commands and last result summary.

## Verification

```bash
make check
dotnet build Anima.Workflow.slnf
dotnet test  Anima.Bus.slnf
```
