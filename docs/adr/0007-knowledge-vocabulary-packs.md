# ADR 0007: Knowledge vocabulary as data with additive packs

Status: proposed (step 1 implemented)

## Context

`KnowledgeVocabulary.cs` defines the whole vocabulary as C# constants: 21 node
kinds and 27 edge types, plus `KnowledgeKinds.Definitions` /
`KnowledgeEdgeTypes.Definitions`. SQLite already stores both tables and enforces
constraints from those rows, so the runtime is half data-driven — but the source
of truth is compile-time, and nothing can extend it:

- Every edge type is a fixed row; unknown types are rejected with 422.
- Node kinds are the opposite: `KnowledgeOptions.AllowAdHocKinds = true`
  registers any unknown kind on first write as an `"Ad-hoc kind."` row, so a typo
  becomes permanent vocabulary with no description, no constraints and no owner.
- `EdgeTypeDefinition` cannot express `required_props`, explicit
  source/destination pairs, inverse relations, symmetry or acyclicity.
- Logic that depends on vocabulary semantics still compares *names*:
  `'part_of'` / `'belongs_to'` in scope and anchor walks
  (`SqliteKnowledgeGraph.cs:754,770`), `k.name IN ('task','goal')` and
  `t.name IN ('part_of','belongs_to')` in reads
  (`SqliteKnowledgeGraph.Reads.cs:569,584`), and 54 `KnowledgeKinds.*` uses in
  `KnowledgeWorkflowStore.cs`.
- `Migrate()` returns as soon as `PRAGMA user_version >= 1`, so adding columns
  needs an ordered migration runner first.

## Decision

Ship the standard vocabulary as **embedded data**, allow **additive extension**
through user packs, and keep a small **immutable core**.

### Three layers, one direction

```
embedded core pack  ─┐
                     ├─→ KnowledgeVocabularyCatalog (immutable, built at startup)
user packs (files)  ─┘         │
                               ├─→ reconcile → node_kind / edge_type
                               └─→ injected into Sqlite + InMemory stores
```

- **Core pack** — `Anima.Knowledge.Abstractions/KnowledgeVocabulary.core.json`,
  embedded resource, `name: "core"`. Ids `1..999`, fixed forever, append-only.
- **Namespacing** — a pack may only add names under its own prefix: a pack named
  `acme` defines `acme:invoice`, while references to core names stay bare
  (widening `part_of` keeps that name). Inverse names live in the same namespace
  as their edge type. This makes a future core entry colliding with a pack entry
  impossible rather than merely detected.
- **User packs** — same schema, discovered in application order:
  `<repo>/config/knowledge/*.json`, then `~/.config/anima/knowledge/*.json`, then
  `Anima:Knowledge:Packs` (files, directories or `*.json` globs). Missing default
  directories are ignored; a missing explicit path fails startup. Later packs may
  only widen what earlier ones added.
- **Catalog** — `KnowledgeVocabularyCatalog`: immutable, holds resolved
  `NodeKindDefinition` / `EdgeTypeDefinition` sets with lookups, `AllowsPair`
  (write-path pair validation), `InverseOf`-equivalent direction resolution and
  `IsAcyclic` (cycle enforcement). Built once, injected; stores stop reading the
  static `Definitions`.

### Pack schema

```jsonc
{
  "schema_version": 1,
  "name": "acme",
  "node_kinds": [
    { "name": "invoice", "description": "...",
      "required_props": ["repo", "amount"],
      "traits": ["plannable"],
      "id": null, "deprecated": false }
  ],
  "edge_types": [
    { "name": "billed_to", "description": "...",
      "pairs": [["invoice", "repo"]],
      "inverse_name": "billed_from", "symmetric": false, "acyclic": true,
      "resolves": false, "single_outgoing": false }
  ]
}
```

`pairs` is the canonical endpoint form. `source_kinds` / `destination_kinds` stay
as authoring sugar for cross products, are mutually exclusive with `pairs`, and
are always derived and re-emitted for wire compatibility. An empty list or `*`
means "any kind", preserving today's semantics. Unknown JSON members are
rejected (`JsonUnmappedMemberHandling.Disallow`) so a typo cannot silently become
a wildcard.

### Immutability policy

- Node kinds are **additive only**. A pack that redefines an existing kind fails
  startup; there is no merge, override or shadowing.
- Edge types may be **widened, never narrowed**. A pack may add endpoint pairs to
  an existing edge type (for example, letting `related_to` connect a new kind).
  It may not remove pairs, may not change `resolves`, `single_outgoing`,
  `acyclic`, `symmetric`, `inverse_name` or traits, and may not redeclare the id.
  Those flags are fixed by the defining pack because they carry graph semantics
  (derived `resolved` state, single-outgoing enforcement, cycle checks), not
  metadata. A widening entry **inherits** every field it omits (`null` flags,
  empty traits, no `inverse_name`), so a pack can widen pairs without restating
  them; declaring a different value is the error.
