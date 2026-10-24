# Anima workflow UI

Read-only React/Vite visualizer for workflow state.

```bash
npm install
npm run dev     # http://localhost:5173, proxies /api to :7415
npm run build   # writes ui/dist, served by Workflow.Service at /ui/
```

`Anima.Workflow.Service` serves `ui/dist` at `http://127.0.0.1:7415/ui/` when it
exists. Live updates come from the bus at
`http://127.0.0.1:7412/api/bus/stream?topics=anima.**`; the bus service allows
the UI origins by default. Polling every 10s is the fallback.

No mutations are exposed here: the harness and agents own workflow changes.
