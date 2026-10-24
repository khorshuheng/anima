#!/usr/bin/env bash
# Example deploy script for the local Docker Compose demo.
#
# Copy this file outside the Anima repo (for example
# /srv/anima-deploy/scripts/update-compose.sh), set ANIMA_COMPOSE_FILE, and
# point the deploy rules at it. The script is trusted code: it is selected by
# the service rules, never by the push payload.

set -euo pipefail

compose_file=${ANIMA_COMPOSE_FILE:?set ANIMA_COMPOSE_FILE to docker/compose.yml}
cd "$(dirname "$compose_file")"

printf 'deploying %s %s (%s) in %s\n' \
  "${ANIMA_DEPLOY_REPO:-unknown}" \
  "${ANIMA_DEPLOY_REF:-unknown}" \
  "${ANIMA_DEPLOY_AFTER:-unknown}" \
  "${ANIMA_DEPLOY_ENVIRONMENT:-dev}"

docker compose -f "$(basename "$compose_file")" up -d --build
