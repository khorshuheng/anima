# Multi-agent workflow

## Working in parallel

Each component has a solution filter and an owner-facing boundary. An agent
working on automation does not need to build the agent host:

| Slice | Build | Test |
| --- | --- | --- |
| Contracts | `dotnet build Anima.Contracts.slnf` | `dotnet test Anima.Contracts.slnf` |
| Bus | `dotnet build Anima.Bus.slnf` | `dotnet test Anima.Bus.slnf` |
| Knowledge | `dotnet build Anima.Knowledge.slnf` | `dotnet test Anima.Knowledge.slnf` |
| Automation | `dotnet build Anima.Automation.slnf` | `dotnet test Anima.Automation.slnf` |
| Deploy | `dotnet build Anima.Deploy.slnf` | `dotnet test Anima.Deploy.slnf` |
| Agent | `dotnet build Anima.Agent.slnf` | `dotnet test Anima.Agent.slnf` |

Before handing off, run the full `make check` once to catch cross-slice issues.

## Contract-first parallel work

Contracts are the only shared surface that changes without codegen:

1. One agent edits `contracts/proto/anima/<area>/v1/*.proto`.
2. They run `make contracts` and commit `contracts/descriptors/anima.pb`.
3. Other agents pull the descriptor, validate payloads with
   `anima contracts validate <Message> <file>`, and continue.

No project rebuild is needed for a contract change, so contract work does not
serialize the other slices.

## End-to-end example

1. **Plan** (knowledge): create a repo node, then a goal with `belongs_to` and
   tasks with `part_of`/`depends_on`.
2. **Delegate** (agent -> bus): orchestrator sends `delegation` to
   `anima.agent.worker.inbox`.
3. **Work** (worker agent): calls `knowledge.remember` and
   `automation.run_script` tools.
4. **Verify** (automation): run `specs/login-smoke.yaml` against dev/UAT.
5. **Deliver** (knowledge): create a `change` node with `implements` to resolve
   the task.
6. **Deploy** (deploy): local push hook posts to the sink; the matching rule runs
   `example-deploy.cmd` (Windows) or `example-deploy.sh` (Linux).
7. **Record**: deploy result can be attached as a `decision` or `note` node.

## Agent-facing endpoints

| Service | Endpoint | Use |
| --- | --- | --- |
| Agent | `POST /api/agents/{id}/turns` | run one turn |
| Agent | `GET /api/agents/{id}/mailbox/stream` | SSE inbox |
| Knowledge | `POST /api/plans`, `/api/tasks` | plan/task writes |
| Knowledge | `GET /api/search`, `/api/nodes` | memory reads |
| Bus | `POST /api/bus/publish` | publish a contract message |
| Bus | `GET /api/bus/stream?topics=anima.**` | SSE debug stream |
| Automation | `POST /api/sessions/{id}/scripts` | run a derived script |
| Automation | `POST /api/sessions/{id}/cdp` | raw CDP (Chromium only) |
| Deploy | `POST /hooks/git/{provider}` | push sink |
| Deploy | `GET /api/runs/{id}/log` | deploy output |

All services expose `/health`, `/api/health`, `/openapi/v1.json` and `/`.

## Division-of-work rules

- Do not edit another component's implementation to make your slice pass; add or
  change an abstraction and wire it in the host.
- If a change crosses components, name it explicitly in the handoff.
- Keep platform assumptions honest: Chrome is installed; the deploy target is
  Windows without PowerShell; automation CDP is Chromium-only.
- Agents adding tasks to memgraph should anchor goals with `belongs_to` so
  project-scoped reads keep working.
