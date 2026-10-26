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
| `Anima.Deploy.Service` | 7414 | Git-push sink that matches external rules and runs trusted deploy scripts |
| `Anima.Workflow.Service` | 7415 | Durable workflow state: questions, spec, assignments, results, timeline |
| `Anima.Dashboard.Service` | 7416 | Read-only dashboard: serves `ui/dist` at `/` and proxies workflow, graph and bus reads |

Ports live in `src/Anima.Hosting/AnimaPorts.cs`. `make dev` starts the six
services; agent harnesses are not started by Anima.

## Layout

```
contracts/proto/            user-editable .proto contracts (incl. workflow roles/events)
contracts/descriptors/      compiled descriptor set (runtime schema)
specs/                      agent-derived UI scripts (anima-yaml/v1)
scripts/                    runbook scripts, git hooks, deploy hook installer
examples/deploy/            deploy rule/script examples (keep real scripts outside the repo)
docker/                     multistage image + local compose demo
src/
  Anima.Contracts           descriptor catalog + descriptor-driven JSON codec
  Anima.Bus.*               abstractions, in-proc bus, HTTP client, host
  Anima.Knowledge.*         abstractions, SQLite graph, in-memory test graph, memgraph-compatible client, host
  Anima.Automation.*        driver/session abstractions, Playwright driver, YAML DSL, host
  Anima.Deploy.*            hook normalization/validation, script runner, host
  Anima.Workflow.*          state models, graph-backed store, bus dispatcher, host
  Anima.Dashboard.Service   unified dashboard host and read-only BFF proxy
  Anima.Hosting             shared host defaults, repo layout, ports
ui/                         React/Vite read-only dashboard (served at / by the dashboard)
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
make dev            # knowledge, bus, automation, deploy, workflow, dashboard
make browser-check  # Chrome executable Playwright will launch
make docker-build   # local demo image (knowledge/bus/workflow/dashboard)
make docker-up      # dashboard only, on 127.0.0.1:7416
```

Dashboard UI: `http://127.0.0.1:7416/`. Vite dev server: `make ui-dev`.
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
analysis -> awaiting_answers -> spec_ready -> assigned -> implementing ->
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

Inspect it at `http://127.0.0.1:7416/` or with `anima workflow show <id>`.
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
prints the reply envelope. Workflow `POST /api/workflows/{id}/dispatch` is
fire-and-forget: it publishes the role task and returns `202`; assignments, acks
and results use the workflow API.

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
- `--mode` (or `ANIMA_BROWSER_MODE`) picks how Chrome is obtained:
  - `launch` (default) spawns a throwaway Chrome; each session gets its own
    context, base URL and optional trace.
  - `profile` spawns Chrome on a persistent user data directory
    (`--profile-dir`, `ANIMA_CHROME_PROFILE`, or the `anima/automation/
    chrome-profile` directory under `~/.local/share` on Linux and
    `%LOCALAPPDATA%` on Windows) so logins and cookies survive restarts. All
    sessions share one context, so tracing needs `launch` mode. Nothing prunes
    that directory; delete it to reset the logged-in state.
  - `connect` attaches over CDP (`--connect`, `ANIMA_CDP_ENDPOINT`, default
    `http://127.0.0.1:9222`) to a Chrome you already started; Anima never closes
    it. The endpoint may be an http URL or the `ws://` browser endpoint that
    `/json/version` reports. Start it with a non-default profile dir, e.g.
    `google-chrome --user-data-dir=/tmp/anima-chrome --remote-debugging-port=9222`
    (Chrome 136+ refuses remote debugging on the default profile). Certificates
    are that browser's policy, so `IgnoreHttpsErrors` does not apply.
- `IgnoreHttpsErrors` (default `true`) applies to the contexts Anima creates, so
  `launch` and `profile` modes accept dev/UAT certificates; in `connect` mode the
  attached Chrome decides and the setting is ignored with a log line.
- Every session keeps an event log: console messages, unhandled page errors,
  requests, responses, request failures, main-frame navigations and JS dialogs.
  Metadata is always captured; `captureHeaders`/`captureBodies` (or
  `--capture-headers`/`--capture-bodies`) add headers and bodies.
  `authorization`/`cookie`/`set-cookie` headers and credential query parameters
  (`token`, `code`, `api_key`, ...) are always redacted, and responses above
  256 KiB are logged without their body. Dialogs are answered
  with `dialogBehavior` (`dismiss`, the default, or `accept`).
  - Poll: `GET /api/sessions/{id}/events?after=<seq>&limit=<n>` returns
    `{ items, dropped }`; `limit` must be greater than zero.
  - Stream: `GET /api/sessions/{id}/events/stream?after=<seq>` replays the buffer,
    then emits `event: browser-event` frames like `/api/bus/stream`, `: keepalive`
    comments while idle, and a final `event: closed` when the session ends. A
    cursor beyond the session's latest sequence resumes at the next event instead
    of going silent, so a persisted or foreign cursor is safe.
  - The buffer holds `Anima:Automation:Playwright:EventBufferSize` events
    (default 500) per session; `dropped` counts evictions, and a slow stream
    subscriber drops its own backlog rather than stalling the page.
  - `anima scripts run ... --events` prints the log afterwards;
    `--events-errors` keeps only console warnings/errors, page errors, failed
    requests, dialogs and 4xx/5xx responses.
