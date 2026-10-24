# Agent instructions for Anima

Anima is infrastructure for multi-agent workflows. **Anima does not contain an
agent runtime, does not spawn agents, does not call an LLM, and does not create
git worktrees.** A dedicated harness (pi or another runtime) owns agents and
worktrees; it uses Anima through HTTP, the bus and `anima` CLI.

## Hard rules

1. **No agent execution in Anima.** Do not add a process launcher, a subagent
   runner, an LLM client, or code that starts an `Anima.*` service. If a feature
   needs an agent, it belongs in the harness.
2. **No worktree creation in Anima.** The harness creates worktrees and posts
   assignment metadata (path, branch, agent id). Anima stores and reports it.
3. **Component boundaries.**
   - `*.Abstractions` must not reference another component's implementation.
   - Service hosts are composition roots; no service-to-service project refs.
   - Shared code lives in `Anima.Contracts`, `Anima.Hosting`, or an
     `*.Abstractions` project.
4. **Contracts are schema-first.** Edit `contracts/proto/`, run `make contracts`,
   validate with `anima contracts validate` or `POST /api/bus/validate`. Never add
   generated C# DTOs for bus messages.
5. **Build only your slice** with `dotnet build Anima.<Area>.slnf`; run the full
   `make check` before handoff. Do not weaken warnings-as-errors or analyzers.
6. **Tests.** Behavior changes need tests in `tests/Anima.<Area>.*.Tests`. Bug
   fixes need a failing test first.
7. **Q&A goes through the workflow API.** Bus-only Q&A loses durability and state;
   the bus carries `anima.workflow.events` wake-ups and role tasks, not the
   authoritative question/answer record.
8. **Context discipline.** Default to `summary=true` and property filters on
   knowledge reads; fetch the spec only when needed; role tasks carry ids, not full
   documents.
9. **Platform reality.** Chrome is installed; the deploy target is Windows without
   PowerShell; CDP is Chromium-only. Do not add assumptions that contradict this.
10. **No silent scope changes.** Name cross-component changes in the summary.

## Commands

| Task | Command |
| --- | --- |
| Full build/test | `make build`, `make test`, `make check` |
| Component build | `dotnet build Anima.Workflow.slnf` |
| Component test | `dotnet test Anima.Bus.slnf` |
| Recompile contracts | `make contracts` |
| Validate a payload | `anima contracts validate <Message> payload.json` |
| Validate a UI script | `anima scripts validate specs/login-smoke.yaml` |
| Run all services | `make dev` |
| Browser executable | `make browser-check` |
| Workflow | `anima workflow ...` |
| Bus | `anima bus publish|ask|watch` |

## Where to make a change

| Change | Project |
| --- | --- |
| Bus semantics/topics | `Anima.Bus.Abstractions`, `Anima.Bus.Core`, `Anima.Bus.Client` |
| Workflow state/status rules | `Anima.Workflow.Core` (`KnowledgeWorkflowStore`) |
| Workflow HTTP surface | `Anima.Workflow.Service` |
| Graph kinds/edges/updates | `Anima.Knowledge.Abstractions`, `Core`, `Client`, `Service` |
| Script verb | `Anima.Automation.Dsl` |
| Browser action | `Anima.Automation.Abstractions` then `Playwright` |
| Deploy matching/runner | `Anima.Deploy.Core` |
| Contract | `contracts/proto/` + `make contracts` |
| Harness runbook | `.agents/skills/anima-orchestration/SKILL.md` |

## Definition of done

- `make check` passes.
- New behavior has tests in the owning component.
- Contract changes are compiled and validated.
- Docs and the orchestration skill are updated when a seam, endpoint, CLI command
  or wire shape changes.

## Worktree / subagent process hygiene

Shared Anima services run on reserved ports **7410–7416**. A worktree worker must
never stop a service it did not start.

- Never use `pkill -f`, `killall`, or `fuser -k` on a shared port or name. Broad
  process-name matches kill every checkout of that service.
- Start private smoke instances on ports **7500+** with a private temp DB, using
  the compiled DLL (`dotnet path/to/X.Service.dll --urls ...`), not `dotnet run`.
  Capture `$!`, set a trap, and kill only that PID (or its process group).
- If a shared service is down, report it and continue with a private instance;
  do not restart or kill shared services.
- Cleanup must be scoped: `kill "$pid"` / `kill -- -"$pgid"`, never by service
  name.
