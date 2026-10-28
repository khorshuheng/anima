# Deployment

The deploy service is a **host-side push receiver**. It verifies a git push,
matches repository/branch rules from a JSON file outside the repo, and runs a
trusted script from a fixed script root. It never takes a script path from the
push payload.

The Docker Compose stack is a local demo of the Anima services; it is not the
deployment executor. Production/dev/uat scripts commonly trigger GitHub Actions
instead of rebuilding on the deployment host.

## Host deploy service

```bash
export Anima__Deploy__ScriptRoot=/srv/anima-deploy/scripts
export Anima__Deploy__RulesPath=/srv/anima-deploy/rules.json
export Anima__Deploy__VerifySignature=true
export Anima__Deploy__Secret='replace-with-a-long-random-secret'
dotnet run --project src/Anima.Deploy.Service
```

The service listens on `127.0.0.1:7414` by default. Its routes are:

- `POST /hooks/git/{provider}` — generic/GitHub/Gitea push payload.
- `GET /api/runs` — queued/running/finished runs.
- `GET /api/runs/{id}` and `/api/runs/{id}/log` — one run.

### Rules

`RulesPath` is an array of rules. `RepositoryPattern` and `RefPattern` are glob
patterns; `Script` is relative to `ScriptRoot`; `Environment` is passed to the
script. Scripts and rules stay outside the repo.

```json
[
  {
    "name": "anima-dev-compose",
    "repositoryPattern": "khorshuheng/anima",
    "refPattern": "refs/heads/main",
    "script": "update-compose.sh",
    "environment": "dev",
    "enabled": true
  },
  {
    "name": "anima-uat-actions",
    "repositoryPattern": "khorshuheng/anima",
    "refPattern": "refs/heads/release/**",
    "script": "trigger-github-actions.py",
    "environment": "uat",
    "enabled": true
  }
]
```

A matched script receives:

```text
ANIMA_DEPLOY_RUN_ID
ANIMA_DEPLOY_RULE
ANIMA_DEPLOY_REPO
ANIMA_DEPLOY_REF
ANIMA_DEPLOY_BEFORE
ANIMA_DEPLOY_AFTER
ANIMA_DEPLOY_PUSHER
ANIMA_DEPLOY_ENVIRONMENT
ANIMA_DEPLOY_SCRIPT_ROOT
```

See `examples/deploy/` for a compose updater and a GitHub Actions trigger.

### Bare repository hook

Install the hook into the bare repository that receives deployment pushes:

```bash
git init --bare /srv/anima-deploy.git
scripts/install-deploy-hook.sh /srv/anima-deploy.git
$EDITOR /srv/anima-deploy.git/hooks/anima-deploy.env
```

The env file is mode 600 and contains:

```bash
ANIMA_DEPLOY_HOOK_URL=http://127.0.0.1:7414/hooks/git/generic
ANIMA_DEPLOY_REPO=khorshuheng/anima
ANIMA_DEPLOY_SECRET=<same value as Anima:Deploy:Secret>
```

Then push to it:

```bash
git remote add deploy /srv/anima-deploy.git
git push deploy main
```

The `post-receive` hook sends one request per updated ref, with the old/new SHA
and ref. Deletions are ignored. The hook only queues work; the service worker
runs scripts one at a time.

## Local Compose demo

The demo builds one multistage image and runs knowledge, bus, workflow and
dashboard. Only the dashboard is published, on localhost:

```bash
make docker-build
make docker-up
curl -fsS http://127.0.0.1:7416/health
```

Open `http://127.0.0.1:7416/`. The knowledge/bus/workflow ports are not
published; containers reach each other by service name. The SQLite knowledge DB
is stored in the `anima-knowledge` volume. Reset it with:

```bash
docker compose -f docker/compose.yml down -v
```

## Knowledge vocabulary packs

The standard vocabulary is embedded; extra node kinds and edge types come from
packs, applied in this order at startup:

1. `<repo>/config/knowledge/*.json`
2. `~/.config/anima/knowledge/*.json`
3. `Anima__Knowledge__Packs__0`, `__1`, ... (files, directories or `*.json` globs)

`<repo>` is `$ANIMA_REPO_ROOT`, which the image sets to `/app`. The image copies
`config/`, so a pack committed to `config/knowledge/` ships with it; a file only in
a developer checkout does not. `examples/packs/example-vocabulary.json` is a
validated sample (`example:` namespace) you can copy as a starting point.

The compose demo can show a pack without changing the default stack:

```bash
docker compose -f docker/compose.yml -f docker/compose.pack.yml up -d
```

`docker/compose.pack.yml` bind-mounts `examples/packs` and points
`Anima__Knowledge__Packs__0` at the sample pack (it cannot be baked into the image
because `.dockerignore` excludes `examples/`).

To add a pack to a running compose stack without rebuilding, mount it and point the
service at it:

```yaml
  knowledge:
    volumes:
      - ./config/knowledge:/packs:ro
    environment:
      Anima__Knowledge__Packs__0: /packs/acme.json
      Anima__Knowledge__VocabularyMode: Warn   # optional: skip a broken pack instead of failing
```

```json
{
  "schema_version": 1,
  "name": "acme",
  "node_kinds": [{ "name": "acme:invoice", "required_props": ["repo"] }],
  "edge_types": [
    {
      "name": "acme:billed_to",
      "pairs": [["acme:invoice", "repo"]],
      "inverse_name": "acme:billed_from"
    }
  ]
}
```

