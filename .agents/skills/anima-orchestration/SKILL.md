---
name: anima-orchestration
description: Run Anima's harness-driven multi-agent workflow - analyst questions, user answers, refined spec, parallel implementation in harness-created git worktrees, merge and review - with durable state in the knowledge graph and messages on the bus. Use when asked to plan, implement or review a feature through Anima, or to use anima workflow/bus commands.
---

# Anima orchestration

Anima is infrastructure. **You (the harness) own the agents and the git
worktrees.** Anima stores state, validates contracts, transports messages, runs
UI automation and deploy scripts. It never spawns an agent and never creates a
worktree; do not wait for it to.

Read `docs/workflow-design.md` for the full contract. Services and ports:
knowledge 7411, bus 7412, automation 7413, deploy 7414, workflow 7415,
dashboard 7416.

## 0. Preconditions

```bash
make dev                 # knowledge, bus, automation, deploy, workflow, dashboard
anima workflow list      # CLI talks to ANIMA_WORKFLOW_URL (default 7415)
```

If the repo has no commit, create one before making worktrees. The workflow
service does not touch git, but your worktree tooling does.

## 1. Start the workflow

```bash
anima workflow start \
  --name "Feature X" \
  --requirement-file .anima/requirement.md \
  --repo "$PWD" --base-ref main
```

Note the returned `id`. Optionally pass `--tasks tasks.json` if the requirement
already implies a task breakdown.

## 2. Analyst Q&A loop

Run the analyst as a subagent (harness-owned). Give it:

- the workflow id and requirement,
- the command to post questions: `anima workflow ask <id> --text "..."`,
- a strict limit: only post questions whose answers change the spec.

Then, as the main agent:

```bash
anima workflow questions <id>          # open questions only
# ask the human, then:
anima workflow answer <id> --answer 12="Target is Windows" --answer 13="Chrome only"
```

Repeat until the analyst has no more questions. Stop when the analyst says the
spec is ready; do not invent questions for Anima.

Watch for wake-ups if useful, but always re-read state (the visualizer at
`http://127.0.0.1:7416/` does the same):

```bash
anima bus watch --topics anima.workflow.events
```

## 3. Write the spec

The analyst writes a markdown spec and a task list. Post both:

```bash
anima workflow spec-write <id> --file spec.md --tasks tasks.json
anima workflow show <id>       # status -> specReady
```

`tasks.json`:

```json
[
  {
    "key": "api",
    "title": "Add endpoint",
    "description": "POST /x",
    "acceptanceCriteria": ["201 on valid body"],
    "verifyCommands": ["dotnet test tests/Api.Tests"]
  }
]
```

## 4. Task roles and worker packets

| Role | Runs where | Owns |
| --- | --- | --- |
| analyst | harness session | questions and the spec text; never code |
| implementer | one worktree per task | the assigned task, its tests, a commit, and result evidence |
| merger | merge worktree | branch integration and conflict reporting |
| reviewer | merged worktree (read-only unless fixing) | verify commands, diff inspection, findings |
| orchestrator | main session | packets, assignments/acks, gates, results; never edits a worker's worktree |

Give each implementer a **worker packet**, not the whole workflow. The spec
identity ties the work to an exact revision:

```json
{
  "workflowId": 56,
  "taskKey": "orchestration-tooling",
  "specNodeId": 60,
  "specVersion": 2,
  "specHash": "sha256:9f2c...",
  "baseRef": "main",
  "branch": "hardening/tooling",
  "worktree": "/abs/path/.anima/worktrees/56-orchestration-tooling",
  "messageId": "<inbox envelope message id>",
  "acceptanceCriteria": ["private-service start/stop works"],
  "verifyCommands": ["make check"],
  "ownedPaths": ["scripts/", "workflows/"]
}
```

`anima workflow spec <id>` exposes `version` and `hash`; each task carries its
spec `nodeId`. Include both in the packet so a later result can be matched to
the spec the worker actually read. Keep the packet to ids, owned paths,
acceptance and verify commands; read the spec body only when the packet is not
enough.

## 5. Parallel implementation (harness-created worktrees)

For each task you want in parallel:

1. Create a worktree and branch yourself:
   `git worktree add -b anima/<wf>-<task> .anima/worktrees/<wf>-<task> <baseRef>`
2. Register the assignment and its inbox message:
   `anima workflow assign <id> --task <key> --agent <agentId> --path <path> --branch <branch> --message-id <messageId>`