- `waitForResponse` confirms a write from the network instead of the DOM: it
  matches method/url glob/status against the session event log (including a
  response that arrived before the step ran), returns the status and optionally
  stores the body in a variable. Each response satisfies at most one wait, so two
  identical writes can be confirmed separately. `status` must be an integer and
  `timeout` a positive number of milliseconds (both are rejected otherwise, so a
  typo cannot widen the match). The body is read on demand, so
  extracting works without `captureBodies` while the document stays put; a page
  that navigates straight after the response invalidates it (`captureBodies` does
  not help there — Chrome drops the body once the document changes), and the step
  then fails instead of storing an empty string.
- Server-side actions cover interaction (`click` with `modifiers: ["Control","Shift"]`
  and `value` as the mouse button, `dblclick`,
  `type` for per-keystroke filters, `clear`, `hover`, `check`, `scroll`/`scrollintoview`,
  `dragto`, `setinputfiles`), state reads (`text`, `attribute`, `value`, `texts`,
  `count`, `html`, `aria`, `isvisible`, `isenabled`, `ischecked`, `box`),
  synchronisation (`waitforurl`, `waitforcount`, `waitforloadstate`), navigation
  (`reload`, `back`, `setviewport`) and `screenshot` (viewport, full page or a
  single element). `POST /api/sessions/{id}/actions`.
- `POST /api/sessions/{id}/actions/batch` runs `{"steps":[...]}` sequentially and
  returns a per-step result shaped like a script run
  (`index`, `action`, `success`, `durationMs`, `output`, `error`); `stopOnError`
  defaults to true and the batch is capped at 200 steps. This is the multi-step
  path when you don't want to write YAML.
- `timeoutMs` applies to every action, not just the waiting ones: the driver narrows
  the session's default timeout for the duration of the action, so a click on a
  missing element honours it too.
- `press` without a `target` presses on the page itself (Escape closes a modal);
  `select` takes `value` or `values: [...]` for a multiple-select; `waitfor` takes
  `state: visible|hidden|attached|detached`; `type` takes `clear: true`; `click` and
  `dblclick` take `position: {x, y}` as well as `modifiers`. `select` accepts an
  option's value or its label, in both the single and the `values` form, and reports
  the available values when nothing matches instead of waiting out the timeout.
- `GET /api/sessions/{id}/screenshot?fullPage=&target=` returns the same capture as
  `image/png` for harnesses that cannot read the service filesystem. The action
  response returns the file path plus dimensions, size and whether a height cap
  truncated it — a vision-capable agent loads the file, and `aria` is usually the
  cheaper first look. Screenshots land in an absolute directory
  (`Anima:Automation:Playwright:ScreenshotDirectory`, default
  `<temp>/anima/screenshots`). The action reuses one file per session and view
  unless `name` is given, so polling does not fill the directory; the GET captures
  to a unique file and deletes it after reading.
- `setinputfiles` reads a path on the service host. Set
  `Anima:Automation:Playwright:UploadDirectory` to confine uploads to one
  directory; without it any path the service user can read is allowed, which is
  only acceptable because the service binds to localhost by default.
- Scripts are `anima-yaml/v1` data in `specs/`; validate then run:

```bash
anima scripts validate specs/login-smoke.yaml
anima scripts run specs/login-smoke.yaml --base-url http://uat.local
```

```yaml
name: order-write
steps:
  - goto: /orders/new
  - fill: { target: "#amount", value: "12.00" }
  - click: "#save"
  - waitForResponse: { method: POST, url: "*/api/orders", status: 201, extract: created }
  - expect: { url: /orders/42 }
```

## Deploy sink

Push hooks post to `POST /hooks/git/{provider}`; rules match repository/ref and
run a trusted script from an external `ScriptRoot`. Rules and scripts do not
need to be committed to the pushed repository. Runs are queued and logged.

- Linux/macOS: `.sh` runs with `bash`.
- Windows deployment: `.cmd`/`.bat` run with `cmd.exe`, `.exe` directly.
  **PowerShell is not assumed**; `.ps1` requires `Deploy:PowerShellExecutable`.
  `Deploy:Shell` overrides inference.
- Bare deployment repo: `scripts/install-deploy-hook.sh /srv/anima-deploy.git`;
  edit `hooks/anima-deploy.env`, then push. The hook needs only `curl`.
- Rules live in `Anima:Deploy:RulesPath`; scripts live under
  `Anima:Deploy:ScriptRoot`. Both can be outside the repository.
- Inspect runs: `GET /api/runs`, `GET /api/runs/{id}`, `GET /api/runs/{id}/log`.
- Full setup, Compose demo and GitHub Actions pattern: `docs/deployment.md`.

## Context discipline

- Workflow list/context endpoints are compact; the spec is a separate read.
- Knowledge reads support `summary=true` and exact property filters.
- Role tasks carry a task key, acceptance criteria and a spec id, not the spec.
- Command output is tail-truncated in results.
- `GET /api/workflows/{id}/context?taskKey=` is the agent brief: worktree path,
  branch, acceptance criteria, verify commands and last result summary.

## Code quality

`make check` enforces SDK analyzers, Roslynator and SonarAnalyzer under
warnings-as-errors, plus `dotnet format --verify-no-changes`. SonarLint can be
used in the IDE; SonarQube is not wired to a server. `docs/code-quality.md`
documents the rule policy.

CodeGraph is a local structural index (`codegraph init` once, `make graph-sync`
after merges). Use `codegraph context`/`impact` in the harness for task packets
and review blast radius; do not let workers rely on a stale main-worktree index.

## Verification

```bash
make check
dotnet build Anima.Workflow.slnf
dotnet test  Anima.Bus.slnf
```
