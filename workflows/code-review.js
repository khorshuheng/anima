// Repo-owned independent review workflow.
//
// Generate the packet first:
//   scripts/review-packet.sh <base-ref> <head-ref> \
//     --verify "make check" --doc docs/code-quality.md
//
// Then launch one or more read-only reviewer lanes against the same packet:
//   subagent({
//     workflow: "./workflows/code-review.js",
//     args: {
//       repo: "/abs/path/to/repo",
//       baseRef: "main",
//       headRef: "HEAD",
//       packet: "/abs/path/to/repo/artifacts/review/.../packet.md",
//       diff: "/abs/path/to/repo/artifacts/review/.../diff.patch",
//       reviewers: [
//         { key: "correctness", agent: "reviewer", focus: "async, state, concurrency" },
//         { key: "contract", agent: "reviewer", focus: "CLI, docs, API, protocol" }
//       ]
//     }
//   });
//
// The workflow validates the review packet contract before dispatching. It does
// not read the filesystem, create worktrees, edit files, or call Anima.

const MAX_REVIEWERS = 8;
const MAX_FIELD_LENGTH = 16 * 1024;
const MAX_ACCEPTANCE_ITEMS = 20;
const WORKFLOW_KEY = /^[A-Za-z0-9][A-Za-z0-9._-]*$/;

function fail(message) {
  throw new Error("code-review: " + message);
}