3. Spawn the implementer subagent with the worker packet, not the spec.
4. The implementer acks after reading the packet and before editing:

```bash
anima workflow ack <id> --task <key>
# equivalent: POST /api/workflows/{id}/assignments/{taskKey}/ack
```

`AssignmentView.messageId`, `acknowledgedAt` and `runId` show which packet was
delivered and whether the worker started. An ack is a liveness signal, not
permission: the orchestrator still posts the result.

5. The implementer works only in its worktree, runs the verify commands, and you
   post the result with evidence:

```bash
anima workflow result <id> --task api --kind implementation \
  --summary "added POST /x and tests" \
  --changed-file src/Api.cs --changed-file tests/Api.Tests.cs \
  --head-sha "$(git rev-parse HEAD)" --branch anima/<wf>-api \
  --base-ref main --run-id <agent-run-id> \
  --test "dotnet test tests/Api.Tests" \
  --artifact reports/api-tests.log \
  --agent impl-api --worktree .anima/worktrees/<wf>-api
```

Result evidence fields: `HeadSha`, `Branch`, `BaseRef`, `RunId`, `TestCommands`,
`ArtifactPaths`. Post what actually ran; a green summary without commands and a
head SHA is not evidence. One writer per worktree. Do not reuse a worktree. When
all tasks have successful results, status becomes `implementation_complete`.

Prefer the repo-owned `workflows/feature-delivery.js` template over ad-hoc
scripts: it fans one child per lane out with `runs.all`, validates the lane list
before dispatch, and returns an ordered board with each child's output. See
`workflows/README.md` for the lane fields and isolation rules.

## 6. Merge gate

Merge only when every implementation task has a successful result carrying a
head SHA, branch and at least one test command. A missing or failing result
blocks the merge instead of being summarized away.

Create a merge worktree from the base ref and run the merger agent there.

```bash
anima workflow result <id> --kind merge --summary "merged 3 branches" \
  --head-sha "$(git rev-parse HEAD)" --branch merge/<wf> --base-ref main \
  --test "make check" --artifact reports/merge-check.log \
  --agent merger --worktree .anima/worktrees/<wf>-merge
```

On conflicts, post `--failure --conflict path`. Anima marks the workflow failed;
resolve and post a new merge result to recover.

## 7. Review gate

Build a pinned review packet, then launch one or more read-only reviewer lanes
through the repo-owned `workflows/code-review.js` template. Do not ask the
reviewer to reconstruct the diff from memory or a moving working tree.

```bash
scripts/review-packet.sh "$BASE_SHA" "$HEAD_SHA" \
  --verify "make check" \
  --doc docs/code-quality.md \
  --doc .agents/skills/anima-orchestration/SKILL.md
```

Pass the resulting `packet.md`, `diff.patch` and `changed-files.txt` paths plus
`baseRef`, `headRef`, `verifyCommands` and `docs` to `workflows/code-review.js`.
Use separate reviewer lanes for correctness and contract drift when the diff is
non-trivial. Each lane must return JSON with `verdict`, `diffReviewed`,
`findings`, `notExamined` and `testsRun`.

Map the result to Anima:

- P0/P1 -> `--finding "error:P1 ..."`; the review cannot be successful.
- P2 -> `--finding "warning:P2 ..."`.
- `blocked` or `diffReviewed: false` -> `--failure`; it is not a clean review.
- `ok` with no P0/P1 -> successful review with head SHA, branch, base ref and
  the verify commands it actually ran.

```bash
anima workflow result <id> --kind review --summary "P1: ..." --failure \
  --finding "error:P1 missing auth check src/Api.cs:42" \
  --finding "warning:P2 no retry test" \
  --head-sha "$HEAD_SHA" --branch merge/<wf> --base-ref main \
  --test "make check" --agent reviewer --worktree .anima/worktrees/<wf>-merge
```

P0/P1 findings block completion and must be fixed and re-reviewed. A successful
review with no blocking findings sets `completed`; a failed review sets `failed`.
Do not mark a review successful from prose alone: require the structured verdict,
the diff packet, the command and the head SHA.

## 8. Bus usage

- Events: `anima bus watch --topics anima.workflow.events`.
- Knowledge changes: `anima.knowledge.changed` (question/spec/task/result nodes).
- Request/response to a running agent:

```bash
anima bus ask --topic anima.agent.analyst.inbox \
  --type anima.workflow.v1.WorkflowRoleTask \
  --payload @role-task.json --timeout 120
```

