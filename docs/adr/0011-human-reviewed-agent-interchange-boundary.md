# Human-reviewed agent-interchange boundary

- Status: Accepted
- Date: 2026-10-12
- Amended by: [ADR 0012 — Context-free New-work proposals](0012-context-free-new-work-proposals.md), which adds a creation-only proposal alongside the snapshot-bound Change proposal.

## Context

dot-orbit plans to exchange a bounded view of current work with external planning tools and accept proposed changes without integrating an AI provider. The earlier requirements left incompatible interpretations: snapshots were described as incomplete-only while proposals could reopen or archive existing Tasks; version 1 was expected to convert an invented previous version; reusable Category and Participant mutations were not bounded; and proposal ingress, identity, stale-state protection, approval scope, and resource limits were unresolved.

The proposal is attacker-controlled plaintext. Schema-valid input can still violate domain invariants, hide aggregate effects, exhaust resources, or become stale between preview and Apply. `DATA-008` also requires a validated encrypted recovery point before import changes working data. A user-facing digest or a revision check performed only during Review cannot close that time-of-check/time-of-use gap.

Agent interchange is distinct from the complete one-way plaintext workspace export. It also must provide one application boundary that a later authenticated local MCP transport can reuse without obtaining autonomous mutation authority.

## Decision

Adopt the [agent-interchange version 1 contract](../planning/agent-interchange-v1-contract.md).

Version 1 is one provider-neutral, human-reviewed exchange:

- dot-orbit generates an immutable Planning snapshot of all reusable Categories and Participants, active Projects, and non-archived, non-binned standalone or active-Project Tasks, including completed Tasks;
- an external tool returns one strict, versioned JSON proposal, tied to that snapshot when it updates or connects existing work;
- dot-orbit validates the ordered proposal prospectively into one immutable execution plan, derives its complete authoritative effect model only from that plan, and presents the directly added or changed work while treating routine derived mechanics as implied; and
- the user approves the whole proposal, which applies once as one recovery-backed transaction or not at all.

Archived and binned Project and Task content is not disclosed externally. The complete internal effect model still validates every affected aggregate, including archived and already-binned Project children. Human review shows the direct aggregate action and identifies normal child lifecycle consequences as implied rather than expanding every child into a row. Category and Participant catalogue labels are included for stable references, and the copy preview discloses that they may reveal context used only by hidden work.

Version 1 may create Categories and Participants and may reference or associate them. It cannot rename, merge, recolour, reorder, or delete those global reusable records. It cannot directly change Today or global order, restore hidden work, duplicate, permanently delete, or empty Bin. Existing domain validation and aggregate behaviour remain authoritative.

Use JSON Schema 2020-12 with immutable packaged version-1 schemas and a closed offline reference catalogue. Strict tokenisation rejects duplicate decoded members and the other lexical forms excluded by the contract before object materialisation. Input, output, validation, and preview budgets are published contract values. Version 1 generates and accepts only version 1; compatibility conversion begins only if a real later version explicitly retains version 1.

Identify a snapshot with `sha256:` plus SHA-256 of RFC 8785 JCS over the snapshot without its identity property. The digest binds content but does not establish provenance or authentication. Store a durable positive signed-64-bit Workspace revision that starts at `1` and advances exactly once with every transaction that actually changes user data. A recovery restore rebases restored data to a checked successor greater than both the pre-restore and recovered revisions, carrying that successor through migration when necessary, so an old proposal cannot become current again. Workspace revision does not reuse automatic-recovery generations or desktop autosave request revisions.

Read workspace state and revision atomically. Change-proposal Review checks both source revision and recomputed snapshot identity; New-work Review captures the current revision internally. Apply repeats the applicable checks through an operation-scoped SQLite connection that retains a database-level exclusive lock across validated recovery creation, the final source check, proposal transaction, and commit. If commit succeeds, later projection-refresh or recovery-scheduling failure is maintenance failure after a successful apply and must never replay the proposal.

Provide one desktop proposal text surface with three explicit input paths: typing or paste, **Load from file…**, and a single local file dropped onto the surface. Every path converges on the same bounded strict-UTF-8 text boundary. The file picker is the keyboard-accessible equivalent of drop. The application never monitors clipboard or filesystem state, follows a proposal-file link, watches or reloads a file, retains its path, or reads meeting material. Any attempted input change after Review invalidates the retained plan, effect model, and approval.

Assign module responsibilities without scaffolding an unused module:

- Core owns snapshot/proposal models, semantic validation, prospective execution plans, previews, and the atomic apply contract.
- SQLite owns revision persistence, atomic revision-bearing reads, checked revision advancement, migration, and recovery-backed exclusive application.
- A distinct interchange JSON adapter owns deterministic generation, strict parsing, packaged schemas, JCS identity, and bounded safe failures. It does not reuse the complete-workspace plaintext export contract.
- Desktop owns clipboard composition, text editing, proposal-file selection/drop, review, focus and accessibility, confirmation, and explicit approval.
- A future MCP adapter owns only authenticated transport and pending-proposal delivery. It cannot approve or call the mutation boundary directly.

## Consequences

- The first implementation slice needs a forward-only encrypted schema migration for Workspace revision and supported-upgrade tests from every retained schema.
- Completed active Tasks appear in the external planning snapshot so reopen and archive operations have a valid source record. Archived and Bin Project/Task content remains private to the local workspace.
- All reusable Category and Participant labels leave the application in a snapshot, including labels used only by hidden work; explicit copy review must disclose that tradeoff.
- Whole-proposal approval avoids rewriting operation dependencies but requires the user to reject and regenerate or edit a proposal when any operation is unwanted.
- Paste, picker, and drop improve deliberate desktop usability while adding one file-system attack surface that must use single-open, no-follow, cancellable bounded reads and equivalent accessible status.
- The pending plan and approval are session-local and single-use. A stale revision, changed input, lock, close, navigation, or restart requires a fresh Review.
- Cross-platform integration tests must prove SQLite lock retention across backup and commit, exact revision increments, rollback, hidden aggregate previews, bounded input, and no replay after post-commit maintenance failure.
- Future transports and schema versions must reuse or explicitly evolve this boundary. They cannot introduce transport-specific document shapes, a second mutation model, or autonomous approval.
- ADR 0006 remains the module-direction foundation, ADR 0008 recovery generations remain distinct from Workspace revision, and ADR 0009 governs the eventual forward-only revision migration.
