#!/usr/bin/env bash
# Optional: install Playwright-managed browser builds (firefox/webkit,
# or chromium if you do not want to use a system Chrome).
# Chrome uses the installed system executable and needs no download.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if [ "$#" -eq 0 ]; then
  if [ -n "${ANIMA_BROWSER:-}" ]; then
    set -- $ANIMA_BROWSER
  else
    echo "usage: $0 <chromium|firefox|webkit> [...]" >&2
    echo "       (or set ANIMA_BROWSER='firefox webkit')" >&2
    exit 2
  fi
fi

version="$(grep -oP '(?<=Include="Microsoft.Playwright" Version=")[^"]+' "$root/Directory.Packages.props")"
ps1="$(find "$root/src/Anima.Automation.Playwright/bin" -name playwright.ps1 -print -quit 2>/dev/null || true)"

if command -v pwsh >/dev/null 2>&1; then
  if [ -z "$ps1" ]; then
    echo "Build first: dotnet build src/Anima.Automation.Playwright" >&2
    exit 1
  fi
  pwsh -NoProfile -File "$ps1" install "$@"
elif command -v npx >/dev/null 2>&1; then
  echo "pwsh not found; using npx playwright@$version" >&2
  npx -y "playwright@$version" install "$@"
else
  echo "Install PowerShell (pwsh) or Node (npx), or install browsers manually." >&2
  exit 1
fi
