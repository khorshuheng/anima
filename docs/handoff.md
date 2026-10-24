# Agent handoff

Use this file as the compact context for the next session.

## Baseline
- Repo: https://github.com/khorshuheng/anima (public)
- Local: `/home/yggdrasil/Projects/anima`
- Branch: `main`, clean at the latest commit
- Build gate: `make check` (123 tests at the Option A close-out), `make ui-build`
- Option A is complete: workflow dispatch is fire-and-forget; assignments,
  acks and results are durable workflow API facts.

## Durable plan
- Anima knowledge graph: goal `86`, tasks `87-97`, plus review follow-ups
  `149-151`, spec `98`
- Completed workflows: `#14` (dashboard), `#56` (hardening), `#115` (Option A)
- Resolved by change `152`: task `88` (result evidence) and task `89`
  (dispatch/assignment ack correlation)
- Still open: task `87` (use feature-delivery.js for a real lane), `90`
  spec-hash backfill, `91` reassignment/ack semantics, `92` WorkflowRoleTask
  enrichment, `93` private-service enforcement, `94` bus eviction, `95`
  dashboard HTTP tests, `96` lazy graph bundle, `97` analyzer tightening
- Review follow-ups: `149` ack message-id correlation, `150` Workflow.Service
  HTTP integration tests, `151` dispatch 503 docs when no assignment exists
- CI is intentionally excluded.

## Review step
- Build a pinned packet:
  `scripts/review-packet.sh <base> <head> --verify "make check" --doc <path>`
- Launch read-only reviewers with `workflows/code-review.js`.
- Require JSON verdict `ok | changes_required | blocked`; a missing diff is
  blocked, never clean. Map P0/P1 to `error` findings and P2 to `warning`.

## Quality stack
- Roslynator.Analyzers / Formatting 5.0.1
- SonarAnalyzer.CSharp 10.35.0.4138
- Warnings-as-errors; rule policy in `docs/code-quality.md`
- CodeGraph indexed (`.codegraph/` ignored); `make graph-sync` after merges

## Key files
- `src/Anima.Workflow.*` — specs, assignments, acks, result evidence, dispatch
- `workflows/feature-delivery.js` — feature lane template
- `workflows/code-review.js` — review packet lanes
- `scripts/review-packet.sh` — pinned diff/review contract
- `scripts/private-service.sh` — isolated PID-scoped smoke services
- `docs/workflow-design.md` — workflow/bus contract
- `docs/code-quality.md` — analyzers/CodeGraph/review policy

## Bus contract
- The bus is an ephemeral wake-up channel: no replay, offsets, ack or offline
  delivery guarantee.
- `workflow dispatch` publishes and returns `202` with `messageId` immediately;
  it does not wait for a reply.
- Registration -> `assigned`; ack -> `implementing`; results are HTTP-only.
- A publish failure returns `503`; the assignment may already exist when
  task/path/branch were supplied.

## Operational constraints
- Anima never spawns agents; the harness owns agents and worktrees.
- In-memory knowledge is test-only; SQLite is production.
- Shared services use ports 7410-7416; never stop them by name.
- Workers use compiled DLLs, private ports 7500+, and kill only captured PIDs.
- Deploy target is Windows without PowerShell; Chrome is system-installed.

## Commands
```bash
make check
make ui-build
make dev
make review-packet BASE=<base> HEAD=<head>
codegraph sync
anima workflow list
anima knowledge search <terms> --scope all
```
