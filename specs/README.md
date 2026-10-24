# Script specs

Agents derive these scripts once from an exploratory session, then the same file
runs unattended through `Anima.Automation.Dsl` (no agent in the loop).

Format: `anima-yaml/v1`.

| Verb | Value | Effect |
| --- | --- | --- |
| `goto` / `navigate` | url or path | navigate; relative paths use the session base URL |
| `click` | selector / Playwright text selector | click |
| `fill` | `{ target, value }` | fill an input |
| `press` | `{ target, key }` | press a key |
| `select` | `{ target, value }` | select an option |
| `waitFor` | `{ target, state?, timeout? }` | wait for visible/hidden/attached/detached |
| `waitForText` | `{ target, text, timeout? }` | wait until an element text contains a value |
| `expect` | `{ url?, target?, text?, value? }` | assert; fails the step on mismatch |
| `extract` | `{ name, target, attribute? }` | store text (or an attribute) in variables |
| `eval` / `evaluate` / `script` | expression | run a page expression, string result |
| `screenshot` | path | capture a full-page screenshot |

Variables use `${name}` and `${env.NAME}`. Runtime variables override script
variables. Unknown verbs, missing fields and malformed YAML are reported as
diagnostics with a step index.

Validate without launching a browser:

```bash
dotnet run --project tools/Anima.Cli -- scripts validate specs/login-smoke.yaml
```

Run (browser must be installed and selected explicitly):

```bash
dotnet run --project tools/Anima.Cli -- scripts run specs/login-smoke.yaml --browser firefox --base-url http://uat.local
```

## Error-path check

The Graph tab is a lazy chunk. To verify the boundary manually, block or delete
the generated `assets/GraphTab-*.js` request after the dashboard has loaded,
open **Graph**, and confirm the Workflows and Bus tabs still work. The panel
offers **Reload page**; a fresh page load is required because a stale hashed
chunk cannot be recovered by re-importing it.
