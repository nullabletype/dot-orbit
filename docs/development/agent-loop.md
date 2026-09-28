# Agentic delivery loop

dot-orbit should be built through small, observable vertical slices rather than one long autonomous run.

## Work record

Use GitHub Issues and a GitHub Project as the durable record. Conversation can shape a decision, but the accepted behaviour, non-goals, dependencies, and evidence belong in the issue.

## Issue readiness

Follow the canonical readiness, dependency, admission, and transition contract in [`docs/agents/issue-tracker.md`](../agents/issue-tracker.md). Agents take only issues currently labelled `ready-for-agent`.

## Execution

1. Take one ready issue.
2. Create an isolated branch or worktree from the agreed base.
3. Reconfirm the issue against current source and docs.
4. Implement the smallest vertical slice that proves the behaviour.
5. Run focused checks continuously and the agreed gate at the end.
6. Record commands, results, screenshots where relevant, and the tested commit SHA.
7. Request independent review when available.
8. Limit correction cycles; if the same unresolved design problem persists, return the issue to specification rather than looping indefinitely.
9. When the implementation is complete, verified, and ready for human review:
   - if the issue changes UI or UX, prepare the local build and wait for the user's local approval before publishing the branch;
   - otherwise, push the branch and open the review pull request without waiting for another instruction.
10. After local approval of UI or UX changes, push the branch and open the review pull request without waiting for another instruction.
11. Merge only verified work with explicit user authorisation, and update documentation in the same change when behaviour changes.

## Resumable handoff

Incomplete work must complete [`.github/HANDOFF_TEMPLATE.md`](../../.github/HANDOFF_TEMPLATE.md). Keep the handoff with the durable issue or pull-request record so another agent can resume without reconstructing local context.

## Evidence rules

- Evidence belongs to the exact tested commit.
- Before merging, follow the live controls, rollout order, and drift audit in [`main-branch-protection.md`](main-branch-protection.md).
- Passing automated checks do not substitute for required visual or desktop behaviour checks.
- A proposal is not implementation evidence.
- If independent review was unavailable, state that rather than implying review occurred.
- Record concise verification evidence in the issue or pull request. Do not upload logs, screenshots, test results, source trees, intermediate outputs, or general evidence bundles to GitHub artifact storage.
- GitHub artifacts are reserved for packaged, Release-configuration application binaries for supported runtimes; PDB files are optional. Containers and container filesystems are prohibited.
- Treat the cross-platform native desktop smoke journey and headless interaction suite as the routine pull-request baseline. Add targeted manual visual or accessibility checks when the changed behaviour needs them, and reserve full assistive-technology and supported-runtime verification for release milestones.
