# Unified Anima dashboard

Status: refined spec (workflow 14; latest spec node is tracked by the workflow)

## Goal
One local user-facing server and UI at `:7416`:

- interactive knowledge-graph visualization
- current/previous workflows with questions, spec, assignments, results, timeline
- live bus topics, producers/type names, subscribers and counters

## Boundaries
- Read-only browser; the harness remains the only writer.
- Local-only; no authentication in this iteration.
- Single origin: dashboard server serves the UI and proxies read APIs.
- `Anima.Workflow.Service` stops serving `/ui`; it stays API-only.
- No agent runtime or agent spawning in Anima.

## Implementation tasks

### graph-api — Knowledge graph snapshot
- `IKnowledgeGraph.GetGraphAsync(GraphSnapshotQuery)` returning
  `GraphSnapshot(Nodes, Edges, NodeCount, EdgeCount, Truncated)`.
- `GET /api/graph?maxNodes=500&maxEdges=1000` on the knowledge service.
- Summary nodes (no content), edges only between included nodes, caps enforced.
- Implement in InMemory, SQLite, Publishing decorator and HTTP client;
  add SQLite tests for caps/truncation and endpoint smoke.

### bus-diagnostics — Bus snapshot
- `IBusDiagnostics.GetSnapshot()` and `GET /api/bus/snapshot`.
- Topics: topic, published count, last timestamp, type names, senders.
- Subscriptions: id, name, pattern, delivered, dropped, failed, last activity.
- Track in `InProcMessageBus`; add tests.

### dashboard-service — Unified server/BFF
- New `Anima.Dashboard.Service` on `:7416`; serves `ui/dist` at `/`.
- GET BFF endpoints:
  - `/api/dashboard/workflows[/{id}[/...]]` -> workflow service
  - `/api/dashboard/graph` -> knowledge `/api/graph`
  - `/api/dashboard/bus` -> bus `/api/bus/snapshot`
  - `/api/dashboard/bus/stream?topics=...` -> SSE proxy
- Upstream timeout -> 504, unavailable -> 502.
- Remove UI hosting from Workflow.Service.
- Wire `AnimaPorts.Dashboard`, dev script, Makefile, solution/filter.

### ui — Unified React dashboard
- Three tabs: Workflows, Graph, Bus.
- Graph: vis-network force layout, pan/zoom, node inspector, truncation banner.
- Bus: topics/producers and subscriber tables, live event feed.
- Workflow: existing views, now under `/api/dashboard/workflows`.
- Live via `/api/dashboard/bus/stream`; polling fallback.
- Vite base `/`, dev proxy to `:7416`.

## Process gates (not spec tasks)

### merge — Integration
- Harness-owned: merge implementation branches into `main`, resolve conflicts,
  rebuild, run the full test suite, and post a merge result with changed files.
- Requires successful results for graph-api, bus-diagnostics, dashboard-service and ui.

### review — Code review
- Apply the code-review skill to the integrated diff.
- Post findings and a review result. A failed review must be fixed before close.

### verify — Validation
- `make check`, `make ui-build`, all solution filters.
- Six-service smoke (knowledge, bus, workflow, automation, deploy, dashboard).
- Dashboard `/`, `/api/dashboard/graph`, `/api/dashboard/bus` return 200.
- Graph caps and bus counters visible.

## Worker isolation
- Shared services use ports 7410–7416 and are off-limits for worker cleanup.
- Workers use private ports 7500+ and a private temp DB.
- Workers run the compiled service DLL, capture `$!`, set a trap, and kill only
  that PID/process group. No `pkill -f`, `killall`, or `fuser -k` by name/port.
- A down shared service is reported, never killed/restarted by a worker.

## Acceptance
- Dashboard boots and serves the UI.
- Graph shows persisted nodes/edges with caps and inspector.
- Workflows list/detail work through the BFF.
- Bus snapshot shows topics/senders/types and subscriber counters; live feed updates.
- Workflow.Service `/ui` is gone.
- New tests for graph snapshot and bus diagnostics pass.
