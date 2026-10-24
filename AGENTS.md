# Agent instructions for Anima

This is a .NET 10 monorepo for multi-agent workflows. Multiple agents work here
in parallel, so respect component boundaries and use the solution filters.

## Hard rules

1. **Component boundaries.**
   - A component's `*.Abstractions` project must not reference another
     component's implementation.
   - Service hosts (`src/Anima.<Area>.Service`) are the composition root. Do not
     add service-to-service project references; depend on interfaces and wire
     them in the host.
   - Cross-component shared code belongs in `Anima.Contracts`, `Anima.Hosting` or
     an `*.Abstractions` project.
2. **Contracts are schema-first.** Add or change `.proto` files under
   `contracts/proto/` and run `make contracts` (or
   `dotnet run --project tools/Anima.Cli -- contracts compile`). Never add
   generated C# DTOs for bus messages. Payloads are the descriptor-driven JSON
   projection; validate with `anima contracts validate` or
   `POST /api/bus/validate`.
3. **Build only your slice.** Use `dotnet build Anima.<Area>.slnf` and
   `dotnet test Anima.<Area>.slnf` so parallel work does not contend on the full
   solution. The full solution must still pass: `make check`.
4. **Tests.** Put behavior tests in `tests/Anima.<Area>.*.Tests`. Bug fixes need a
   failing test first. Do not weaken `TreatWarningsAsErrors` or
   `LatestRecommended` analyzers; fix the code.
5. **Windows deployment target.**
   - Do not assume PowerShell. Deploy scripts are `.cmd`/`.bat` (`cmd.exe`),
     `.exe` (direct) or `.sh` (`bash`); `.ps1` only when
     `Deploy:PowerShellExecutable` is explicitly configured.
   - Automation uses the installed Chrome executable; do not add a browser
     download requirement. `ANIMA_CHROME_PATH` overrides discovery.
   - Paths must work with both separators; use `Path.Combine`/`RepoLayout`.
6. **UI automation.** Keep the Playwright dependency behind `IBrowserDriver` /
   `IBrowserSession`. Raw CDP is Chromium-only; code must tolerate `Cdp == null`.
   No stealth patches. Scripts live in `specs/` and are `anima-yaml/v1`.
7. **Shared memory.** One agent process with `InMemory` knowledge is isolated.
   For multi-process agents set `Anima:Knowledge:Transport=Http`; do not assume
   in-memory state is shared.
8. **Ports.** Do not invent ports; use `AnimaPorts`.
9. **No silent scope changes.** If a task needs a cross-component change, say so
   in the summary and keep the diff minimal.

## Commands

| Task | Command |
| --- | --- |
| Full build/test | `make build`, `make test`, `make check` |
| Component build | `dotnet build Anima.Knowledge.slnf` |
| Component test | `dotnet test Anima.Automation.slnf` |
| Recompile contracts | `make contracts` |
| Validate a payload | `anima contracts validate <Message> payload.json` |
| Validate a UI script | `anima scripts validate specs/login-smoke.yaml` |
| Run all services | `make dev` |
| Browser executable | `make browser-check` |

On Windows, replace `make` with the underlying `dotnet` command.

## Where to make a change

| Change | Project |
| --- | --- |
| New bus semantics/topics | `Anima.Bus.Abstractions`, `Anima.Bus.Core` |
| New graph kind/edge or plan rule | `Anima.Knowledge.Abstractions` then `Core` |
| New script verb | `Anima.Automation.Dsl` (`AutomationScriptParser` + runner) |
| New browser action | `Anima.Automation.Abstractions` then `Playwright` |
| Deploy matching/runner behavior | `Anima.Deploy.Core` |
| New agent tool | `Anima.Agent.Core` (implement `IAgentTool`) |
| New planner/LLM integration | implement `IAgentPlanner`, register it in the agent host |
| New HTTP endpoint | the owning `*.Service/Program.cs` |
| New contract | `contracts/proto/` + `make contracts` |

## Planning work

If the `task-planning` skill is available, record goals/tasks in memgraph and
anchor each goal to this repo with `belongs_to`. Tasks map naturally to
`part_of` and `depends_on` edges. Keep the graph updated as you close work.

## Definition of done

- `make check` passes.
- New behavior has tests in the owning component.
- Public interfaces are documented with XML comments where non-obvious.
- Contract changes are compiled and validated.
- Docs updated when a seam, port, command or wire shape changes.
