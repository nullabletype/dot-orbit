# Agent-interchange version 2 contract

Status: accepted for #164.

Version 2 extends the [immutable version 1 contract](agent-interchange-v1-contract.md) with reviewed Today placement. The v1 documents, parser behavior, queued proposals and Apply semantics remain supported unchanged. There is no implicit conversion or promotion of authority. The bounds, strict lexical validation, ordered simulation, local-reference rules, closed offline schema catalogue, privacy limits and whole-proposal approval boundary remain those of v1.

## Documents and identity

The three v2 schemas and matching synthetic examples live under `schemas/interchange/v2/` and `examples/interchange/v2/`. Every v2 document declares `schemaVersion: 2`. Snapshot shape and inclusion rules are otherwise unchanged; the declared version participates in the canonical identity. Change Review and Apply recompute identity using the proposal's declared version. Latest snapshot and instruction copies use v2; explicit v1 resources remain available. Unsupported versions fail closed.

## Operations

- V2 New-work and Change `createTask` accept optional `todayLane`: `planned`, `inProgress`, or null. Omission/null creates the Task outside Today. The two lane values create it in that lane and produce a direct Today preview effect alongside creation.
- V2 Change adds `setTaskToday` with `operationId`, `task: { "existingId": "…" }`, required `todayLane` of `planned`, `inProgress`, or `none`, and optional inert `explanation`. `none` removes membership. Local references are excluded from this operation; specify a new Task's lane in `createTask`.
- No version adds shared Task, Today-lane, Project, or Category ordering authority. Existing Project-local task ordering remains unchanged. Today lanes are projections of the existing shared order.

## Validation and Review

The whole ordered proposal is simulated before approval. A repeated Today facet, an unknown lane/casing/member, a no-op, an unresolved reference, or a hidden target fails. Only active incomplete Tasks may end in Today. Reopen followed by Today placement is valid; a requested lane followed by completion, archive, or Bin is contradictory and fails final validation. Ordinary lifecycle actions without an explicit Today request continue to clear membership under their existing semantics.

Every explicit Today addition, lane move, or removal is a direct `task-today` effect, naming the Task and its current/proposed state: Not in Today, Today: Planned, or Today: In progress. Review groups that effect with the Task's other direct changes. Lifecycle-driven Today removal remains a derived effect under the existing complete-preview rules. Text and accessible names carry the meaning without colour or icons. Existing keyboard navigation, approval focus, status announcements and invalidation remain in place.

## Transport, queue and Apply

Typing, Paste, file, drop, and approved local MCP use the same declared-version parser and semantic planner. MCP discovery includes v1/v2 instruction/schema bundles and snapshot resources; read tools default to v2 and accept explicit v1. MCP has no approve, Apply, or direct mutation tool.

Queued documents retain their original bytes and version. Their encrypted compatibility basis retains the Task and parent dependencies needed for safe Today planning, including prior lane. An unrelated change may permit a new complete Review; a changed dependency fails closed. The retained approved plan uses the existing exclusive workspace identity/revision check, encrypted recovery point, atomic transaction, one revision increment, post-commit maintenance and receipt/approval replay prevention. A stale or failed apply cannot partially change Today.
