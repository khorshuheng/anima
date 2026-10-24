# Architecture

## Boundary

```
+-------------------------------------------------------------+
| Dedicated agent harness (pi or other)                       |
|  - owns agents, their lifetime, prompts and models          |
|  - creates git worktrees and merges branches               |
|  - drives analyst -> spec -> implement -> merge -> review   |
+---------------------------+---------------------------------+
                            | HTTP + bus + anima CLI
+---------------------------v---------------------------------+
| Anima (infrastructure only)                                 |
|                                                             |
|  Workflow 7415   Knowledge 7411   Bus 7412                  |
|  Automation 7413  Deploy 7414  Dashboard 7416               |
+-------------------------------------------------------------+
```

Anima never starts an agent, never calls an LLM and never runs `git worktree`.
Those are harness responsibilities. Anima stores facts, validates contracts,
transports messages, drives browsers and runs deploy scripts.

## Services

| Service | Owns |
| --- | --- |
| Workflow | questions/answers, spec + tasks, assignment metadata, results, phases, timeline, bus dispatch |
| Knowledge | SQLite graph, task/memory and workflow vocabulary, compact reads, graph events |
| Bus | publish, SSE subscribe, request/response, contract validation |
| Automation | browser sessions, CDP, YAML script execution |
| Deploy | push normalization, rule matching, script execution |
| Dashboard | read-only UI host and BFF: proxies workflow, graph and bus reads |

## Component graph

```
Anima.Contracts
  ^
Anima.Bus.Abstractions  <-- Anima.Bus.Core   <-- Anima.Bus.Service
                        <-- Anima.Bus.Client (HTTP publish + SSE + mailbox)
Anima.Knowledge.Abstractions
  ^                     <-- Anima.Knowledge.Core
  ^                     <-- Anima.Knowledge.Client
  ^                     <-- Anima.Knowledge.Service
Anima.Workflow.Abstractions
  ^                     <-- Anima.Workflow.Core (store, events, dispatcher)
  ^                     <-- Anima.Workflow.Service
Anima.Automation.Abstractions <-- Playwright / Dsl / Service
Anima.Deploy.Abstractions     <-- Deploy.Core / Service
Anima.Hosting (host defaults, repo layout, ports, contracts)
```

There is no `Anima.Agent.*`. External agents live in the harness.

## Workflow state

Facts posted by the harness and its agents drive the status:

```
analysis -> awaiting_answers -> spec_ready -> implementing
        -> implementation_complete -> merge_complete -> completed
        -> failed (failed implementation/merge/review)
```

- Questions are open until an incoming `answers` edge exists.
- A spec creates task nodes (`part_of` goal, `derived_from` spec).
- An assignment is metadata: task, agent, worktree path, branch (`worktree` node).
- Results are facts: implementation, merge, review, verification (`result` node).
- Review findings are separate `finding` nodes (`found_in` result).
- Status changes create `phase` nodes with `precedes` ordering.
- Phase gates are enforced on write and on derivation: merge requires every spec
  task to have a successful implementation; review requires a successful merge
  and cannot pass with error findings.
- Result aggregation paginates through all result pages, so `MaxPageSize` is a
  view limit, not a status-accuracy limit.

`KnowledgeWorkflowStore` is the only place that derives status. It never infers
success from an agent's silence; a result must be posted.

## Q&A path

The workflow API is the system of record. The bus publishes
`anima.workflow.v1.WorkflowEvent` on `anima.workflow.events` after each mutation
so agents can wake up, but they read state from the API. Workflow events are
queued (drop-oldest) and never block a mutation.

Why not bus-only Q&A: no durability, no replay for a late agent, no state
transition, no human-gate handling. Why not workflow-calls-bus for Q&A: the
workflow would block on a human answering, and the harness already owns the
human interaction.

## Bus

- In-proc bus for single-process composition.
- HTTP client (`Anima.Bus.Client`) for cross-process agents: POST publish, SSE
  subscribe, reconnect, bounded queues.
- `MessageEnvelope.ReplyTo` plus `IBusRequester` gives request/response with a
  private reply topic and a timeout.
- Workflow `POST /dispatch` sends `anima.workflow.v1.WorkflowRoleTask` to
  `anima.agent.<id>.inbox` and awaits `WorkflowRoleResult`; it does not create the
  agent.

## Knowledge graph

Kinds include the original task/memory vocabulary plus `spec`, `question`,
`answer`, `workflow`, `phase`, `result`, `finding`, `worktree`, `agent`.

Key edges: `tracks` workflow->goal, `specifies` spec->workflow, `asks`
question->workflow, `answers` answer->question (resolves), `phase_of`,
`precedes`, `worktree_of`, `assigned_to`, `result_of`, `found_in`,
`implements_spec`, `supersedes`.

Reads support `summary=true` (drop content) and exact property filters, which is
how the workflow finds a spec task or assignment without fetching the graph.

Persistence is SQLite (`Anima.Knowledge.Sqlite`) with versioned migrations, WAL,
foreign keys, JSON properties and an FTS5 index kept in sync by triggers. Writes
are serialized in-process and transactional; reads run on WAL connections.
In-memory is test-only.

Every successful mutation enqueues a compact `GraphEvent` on
`anima.knowledge.changed`. Publishing is bounded and runs on a background queue;
events carry ids and names, not payloads, and consumers refetch.
`Anima.Knowledge.Client` targets any memgraph-compatible service.

## Contracts

Descriptor-driven JSON: `.proto` is the IDL, `contracts/descriptors/anima.pb` is
loaded at runtime, and payloads are validated against the descriptors. No C#
message types are generated. `application/x-protobuf+json` is the bus content
type.

## UI

`ui/` is a React/Vite read-only dashboard. `Anima.Dashboard.Service` (7416)
serves `ui/dist` at `/` and proxies reads under `/api/dashboard/*` to the
knowledge, bus and workflow services, so the browser has one origin and no
mutations are exposed. `Anima.Workflow.Service` is API-only. SSE from
`/api/dashboard/bus/stream` carries `anima.**` traffic; a 10-second poll is the
fallback.

## Automation and deploy

Automation keeps Playwright behind `IBrowserDriver`/`IBrowserSession`; Chrome is
system-installed and CDP is only available on Chromium-family engines. Scripts are
declarative `anima-yaml/v1`.

Deploy normalizes push payloads, matches rules and runs repo-owned scripts with
explicit interpreter selection (`.cmd`/`.exe` on Windows, `.sh` on Unix; `.ps1`
only when configured). Anima does not create the worktree or branch that gets
deployed; the harness or CI does.

## Failure behavior

- Bus handler failures retry then dead-letter; HTTP subscriptions reconnect.
- Workflow events are best-effort; state is already durable.
- Workflow mutations are validated (unknown task, duplicate question, already
  answered) and return 422.
- Deploy runs capture exit codes and logs.
- Automation step failures stop the script and report the step index.
