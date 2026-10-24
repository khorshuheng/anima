# ADR 0004: Playwright behind a driver, scripts as data; explicit deploy shells

Status: accepted

## Context

UI automation must be CDP-capable with no stealth focus, and the deploy target is
Windows without PowerShell. Agents should be able to derive reusable scripts and
execution should not require an agent in the loop.

## Decision

- Automation: `IBrowserDriver`/`IBrowserSession` own the browser; Playwright is
  the implementation. Raw CDP is exposed only on Chromium-family engines.
- Chrome is assumed installed and launched from the system executable; no browser
  download is required. Other engines remain opt-in.
- Scripts are `anima-yaml/v1` data, validated then interpreted step by step.
- Deploy scripts are launched by explicit interpreter rules: `.cmd`/`.bat` ->
  `cmd.exe /c`, `.exe` direct, `.sh` -> `bash`, `.ps1` only when
  `PowerShellExecutable` is configured. `Shell` overrides inference.
- Hook payloads are normalized server-side; scripts are repo-owned paths from
  rules, never payload input.

## Consequences

- Automation can move to a remote service without changing scripts.
- Windows deploys need only `cmd.exe` or an executable; PowerShell is not a
  hidden dependency.
- Script format changes are additive and testable without a browser.
