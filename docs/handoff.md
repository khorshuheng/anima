# Agent handoff

Use this file as the compact context for the next session.

## Baseline
- Repo: https://github.com/khorshuheng/anima (public)
- Local: `/home/yggdrasil/Projects/anima`
- Branch: `main`, clean at `2c2de1b` (or later)
- Build gate: `make check` (116 tests at time of handoff), `make ui-build`

## Durable plan
- Anima knowledge graph: goal `86`, tasks `87-97`, spec `98`
- Completed workflows: `#14` (dashboard), `#56` (hardening)
- Next slice: task `87` (repo workflow template), then `88` (require
  structured result evidence), then `89` (automatic assignment ack)
- Remaining tasks: 90 spec-hash backfill, 91 reassignment semantics,
  92 WorkflowRoleTask enrichment, 93 private-service enforcement,
  94 bus eviction, 95 dashboard HTTP tests, 96 lazy graph bundle,
  97 analyzer tightening. CI is intentionally excluded.

## Quality stack
- Roslynator.Analyzers / Formatting 5.0.1
- SonarAnalyzer.CSharp 10.35.0.4138
- Warnings-as-errors; rule policy in `docs/code-quality.md`
- CodeGraph indexed (`.codegraph/` ignored); `make graph-sync` after merges

## Key files
- `src/Anima.Workflow.*` — specs, assignments, acks, result evidence
- `workflows/feature-delivery.js` — repo-owned subagent fanout template
- `scripts/private-service.sh` — isolated PID-scoped smoke services
- `docs/orchestration-hardening.md` — hardening spec
- `docs/code-quality.md` — analyzers/CodeGraph policy
- `docs/unified-dashboard.md` — dashboard spec

## Operational constraints
- Anima never spawns agents; the harness owns agents and worktrees.
- In-memory knowledge is test-only; SQLite is production.
- Shared services use ports 7410-7416; never stop them by name.
- Workers use compiled DLLs, private ports 7500+, and kill only captured PIDs.
- Deploy target is Windows without PowerShell; Chrome is system-installed.
- Review with the code-review skill before closing a workflow.

## Commands
```bash
make check
make ui-build
make dev
codegraph sync
anima workflow list
anima knowledge search <terms> --scope all
```
