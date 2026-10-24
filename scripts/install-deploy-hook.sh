#!/usr/bin/env bash
# Install the Anima post-receive hook into a bare deployment repository.
#
#   scripts/install-deploy-hook.sh /srv/anima-deploy.git
#
# Edit <repo>/hooks/anima-deploy.env to set the service URL, repository name and
# optional HMAC secret. The hook and env file are not copied from the working
# tree; only the hook script comes from this repo.

set -euo pipefail

if [ "$#" -ne 1 ]; then
  echo "usage: scripts/install-deploy-hook.sh <bare-repo-path>" >&2
  exit 2
fi

repo=$1
if [ ! -d "$repo" ]; then
  echo "bare repo '$repo' does not exist" >&2
  exit 2
fi
if [ ! -d "$repo/hooks" ]; then
  echo "'$repo' does not look like a git repo (missing hooks/)" >&2
  exit 2
fi

script_dir=$(cd "$(dirname "$0")" && pwd)
source_hook="$script_dir/git-hooks/post-receive"
target_hook="$repo/hooks/post-receive"
env_file="$repo/hooks/anima-deploy.env"

install -m 0755 "$source_hook" "$target_hook"
if [ ! -f "$env_file" ]; then
  umask 077
  cat >"$env_file" <<'EOF'
# Anima deploy hook settings. Keep this file mode 600.
ANIMA_DEPLOY_HOOK_URL=http://127.0.0.1:7414/hooks/git/generic
# Use the repository full name, e.g. khorshuheng/anima.
ANIMA_DEPLOY_REPO=khorshuheng/anima
# Set the same value as Anima:Deploy:Secret when signature verification is on.
# ANIMA_DEPLOY_SECRET=
EOF
  chmod 600 "$env_file"
  echo "created $env_file; edit it before pushing"
else
  echo "kept existing $env_file"
fi

echo "installed $target_hook"
