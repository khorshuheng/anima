# Multi-agent workflow runbook

The harness (pi) is the orchestrator. Anima is the shared state, transport,
contract, automation and deploy layer. The project skill
`.agents/skills/anima-orchestration/SKILL.md` is the executable form of this doc.

## Ownership

| Concern | Harness | Anima |
| --- | --- | --- |
| Create/stop agents | yes | **no** |
| Prompt/model choice | yes | **no** |
| Git worktrees/branches | yes | metadata only |
| Analyst Q&A state | asks the user | stores questions/answers |
| Refined spec | writes the content | stores spec + tasks |
| Implementation results | reports them | stores them |
| Merge/review | runs them | stores results/findings |
| Contracts/validation | uses them | owns them |
| Bus messages | produces/consumes | transports/validates |
| UI automation | scripts it | executes it |
| Deploy | triggers via push | runs the sink script |

## Parallel slices

Each component keeps a solution filter, so agents can work in parallel without
building the whole repo:

| Slice | Build | Test |
| --- | --- | --- |
| Contracts | `dotnet build Anima.Contracts.slnf` | `dotnet test Anima.Contracts.slnf` |
| Bus | `dotnet build Anima.Bus.slnf` | `dotnet test Anima.Bus.slnf` |
| Knowledge | `dotnet build Anima.Knowledge.slnf` | `dotnet test Anima.Knowledge.slnf` |
| Workflow | `dotnet build Anima.Workflow.slnf` | `dotnet test Anima.Workflow.slnf` |
| Automation | `dotnet build Anima.Automation.slnf` | `dotnet test Anima.Automation.slnf` |
| Deploy | `dotnet build Anima.Deploy.slnf` | `dotnet test Anima.Deploy.slnf` |

## Sequence

1. **Start** (`anima workflow start`). Anima creates a goal, a workflow node
   anchored to the repo, and an `analysis` phase.
2. **Analyst questions.** The harness runs an analyst agent. The analyst posts
   questions (`anima workflow ask`). Anima moves to `awaiting_answers` and emits
   `question.added`.
3. **User answers.** The main agent reads open questions
   (`anima workflow questions <id>`), asks the human, and posts answers
   (`anima workflow answer`). Repeat until the analyst has no more questions.
4. **Spec.** The analyst writes a markdown spec plus a task list and posts both
   (`anima workflow spec-write`). Anima creates task nodes and moves to
   `spec_ready`.
5. **Implementation.** For each task the harness creates a worktree and branch
   and spawns an implementer with a worker packet (workflow/task ids, spec
   node/version/hash, base ref, branch, worktree, acceptance, verify commands).
   Send the bus role task with `anima workflow dispatch <id> --task <key>
   --path <worktree> --branch <branch>`: dispatch registers the assignment with
   the envelope message id before publishing and returns `202` with that
   `messageId` immediately. The implementer must already be running/subscribed;
   the bus does not queue for offline agents. The implementer acks the assignment
   with its run id (`anima workflow ack`,
   or `POST /api/workflows/{id}/assignments/{taskKey}/ack`), works in its own
   worktree, commits, and posts a result (`anima workflow result --kind
   implementation`) with evidence: head SHA, branch, base ref, run id, test
   commands and artifact paths. Implementations can run in parallel; Anima only
   records their paths and outcomes. A successful implementation, merge or
   review without head SHA, branch, base ref and at least one test command is
   rejected with 422.
6. **Merge gate.** Merge only after every implementation task has a successful
   result with head SHA, branch and test commands. The harness creates a merge
   worktree, runs the merger agent, and posts `--kind merge` with the same
   evidence plus conflicts/changed files.
7. **Review gate.** Build a pinned review packet with
   `scripts/review-packet.sh <base> <head> --verify <command> --doc <path>` and
   launch read-only reviewer lanes through `workflows/code-review.js`. The
   reviewer reads `packet.md`/`diff.patch`/`changed-files.txt`, applies the
   code-review skill, and returns a JSON verdict (`ok`, `changes_required` or
   `blocked`) plus findings. A missing or unreadable diff is `blocked`, never
   clean. Map P0/P1 to Anima `error` findings and P2 to `warning`; post
   `--kind review` with head SHA, branch, base ref and the commands actually
   run. P0/P1 findings must be fixed and re-reviewed; a failed or blocked review
   marks the workflow `failed` and a retry posts another result.
8. **Close.** A successful review sets `completed`. The harness records the
   change in memgraph/knowledge as needed.

## Worktree rules (harness side)

- Create from the recorded `baseRef`; branch name includes workflow + task key.
- One writer per worktree.
- Register the assignment with the real path, branch and inbox message id
  before work starts. `workflow dispatch <id> --task --path --branch` does this in
  one step and returns the message id; wait for the worker's ack before treating
  it as started.
- Give the worker a packet (ids, owned paths, acceptance, verify commands), not
  the full workflow history.
- Keep logs out of agent context; post an output tail or an artifact path.
- Do not reuse a worktree for two tasks.
- Remove the worktree after merge; branches can remain for audit.

## Repo workflow template

Use `workflows/feature-delivery.js` for parallel lanes instead of ad-hoc
`/tmp` scripts. It validates `args.lanes` before dispatch, uses `runs.all`, and
returns an ordered board with each lane's output. See `workflows/README.md`.

## Private service instances

Shared services on 7410-7416 belong to the main checkout. A worker that needs a
service starts its own with `scripts/private-service.sh` (ports >= 7500, compiled
DLL, private temp DB, PID-scoped `stop`). Never use `pkill -f`, `killall`, or
`fuser -k`, and never stop a service you did not start.

## Context economy

- `anima workflow context <id> --task <key>` returns the brief (acceptance,
  verify commands, worktree, last result) without the spec body.
- `anima workflow spec <id>` is an explicit read, not part of every turn.
- Knowledge lists default to `summary=true`; content is opt-in.
- Role tasks carry ids and short instructions, not full documents.
- Post a summary and changed-file list in results; put long logs in `--output-tail`
  only when needed.

## Bus usage

- Wake-ups: `anima bus watch --topics anima.workflow.events`.
- Agent-to-agent request/response: `anima bus ask ...`; the requester subscribes
  to a private reply topic and times out.
- Workflow dispatch: `anima workflow dispatch <id> --agent <id> --role analyst
  --instructions ...` publishes and returns immediately; the agent must already be
  running. Anima does not start it. Use `anima workflow ack/result` for durable
  state, and treat events as hints: always re-read workflow state.

## Failure handling

| Symptom | Action |
| --- | --- |
| 422 on answer | question already answered or not in this workflow; re-read questions |
| 422 on assignment | spec not posted or task key mismatch |
| Dispatch `202` but no worker | bus delivery is best-effort; start/subscribe the agent, then retry or assign explicitly |
| Status `assigned` | assignment registered but not acked; worker has not confirmed start |
| Implementation result rejected | task key not in the current spec |
| Review failed | read findings, fix, post a new review result |
