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
| Main agent | pi session | workflow state, open questions, user answers, worktree assignments |
| Analyst | pi subagent | question list, requirement, answers; writes questions and the refined spec |
| Implementer | pi subagent | one git worktree + branch, task brief, acceptance criteria |
| Merger | pi subagent | merge worktree + branches, conflict state |
| Reviewer | pi subagent | merged worktree, spec, diff, verify commands |

The main agent asks the user questions and posts answers back. Subagents report
results; no result is inferred by Anima.

## State machine

```
analysis -> awaiting_answers -> spec_ready -> implementing -> merging -> reviewing -> completed
     |             |                |              |            |           |
     +-------------+----------------+--------------+------------+-----------+--> failed
```

Transitions are derived from posted facts, not set blindly:

- questions exist with no answers -> `awaiting_answers`
- answers posted for every open question -> analysis resumes
- spec posted -> `spec_ready`
- every task has an implementation result with `success=true` -> `implementing`
  complete (otherwise `failed` or `implementing` if some are outstanding)
- merge result with `success=true` -> `merging` complete
- review result with `passed=true` -> `completed`; otherwise `failed`

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
POST   /api/workflows/{id}/worktrees              { taskKey, baseRef? } -> path/branch
GET    /api/workflows/{id}/worktrees
DELETE /api/workflows/{id}/worktrees/{taskKey}
POST   /api/workflows/{id}/worktrees/merge        -> merge worktree path/branch
POST   /api/workflows/{id}/results                { taskKey, kind, success, summary, ... }
GET    /api/workflows/{id}/timeline               phases + result summaries
POST   /api/workflows/{id}/dispatch               bus role task to a named agent
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

`tasks` is optional; the analyst can post them with the spec.

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

## Worktree lifecycle

- Allocation: `git worktree add -b <prefix>/<workflow>-<task> <path> <baseRef>`.
- Path: `<repo>/.anima/worktrees/<workflow-id>-<task>-<short-id>` (gitignored).
- Implementation agents work only in their path; results include the branch.
- Merge worktree: `<repo>/.anima/worktrees/<workflow-id>-merge`.
- Release removes the worktree; branches remain unless `deleteBranch=true`.
- Anima requires the repo to have a commit and a resolvable base ref.

## Files and ownership

| Area | Projects |
| --- | --- |
| Bus transport/request | `Anima.Bus.Abstractions`, `Anima.Bus.Core`, `Anima.Bus.Client`, `Anima.Bus.Service` |
| Graph vocabulary/updates | `Anima.Knowledge.Abstractions`, `Anima.Knowledge.Core`, `Anima.Knowledge.Client`, `Anima.Knowledge.Service` |
| Workflow state | `Anima.Workflow.Abstractions`, `Anima.Workflow.Core`, `Anima.Workflow.Service` |
| Agent-facing CLI | `tools/Anima.Cli` |
| Contracts | `contracts/proto/anima/workflow/v1/workflow.proto` |
