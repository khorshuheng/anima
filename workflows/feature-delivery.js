// Repo-owned pi-subagents workflow template for parallel feature lanes.
//
// Launch it with a plain-JSON lane list:
//
//   subagent({
//     workflow: "./workflows/feature-delivery.js",
//     args: {
//       lanes: [
//         {
//           key: "api",
//           agent: "worker",
//           cwd: "/abs/path/to/worktree",
//           task: "Implement POST /x in your worktree.",
//           label: "Implement API endpoint",
//           acceptance: ["201 on a valid body", "400 on an invalid body"]
//         }
//       ]
//     }
//   });
//
// The workflow validates the whole lane inventory before dispatching, so one bad
// lane fails the script without launching any child. Each lane maps to one
// `runs.all` child; the result board keeps the input order and includes each
// child's output. Worktrees, branches and results stay harness-owned: this
// template does not write files or call Anima.

const MAX_LANES = 16;
const MAX_FIELD_LENGTH = 16 * 1024;
const MAX_ACCEPTANCE_ITEMS = 20;
const WORKFLOW_KEY = /^[A-Za-z0-9][A-Za-z0-9._-]*$/;
const DEFAULT_BUDGET = [
  "Work economically: read only files named in the task/spec, use CodeGraph",
  "for callers and impact instead of scanning the repository, batch shell",
  "commands, run the full test suite once at the end, and stop/report if blocked."
].join(" ");

function fail(message) {
  throw new Error("feature-delivery: " + message);
}

function isObject(value) {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function requireString(value, field, index) {
  if (typeof value !== "string" || value.trim() === "") {
    fail("lanes[" + index + "]." + field + " must be a non-empty string");
  }
  if (value.length > MAX_FIELD_LENGTH) {
    fail("lanes[" + index + "]." + field + " exceeds " + MAX_FIELD_LENGTH + " characters");
  }
  return value;
}

function requireAbsolutePath(value, field, index) {
  requireString(value, field, index);
  if (!value.startsWith("/") && !/^[A-Za-z]:[\\/]/.test(value)) {
    fail("lanes[" + index + "]." + field + " must be an absolute path");
  }
  return value;
}

function normalizeLane(lane, index, seenKeys) {
  if (!isObject(lane)) {
    fail("lanes[" + index + "] must be an object");
  }

  const key = requireString(lane.key, "key", index);
  if (!WORKFLOW_KEY.test(key)) {
    fail("lanes[" + index + "].key must be a workflow key (letters, digits, '.', '_', '-'): " + key);
  }
  if (seenKeys.has(key)) {
    fail("duplicate lane key: " + key);
  }
  seenKeys.add(key);

  const agent = requireString(lane.agent, "agent", index);
  const cwd = requireAbsolutePath(lane.cwd, "cwd", index);
  const task = requireString(lane.task, "task", index);
  const budget = lane.budget === undefined
    ? ""
    : requireString(lane.budget, "budget", index);

  let label = agent + " " + key;
  if (lane.label !== undefined) {
    label = requireString(lane.label, "label", index);
  }

  const acceptance = [];
  if (lane.acceptance !== undefined) {
    if (!Array.isArray(lane.acceptance)) {
      fail("lanes[" + index + "].acceptance must be an array of strings");
    }
    if (lane.acceptance.length > MAX_ACCEPTANCE_ITEMS) {
      fail("lanes[" + index + "].acceptance has more than " + MAX_ACCEPTANCE_ITEMS + " items");
    }
    for (let itemIndex = 0; itemIndex < lane.acceptance.length; itemIndex += 1) {
      acceptance.push(requireString(lane.acceptance[itemIndex], "acceptance[" + itemIndex + "]", index));
    }
  }

  return { key, agent, cwd, task, label, acceptance, budget };
}

function laneTask(lane, workflowBudget) {
  return [
    lane.task,
    lane.acceptance.length === 0
      ? ""
      : "\nAcceptance criteria:\n" + lane.acceptance.map((item) => "- " + item).join("\n"),
    "\nExecution guidance:\n" + (lane.budget || workflowBudget || DEFAULT_BUDGET)
  ].filter(Boolean).join("\n");
}

if (!isObject(args)) {
  fail("args must be an object with a lanes array");
}
if (!Array.isArray(args.lanes) || args.lanes.length === 0) {
  fail("args.lanes must be a non-empty array");
}
if (args.lanes.length > MAX_LANES) {
  fail("args.lanes has more than " + MAX_LANES + " lanes");
}

const workflowBudget = args.budget === undefined
  ? DEFAULT_BUDGET
  : requireString(args.budget, "budget");

const seenKeys = new Set();
const lanes = args.lanes.map((lane, index) => normalizeLane(lane, index, seenKeys));

const results = await runs.all(
  lanes.map((lane) => ({
    key: lane.key,
    label: lane.label,
    agent: lane.agent,
    cwd: lane.cwd,
    task: laneTask(lane, workflowBudget)
  }))
);

const board = results.map((result, index) => ({
  key: lanes[index].key,
  agent: lanes[index].agent,
  cwd: lanes[index].cwd,
  ok: result.ok === true,
  state: result.state,
  runId: result.runId,
  output: result.output,
  error: result.error
}));

return {
  ok: board.every((lane) => lane.ok),
  lanes: board
};
