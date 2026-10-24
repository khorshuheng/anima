# Workflow UI

Read-only React/Vite dashboard, served by `Anima.Dashboard.Service` at
`http://127.0.0.1:7416/` after `make ui-build`. `Anima.Workflow.Service` is
API-only; the dashboard proxies reads under `/api/dashboard/*`.

What it shows:

- **Workflows** tab: list/status, requirement, Q/A, spec/tasks, assignments,
  implementation/merge/review results/findings and phase timeline
- **Graph** tab: force-directed knowledge graph, caps/truncation, node inspector
- **Bus** tab: topics/producers, subscribers and counters, live event feed

Live updates come from the bus:

```text
http://127.0.0.1:7416/api/dashboard/bus/stream?topics=anima.**
```

The dashboard proxies the SSE stream to the bus at `:7412` on the same origin.
If SSE fails, the UI polls every 10 seconds.

Development:

```bash
make ui-install
make ui-dev      # http://localhost:5173, /api proxied to :7416
make ui-build    # ui/dist, served at / by the dashboard
```

The UI does not mutate workflows; the harness and agents post facts through the
workflow API.
