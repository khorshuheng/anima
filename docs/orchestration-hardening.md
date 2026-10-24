# Orchestration hardening

Status: refined spec; workflow instance created through the Workflow API

## Goal
Turn the lessons from the dashboard run into durable workflow/API/skill/tooling
improvements.

## Tasks

### workflow-contracts
- Spec identity: `SpecView.Hash`, `WorkflowState.SpecVersion/SpecHash`,
  `WorkflowContext.SpecVersion/SpecHash`; store a SHA-256 hash of spec markdown + tasks.
- Assignment ack: `AssignmentInput.MessageId`; `AssignmentView.MessageId`,
  `AcknowledgedAt`, `RunId`; `AcknowledgeAssignmentAsync` and
  `POST /api/workflows/{id}/assignments/{taskKey}/ack`.
- Structured result evidence: `ResultInput/ResultView` gain `HeadSha`, `Branch`,
  `BaseRef`, `RunId`, `TestCommands`, `ArtifactPaths`; persisted in result props.
- CLI: `workflow assign --message-id`, `workflow ack`, and result evidence flags.
- Tests for spec hash, ack, evidence round-trip.

### orchestration-tooling
- `scripts/private-service.sh` start/stop/status with reserved-port refusal,
  PID files, `$!` capture, traps; no pkill.
- `workflows/feature-delivery.js` repo-owned pi-subagents template taking
  `lanes` (key/agent/cwd/task) and optional gates; no /tmp scripts.
- `workflows/README.md` documenting parameters and isolation rules.
- Update `.agents/skills/anima-orchestration` with task roles, worktree packet,
  bus ack/result evidence, merge/review gates, private-service usage.
- Do not modify runtime code.

### dashboard-hardening
- Bus diagnostics: cap tracked topics and per-topic type/sender sets; cap
  snapshot topics with a truncation flag; UI shows the flag.
- Graph snapshot: add `newest` (or `sinceId`) window so current workflows are
  reachable after the cap; forward through dashboard BFF and UI.
- Dashboard proxy: apply request timeout to body reads; generic 502 message;
  use `IOptions<DashboardOptions>` and validation.
- Ensure the bus SSE single-writer and static-cache fixes remain.
- Tests for bounds/window/proxy timeouts.

## Process gates
- Merge after all three implementation tasks pass.
- Review with the code-review skill; P0/P1 fixed before completion.
