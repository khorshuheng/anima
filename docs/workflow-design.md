# Workflow design: external agents, no spawning

Status: implementation brief

## Non-negotiable constraint

Anima never spawns agents. There is no process launcher, no subagent runner and
no LLM client in this repo. External agents (pi, myself, other agent runtimes)
connect to Anima services over HTTP and the bus. Anima stores state, allocates
git worktrees, and transports messages.

## Roles played by external agents

| Role | Who drives it | What Anima gives it |
| --- | --- | --- |
| Main agent | pi session | workflow state, open questions, user answers, assignment metadata |
| Analyst | pi subagent | question list, requirement, answers; writes questions and the refined spec |
| Implementer | pi subagent | a harness-created worktree + branch, task brief, acceptance criteria |
| Merger | pi subagent | a harness-created merge worktree + branches, conflict state |
| Reviewer | pi subagent | merged worktree, spec, diff, verify commands |

The main agent asks the user questions and posts answers back. Subagents report
results; no result is inferred by Anima.

## State machine

```
analysis -> awaiting_answers -> spec_ready -> implementing -> implementation_complete
        -> merge_complete -> completed
     |             |                |              |            |           |
     +-------------+----------------+--------------+------------+-----------+--> failed
```

Transitions are derived from posted facts, not set blindly:

- questions exist with no answers -> `awaiting_answers`
- answers posted for every open question -> analysis resumes
- spec posted -> `spec_ready`
- every task has an implementation result with `success=true` -> `implementing`
  complete (otherwise `failed` or `implementing` if some are outstanding)
- merge result with `success=true` (and all tasks implemented) -> `merge_complete`
- review result with `success=true`, no error findings, and a successful merge
  -> `completed`; otherwise `failed`
- an invalid merge/review is rejected at write time with 422

## HTTP surface (port 7415)

```
POST   /api/workflows
GET    /api/workflows/{id}
GET    /api/workflows/{id}/context?taskKey=       compact agent brief
GET    /api/workflows/{id}/questions?open=true
POST   /api/workflows/{id}/questions              { questions:[{text,rationale?}] }
POST   /api/workflows/{id}/answers                { answers:[{questionId,text}] }
GET    /api/workflows/{id}/spec
POST   /api/workflows/{id}/spec                   { markdown, tasks:[...] }
POST   /api/workflows/{id}/assignments            { taskKey, agentId, worktreePath, branch }
GET    /api/workflows/{id}/assignments
DELETE /api/workflows/{id}/assignments/{taskKey}
POST   /api/workflows/{id}/results                { taskKey, kind, success, summary, ... }
GET    /api/workflows/{id}/timeline               phases + result summaries
POST   /api/workflows/{id}/dispatch               bus role task; optional assignment
                                                 (--task/--path/--branch) returns messageId
```

Start request:

```json
{
  "name": "Feature X",
  "requirement": "free text from the user",
  "repoPath": "/abs/path/to/repo",
  "baseRef": "main",
  "tasks": [
    {
      "key": "api",
      "title": "Add endpoint",
      "description": "POST /x",
      "acceptanceCriteria": ["201 on valid body"],
      "verifyCommands": ["dotnet test tests/X.Tests"]
    }
  ]
}
```

`tasks` is optional. If the spec is posted with an empty task list, the start
request's tasks are used; otherwise the analyst can post them with the spec.

## Knowledge model additions

Kinds: `spec`, `question`, `answer`, `workflow`, `phase`, `review`, `finding`,
`worktree`, `agent`.

Edges: `tracks` (workflow->goal), `specifies` (spec->workflow|goal),
`asks` (question->workflow), `answers` (answer->question, resolves),
`phase_of` (phase->workflow), `precedes` (phase->phase),
`reviews` (review->workflow|change|spec), `found_in` (finding->review),
`assigned_to` (phase->agent), `worktree_of` (worktree->workflow|phase),
`implements_spec` (change->spec), `supersedes` (spec->spec).

A question is open until an incoming `answers` edge exists. This keeps
resolution graph-derived rather than adding a status field to everything.

