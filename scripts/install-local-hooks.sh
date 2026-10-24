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
echo "installed $hook_dir/post-receive"
