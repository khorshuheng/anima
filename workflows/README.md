# Repo-owned pi-subagents workflows

These scripts are checked in so a harness run does not depend on a `/tmp` script
that disappears with the session. The workflow sandbox has no filesystem, shell
or Anima access; the script only validates `args` and launches child agents.

## `feature-delivery.js`

One child per lane, all lanes launched together with `runs.all`.

```js
subagent({
  cwd: "/path/to/repo",
  workflow: "./workflows/feature-delivery.js",
  args: {
    lanes: [
      {
        key: "api",
        agent: "worker",
        cwd: "/abs/path/to/worktrees/feature-api",
        task: "Implement the assigned workflow task. Commit on your branch.",
        label: "Implement API task",
        acceptance: ["POST /api/x returns 201", "invalid body returns 400"]
      },
      {
        key: "ui",
        agent: "worker",
        cwd: "/abs/path/to/worktrees/feature-ui",
        task: "Implement the assigned UI task. Commit on your branch."
      }
    ]
  }
});
```

### Parameters

| Field | Required | Meaning |
| --- | --- | --- |
| `args.lanes` | yes | 1-16 lane objects; the whole list is validated before any child launches |
| `lanes[].key` | yes | Unique workflow key (`letters`, digits, `.`, `_`, `-`); used as the child key |
| `lanes[].agent` | yes | Agent name resolved in the launch `cwd`/agent scope, e.g. `worker` |
| `lanes[].cwd` | yes | Absolute path to the worktree the child works in |
| `lanes[].task` | yes | Child brief; keep ids and file paths, not whole specs |
| `lanes[].label` | no | Display label; defaults to `<agent> <key>` |
| `lanes[].acceptance` | no | Up to 20 strings appended to the task as an acceptance list |

Validation failures throw before dispatch: non-object args, empty/oversized lane
lists, duplicate keys, unknown fields with invalid types, relative `cwd`, and
oversized strings. Task text is capped at 16 KiB per field; put long documents in
files and reference the path.

### Result

The script returns a plain-JSON board in input order:

```json
{
  "ok": false,
  "lanes": [
    {
      "key": "api",
      "agent": "worker",
      "cwd": "/abs/path/to/worktrees/feature-api",
      "ok": true,
      "state": "complete",
      "runId": "<child run id>",
      "output": "<child final output>"
    }
  ]
}
```

`ok` is true only when every lane child completed successfully. Read `output` per
lane; the board intentionally does not copy child transcripts.

## Isolation rules

- One writer per worktree. Create the worktree and branch in the harness, give
  each lane a distinct `cwd`, and never reuse a worktree for two lanes.
- The workflow template does not create worktrees, commit, or merge. The harness
  registers assignments and posts results to Anima.
- Child workers may start private service instances on ports >= 7500 with
  `scripts/private-service.sh`; they must not touch shared services on 7410-7416.
- A failed or blocked lane does not cancel sibling lanes (`runs.all` returns every
  receipt), but treat overall delivery as incomplete until each `ok` is true.
- Pass ids, paths and acceptance criteria in `args`; do not put secrets or long
  specs there. Workflow args are persisted with run evidence.