- Workflow dispatch does the same and waits:

```bash
anima workflow dispatch <id> --agent analyst --role analyst \
  --instructions "Ask only blocking questions" --context context.json
```

Anima will never start that agent. If dispatch times out, the agent is not
subscribed.

## 9. Private service instances

Shared services run on **7410-7416** and belong to the main checkout. A worktree
worker that needs its own service must use `scripts/private-service.sh`, which
refuses the reserved range, requires ports 7500+, and only signals the PID it
recorded for that port.

```bash
# compiled DLL (not `dotnet run`), private port, private temp DB
scripts/private-service.sh start \
  --dll src/Anima.Knowledge.Service/bin/Debug/net10.0/Anima.Knowledge.Service.dll \
  --port 7655 --env ANIMA_KNOWLEDGE_DB=/tmp/anima-smoke/knowledge.db
scripts/private-service.sh status --port 7655
scripts/private-service.sh stop --port 7655
```

- `start` writes `~/.cache/anima/private-services/<port>.pid` and `<port>.log`
  and prints the PID. `stop` verifies `/proc/<pid>/cmdline` contains the port
  before sending `SIGTERM`, so a reused or stale PID is never killed.
- Pass configuration with `--env KEY=VALUE`; use a temp database path per
  instance so the shared SQLite file is never touched.
- Never use `pkill -f`, `killall`, or `fuser -k`, and never stop a service you
  did not start. If a shared service is down, report it and continue privately.
- Clean up your own PID: `stop --port <port>`, then confirm the port is free.

## 10. Repo workflow template

`workflows/feature-delivery.js` is the checked-in pi-subagents template for
parallel lanes; do not paste `/tmp` scripts into a run. It validates `args.lanes`
before launching and returns an ordered board with each lane's `ok`, `runId` and
`output`.

```js
subagent({
  cwd: "/path/to/repo",
  workflow: "./workflows/feature-delivery.js",
  args: {
    lanes: [
      {
        key: "api",
        agent: "worker",
        cwd: "/abs/path/.anima/worktrees/56-api",
        task: "<worker packet and task brief>",
        label: "Implement API task",
        acceptance: ["POST /x returns 201"]
      }
    ]
  }
});
```

Lane fields, result shape and isolation rules are in `workflows/README.md`. One
writer per lane/worktree; the template never creates worktrees or posts results
for you.

## Context discipline (important)

- Use `anima workflow context <id> --task <key>` for implementer briefs; it
  omits the spec body.
- Read the spec only in the analyst/reviewer steps: `anima workflow spec <id>`.
- Knowledge reads default to summary; pass only filters you need.
- Post summaries + changed files. Put long command output in `--output-tail` only
  when it is genuinely needed.
- Do not paste workflow history into subagent prompts; pass ids and the brief.

## Failure cheatsheet

| Symptom | Meaning / fix |
| --- | --- |
| 422 `already answered` | re-read questions; answer only open ones |
| 422 `post the spec first` | writer spec/tasks before assigning |
| 422 `not part of workflow` | task key mismatch; read `workflow spec` |
| ack 404/422 | no assignment for that task key, or assignment not registered with `--message-id` |
| result rejected | task key not in the current spec, or check the evidence fields |
| merge gate blocked | an implementation result is missing, failed, or has no head SHA/test commands |
| dispatch timeout | agent id/topic wrong or agent not running; Anima will not start it |
| status `failed` after review | read `anima workflow timeline` and findings, fix, post a new review |
| empty context | task key not in the current spec |

## Close-out

When `completed`, record the delivered change in memgraph:

```text
change --implements--> task, change --modifies--> files
```

Then remove worktrees and keep branches if the audit trail matters.

## Structural context and quality gates

- If `.codegraph/` exists on the main checkout, run `codegraph sync` after a
  merge and use `codegraph context "<task>"`, `codegraph impact <symbol>` and
  `codegraph callers <symbol>` when building a worker packet or reviewing blast
  radius. Do not assume a worktree is covered by the main index.
- Before assigning a worker, use `workflow context` plus CodeGraph impact (when
  available) and include the spec hash/version.
- Every worker runs its scoped verification commands. Merge runs the full
  `make check`; review must record a verdict and findings.
- `make check` includes Roslynator + SonarAnalyzer under warnings-as-errors.
  Fix or explicitly configure a rule in `.editorconfig`; never blanket-disable.