- Deletion is never applied. Entries gain `deprecated: true`, stay readable and
  queried, and are hidden from suggestion endpoints. Removing a core entry stays
  a core-pack edit in-repo.
- Core ids are permanent. A name resolving to a different id than the core pack
  declares is a startup error, not a silent remap.

### Ids

`kind_id` / `edge_type_id` appear in the wire shape, so ids must be stable across
processes and stores. Core ids are explicit and immutable (`1..21` kinds,
`1..27` edges, two separate id spaces). Pack entries either declare an id
`>= 1000` or get a deterministic FNV-1a derived id in `1000..66535` with
collision probing, so the in-memory store and SQLite agree without a database
round-trip.

### Edge direction

Edges are directed: one `source` and one `destination`, validated against the
edge type's endpoint pairs. Everything that reads edges is direction-aware:

| Surface | Direction support |
| --- | --- |
| `NeighborQuery.Direction` | `Outgoing`, `Incoming` or `Both`; `NeighborResult.Direction` always reports the **stored** orientation relative to the queried node, so it stays consistent with `Edge.Source`/`Destination` |
| `NodeQuery.Via` | first hop is undirected, so a name is enough |
| `NodeQuery.Descend` | always walks downward (children are the edge's source side); naming the inverse changes which edges match, never the walk direction |
| HTTP | `GET /api/nodes/{id}/neighbors?direction=outgoing|incoming|both&type=<name>`, and `type` accepts the inverse name |
| Writes | canonical name only; naming the inverse returns 422 with the canonical name and the instruction to write it from the other endpoint |

An inverse name resolves to `(definition, reversed: true)`; the requested direction
is flipped before matching stored edges. A symmetric type ignores the requested
direction and matches either endpoint. Edge type names never appear in SQL: trait
and traversal sets are passed as JSON and expanded with `json_each`.

### Field semantics

| Field | Implementation |
| --- | --- |
| `required_props` | Hard 422 on create, and on patch when the replacement property set drops a required key or blanks it. Enforced **only for kinds that declare it**, so no core kind gained a requirement. Keys compare `OrdinalIgnoreCase`; storage stays `Ordinal`. A patch replaces the property dictionary, so the check runs against the replacement set. |
| `inverse_name` | A **virtual** relation: the same stored edge read backwards. Edges are not double-stored, and the inverse must not also be defined as a real edge type. Reads accept either name; writes must use the canonical name. |
| `symmetric` | Special case of the inverse: `inverse_name` is the type itself, the pair set must be closed under swap, and the relation is undirected however it is stored. Cannot also be `single_outgoing`. |
| `acyclic` | Reachability check on edge insert: recursive CTE in SQLite, BFS in memory, with the write gate serializing writers. Core acyclic types: `part_of`, `depends_on`, `precedes`, `supersedes`. |
| `resolves`, `single_outgoing` | Already implemented; now read from the catalog instead of static definitions. |
| `traits` | Replaces name comparisons in generic graph logic (see below). |

Without `traits`, data-driving the definitions while leaving
`k.name IN ('task','goal')` in SQL would just relocate the hardcoding.

Three traits are defined, all core-owned:

| Trait | Applies to | Meaning |
| --- | --- | --- |
| `plannable` | node kinds (`task`, `goal`) | participates in plan and scope reads, and may be a task parent |
| `hierarchy` | edge types (`part_of`) | links a node to its parent for ancestor walks |
| `anchor` | edge types (`belongs_to`) | attaches a scope to its anchor, terminating an ancestor walk |

A node is anchored when an ancestor reached through `hierarchy` edges has an
outgoing `anchor` edge. The anchor walk no longer checks the node kind: the edge
type's `source_kinds` already restricts which kinds may carry an anchor edge, so
a kind check would be redundant coupling.

A pack that introduces a plannable parent kind must also **widen** the core
`part_of` edge to accept it, because the plan/task API keeps writing `part_of`.
That is the intended division of labour: the API owns which edge type it creates,
the vocabulary owns which kinds may appear on each end.

The plan and task API keep naming `task`, `goal` and `part_of` directly. Those are
that API's own contract (it creates those nodes and edges), not vocabulary drift
in generic logic, and a pack widening the pair sets does not change it.

### Startup and operations

- Migrations are DbUp scripts embedded in `Anima.Knowledge.Sqlite` and journaled
  in `SchemaVersions` (ADR 0008). Because no database has shipped yet,
  `0001_init.sql` is the final schema — it already carries the vocabulary columns
  (`required_props`, `traits`, `pairs`, `inverse_name`, `symmetric`, `acyclic`,
  `source`, `pack_version`, `checksum`, `active`, `deprecated`), so no `0002` is
  needed for this work. `PRAGMA user_version` is retired.
- Seeding becomes reconciliation: upsert core and pack rows with `source` and a
  checksum. A row whose pack disappeared is flagged inactive when no node uses
  it and reported when one does; nothing is deleted.
- `Anima:Knowledge:VocabularyMode` = `strict` (default: fail startup) | `warn`
  (log and skip the offending pack, then validate the rest). Packs are validated
  incrementally, so a skipped pack cannot mask a later conflict. Core violations
  are always fatal.
- `deprecated: true` hides an entry from `GET /api/node-kinds` and
  `GET /api/edge-types`, while writes still succeed with a warning so existing
  automation can finish migrating. Deprecated entries stay visible in
  `GET /api/vocabulary` (and the dashboard Vocabulary tab) so an operator can see
  what is still in use.

  Removal lifecycle, in full: mark the entry `deprecated: true` in the pack and
  restart → writes keep working but warn and the entry disappears from kind/edge
  listings → migrate the data off it → delete the remaining nodes/edges → drop the
  entry from the pack and restart. Reconcile then deactivates the row because
  nothing references it, and the retired name is rejected from then on
  (`is inactive because the pack that defined it was removed`). Dropping a pack
  entry while nodes still use it fails startup with the referencing kind named, so
  removal cannot silently orphan data. `SqliteVocabularyReconcileTests` covers this
  sequence end to end.
- Validation surface, mirroring `anima contracts validate`:
  `anima knowledge vocabulary show` (merged vocabulary),
  `anima knowledge vocabulary validate <pack.json>`,
  `GET /api/vocabulary` and `POST /api/vocabulary/validate` (the candidate pack is
  parsed and merged on top of the core pack, so typos, unknown kinds and core
  redefinitions fail with a message instead of at boot).
- `GET /api/node-kinds` gains `required_props`, `traits`, `deprecated`;
  `GET /api/edge-types` gains `pairs`, `inverse_name`, `symmetric`, `acyclic`.
  Both are additive JSON changes; `Node.kind` and `Edge.edge_type` stay strings,
  so no proto change is required.

### Compile-time names

`KnowledgeKinds` / `KnowledgeEdgeTypes` constants stay, because consumers such as
`KnowledgeWorkflowStore` need compile-time names. A drift test asserts the
constant set equals the core pack name set, so the two cannot diverge silently.
Generating the constants from the pack is a possible follow-up; it removes the
duplication but adds a build step.

## Consequences

- The standard vocabulary is reviewable as data, diffable, and versioned with the
  assembly; extensions are additive and validated instead of emerging from
  typos.
- `AllowAdHocKinds` no longer means "the vocabulary is open": ad-hoc kinds are
  recorded with `source: ad-hoc` and can be promoted into a pack or rejected.
- Widening core edges keeps the graph extensible without letting a pack change
  semantics that derived state depends on.
- Inverse and symmetric relations cost query complexity (union branches), not
  storage or event noise.
- Every vocabulary-shaped read passes through the catalog, so a pack change is
  visible to the stores without a rebuild.

## Open questions

Resolved:

- **Namespacing** is enforced for pack-added names.
- **`VocabularyMode`** `strict|warn` is implemented, strict by default.
- **Deprecation** hides an entry from listings and warns on write; the removal
  lifecycle is documented above and covered by a test.
- **Ad-hoc kinds** stay enabled by default. Rows are labelled `source = 'ad-hoc'`,
  allocate from the pack id band and are excluded from reconcile, so they no longer
  masquerade as core vocabulary or collide with pack ids. Requiring packs for every
  agent-authored memory kind would break existing harness writes for no gain now
  that ad-hoc kinds are visible and promotable; turning the default off remains a
  one-line config decision if curation becomes a problem.
- **Cross-pack widening**: `POST /api/vocabulary/validate` now validates the
  candidate on top of the packs the service actually loaded (`LoadWithPacks`), so a
  pack that widens another pack's entry behaves the same at validation time and at
  the next startup.

## Rollout

1. *(done)* Vocabulary types, embedded core pack, loader/validator, drift tests.
   Stores still read the static definitions; behavior is unchanged.
2. *(done)* DbUp migration runner with a `SchemaVersions` journal and the final
   initial schema (ADR 0008). Remaining: `anima knowledge vocabulary validate`.
3. *(done)* Traits: `plannable`, `hierarchy`, `anchor` in the core pack, with the
   stores consuming `KnowledgeVocabularyCatalog` instead of the static definitions.
   Name literals are gone from SQL; the plan/task API and `KnowledgeWorkflowStore`
   keep their own domain names by design.
4. Pack loading, reconciliation, id policy enforcement, widening rules wired into
   the catalog consumed by both stores.
5. *(done)* `required_props` enforcement for kinds that declare it (hard 422 on
   create and on patch; core kinds declare none).
6. *(done)* Inverse and symmetric read/write semantics, plus `acyclic` enforcement
   on edge insert (recursive CTE in SQLite, BFS in memory).
7. *(done)* Reconcile: a pack that disappears deactivates its unused rows and fails
   startup when the graph still uses them. Ad-hoc kinds are owned by writes
   (`source = 'ad-hoc'`), allocate from the pack id band, and are never
   deactivated by reconcile. A row whose id disagrees with the catalog fails
   startup instead of diverging between SQLite and the in-memory store.
