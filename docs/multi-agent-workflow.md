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
5. **Implementation.** For each task the harness creates a worktree and branch,
   spawns an implementer, and registers the assignment (`anima workflow assign`).
   The implementer works in its own worktree and posts a result
   (`anima workflow result --kind implementation`). Implementations can run in
   parallel; Anima only records their paths and outcomes.
6. **Merge.** The harness creates a merge worktree, runs the merger agent, and
   posts `--kind merge` with conflicts/changed files.
7. **Review.** A reviewer agent works in the merged worktree, runs verify
   commands, and posts `--kind review` plus findings. A failed review marks the
   workflow `failed`; a retry can post another result.
8. **Close.** A successful review sets `completed`. The harness records the
   change in memgraph/knowledge as needed.

## Worktree rules (harness side)

- Create from the recorded `baseRef`; branch name includes workflow + task key.
- One writer per worktree.
- Register the assignment with the real path and branch before work starts.
- Keep logs out of agent context; post an output tail or a path.
- Do not reuse a worktree for two tasks.
- Remove the worktree after merge; branches can remain for audit.

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
  --instructions ...`; the agent must already be running. Anima does not start it.
- Treat events as hints; always re-read workflow state.

## Failure handling

| Symptom | Action |
| --- | --- |
| 422 on answer | question already answered or not in this workflow; re-read questions |
| 422 on assignment | spec not posted or task key mismatch |
| Dispatch timeout | agent id/topic wrong or agent not subscribed; check mailbox |
| Implementation result rejected | task key not in the current spec |
| Review failed | read findings, fix, post a new review result |
