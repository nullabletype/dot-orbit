# Agent-loop evaluation baseline

This dependency-free local suite measures whether material changes to dot-orbit's agent instructions or orchestration preserve six representative engineering behaviours. It is a small regression signal, not a product gate, model leaderboard, or substitute for product tests, cross-platform CI, independent review, rendered UI review, or assistive-technology verification.

## Cases and scoring

Each case manifest under `cases/` defines a privacy-safe starting state, exact allowed paths, the task prompt, expected observable result, deterministic checks, and a 100-point rubric. A trial passes only when every check passes; the score exists to show which boundary failed, not to average away a required failure.

| Case | Engineering behaviour |
| --- | --- |
| `specification-boundary` | Complete, bounded specification work aligned with the local-only encrypted product boundary |
| `bounded-ordering` | A small manual-ordering code change with immutable input and edge handling |
| `regression-date-validation` | A regression repair with a test for impossible and leap-year dates |
| `documentation-drift` | Reconciliation of operator documentation with executable help |
| `accessibility-verification` | Accessible name, keyboard order, non-colour state, and honest verification limits |
| `dependency-security-review` | Supported reproducible package/action pins from supplied offline evidence |

The fixtures are transparent and synthetic. They test repeatable repository behaviour rather than resistance to benchmark gaming. Version a case when its product or architecture assumptions change; do not silently loosen its grader.

## Run locally

The candidate is an executable that reads the task from standard input and runs with a detached temporary Git worktree as its current directory. Run at least two trials. The result path must be outside the source checkout and must not already exist.

```sh
node evaluations/agent-loop/run.mjs \
  --candidate codex \
  --candidate-arg --ask-for-approval \
  --candidate-arg never \
  --candidate-arg --model \
  --candidate-arg gpt-5.5 \
  --candidate-arg exec \
  --candidate-arg --ephemeral \
  --candidate-arg --sandbox \
  --candidate-arg workspace-write \
  --candidate-arg - \
  --candidate-identity codex-v0.142.5-gpt-5.5-medium \
  --trials 2 \
  --max-corrections 1 \
  --results /absolute/private/path/baseline.json
```

Use the deterministic reference candidate to validate the harness and every positive grader without invoking a model:

```sh
node evaluations/agent-loop/run.mjs \
  --candidate node \
  --candidate-arg evaluations/agent-loop/reference-candidate.mjs \
  --candidate-identity scripted-reference \
  --trials 2 \
  --max-corrections 0 \
  --results /absolute/private/path/reference.json
```

The scripted reference is `agentPerformance: false` in interpretation: it proves isolation, grading, repeated-trial aggregation, and serialization, but does not show that current instructions cause an agent to succeed. A real instruction baseline uses an actual agent candidate as in the first command.

An optional independent reviewer follows the same stdin/cwd contract. Add `--reviewer <executable>` and repeated `--reviewer-arg <argument>` values. Exit zero means approved; another normal exit means changes requested; a timeout or spawn error is recorded as errored. When omitted, every trial records review as unavailable rather than implying review happened.

## Recorded metrics

The machine-readable JSON records the exact source commit; location-independent content hashes of every tracked Markdown instruction/reference and every tracked suite/orchestration input except prior baselines; the candidate identity label; repeated-trial successes; first-pass gate rate; harness-owned correction cycles; unrelated-file churn; elapsed time; failed/timed-out candidate invocations; and independent-review outcome. Elapsed time is noisy and non-scoring. Candidate output byte count is retained only to confirm output was discarded. External user instructions, installed skills, and model configuration are not repository inputs, so the operator must represent them in a non-sensitive candidate identity and interpret comparisons accordingly.

The initial real-agent baseline is checked in at [`baselines/2026-10-03-codex-gpt-5.5.json`](baselines/2026-10-03-codex-gpt-5.5.json); read its aggregate rather than copying mutable statistics into this hashed runbook. The candidate model is part of that baseline identity, not a permanent recommendation; select and label the supported candidate being measured when recording a later baseline. A result records the clean evaluated commit immediately before the excluded result JSON is added, so committing that JSON does not invalidate the instruction or harness hashes.

The runner never serializes prompts, stdout, stderr, diffs, file contents, environment values, credentials, raw traces, or reviewer prose. Results are local records; they are not GitHub Actions artifacts. A deliberately curated aggregate baseline may be committed after privacy review, but raw logs and general evidence bundles may not.

`toolFailures` counts candidate process failures visible to the harness. It cannot observe every internal tool call made by an external agent. This limitation is included in every result record.

## Isolation and failure handling

The runner refuses a dirty source checkout, records its branch and exact commit, creates one detached OS-temporary worktree per case and trial, and rechecks the source checkout before writing results. Candidate commands use argument arrays with `shell: false`. A timeout first terminates the candidate process group, then force-kills it after a bounded grace period and records the timeout even if the child ignores both signals. Manifests accept only fixed built-in grader kinds. Paths must be repository-relative and may not escape with `..`.

Infrastructure failures must be rerun and must not be interpreted as candidate quality. Failed deterministic checks may trigger only the configured bounded correction cycles. Raw candidate output is discarded even on failure.

## When to reevaluate

Run the reference candidate first, then a real two-or-more-trial baseline when a material change can affect any of these branches:

- task interpretation, autonomy, privacy, or scope in `AGENTS.md` or a document it points to;
- skill selection or instructions used for specification, implementation, testing, accessibility, security, or review;
- issue, handoff, pull-request, or other agent-facing templates;
- worktree isolation, candidate invocation, correction, independent-review, or orchestration behaviour;
- any case fixture, allowed scope, deterministic check, rubric, result field, or aggregate calculation.

Harness, schema, or case changes require a new reference run and a new real baseline before comparing results with the prior version. Copy-only changes that cannot affect agent behaviour or grading may record that reevaluation was unnecessary, with the reason, in the associated pull request.

## Harness checks

```sh
node --test evaluations/agent-loop/harness.test.mjs
node evaluations/agent-loop/run.mjs --help
```

The repository's canonical .NET verification gate remains separate and unchanged.
