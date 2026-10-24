# Workflow UI

Read-only React/Vite visualizer, served by `Anima.Workflow.Service` at
`http://127.0.0.1:7415/ui/` after `make ui-build`.

What it shows:

- workflow list and status chips
- requirement and progress counters
- all questions with open/answered state and the user's answers
- refined spec markdown, task acceptance criteria and verify commands
- harness-owned worktree assignments (path, branch, agent)
- implementation/merge/review results, changed files, conflicts and findings
- phase timeline

Live updates come from the bus:

```text
http://127.0.0.1:7412/api/bus/stream?topics=anima.**
```

The bus service allows `http://127.0.0.1:7415`, `http://localhost:7415` and the
Vite dev origins. If SSE fails, the UI polls every 10 seconds.

Development:

```bash
make ui-install
make ui-dev      # http://localhost:5173, /api proxied to :7415
make ui-build    # ui/dist, served at /ui/
```

The UI does not mutate workflows; the harness and agents post facts through the
workflow API.
