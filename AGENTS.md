# Agent guidance

## Working style

- Treat this as a real desktop application, not a demo, once implementation begins.
- Keep changes bounded to one issue and preserve unrelated work.
- Inspect the current checkout and relevant domain documents before editing.
- Distinguish clearly between proposed, implemented, and verified behaviour.
- Prefer small vertical slices that leave the repository runnable.
- Follow `docs/development/agent-loop.md` for branch publication and review pull requests. Merge, tag, or create releases only with explicit user authorisation.
- Use current, supported releases for direct and transitive dependencies, SDKs, runtimes, GitHub Actions, runner images, and packaging tools. Verify upstream support status and applicable security updates when selecting or updating versions. Deprecated, end-of-life, or unmaintained components block acceptance; a successful build or floating version tag is not evidence of support. Preserve reproducibility with explicit pins and lockfiles.
- Keep builds deterministic, enable nullable reference types and supported analyzers, and treat compiler and analyzer warnings as errors. Suppress a diagnostic only with a narrow, documented justification.
- Make time and side effects deterministic through `TimeProvider` and narrow adapters for filesystem, identifier generation, and platform operations. Do not hide domain logic behind broad service abstractions.
- Never log passwords, encryption keys, plaintext task content, database contents, backup contents, clipboard payloads, or other sensitive user data. Diagnostics must use non-sensitive operational metadata only.
- Treat accessibility as an acceptance requirement for every new or changed user action: it must be keyboard operable, expose meaningful accessible name, role, and state, preserve logical focus order, announce relevant changes, and never rely on colour or an icon alone.
- GitHub artifact storage is exclusively for packaged, Release-configuration application binaries for an explicitly supported runtime. PDB files are optional. Never upload Docker/OCI images, container filesystems, SDKs, runtimes as standalone payloads, dependency caches, source trees, intermediate build output, test results, logs, screenshots, or general evidence bundles as GitHub artifacts. See `docs/development/github-artifact-policy.md`.

## Sources of truth

- Product boundary: `docs/product/brief.md`
- Behavioural requirements: `docs/product/requirements.md`
- Domain language and invariants: `CONTEXT.md`
- UI decisions: `docs/design/ui-specification.md`
- UI components and visual states: reuse and test the recipes in `docs/design/style-guide.md`; rendered human review remains required when visual judgement is involved.
- Architectural decisions: `docs/adr/`
- Delivery process: `docs/development/agent-loop.md`
- Verification expectations: `docs/development/definition-of-done.md`
- GitHub artifact restrictions: `docs/development/github-artifact-policy.md`

If these disagree, stop and surface the conflict rather than silently choosing one.

## Agent coordination

- Use sub-agents by default when work can be split into useful, independently bounded implementation, research, testing, or review tasks. Do not create sub-agents for trivial or atomic work where delegation would add overhead without improving the result.
- Choose the lowest-cost model that is capable of completing each delegated task reliably. Reserve premium models for work whose complexity or risk justifies them; do not use them for routine work such as drafting commit messages.
- For decomposable work, the parent agent acts as the orchestrator: it defines task boundaries and acceptance criteria, delegates execution and independent review, evaluates the returned evidence, resolves conflicts, and makes the final integration decisions. It should not duplicate implementation already assigned to a sub-agent.
- Keep each sub-agent's scope explicit and non-overlapping. The parent remains responsible for inspecting the integrated diff and ensuring the repository-wide result satisfies the issue.
- Never let multiple mutating sub-agents work concurrently in the same checkout. Give each writer a separate worktree and branch, or designate one writer and keep every other concurrent agent read-only.
- When changing agent instructions, skills, templates, evaluation cases, or orchestration, follow the reevaluation triggers and privacy boundary in `evaluations/agent-loop/README.md`.

## Testing

- Add coverage at the boundary that proves the behaviour: unit tests for major domain and application behaviour; integration tests against the real SQLite3MC stack for persistence, encryption, migration, backup, and recovery; and interaction tests for important Avalonia workflows. Tests must exercise acceptance criteria and important failure or boundary cases, not merely execute code paths.
- Every bug fix requires a regression test that fails for the original defect and passes with the correction.
- Before opening or updating a pull request, complete a locked dependency restore and deterministic build and pass every affected test available in the local environment. Do not carry known test failures, skipped required coverage, or unresolved locally runnable checks into the pull request.
- Before merge, the required CI matrix must pass on macOS, Windows, and Linux, including a smoke test against a real encrypted store on each platform. A platform check that could not run is not a pass.
- When practical, have an agent other than the implementer review the change and its tests; the parent agent decides whether the evidence is sufficient to integrate.

## Implementation loop

1. Select one issue labelled `ready-for-agent`.
2. Restate its acceptance criteria and non-goals.
3. Work on an isolated branch or worktree.
4. Implement the smallest end-to-end slice.
5. Run the relevant automated and manual checks.
6. Record evidence against the exact tested commit.
7. Request independent review when available.
8. Apply bounded corrections, then return unresolved design questions to the issue.
9. If the work is complete, follow the delivery process; otherwise leave a resumable handoff.

## Agent skills

### Issue tracker

GitHub Issues are the proposed work record. External pull requests are not currently treated as feature requests. See `docs/agents/issue-tracker.md`.

### Triage labels

The repository uses the five standard triage roles. See `docs/agents/triage-labels.md`.

### Domain docs

This is a single-context repository with `CONTEXT.md` at the root and ADRs under `docs/adr/`. See `docs/agents/domain.md`.
