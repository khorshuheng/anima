#!/usr/bin/env bash
# Sample deploy target. Replace the echo with the real dev/UAT command
# (rsync, docker compose pull/up, kubectl rollout, ...).
set -euo pipefail

: "${ANIMA_DEPLOY_REPO:?repo is required}"
: "${ANIMA_DEPLOY_REF:?ref is required}"

echo "deploying ${ANIMA_DEPLOY_REPO}@${ANIMA_DEPLOY_REF} to ${ANIMA_DEPLOY_ENVIRONMENT:-dev}"
echo "commit ${ANIMA_DEPLOY_BEFORE:-?} -> ${ANIMA_DEPLOY_AFTER:-?} by ${ANIMA_DEPLOY_PUSHER:-unknown}"
echo "rule ${ANIMA_DEPLOY_RULE:-unknown} (run ${ANIMA_DEPLOY_RUN_ID:-unknown})"