A pack may only add names under its own prefix (`acme:`); references to core names
(`repo`, `part_of`) stay unprefixed.

- Packs are additive: they cannot redefine a core entry, and may only widen an
  existing edge type's endpoint pairs. Omitted flags, traits and `inverse_name`
  are inherited.
- `inverse_name` is virtual: read the relation from either end, write it in the
  canonical direction only.
- An invalid pack fails startup with the reason. Set
  `Anima__Knowledge__VocabularyMode=Warn` to log and skip the offending pack instead
  (useful while authoring; strict is the default).
- `deprecated: true` hides a kind or edge type from the kind/edge listings while
  writes continue with a warning; `GET /api/vocabulary` (and the dashboard
  Vocabulary tab) still lists it. To retire an entry: deprecate it, migrate the
  data off it, delete the remaining nodes, drop it from the pack and restart —
  startup deactivates the row when nothing references it, and refuses to start when
  something still does.
- Check a pack before deploying it:

  ```bash
  anima knowledge vocabulary validate config/knowledge/acme.json
  anima knowledge vocabulary show
  ```
- A pack that disappears deactivates its unused kinds/edge types automatically and
  fails startup if the graph still uses them.
- Ad-hoc kinds (an unknown kind written with `AllowAdHocKinds`) are recorded with
  `source = 'ad-hoc'` and share the pack id band, so formalizing one in a pack
  keeps the same id.

## Knowledge schema migrations

Knowledge applies its schema itself at startup: DbUp runs the SQL scripts embedded
in `Anima.Knowledge.Sqlite` (`src/Anima.Knowledge.Sqlite/Migrations/*.sql`) in
filename order, once each, and journals them in the `SchemaVersions` table. See
`docs/adr/0008-schema-migrations.md`.

- Nothing has been released, so no database needs upgrading: `0001_init.sql` is
the whole schema and there is no `0002`. A database from before DbUp is deleted,
not migrated.
- `PRAGMA user_version` is not used; `SchemaVersions` is the only version record.
- To inspect what was applied:

  ```bash
  sqlite3 "$HOME/.local/share/anima/knowledge.db" \
    'SELECT ScriptName, Applied FROM SchemaVersions ORDER BY SchemaVersionID;'
  ```
- Applied scripts are immutable. Adding a migration means adding
  `0002_<name>.sql`, never editing `0001_init.sql`.
- To start over locally, delete the state instead of writing a migration:
  `docker compose -f docker/compose.yml down -v` and
  `rm -f "$HOME/.local/share/anima/knowledge.db"*`.

The image build:

- restores and publishes the four services in a .NET SDK stage;
- builds `ui/dist` in a Node stage;
- copies only the published services, UI assets and contract descriptors into
  the ASP.NET runtime image;
- sets `ANIMA_REPO_ROOT=/app` so the dashboard serves `/app/ui/dist` and the
  services load `/app/contracts/descriptors`.

`.dockerignore` excludes VCS/agent state, build outputs, tests/tools/docs,
generated UI assets, local databases, env files and keys. It keeps only what the
Dockerfile actually copies.

## Production/uat pattern

Use a small Python script (for example `examples/deploy/trigger-github-actions.py`)
to dispatch a GitHub Actions workflow with `GITHUB_TOKEN`. The deploy host then
only needs network access to GitHub; it does not need Docker or repository
credentials. The local compose updater is for demo/testing only.

## Security and limits

- The deploy service is local-only by default; put it behind a private network
  and TLS if it is exposed.
- Enable HMAC verification and keep the secret in the hook env file/service
  config, never in git.
- Script content is trusted and must live under `ScriptRoot`; never derive it
  from the payload.
- Runs are currently held in memory and the queue is single-process. Restarting
  the deploy service loses run history; the script itself should be idempotent.
- The deploy component has unit tests for parsing, matching, signatures, rule
  loading, interpretation and script execution. A full bare-repo-to-service
  smoke is still a manual/integration check.

## Copy the host SQLite DB into the compose volume

For local testing, take a consistent snapshot rather than copying the live
database file:

```bash
sqlite3 "$HOME/.local/share/anima/knowledge.db" \
  ".backup '/tmp/anima-knowledge-snapshot.db'"

docker compose -f docker/compose.yml stop dashboard workflow knowledge

docker run --rm --entrypoint sh \
  -v anima_anima-knowledge:/data \
  -v /tmp:/backup \
  anima:local -lc '
    cp /backup/anima-knowledge-snapshot.db /data/knowledge.db
    rm -f /data/knowledge.db-wal /data/knowledge.db-shm
    chmod 0644 /data/knowledge.db
  '

docker compose -f docker/compose.yml up -d knowledge workflow dashboard
```

This leaves the host database and its live knowledge service untouched. The
compose volume is still separate: future writes in the container do not flow
back to the host DB. Verify with:

```bash
curl -fsS http://127.0.0.1:7416/api/dashboard/workflows | jq '.items | length'
curl -fsS 'http://127.0.0.1:7411/api/graph-health' | jq .
```

## Never commit SQLite files

`*.db`, `*.db-wal`, `*.db-shm`, `*.sqlite`, `*.sqlite3`, `*.db3` and journal/WAL
variants are ignored by `.gitignore` and `.dockerignore`. Install the local
guards as well:

```bash
make hooks-install REPO="$PWD"
```

The `pre-commit` hook rejects staged database files, including files added with
`git add -f`; `pre-push` rejects commits that add or modify them. Keep all
runtime databases outside the repository or in the compose named volume.
