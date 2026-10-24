# Code quality

## Build gates

The repo already enables the SDK .NET analyzers (`LatestRecommended`,
`AnalysisMode=Recommended`, `EnforceCodeStyleInBuild`) with
`TreatWarningsAsErrors=true`. On top of that:

- **Roslynator.Analyzers 5.0.1** — refactorings, simplification and correctness.
- **Roslynator.Formatting.Analyzers 5.0.1** — formatting consistency.
- **SonarAnalyzer.CSharp 10.35.0.4138** — bug/security/code-smell rules without a
  Sonar server.

All are `PrivateAssets="all"` analyzer packages declared once in
`Directory.Build.props` and pinned in `Directory.Packages.props`.

```bash
make check          # build + tests + dotnet format --verify-no-changes
make build          # strict build with analyzers
make fmt            # apply dotnet format
```

### Rule policy

A new analyzer rule is either fixed or explicitly configured in
`.editorconfig` with a reason. Current downgrades are limited to:

- style-only Sonar rules (`S2325`, `S3358`, `S3267`, `S1751`, `S4456`,
  `S3400`, `S3241`, `S2857`, `S2365`, `S2094`, `S1066`, `S6966`)
- known false positives in this codebase (`S1075` hardcoded defaults,
  `S2077` constant+parameterized SQL)
- `S8949` (background lifetimes are explicit)
- `S8969` (xUnit `Assert.NotNull` is not a nullable-flow annotation)

Do not add `NoWarn` or a new downgrade without a comment and, ideally, a test
or a code fix. Fix correctness/security rules instead of suppressing them.

## SonarLint / SonarQube

- **SonarLint** is an IDE extension; use it locally if installed. It is not a
  repo dependency and does not gate CI.
- **SonarQube/SonarCloud** is intentionally not wired. It requires a server,
  token and coverage pipeline. If adopted, add `dotnet-sonarscanner` as a CI
  step, not a local build dependency.

## CodeGraph

CodeGraph is a local structural index, separate from rule-based analyzers.
The index is generated under `.codegraph/` and ignored by git.

```bash
codegraph init          # once per checkout
codegraph sync          # after merges
codegraph status
codegraph context "workflow assignment acknowledgement result evidence"
codegraph impact KnowledgeWorkflowStore
codegraph callers IKnowledgeGraph.GetGraphAsync
```

Use it in the harness/reviewer, not inside every worker:

- build a task packet with `codegraph context` / `impact` before worktree
  assignment;
- after merge, `codegraph sync` and use `impact`/`callers` to review blast radius
  and forbidden cross-component references;
- a worktree has its own checkout; do not assume the main index describes it.

`make graph-sync` wraps `codegraph sync`.

## Independent review packet

Review is a separate phase from build gates. Pinning the change makes the review
reproducible and prevents a reviewer from silently reviewing only the working
tree:

```bash
scripts/review-packet.sh 8c1b66f HEAD \
  --verify "make check" \
  --doc docs/code-quality.md \
  --doc .agents/skills/anima-orchestration/SKILL.md
# or: make review-packet BASE=8c1b66f HEAD=HEAD
```

The packet is written to `artifacts/review/<base>..<head>/` (gitignored) and
contains `packet.md`, `diff.patch`, `changed-files.txt` and the commit log.
Launch review lanes with `workflows/code-review.js`; each lane is read-only and
must return:

- `verdict: ok | changes_required | blocked`
- `diffReviewed` plus `findings` (`P0/P1/P2`, kind, path/line, evidence label)
- `notExamined` and `testsRun`

A blocked verdict, or a reviewer that could not read the diff, is a failed
review. P0/P1 findings block completion; P2 findings are recorded. The CLI and
docs are part of the contract: a documented command that does not run is a P1.

## Review cost controls

Reviewers cache prompt reads, but each review is a fresh session and long,
broad exploration still accumulates millions of tokens. Defaults:

- one reviewer lane; add a second only for high-risk diffs;
- `budget: { maxToolCalls: 18, maxFiles: 12 }` on the review workflow;
- read the diff, changed files and direct callers first; do not inventory the
  whole repository;
- on re-review use the previous reviewed SHA as the base, set `incremental`,
  and include prior findings;
- keep `make check`/analyzer/test gates separate from the reviewer.

Worker lanes should follow the same discipline: named files and CodeGraph
callers, batched shell commands, one full verify run, then stop and report.
