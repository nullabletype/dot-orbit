# Agentic delivery loop

dot-orbit should be built through small, observable vertical slices rather than one long autonomous run.

## Work record

Use GitHub Issues and a GitHub Project as the durable record. Conversation can shape a decision, but the accepted behaviour, non-goals, dependencies, and evidence belong in the issue.

## Issue readiness

An issue is `ready-for-agent` only when it contains:

- a user-visible outcome;
- acceptance criteria;
- explicit non-goals;
- relevant domain and ADR links;
- dependencies and known constraints;
- required automated and manual verification; and
- no unresolved decision that could materially change the implementation.

## Execution

1. Take one ready issue.
2. Create an isolated branch or worktree from the agreed base.
3. Reconfirm the issue against current source and docs.
4. Implement the smallest vertical slice that proves the behaviour.
5. Run focused checks continuously and the agreed gate at the end.
6. Record commands, results, screenshots where relevant, and the tested commit SHA.
7. Request independent review when available.
8. Limit correction cycles; if the same unresolved design problem persists, return the issue to specification rather than looping indefinitely.
9. Merge only verified work and update documentation in the same change when behaviour changes.

## Resumable handoff

Incomplete work must leave:

- current branch and commit;
- changed files;
- checks already run and their results;
- remaining acceptance criteria;
- exact blocker or decision required; and
- the next safe action.

## Evidence rules

- Evidence belongs to the exact tested commit.
- Passing automated checks do not substitute for required visual or desktop behaviour checks.
- A proposal is not implementation evidence.
- If independent review was unavailable, state that rather than implying review occurred.
- Record concise verification evidence in the issue or pull request. Do not upload logs, screenshots, test results, source trees, intermediate outputs, or general evidence bundles to GitHub artifact storage.
- GitHub artifacts are reserved for packaged, Release-configuration application binaries for supported runtimes; PDB files are optional. Containers and container filesystems are prohibited.

## Suggested first issue sequence

These are candidate slices, not filed issues:

1. Choose the implementation stack and record the decision.
2. Create a runnable desktop shell with the six-view navigation.
3. Implement the in-memory domain model and derived Project status.
4. Implement Projects and per-Project subtask ordering.
5. Implement the shared Today/Backlog order.
6. Implement Category inheritance, overrides, and Category ordering.
7. Implement direct inspector editing and Markdown rich copy.
8. Add local persistence with migration and recovery tests.
9. Implement Completed grouping and searchable Archive.
10. Package a first local release for the supported platforms.
