# Anima dashboard UI

Read-only React/Vite dashboard served by `Anima.Dashboard.Service` at
`http://127.0.0.1:7416/`.

```bash
npm install
npm run dev     # http://localhost:5173, proxies /api to :7416
npm run build   # writes ui/dist, served by the dashboard service at /
```

Three tabs:

- **Workflows** — workflow list, questions/answers, refined spec, assignments,
  results and phases (the same views as before).
- **Graph** — knowledge graph snapshot rendered with
  [vis-network](https://visjs.github.io/vis-network/) as a force-directed graph
  with pan/zoom, node inspector, kind legend and a truncation banner.
  The snapshot is fetched with `maxNodes=500&maxEdges=1000`; the server returns
  graph totals plus `truncated`, and the banner reports how much was cut.
- **Bus** — topic/producer and subscriber tables with counters, plus a live
  event feed.

Every read goes through the dashboard BFF on the same origin:

| UI need | Endpoint |
| --- | --- |
| Workflows | `/api/dashboard/workflows[/{id}[/...]]` |
| Graph snapshot | `/api/dashboard/graph?maxNodes=500&maxEdges=1000` |
| Bus snapshot | `/api/dashboard/bus` |
| Live bus events | `/api/dashboard/bus/stream?topics=anima.**` (SSE) |

Workflow and graph state reload on `WorkflowEvent`/`GraphEvent` frames; the bus
snapshot refreshes on a debounce while events arrive. If the SSE stream reports
an error the tabs fall back to polling every 10s until `EventSource` reconnects.
Stale responses are discarded with per-view request counters.

No mutations are exposed here: the harness and agents own workflow changes.
