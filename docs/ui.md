# Workflow UI

Read-only React/Vite dashboard, served by `Anima.Dashboard.Service` at
`http://127.0.0.1:7416/` after `make ui-build`. `Anima.Workflow.Service` is
API-only; the dashboard proxies reads under `/api/dashboard/*`.

What it shows:

- **Workflows** tab: list/status, requirement, Q/A, spec/tasks, assignments,
  implementation/merge/review results/findings and phase timeline
- **Graph** tab: force-directed knowledge graph, caps/truncation, node inspector
- **Vocabulary** tab: the merged vocabulary the knowledge service loaded — node
  kinds and edge types with their owning pack (`core` or a user pack), traits,
  required properties, endpoint pairs, inverse names, acyclic/symmetric flags and
  deprecation state
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

The Vocabulary tab reads `/api/dashboard/vocabulary`, which proxies the knowledge
service's merged catalog (`GET /api/vocabulary`). Rows contributed by a user pack
carry a coloured edge so core vocabulary stays visually dominant; deprecated
entries show a `deprecated` chip. It is read-only: packs are loaded at knowledge
startup from `config/knowledge`, `~/.config/anima/knowledge` and
`Anima:Knowledge:Packs`, and changing them requires a restart (see ADR 0007 and
`docs/deployment.md`).

The Workflows tab includes a Run summary panel with task roles, recorded
agents/runs, result and review counts, review iterations, duration and
evidence-completeness indicators.
