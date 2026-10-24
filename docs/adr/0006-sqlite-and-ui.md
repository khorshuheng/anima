# ADR 0006: SQLite knowledge store and a read-only React visualizer

Status: accepted

## Context

The knowledge graph was in-memory, so workflow state and planning data vanished
on restart. memgraph and librarian both use SQLite with versioned migrations,
FTS5 and a single writer, and the deployment target is a single local host. The
workflow also needs a human-viewable state surface.

## Decision

- Production knowledge is SQLite (`Microsoft.Data.Sqlite`) with:
  - versioned migrations (`PRAGMA user_version`),
  - WAL, foreign keys, busy timeout,
  - an in-process write gate plus `BEGIN` transactions,
  - JSON properties with `json_each` filters,
  - an FTS5 external-content index kept in sync by triggers.
- In-memory remains only for tests (`Provider=InMemory`).
- Successful mutations publish `anima.knowledge.v1.GraphEvent` on
  `anima.knowledge.changed`. Publish failures never fail the write.
- The workflow visualizer is a React/Vite read-only SPA served by the workflow
  host at `/ui`; it uses the workflow HTTP API and subscribes to bus SSE
  (`anima.**`) with polling as fallback. Bus CORS is restricted to local UI
  origins.

## Consequences

- Workflow, spec and plan state survive restarts and can be inspected with
  `sqlite3` or the knowledge API.
- FTS5 provides real search instead of an in-memory scan.
- Agents and the UI can react to graph changes without polling the database.
- SQLite is single-writer; the service serializes writes and keeps reads in WAL.
- The UI has no mutations; the harness remains the only writer of workflow facts.