## Context discipline

- List endpoints are summary-only by default; content requires a specific read.
- Specs are referenced by node id; role tasks carry `specId` and a short brief,
  never the full spec.
- Command output is tail-truncated (default 2000 chars) in results and CLI.
- The workflow context endpoint returns: task key, title, acceptance criteria,
  worktree path/branch, spec id, and the last result summary only.
- No endpoint returns raw agent transcripts.

## Dashboard

`Anima.Dashboard.Service` (port 7416) serves the unified UI at `/` and a
read-only BFF at `/api/dashboard/*`. It proxies workflow, knowledge-graph and
bus-diagnostics reads and the bus SSE stream. The knowledge graph and bus
contracts are defined by their services; the dashboard does not mutate state.

## Q&A path (decision)

The workflow API is the system of record for questions and answers:

- Analyst subagent: `POST /questions`; main agent: `GET /questions?open=true`;
  user answers: `POST /answers`; analyst: `GET /answers?since=<id>`.
- The workflow service publishes `anima.workflow.v1.WorkflowEvent` on
  `anima.workflow.events` after every mutation (question added, answer added,
  spec updated, assignment registered, result posted, status changed).
- Agents subscribe with `anima bus watch --topics anima.workflow.events` to wake
  up, but they never depend on the event to read state. Bus-only Q&A is rejected:
  it has no durability, replay, or state transition, and the workflow service
  blocking on a human gate would be wrong.
- `POST /dispatch` is for agent-to-agent role tasks, not for Q&A. It publishes a
  role task and awaits a reply; the harness owns the agent's lifetime. When
  `taskKey`, `worktreePath` and `branch` are supplied, the service registers the
  assignment with the generated envelope message id before publishing. The
  response includes `messageId`; the worker acknowledges with its run id.
- Successful implementation, merge and review results require structured
  evidence: a 7-64 character hexadecimal `headSha`, `branch`, `baseRef` and at
  least one `testCommands` entry. Failed results may omit evidence.

## Bus protocol

- `MessageEnvelope` gains `ReplyTo`.
- `IBusRequester.AskAsync` subscribes to a private reply topic, publishes with
  `ReplyTo`, enforces a timeout, and correlates by envelope id/correlation id.
- `Anima.Bus.Client` implements `IMessageBus` over `POST /api/bus/publish` and
  `GET /api/bus/stream` (SSE), so workflow-service and operator tooling can use
  a remote bus without in-proc coupling.
- Workflow dispatch publishes `anima.workflow.v1.WorkflowRoleTask` to
  `anima.agent.<agentId>.inbox` and awaits `WorkflowRoleResult` on `ReplyTo`.
  External agents can also use `anima bus watch/ask/publish` directly.
- Anima never starts the agent that answers.

## Worktree ownership

The harness creates and removes worktrees. Anima only stores the assignment:
task key, agent id, path, branch and status. There is no Anima endpoint that runs
`git worktree`, and no Anima code that starts an agent.

1. Harness: `git worktree add -b anima/<workflow>-<task> <path> <baseRef>`.
2. Harness: `anima workflow assign <id> --task <key> --agent <id> --path <path> --branch <branch>`.
3. Harness spawns the implementer in that path.
4. Implementer works, runs verify commands, posts a result with the branch and
   changed files.
5. Harness removes the worktree after merge (branches can stay for audit).

## Files and ownership

| Area | Projects |
| --- | --- |
| Bus transport/request | `Anima.Bus.Abstractions`, `Anima.Bus.Core`, `Anima.Bus.Client`, `Anima.Bus.Service` |
| Graph vocabulary/updates | `Anima.Knowledge.Abstractions`, `Anima.Knowledge.Core`, `Anima.Knowledge.Client`, `Anima.Knowledge.Service` |
| Workflow state | `Anima.Workflow.Abstractions`, `Anima.Workflow.Core`, `Anima.Workflow.Service` |
| Agent-facing CLI | `tools/Anima.Cli` |
| Contracts | `contracts/proto/anima/workflow/v1/workflow.proto` |
