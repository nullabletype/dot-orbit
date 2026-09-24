# Agent guidance

## Working style

- Treat this as a real desktop application, not a demo, once implementation begins.
- Keep changes bounded to one issue and preserve unrelated work.
- Inspect the current checkout and relevant domain documents before editing.
- Distinguish clearly between proposed, implemented, and verified behaviour.
- Prefer small vertical slices that leave the repository runnable.
- Do not push, publish, tag, or create releases unless explicitly asked.
- Use current, supported releases for direct and transitive dependencies, SDKs, runtimes, GitHub Actions, runner images, and packaging tools. Verify upstream support status and applicable security updates when selecting or updating versions. Deprecated, end-of-life, or unmaintained components block acceptance; a successful build or floating version tag is not evidence of support. Preserve reproducibility with explicit pins and lockfiles.
- GitHub artifact storage is exclusively for packaged, Release-configuration application binaries for an explicitly supported runtime. PDB files are optional. Never upload Docker/OCI images, container filesystems, SDKs, runtimes as standalone payloads, dependency caches, source trees, intermediate build output, test results, logs, screenshots, or general evidence bundles as GitHub artifacts. See `docs/development/github-artifact-policy.md`.

## Sources of truth

- Product boundary: `docs/product/brief.md`
- Behavioural requirements: `docs/product/requirements.md`
- Domain language and invariants: `CONTEXT.md`
- UI decisions: `docs/design/ui-specification.md`
- Architectural decisions: `docs/adr/`
- Delivery process: `docs/development/agent-loop.md`
- Verification expectations: `docs/development/definition-of-done.md`
- GitHub artifact restrictions: `docs/development/github-artifact-policy.md`

If these disagree, stop and surface the conflict rather than silently choosing one.

## Implementation loop

1. Select one issue labelled `ready-for-agent`.
2. Restate its acceptance criteria and non-goals.
3. Work on an isolated branch or worktree.
4. Implement the smallest end-to-end slice.
5. Run the relevant automated and manual checks.
6. Record evidence against the exact tested commit.
7. Request independent review when available.
8. Apply bounded corrections, then return unresolved design questions to the issue.
9. Leave a resumable handoff if the work is incomplete.

## Agent skills

### Issue tracker

GitHub Issues are the proposed work record. External pull requests are not currently treated as feature requests. See `docs/agents/issue-tracker.md`.

### Triage labels

The repository uses the five standard triage roles. See `docs/agents/triage-labels.md`.

### Domain docs

This is a single-context repository with `CONTEXT.md` at the root and ADRs under `docs/adr/`. See `docs/agents/domain.md`.
