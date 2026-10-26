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
- The driver obtains Chrome in one of three explicit modes (`Mode`, env
  `ANIMA_BROWSER_MODE`): `launch` spawns a throwaway Chrome with one context per
  session; `profile` spawns Chrome on a persistent `UserDataDirectory` (default an
  Anima-owned directory, never the user's real Chrome profile) so cookies and
  logins survive restarts; `connect` attaches over CDP (`ConnectEndpoint`, default
  `http://127.0.0.1:9222`) to a Chrome someone else started and never closes it.
  `profile` and `connect` share one context across sessions, so a session owns only
  its page, has no per-session `BaseURL`/trace, and relative targets are resolved
  by the driver instead of the context. A conflicting profile-plus-endpoint
  configuration fails at startup; the executable itself is probed on first launch.
- Scripts are `anima-yaml/v1` data, validated then interpreted step by step.
- Sessions record a bounded page/network event log: console, page errors, requests,
  responses, request failures, navigations and dialogs. Metadata by default;
  headers and bodies are opt-in and credential headers are always redacted. It is
  exposed by polling and by SSE, and dialogs are auto-dismissed unless the session
  asks for auto-accept. Scripts confirm a write with `waitForResponse` (method,
  url glob, status) rather than by scraping a confirmation banner; response
  events carry a request id so each wait consumes one exchange. Closing a session completes its log, so SSE clients see
  `event: closed` instead of a connection that never speaks again.
- Deploy scripts are launched by explicit interpreter rules: `.cmd`/`.bat` ->
  `cmd.exe /c`, `.exe` direct, `.sh` -> `bash`, `.ps1` only when
  `PowerShellExecutable` is configured. `Shell` overrides inference.
- Hook payloads are normalized server-side; scripts are repo-owned paths from
  rules, never payload input.

## Consequences

- Automation can move to a remote service without changing scripts.
- Sessions are observable without a debugger: console, page errors, network,
  navigations and dialogs land in a bounded per-session buffer that is readable by
  polling and by SSE, so a harness can assert on a failed request instead of
  re-running a script to watch it.
- `IBrowserSession` gained the event-log members (`ReadEvents`,
  `SubscribeEventsAsync`, `DroppedEventCount`); any out-of-tree driver must
  implement them.
- A reusable login survives restarts in `profile` mode, while `connect` mode
  drives a visible Chrome the operator already has, at the cost of a single shared
  context (tracing needs `launch` mode).
- Windows deploys need only `cmd.exe` or an executable; PowerShell is not a
  hidden dependency.
- Script format changes are additive and testable without a browser.
