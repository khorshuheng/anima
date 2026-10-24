#!/usr/bin/env bash
# Install the push sink hook into a working repo or a bare repo.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
repo="${1:-$(pwd)}"

if [ -d "$repo/hooks" ] && [ ! -d "$repo/.git" ]; then
  hook_dir="$repo/hooks"
elif [ -d "$repo/.git" ]; then
  hook_dir="$repo/.git/hooks"
else
  echo "$repo is neither a git repo nor a bare repo" >&2
  exit 1
fi

install -m 0755 "$root/scripts/git-hooks/post-receive" "$hook_dir/post-receive"
if [ -d "$repo/.git" ]; then
  install -m 0755 "$root/scripts/git-hooks/pre-commit" "$hook_dir/pre-commit"
  install -m 0755 "$root/scripts/git-hooks/pre-push" "$hook_dir/pre-push"
  echo "installed $hook_dir/pre-commit"
  echo "installed $hook_dir/pre-push"
fi
echo "installed $hook_dir/post-receive"
