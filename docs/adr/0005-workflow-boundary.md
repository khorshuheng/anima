# ADR 0005: Anima is infrastructure; the harness owns agents and worktrees

Status: accepted

## Context

The workflow needs analyst Q&A, a refined spec, parallel implementation in git
worktrees, a merge and a review. Existing agent runtimes (pi) already provide
agent creation, context management, prompting and worktree tooling. Duplicating
that in Anima would create a second, worse harness and couple service lifetime to
agent lifetime.

## Decision

- Anima has no agent runtime, no LLM client, no process launcher and no
  `git worktree` code.
- The workflow service stores durable facts: questions, answers, spec, task list,
  assignment metadata, results, findings and phases.
- Status is derived from posted facts; Anima never infers an agent's result.
- The harness creates worktrees and agents, registers assignments, and posts
  results. The bus carries events and role tasks, including request/response with
  `ReplyTo`.
- Q&A uses the workflow API as the system of record; bus events are wake-ups.

## Consequences

- Anima can be restarted without killing agents; agents can be restarted without
  losing workflow state (state is in the knowledge graph).
- The harness can be any runtime; only HTTP + CLI + bus contracts are required.
- The project skill (`.agents/skills/anima-orchestration`) carries the runbook.
- Anima cannot enforce worktree isolation; that remains a harness discipline.
