# ADR 0008: Schema migrations with DbUp

Status: accepted

## Context

Only one database exists in Anima, owned by the knowledge service:
`knowledge.db` (compose volume `anima-knowledge`). Bus is in-memory, workflow
persists through the knowledge HTTP API, the dashboard is a read-only BFF, and
automation/deploy have no store.

Schema evolution was a single hardcoded script: `Migrate()` read an embedded
`0001_init.sql`, ran it, and set `PRAGMA user_version = 1`, returning early when
the version was already `>= 1`. That cannot express a second migration, has no
journal, and rewrites the version separately from the DDL.

There are no released users, so the schema can be replaced outright instead of
being upgraded in place.

## Decision

Use **DbUp** (`dbup-sqlite`, scripts as embedded resources) for the knowledge
database.

- **Placement.** The dependency and the migration runner live in
  `Anima.Knowledge.Sqlite`, the project that already owns `Migrations/*.sql` as
  embedded resources and the `Microsoft.Data.Sqlite` reference. Not
  `Anima.Hosting`, not a shared `Anima.Migrations` package. The boundary rule is
  **one schema owner per database**, so the migration project boundary is the
  database boundary, not the service boundary.
- **Runner.** In-process during `SqliteKnowledgeGraph.Initialize()`, before the
  store opens its connection. Sequence: DbUp migration → open store connection →
  `PRAGMA journal_mode = WAL` → vocabulary seed. SQLite is a single file with a
  single writer, so an init container or sidecar would add a volume-permission
  race and buy nothing. Revisit this if the store ever moves to a networked
  database with multiple replicas.
- **Scripts.** `src/Anima.Knowledge.Sqlite/Migrations/*.sql`, embedded, applied in
  filename order, one transaction per script, forward-only.
- **Journal.** DbUp's `SchemaVersions` table is the only schema version source.
  `PRAGMA user_version` is retired — it stays `0` and nothing reads it.
- **Initial schema.** `0001_init.sql` is the final schema, including the
  vocabulary columns (`required_props`, `traits`, `pairs`, `inverse_name`,
  `symmetric`, `acyclic`, `source`, `pack_version`, `checksum`, `active`,
  `deprecated`) from `docs/adr/0007-knowledge-vocabulary-packs.md`. No `0002` is
  needed for the vocabulary work.
- **Data is not schema.** Vocabulary packs reconcile at startup
  (`Seed()` → later the pack loader). They are never applied as migrations: a
  migration runs once, while packs must apply on every boot and stay additive.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Hand-rolled ordered runner + journal table | ~60 lines, but reimplements script discovery, ordering, journaling and logging that DbUp already does, and would need its own tests |
| FluentMigrator | Typed migrations are nicer, but SQLite's limited `ALTER TABLE` pushes table rebuilds, FTS5 virtual tables and triggers into raw SQL anyway, so most scripts would still be `Execute.Sql(...)` with an extra dependency and migration-class layer |
| EF Core migrations | The stores are deliberately raw ADO.NET with no EF anywhere; adding the ORM for migrations only creates a model-vs-handwritten-SQL split |
| Init container / separate migrate command | Worthless for a single-writer embedded file; needed only for networked databases with replicas |

## Consequences

Verified by running `dbup-sqlite` 6.0.4 against this exact script in a scratch
project:

- It depends on `Microsoft.Data.Sqlite.Core` (not System.Data.SQLite), so there
  is no second SQLite driver; the app's `Microsoft.Data.Sqlite` 10.0.13 stays the
  loaded driver.
- The previous `0001_init.sql` executed unmodified: all tables, the FTS5 virtual
  table and all three FTS triggers were created, and `node_fts MATCH` returned
  hits. `SqliteConnectionManager.SplitScriptIntoCommands` does not break
  `BEGIN … END;` trigger bodies.
- The journal is created automatically as
  `SchemaVersions (SchemaVersionID, ScriptName, Applied)`; a second run logs
  "No new scripts need to be executed" and executes nothing.

Trade-offs accepted:

- **No checksums, replaced by a shape guard.** The community journal records script
  name and timestamp only, so an edited-but-applied script is not re-run. While the
  schema is still pre-release, `SqliteKnowledgeGraph.EnsureSchemaShape` verifies at
  startup that the columns the code needs exist, and fails with a delete-and-restart
  instruction instead of a runtime SQL error. The expected columns live in one place,
  `Anima.Knowledge.Sqlite/SchemaContract.cs`, and a migration test asserts the
  migrated schema satisfies it, so the guard cannot silently drift from the script. Applied scripts are immutable by review
  convention, and `MigrationTests` runs the whole set twice against an empty database
  plus once against a simulated legacy database. Replace the shape guard with real
  checksums (or drop it) once the schema is released and 0001 stops changing.
- **One writer.** Two processes on one database file could race the journal.
  Not supported, and not reachable: the shared services use reserved ports and
  one DB, and private smoke instances use their own temp database.
- **No down-migrations.** Recovery is restore-from-snapshot or a forward fix.
  Acceptable while the database is a local file that can be reset with
  `docker compose -f docker/compose.yml down -v`.

## Rules for reaching a second database

These follow from the placement decision and are the checklist when another
service becomes stateful:

1. Database per service; services never share tables.
2. Migrations live with the schema-owning persistence adapter and ship in that
   service's image, so a migration is only ever valid against the code that
   queries those tables.
3. One journal per database. Never one journal spanning several schemas.
4. Who runs migrations is an explicit, documented decision: in-process at startup
   for a single-writer embedded database, a dedicated step for a networked
   database with replicas.
5. Applied scripts are immutable; migrations are forward-only.
6. Schema evolution and reference data stay separate (see the vocabulary packs in
   ADR 0007).
7. Look for an `EnsureDatabase`-style readiness barrier in CI that runs the
   migration set against an empty database, not only against a developer's
   existing one.
