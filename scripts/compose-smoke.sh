#!/usr/bin/env bash
# Verify the local docker compose demo end to end.
#
#   scripts/compose-smoke.sh            # expects the stack to be up
#   scripts/compose-smoke.sh --clean    # wipe the volume, rebuild and bring it up first
#
# Checks container health, the dashboard BFF surface, the SSE stream, and one real
# round trip: create a workflow inside the network, then read it back through the
# dashboard, the knowledge graph and the bus. Exits non-zero on the first failure.
set -euo pipefail

compose_file="docker/compose.yml"
dashboard="http://127.0.0.1:7416"
clean=false

for arg in "$@"; do
  case "$arg" in
    --clean) clean=true ;;
    -h|--help) sed -n '2,9p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown argument: $arg" >&2; exit 2 ;;
  esac
done

failures=0
check() {
  local label=$1 expected=$2 actual=$3
  if [[ "$actual" == "$expected" ]]; then
    printf '  ok    %-46s %s\n' "$label" "$actual"
  else
    printf '  FAIL  %-46s expected %s, got %s\n' "$label" "$expected" "$actual"
    failures=$((failures + 1))
  fi
}

json() { python3 -c "import json,sys;d=json.load(sys.stdin);print($1)"; }

if [[ "$clean" == true ]]; then
  echo "== resetting the compose demo"
  docker compose -f "$compose_file" down -v >/dev/null 2>&1 || true
fi

if [[ "$(docker compose -f "$compose_file" ps --format '{{.Name}}' | wc -l)" -lt 4 ]]; then
  echo "== starting the compose demo"
  docker compose -f "$compose_file" up -d --wait >/dev/null
fi

echo "== containers"
for service in knowledge bus workflow dashboard; do
  status=$(docker compose -f "$compose_file" ps --format '{{.Name}} {{.Status}}' \
    | grep "^anima-${service}-1 " | sed 's/.*(\(.*\))/\1/' || true)
  check "$service health" healthy "$status"
done

echo "== dashboard surface"
check "GET /health" 200 "$(curl -s -o /dev/null -w '%{http_code}' "$dashboard/health")"
check "GET / (ui)" 200 "$(curl -s -o /dev/null -w '%{http_code}' "$dashboard/")"
for path in workflows kinds edge-types vocabulary graph nodes bus goals; do
  check "GET /api/dashboard/$path" 200 \
    "$(curl -s -o /dev/null -w '%{http_code}' "$dashboard/api/dashboard/$path")"
done

echo "== vocabulary"
check "node kinds" 21 \
  "$(curl -s "$dashboard/api/dashboard/kinds" | json "len(d['items'])")"
check "edge types" 27 \
  "$(curl -s "$dashboard/api/dashboard/edge-types" | json "len(d['items'])")"
check "traits exposed" "plannable,hierarchy,anchor" \
  "$(curl -s "$dashboard/api/dashboard/vocabulary" | json "','.join(d['traits'])")"

echo "== server-sent events"
check "bus stream emits ready" 1 \
  "$(curl -s --max-time 3 -N "$dashboard/api/dashboard/bus/stream?topics=anima.**" \
     | grep -c '^event: ready' || true)"

echo "== round trip: workflow -> knowledge -> dashboard -> bus"
name="compose smoke $(date +%s)"
created=$(docker exec anima-workflow-1 sh -lc "
  mkdir -p /tmp/compose-smoke
  curl -sS -X POST http://127.0.0.1:7415/api/workflows \
    -H 'content-type: application/json' \
    -d '{\"name\":\"${name}\",\"requirement\":\"compose smoke\",\"repoPath\":\"/tmp/compose-smoke\",\"baseRef\":\"main\"}'")
id=$(printf '%s' "$created" | json "d['id']" 2>/dev/null || echo "")
check "workflow created" "created" "$([[ -n "$id" ]] && echo created || echo failed)"

if [[ -n "$id" ]]; then
  check "visible through the dashboard" "$name" \
    "$(curl -s "$dashboard/api/dashboard/workflows" | json "[w['name'] for w in d['items'] if w['id']==$id][0]")"
  check "timeline has a phase" 1 \
    "$(curl -s "$dashboard/api/dashboard/workflows/$id/timeline" | json "len(d['phases'])")"
  check "knowledge node persisted" "$name" \
    "$(docker exec anima-knowledge-1 curl -s 'http://127.0.0.1:7411/api/nodes?kind=workflow&scope=all&summary=true' \
       | json "next(n['name'] for n in d['items'] if n['name']=='$name')")"
  check "graph is connected" 0 \
    "$(docker exec anima-knowledge-1 curl -s http://127.0.0.1:7411/api/graph-health | json "d['isolated_nodes']")"
  check "bus recorded workflow events" 1 \
    "$(curl -s "$dashboard/api/dashboard/bus" \
       | json "len([t for t in d['topics'] if t['topic']=='anima.workflow.events'])")"
  check "bus recorded graph events" 1 \
    "$(curl -s "$dashboard/api/dashboard/bus" \
       | json "len([t for t in d['topics'] if t['topic']=='anima.knowledge.changed'])")"
fi

echo
if [[ "$failures" -eq 0 ]]; then
  echo "compose smoke: PASS"
else
  echo "compose smoke: $failures check(s) failed"
  echo "logs: docker compose -f $compose_file logs --tail 50"
  exit 1
fi
