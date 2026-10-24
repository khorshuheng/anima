# ADR 0003: One knowledge service for tasks and long-term memory

Status: accepted

## Context

Task management and long-term memory overlap heavily: both are typed entities
with typed relations, and agents query them together ("what is open for this
repo", "what do we know about this area"). The local memgraph service already
models tasks as graph nodes.

## Decision

One node/edge graph serves both. Tasks are nodes wired with `part_of` /
`depends_on`; memories are `memory`/`note` nodes wired with `mentions` /
`related_to` / `derived_from`. Resolution is derived from incoming resolving
edges. The service mirrors memgraph's HTTP surface, and
`Anima.Knowledge.Client` can target an existing memgraph instance.

## Consequences

- One vocabulary, one query language, no cross-service join.
- Task status is derived, not stored; callers attach a `change` with
  `implements`.
- Storage is in-memory in the scaffold; SQLite or memgraph is the persistence
  seam.
