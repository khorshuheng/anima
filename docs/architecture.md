# Architecture

## Shape

```
                    +---------------------+
                    |  Anima.Agent.Service|  7410
                    |  planner + tools    |
                    +----------+----------+
                               |
        +----------------------+-----------------------+
        |                      |                       |
+-------v--------+   +---------v----------+   +--------v---------+
| Anima.Bus      |   | Anima.Knowledge    |   | Anima.Automation |
| 7412           |   | 7411               |   | 7413             |
| topics/mailbox |   | tasks + memory     |   | sessions + DSL   |
+-------+--------+   +---------+----------+   +--------+---------+
        |                      |                       |
        +----------------------+-----------------------+
                               |
                    +----------v----------+
                    |  Anima.Deploy       |  7414
                    |  push sink + runs   |
                    +---------------------+
```

Each box is a separate project and a separate HTTP host. Hosts wire
implementations **in-process** today (`docs/adr/0001-topology.md`). The arrows are
interface seams, not project references:

- `IMessageBus` / `IBusPublisher` / `IBusSubscriber`
- `IKnowledgeGraph`
- `IBrowserDriver` / `IBrowserSession` / `IScriptRunner`
- `IDeployHookSink` / `IDeployScriptRunner`

## Component graph (project references)

```
Anima.Contracts                       (no component deps)
  ^
Anima.Bus.Abstractions  <-- Anima.Bus.Core <-- Anima.Bus.Service
Anima.Knowledge.Abstractions
  ^                     <-- Anima.Knowledge.Core
  ^                     <-- Anima.Knowledge.Client
  ^                     <-- Anima.Knowledge.Service
Anima.Automation.Abstractions
  ^                     <-- Anima.Automation.Playwright
  ^                     <-- Anima.Automation.Dsl
  ^                     <-- Anima.Automation.Service
Anima.Deploy.Abstractions
  ^                     <-- Anima.Deploy.Core <-- Anima.Deploy.Service
Anima.Agent.Abstractions
  ^                     <-- Anima.Agent.Core <-- Anima.Agent.Service
Anima.Hosting         (shared host defaults, contracts, repo layout)
```

`Anima.Agent.Core` references the other `*.Abstractions` projects only. The agent
host decides which implementations to load, which is why the default topology is
in-process and the remote one is a composition change.

## Agent turn

1. `AgentWorker` creates its mailbox (`anima.agent.<id>.inbox`) and waits.
2. A message (HTTP turn or delegated envelope) is decoded to input text.
3. `IAgentPlanner` returns an `AgentPlan`: respond, delegate, call a tool, run a
   script, or escalate.
4. `AgentRuntime` executes the plan and publishes
   `anima.agent.v1.AgentTurnResult` on `anima.task.result` and the agent outbox.
5. Delegation is a mailbox send; the recipient replies to the sender's inbox.

The default planner is deterministic (`/tool {json}`, `delegate:<agent>: ...`,
`script: ...`). Replace `IAgentPlanner` with an LLM adapter; nothing else changes.

## Knowledge graph

Tasks and memory share one model, mirroring memgraph:

- Node: `id, kind, name, description, content, properties, resolved, timestamps`.
- Edge: `id, source, destination, edgeType, createdAt`.
- Kinds: `goal, task, change, decision, bug, test, repo, file, db_table,
  db_view, memory, note`.
- Edges declare source/destination kinds, whether they resolve the destination,
  and whether they allow one outgoing edge.

Key invariants:

- `resolved` is derived, never a field: an incoming `implements` or `fixes` edge
  resolves a node.
- `part_of` builds the plan tree; `depends_on` orders tasks.
- `belongs_to` (goal -> repo) anchors a work tree; it is single-outgoing.
- A standalone task requires a parent whose `part_of` ancestry reaches an
  anchored goal.
- Plans validate keys, parents, dependencies and cycles before writing.

The in-memory implementation is the default. `Anima.Knowledge.Client` implements
the same interface against memgraph's HTTP API (`X-Session-Id`, kind/edge
vocabulary resolved to ids), so an agent can use the existing local service.

Persistence is a deliberate gap in the scaffold: replace the store behind
`InMemoryKnowledgeGraph` (SQLite or memgraph) without changing the interface.

## Contracts

`Anima.Contracts` loads descriptor sets produced by `protoc` at runtime. The codec
validates JSON against the descriptor: unknown fields are rejected, scalars are
type-checked, `int64` is a string, `bytes` are base64, enums are names, and
well-known types use canonical JSON forms. No C# message classes exist.

`IMessageCodec` is the seam for a future binary protobuf codec; the current wire
form is the descriptor-driven JSON projection.

## Automation

`IBrowserSession` is the script target. `Anima.Automation.Playwright` launches an
installed Chrome/Chromium/Edge executable (or a Playwright-managed build for
firefox/webkit) and exposes raw CDP only on Chromium-family engines.

`anima-yaml/v1` is parsed into typed steps, validated with diagnostics, and
interpreted against a session. Agents derive a script from an exploratory
session, then `specs/*.yaml` runs unattended.

## Deploy sink

`POST /hooks/git/{provider}` verifies HMAC (when a secret is configured),
normalizes GitHub/Gitea/generic payloads, matches rules (`repositoryPattern`,
`refPattern`), and enqueues runs. `DeployWorker` executes scripts sequentially
with push metadata in environment variables and records logs.

Interpreter selection is explicit: `.cmd`/`.bat` -> `cmd.exe /c`, `.exe` direct,
`.sh` -> `bash`, `.ps1` only with a configured PowerShell executable. There is no
implicit shell and no PowerShell assumption on the Windows target.

## Failure behavior

- Bus handler failures retry, then dead-letter to `anima.dlq` (never recursively).
- Agents turn tool/planner exceptions into a failed turn result, not a crash.
- Automation step failures stop the script by default and report the step index.
- Deploy runs capture exit codes and logs; a failed script marks the run failed.