function isObject(value) {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function requireString(value, field) {
  if (typeof value !== "string" || value.trim() === "") {
    fail(field + " must be a non-empty string");
  }
  if (value.length > MAX_FIELD_LENGTH) {
    fail(field + " exceeds " + MAX_FIELD_LENGTH + " characters");
  }
  return value;
}

function requireAbsolutePath(value, field) {
  requireString(value, field);
  if (!value.startsWith("/") && !/^[A-Za-z]:[\\/]/.test(value)) {
    fail(field + " must be an absolute path");
  }
  return value;
}

function stringArray(value, field, maxItems) {
  if (value === undefined) {
    return [];
  }
  if (!Array.isArray(value)) {
    fail(field + " must be an array of strings");
  }
  if (value.length > maxItems) {
    fail(field + " has more than " + maxItems + " items");
  }
  return value.map((item, index) => requireString(item, field + "[" + index + "]"));
}

function normalizeReviewer(reviewer, index, seenKeys) {
  if (!isObject(reviewer)) {
    fail("reviewers[" + index + "] must be an object");
  }

  const key = requireString(reviewer.key, "reviewers[" + index + "].key");
  if (!WORKFLOW_KEY.test(key)) {
    fail("reviewers[" + index + "].key must be a workflow key: " + key);
  }
  if (seenKeys.has(key)) {
    fail("duplicate reviewer key: " + key);
  }
  seenKeys.add(key);

  const agent = requireString(reviewer.agent, "reviewers[" + index + "].agent");
  const focus = reviewer.focus === undefined
    ? ""
    : requireString(reviewer.focus, "reviewers[" + index + "].focus");
  const acceptance = stringArray(
    reviewer.acceptance,
    "reviewers[" + index + "].acceptance",
    MAX_ACCEPTANCE_ITEMS);

  return {
    key,
    agent,
    focus,
    acceptance
  };
}

if (!isObject(args)) {
  fail("args must be an object");
}

const repo = requireAbsolutePath(args.repo, "repo");
const baseRef = requireString(args.baseRef, "baseRef");
const headRef = requireString(args.headRef, "headRef");
const packet = requireAbsolutePath(args.packet, "packet");
const diff = requireAbsolutePath(args.diff, "diff");
const changedFiles = args.changedFiles === undefined
  ? ""
  : requireAbsolutePath(args.changedFiles, "changedFiles");
const verifyCommands = stringArray(args.verifyCommands, "verifyCommands", 20);
const docs = stringArray(args.docs, "docs", 40);

const rawReviewers = args.reviewers === undefined
  ? [{ key: "review", agent: "reviewer" }]
  : args.reviewers;
if (!Array.isArray(rawReviewers) || rawReviewers.length === 0) {
  fail("args.reviewers must be a non-empty array");
}
if (rawReviewers.length > MAX_REVIEWERS) {
  fail("args.reviewers has more than " + MAX_REVIEWERS + " lanes");
}

const seenKeys = new Set();
const reviewers = rawReviewers.map((reviewer, index) =>
  normalizeReviewer(reviewer, index, seenKeys));

function outputContract(focus) {
  return [
    "Return exactly one ```json fenced object with this shape:",
    "{",
    '  "verdict": "ok | changes_required | blocked",',
    '  "diffReviewed": true,',
    '  "findings": [',
    "    {",
    '      "severity": "P0 | P1 | P2",',
    '      "kind": "correctness | wiring | concurrency | protocol | limits | performance | trust | docs",',
    '      "path": "src/Example.cs",',
    '      "line": 42,',
    '      "evidence": "VERIFIED | CODE-READING",',
    '      "summary": "what is wrong",',
    '      "fix": "smallest safe fix"',
    "    }",
    "  ],",
    '  "notExamined": ["surface not reviewed"],',
    '  "testsRun": ["command and observed result"]',
    "}",
    "",
    "Rules:",
    "- A missing or unreadable diff is verdict \"blocked\", never \"ok\".",
    "- Do not invent a diff; state the limitation if the packet cannot be read.",
    "- Detection first. Existing tests are not evidence of soundness.",
    "- If a shell is available, run the verify commands only after forming findings.",
    "- If no shell is available, set testsRun to [] and label findings CODE-READING.",
    "- Read-only: do not edit, create worktrees, commit, or start/stop services.",
    focus ? "- Lane focus: " + focus : ""
  ].filter(Boolean).join("\n");
}

function reviewerTask(reviewer) {
  const verify = verifyCommands.length === 0
    ? "_None supplied in the workflow args._"
    : verifyCommands.map((command) => "- `" + command + "`").join("\n");
  const contractDocs = docs.length === 0
    ? "_None supplied._"
    : docs.map((doc) => "- `" + doc + "`").join("\n");

  return [
    "Act as an independent, read-only reviewer for a committed change.",
    "Apply the code-review skill. Do not trust the author's summary or a green test suite.",
    "",
    "Repository: `" + repo + "`",
    "Base ref: `" + baseRef + "`",
    "Head ref: `" + headRef + "`",
    "Review packet: `" + packet + "`",
    "Diff: `" + diff + "`",
    changedFiles ? "Changed files: `" + changedFiles + "`" : "",
    "",
    "Read the packet, diff and surrounding code before forming findings.",
    "Check the mandatory wiring/contract surfaces: documented CLI flags, help",
    "text, API fields, protocol echoes, and skill examples must match the code.",
    "",
    "Verification commands supplied:",
    verify,
    "",
    "Contract/doc/skill paths to check:",
    contractDocs,
    "",
    outputContract(reviewer.focus),
    reviewer.acceptance.length === 0
      ? ""
      : "\nAdditional acceptance criteria:\n" +
        reviewer.acceptance.map((item) => "- " + item).join("\n")
  ].filter(Boolean).join("\n");
}

const results = await runs.all(
  reviewers.map((reviewer) => ({
    key: reviewer.key,
    label: reviewer.focus === ""
      ? "Independent review: " + reviewer.key
      : "Independent review: " + reviewer.key + " (" + reviewer.focus + ")",
    agent: reviewer.agent,
    cwd: repo,
    task: reviewerTask(reviewer)
  }))
);

const board = results.map((result, index) => ({
  key: reviewers[index].key,
  agent: reviewers[index].agent,
  focus: reviewers[index].focus,
  ok: result.ok === true,
  state: result.state,
  runId: result.runId,
  output: result.output,
  error: result.error
}));

return {
  ok: board.every((lane) => lane.ok),
  baseRef,
  headRef,
  packet,
  diff,
  reviewers: board
};
