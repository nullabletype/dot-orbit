# dot-orbit agent evaluation cases

The reusable runner, budget controls and four generic exercises now live in the optional [Agentic Foundation toolkit](https://github.com/nullabletype/agentic-foundation/tree/main/toolkits/agent-evaluation). They are maintained there rather than copied into this application repository.

Orbit retains only its product-specific cases:

| Case | What it checks |
| --- | --- |
| `specification-boundary` | Issue readiness against Orbit's product, domain, encrypted local-only boundary and delivery contract |
| `accessibility-verification` | Accessible retry action and recovery status in synthetic Avalonia markup, plus honest human-verification limits |

The scripted reference here knows only these two exercises. Its success proves grader plumbing, not agent quality or real desktop accessibility. Local case tests run in Orbit CI without fetching Foundation or calling a model:

```sh
node --test evaluations/agent-loop/cases.test.mjs
```

## Run the cases with the external toolkit

Use Node.js 24.21.0 and Git. Start with clean committed checkouts. From the Orbit root, create a dedicated toolkit checkout using the exact revision in `toolkit.json` (do not switch an existing working checkout):

```sh
git clone https://github.com/nullabletype/agentic-foundation.git ../agentic-foundation-evaluations
git -C ../agentic-foundation-evaluations checkout --detach "$(node -p "require('./evaluations/agent-loop/toolkit.json').commit")"
node ../agentic-foundation-evaluations/toolkits/agent-evaluation/run.mjs \
  --repo . --cases evaluations/agent-loop/cases \
  --candidate node \
  --candidate-arg evaluations/agent-loop/reference-candidate.mjs \
  --candidate-identity orbit-scripted-reference \
  --trials 2 --max-corrections 0 \
  --results /tmp/orbit-reference.json
```

Choose a fresh results filename outside Orbit. This runs four attempts: two cases, twice each. The manifest pins the toolkit source without making it an application build dependency. When upgrading the pin, review the toolkit changes and repeat reference verification. Publish the Foundation toolkit commit before integrating this Orbit change so the pinned revision is fetchable.

For real agent trials, replace the candidate command and arguments with an externally restricted launcher; label its exact agent/model/configuration. The [toolkit README](https://github.com/nullabletype/agentic-foundation/tree/main/toolkits/agent-evaluation) explains arguments, review, results and adding cases. The CLI creates temporary checkouts but is not a sandbox: it forwards the host environment, and its graders are host commands. Real-agent trials need trusted process/command adapters, restricted credentials/network/filesystem/desktop access, and bounded time, output, scratch and token use. Never execute untrusted candidates on the host as a fallback. The included scripted check uses reviewed synthetic code only.

## When to reevaluate

Assess affected cases after material changes to agent scope, autonomy or privacy guidance, selected skills, task/handoff/review templates, candidate execution, correction/review behaviour, case fixtures, graders or result semantics. Run the scripted reference first, then at least two real-agent trials per affected case before relying on a new performance comparison. Record unavailable trials as pending. Copy-only corrections that cannot affect behaviour may explain why reevaluation is unnecessary.

Keep raw prompts, process output, traces, environment values and credentials out of results and public evidence. Record only aggregate outcomes and sanitised identities in bounded private local files with a cleanup owner. Worktree separation does not establish isolation. Model evaluation never replaces application tests, cross-platform CI, independent review, required human visual review, or best-effort manual assistive-technology checks with disclosed gaps.

## Historical baseline

[The original six-case baseline](baselines/2026-10-03-codex-gpt-5.5.json) remains unchanged as historical evidence. Its `dot-orbit-agent-evaluation:v1` success meant deterministic checks only; it cannot be converted into evidence of valid execution and independent approval. Later pre-extraction `:v2` results also belong to that earlier suite.

Extraction changes the case sets and produces `agentic-foundation-agent-evaluation:v1` results with separate toolkit fingerprints. Start a new comparison series; do not compare the old six-case success rate directly with this two-case pack or Foundation's four-case pack. Scripted verification establishes migration plumbing only; fresh real-agent performance baselines remain pending until run under a verified execution boundary.
