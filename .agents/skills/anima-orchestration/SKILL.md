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
make dev                 # knowledge, bus, automation, deploy, workflow
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

## 4. Parallel implementation (harness-created worktrees)

For each task you want in parallel:

1. Create a worktree and branch yourself:
   `git worktree add -b anima/<wf>-<task> .anima/worktrees/<wf>-<task> <baseRef>`
2. Register the assignment:
   `anima workflow assign <id> --task <key> --agent <agentId> --path <path> --branch <branch>`
3. Spawn the implementer subagent with a brief, not the whole spec:
   `anima workflow context <id> --task <key>`
4. The implementer works only in its worktree, runs the verify commands, and you
   post the result:

```bash
anima workflow result <id> --task api --kind implementation \
  --summary "added POST /x and tests" \
  --changed-file src/Api.cs --changed-file tests/Api.Tests.cs \
  --agent impl-api --worktree .anima/worktrees/<wf>-api
```

One writer per worktree. Do not reuse a worktree. When all tasks have successful
results, status becomes `implementation_complete`.

## 5. Merge

Create a merge worktree from the base ref and run the merger agent there.

```bash
anima workflow result <id> --kind merge --summary "merged 3 branches" \
  --agent merger --worktree .anima/worktrees/<wf>-merge
```

On conflicts, post `--failure --conflict path`. Anima marks the workflow failed;
resolve and post a new merge result to recover.

## 6. Review

Run the reviewer in the merged worktree. It should run the verify commands and
inspect the diff. Post findings, including file/line when known:

```bash
anima workflow result <id> --kind review --summary "2 issues" --failure \
  --finding error:"missing auth check" --finding warning:"no retry test" \
  --agent reviewer --worktree .anima/worktrees/<wf>-merge
```

A successful review sets `completed`; a failed one sets `failed`.

## 7. Bus usage

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
| dispatch timeout | agent id/topic wrong or agent not running; Anima will not start it |
| status `failed` after review | read `anima workflow timeline` and findings, fix, post a new review |
| empty context | task key not in the current spec |

## Close-out

When `completed`, record the delivered change in memgraph:

```text
change --implements--> task, change --modifies--> files
```

Then remove worktrees and keep branches if the audit trail matters.
