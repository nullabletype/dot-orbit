# Agent interchange v1 contract

- Status: Accepted
- Date: 2026-10-12
- Issue: [#76 — Resolve the agent-interchange v1 contract and review boundary](https://github.com/nullabletype/dot-orbit/issues/76)

This contract resolves the product, domain, security, and transport questions that must be settled before the agent-interchange implementation slices can enter the automated readiness queue. It is informed by the accompanying [primary-source research](agent-interchange-v1-research.md) and the current application behaviour. It changes no runtime behaviour by itself.

## Boundary

Version 1 is a provider-neutral, human-reviewed planning exchange. dot-orbit produces one immutable planning snapshot. An external tool may return one structured change proposal for that snapshot. dot-orbit treats the proposal as untrusted data, validates it into one immutable execution plan, previews the complete local effect, and applies it only after the user approves the whole proposal.

dot-orbit does not read meeting material, call an AI provider, infer operations from prose, monitor the clipboard, approve changes, or expose an autonomous mutation route. A future MCP transport must reuse this boundary.

## Planning snapshot

### Included scope

The planning snapshot contains the current non-archived working set rather than only incomplete Tasks:

- every Category and Participant as a reusable reference catalogue, including records currently referenced only by archived or binned work;
- every Project that is neither archived nor in Bin; and
- every Task that is neither individually archived nor in Bin and is standalone or belongs to an included Project.

Included Tasks may be incomplete or complete. This is necessary because version 1 permits reopening and individually archiving completed Tasks. A Task whose parent Project is archived is excluded even when the Task is not individually archived.

The snapshot contains stable identifiers, titles and Markdown descriptions, Category relationships and inheritance mode, Participant associations, date-only target and due dates, completion state and captured completion date, Today membership and lane, and ordered identifier arrays for Category, Project, shared Task, and per-Project Task order. Project summaries include derived status, completion progress, and a count of excluded individually archived child Tasks so the filtered child list cannot be mistaken for the full status input.

Every snapshot array has one canonical order before JCS. Category, Project, shared Task, and per-Project Task arrays follow their domain-significant manual order. Category, Project, and Task record arrays follow those same respective global orders; every included Task appears exactly once in shared Task order. The unordered Participant catalogue and every set-valued Participant-association array sort by stable record ID using ordinal Unicode code-point order. Any future unordered set-valued array must define an equivalent stable-key sort in a new schema version rather than inherit database enumeration order.

Today state and the Category, Project, and shared Task orders are context only. A proposal cannot change them directly.

### Excluded scope

The snapshot contains no archived Project or Task content, Bin Project or Task content, recovery data, plaintext-export history, storage paths, or internal persistence positions. Reusable Category and Participant catalogue records are included independently of where they are referenced; the copy preview discloses that scope because a label may otherwise reveal hidden historical context. The snapshot is not the existing complete plaintext workspace export and cannot be used to restore a workspace.

### Identity and revision

Each snapshot declares:

- `format: "dot-orbit-planning-snapshot"`;
- `schemaVersion: 1` as a JSON integer;
- `workspaceRevision` as a canonical positive decimal string of at most 19 digits whose value is no greater than `9,223,372,036,854,775,807`; and
- `snapshotId` as `sha256:` followed by the lowercase hexadecimal SHA-256 digest of `JCS(snapshot-without-snapshotId)` under RFC 8785.

The snapshot ID is a content-integrity identifier, not authentication, provenance, or a secret. Because the revision is part of the hashed content, a workspace that changes and later returns to equivalent visible content still produces a different identity. Intake recomputes the current snapshot identity locally; an arbitrary caller-supplied identifier is never accepted as proof of a matching source snapshot.

The encrypted workspace stores one durable monotonic workspace revision. New and migrated workspaces begin at revision `1`. Each transaction that actually changes user data increments it exactly once in the same transaction, including changes to hidden, archived, or binned state. One atomic proposal increments it once regardless of operation count. A rejected, failed, rolled-back, or effective no-op mutation does not increment it. The storage boundary performs a checked increment and fails when the next value would exceed the maximum rather than wrapping.

Reads, snapshot generation, plaintext export, recovery creation or retention, passphrase rotation, schema migration, device-local preferences, and derived-index maintenance do not increment the workspace revision. Recovery restore is a user-data replacement, not recovery maintenance: before replacement it captures the current revision when present, then the restored database is transactionally rebased to `max(preRestoreRevision, restoredRevision) + 1` before a session can reopen. If the recovery predates the revision schema, the pending successor is retained across the closed historical restore and becomes that migration's initial revision; only a workspace with no revision on either side begins at `1`. Every addition is checked and restore fails closed before overflow. Automatic-recovery generation and desktop autosave request revisions are separate concepts and must not be reused.

Snapshot data and its revision are read in one transaction under the unlocked session's existing serialization boundary. Proposal intake compares both the source revision and recomputed snapshot identity.

Apply uses the existing maintenance pattern rather than the ordinary in-process session gate alone: an operation-scoped SQLite connection retains an `EXCLUSIVE` lock across the final source check, validated recovery creation, repeated source check, proposal transaction, and commit. The connection may roll back the no-op inspection transaction when SQLite backup requires it, but it retains the database-level exclusive lock before starting the mutation transaction. This excludes another process as well as another in-process operation and closes the preview-to-apply race.

## Proposal document

### Envelope and references

A proposal declares:

- `format: "dot-orbit-change-proposal"`;
- `schemaVersion: 1`;
- `sourceSnapshotId`;
- `sourceWorkspaceRevision`; and
- an ordered `operations` array.

`sourceSnapshotId` must match exactly the lowercase `sha256:` digest form defined for a snapshot. `sourceWorkspaceRevision` uses exactly the snapshot's canonical positive decimal-string representation and signed-64-bit bound; it is never a JSON number.

Every operation has a unique bounded `operationId` used only within that proposal. Every create operation has a unique bounded `localId`. References are discriminated objects containing exactly one `existingId` or `localId`; a local reference must point to an earlier create operation of the required record kind. Forward references and caller-selected durable record identifiers are prohibited. dot-orbit generates durable identifiers at apply time.

Array order defines proposal order and dependency order. There is no separate dependency language and no executable expression, URI, JSON Pointer, or remote reference supplied by a proposal.

### Version 1 operation vocabulary

Version 1 permits these typed intents:

- create a Category, with identity colour assigned by dot-orbit at the normal end position;
- create a Participant;
- create a Project with its complete editable content;
- edit a Project's title, Markdown description, or target date;
- set a Project's Category;
- create a standalone or attached Task with its complete editable content;
- edit a Task's title, Markdown description, or due date;
- set a Task's Category inheritance or override;
- replace a Task's Participant associations;
- attach, detach, or move a Task between Projects;
- complete or reopen a Task;
- place an attached Task first, last, before, or after another included Task in the same Project;
- archive an eligible Project or Task; and
- move a Project aggregate or Task to Bin.

Project and Category identity colours are not proposal fields. Category and Participant rename, delete, merge, colour change, and reorder are excluded because they are global reusable records whose changes can silently alter archived or binned context. Existing records are referenced by their stable record ID from the source snapshot; new records are explicit create operations. A duplicate normalised name or label rejects the proposal rather than silently reusing a hidden record.

Create operations require every editable field, using explicit `null` for absent optional dates and empty strings or arrays only where the domain permits them. Edit and set operations contain a non-empty `changes` object: an omitted property is unchanged, while explicit `null` clears only a field whose schema and domain permit absence. Participant association changes are full replacement sets, never ambiguous add/remove deltas, and duplicate references are invalid. Their order has no domain meaning; before preview the prospective model sorts typed references by discriminator (`existingId` before `localId`) and then by the referenced string in ordinal Unicode code-point order. Apply resolves local references to generated durable IDs, and later snapshots sort the resulting stable-ID set as defined above. New Tasks are not added to Today. A Task may be standalone when no genuine Project applies. Application-generated identifiers, identity colours, completion/archive instants, and captured local dates are never proposal input.

Attaching or moving a Task whose effective Category differs from the destination Project must explicitly choose `adoptProjectCategory` or `preserveAsOverride`. An omitted choice is invalid. A relative reorder supplies exactly one `first`, `last`, `before`, or `after` placement. An anchor must be an included Task in the same destination Project at that point in proposal order. Numeric persistence positions are never accepted.

Relative placement operates on the included-Task subsequence while individually archived Tasks remain fixed in their existing absolute Project-order slots. A newly created or attached included Task first contributes its normal new slot at the end; a detached Task removes its slot; the reordered included subsequence is then written back into the included slots. Thus `[A, archived X, B]` with `B before A` becomes `[B, archived X, A]`. The local preview shows the final full Project order, representing excluded Tasks as lifecycle-labelled local rows, so hidden interleaving is visible without adding hidden records to the external snapshot.

### Ordering, conflicts, and terminal actions

Validation simulates the ordered proposal against one prospective workspace state without persisting intermediate changes. Later operations observe valid earlier operations and may reference their created records.

For one target, at most one operation after creation may write each mutation facet: editable content, Category state, Participant associations, Project relationship, completion, Project-local order, and lifecycle. Creation establishes the record's initial facet values but does not consume those post-creation mutation allowances. A newly created record may therefore be edited, moved, reordered, or completed once in the same proposal; it may not be archived or moved to Bin in that proposal. Attach, detach, or move consumes both the relationship and Category facets because attachment sets Category inheritance or override and detachment materialises the effective Category as explicit state. This permits a coherent sequence such as create, edit, move, reorder, and complete while rejecting duplicate or last-write-wins edits to the same facet. Exact no-ops, duplicate operations, contradictory transitions, and operations whose declared outcome is already true are invalid.

Creation follows the normal domain order effects: a new Category or Project is appended to its global order; a new Task is inserted at the top of shared Task order and, when attached, at the end of its Project order. These unavoidable effects are shown in preview. A later Project-local reorder may reposition a newly created attached Task, but no proposal operation can reposition it in shared order.

Archive and Move to Bin are terminal for their target and must be its final operation. A proposal cannot both target a child and archive or bin its parent Project aggregate, because the parent action already owns the child's derived effects. A proposal cannot target an archived or binned record, restore one, permanently delete one, empty Bin, duplicate work, or perform a direct Today or global-order action.

Normal domain side effects still occur. Completing, archiving, or binning a Task may remove it from Today. Archiving or binning a Project removes child Today memberships. These are not independent Today operations and must be shown in the local preview.

## Intake and strict validation

Version 1 provides one dedicated proposal text surface with three equivalent, explicit ways to populate it: deliberate paste or typing, **Load from file…** through the platform file picker, and drag-and-drop of exactly one local regular file onto the surface. The picker remains the keyboard-accessible equivalent of drag-and-drop. Multiple items, directories, non-regular files, symbolic links or reparse points, and unreadable files are rejected. The file adapter opens the selected item exactly once without following links and in a mode that cannot wait for a pipe writer or device readiness (`O_NOFOLLOW | O_NONBLOCK` on Unix, or the platform equivalent), then classifies and size-checks that opened handle and performs one cancellable bounded read pass to end-of-file or the published limit plus one byte. Acquisition and reading run outside the UI thread under one cancellation and finite deadline; a late result is discarded. The adapter never classifies one path and reopens it for ingestion. dot-orbit never reads or monitors clipboard contents, files, or directories without that user action, and it never watches or automatically reloads a selected file.

Every proposed text mutation is measured as strict UTF-8 before it reaches the control. Typing is checked incrementally; a paste is assembled and checked before atomic insertion; and file picker or drop reads at most the published input-byte limit plus one byte into a temporary buffer before requiring strict UTF-8 and no byte-order mark. An over-limit or invalid paste or file intake preserves the prior text and focus. A successful file intake atomically replaces the surface contents, after which the user may inspect or edit them. Fixed non-sensitive success, cancellation, and rejection statuses are announced accessibly. The source path is not retained in the proposal or preview. Populating or editing the surface changes no workspace data; syntax, schema, and domain validation begin only when the user activates **Review**.

Input is strict UTF-8 RFC 8259/I-JSON data: one top-level object; no byte-order mark, comments, trailing commas, unpaired surrogates, fractional or exponent-form numbers, non-finite values, or duplicate decoded member names. Duplicate members are rejected during tokenisation before object materialisation or schema validation. Unknown properties and operation discriminators are rejected.

Version 1 uses JSON Schema 2020-12 and publishes exactly two application schema resources, with shared definitions embedded in their `$defs`:

- `https://raw.githubusercontent.com/nullabletype/dot-orbit/main/schemas/interchange/v1/planning-snapshot.schema.json`; and
- `https://raw.githubusercontent.com/nullabletype/dot-orbit/main/schemas/interchange/v1/change-proposal.schema.json`.

Those immutable schemas are checked in at the matching `schemas/interchange/v1/` paths and packaged with the application. Validation uses a closed in-memory catalogue containing only those schemas, the general 2020-12 meta-schema, and every vocabulary meta-schema it transitively references under its official identifier. Network and filesystem resolution are disabled in build tests and at runtime. The application never resolves HTTP, filesystem, proposal-supplied, or other remote references. Version 1 generates and accepts only version 1; compatibility conversion begins only when a real version 2 exists and explicitly retains version 1.

### Published limits

Proposal limits are part of the v1 contract and apply before recovery creation or mutation:

| Boundary | Version 1 limit |
| --- | ---: |
| Proposal input | 4 MiB UTF-8 |
| JSON nesting | 32 levels |
| Proposal operations | 512 |
| Aggregate JSON tokens | 100,000 |
| Operation/local/stable identifier | 128 Unicode scalar values |
| Title, Category name, or Participant label | 1,024 Unicode scalar values |
| Markdown description or explanation | 65,536 Unicode scalar values |
| Parsed CommonMark AST nodes in one proposed Markdown field | 4,096 |
| CommonMark container nesting in one proposed Markdown field | 32 levels |
| Parsed CommonMark AST nodes retained across one preview | 65,536 |
| Participant associations on one Task | 256 |
| Collected validation failures | 100, with at most 20 presented |
| Expanded preview effects | 2,000 |
| Retained expanded-preview display text | 4 MiB strict UTF-8 |

These field, association, token, and operation limits apply to untrusted proposal values, not to already valid stored values copied into a snapshot. Proposal edits may omit an existing out-of-limit field and leave it unchanged; replacing it must satisfy the proposal limit. Schema `maxLength`, `maxItems`, `maxProperties`, and closed object vocabularies enforce local structure. Streaming guards enforce bytes, depth, tokens, and duplicate members. Application validation enforces aggregate references, facets, effects, preview text bytes, CommonMark work, and safe error counts. Markdown limits count block and inline nodes in the sanitised CommonMark abstract syntax tree before constructing the render model; the aggregate preview count includes existing before-state and proposed after-state Markdown that the preview retains. The preview text count is the sum of each display string's strict UTF-8 byte count at every retained effect occurrence, including application-generated labels and repeated strings; it is checked before constructing UI elements. Fixed preview structure is separately bounded by the 2,000-effect limit. A limit failure rejects the complete proposal with fixed non-sensitive guidance; it never echoes imported content.

Snapshot and reusable-instruction generation has a separate 16 MiB strict-UTF-8 limit because clipboard and later transport output must remain bounded. The measured unit is the complete final payload for one copy or transport response after composition: snapshot-only JSON, instructions-and-schemas without a snapshot, or instructions-and-schemas plus a live snapshot. There is no separate quota that permits the composed payload to exceed 16 MiB. The producer never truncates or omits a valid stored value to fit. If the final payload exceeds the limit, generation fails before clipboard or transport output with fixed guidance that the workspace is too large for v1 interchange. Snapshot schema and tests must therefore represent current domain-valid values even when those values exceed proposal-input field limits.

## Review and approval

After syntactic, schema, reference, stale-source, and domain validation, dot-orbit builds one immutable current-model execution plan. The authoritative preview is derived only from that plan and is visually separated from optional agent explanations.

The preview shows every direct change and every locally derived effect, including:

- completion and archive transitions;
- Today memberships cleared by normal domain rules;
- Category inheritance changes, including counts of excluded children affected by a Project Category change;
- active and individually archived children moved by a Project-to-Bin aggregate;
- already-binned children that remain separately binned, become non-restorable while their parent is in Bin, and remain in Bin after a later parent restore;
- generated Category/Participant creation and record relationships; and
- final relative Project Task ordering.

Archived and Bin content remains local and is not copied back into the proposal or external snapshot. If a hidden effect cannot be described completely within the preview limit, validation rejects the proposal rather than presenting an incomplete preview.

Approval is all-or-nothing. The user cannot select individual operations, because removing an operation would rewrite dependencies and create a different proposal. To change the set, the user rejects it and supplies a new proposal.

The pending review retains the immutable execution plan, complete preview model, source snapshot identity and revision, and `SHA-256(JCS(validated proposal))` identity in memory. The digest is an internal integrity guard derived from sensitive content: it is not authentication and is never logged or exposed as diagnostic evidence. Navigating away, closing the workspace, locking, or restarting discards the pending plan, preview, and approval. After Review, any attempted content-changing interaction—typing, paste, file load, or drop, whether accepted or rejected—first destroys the pending plan, preview, and approval and disables Apply. A rejected mutation or replacement may leave the visible text unchanged, but it cannot leave the old plan actionable. Focus remains on or returns to the intake surface and invalidation is announced accessibly.

Apply never reacquires or reparses the original text or reloads a source file. It flushes or blocks pending inspector changes under the existing editing contract, enters the retained database-level exclusive boundary described above, rechecks source identity and revision, creates and validates the required encrypted recovery point, rechecks again, and applies the retained plan in one transaction. Any stale state, changed preview, recovery failure, cancellation, domain failure, or transaction failure before commit applies nothing.

Commit is the mutation success boundary. Once the transaction commits, the proposal is applied exactly once and the workspace revision has advanced. A later projection refresh or automatic-recovery scheduling failure is reported as committed-with-refresh-or-maintenance-failure; retry refreshes or reconciles only and never replays the proposal. Explicit approval is single-use on both committed and non-committed outcomes.

## Representative contract exercises

| Exercise | Version 1 outcome |
| --- | --- |
| Create a Category, then create a standalone Task that references its local ID | Valid when the create appears first and the normalised Category name is unique; both durable IDs are application-generated. |
| Edit one Task title twice, or move it twice | Invalid because the second operation writes an already-consumed content or relationship facet. |
| Review a proposal, then commit an unrelated hidden-state mutation before Apply | Invalid at Apply because the Workspace revision and recomputed source identity are rechecked under the exclusive boundary; nothing from the proposal applies. |
| Attach a Task whose effective Category differs from the destination Project | Invalid without an explicit `adoptProjectCategory` or `preserveAsOverride`; valid with exactly one choice, which consumes Category and relationship facets. |
| Move an attached Task and place it after another Task in the destination Project | Valid when the move precedes the reorder and the anchor is included in that destination at that point in proposal order. |
| Archive an active Project with incomplete, completed, and Today Tasks | Valid as the Project's terminal operation; local preview shows every Today removal and derived visibility effect without exporting archived child content. |
| Move a Project to Bin when it has active, individually archived, and already-binned children | Valid as one aggregate terminal action only when no child is separately targeted; preview shows moved children and the restore constraint on already-binned children. |
| Supply duplicate JSON members, depth 33, 513 operations, or more than 4 MiB | Invalid before planning; the whole proposal is rejected with bounded non-sensitive guidance. |
| Supply schema version `0`, `2`, or an unknown format | Invalid without conversion or mutation; version 1 accepts only version 1. |

## Untrusted content and diagnostics

Descriptions and optional explanations are untrusted Markdown. They use the existing sanitised render model with raw HTML disabled, no automatic remote images, safe link handling, and plain-text fallback. Explanations are inert review metadata: they are never operations, defaults, validation inputs, or Task content unless an explicit typed field separately contains that content.

Snapshot and proposal content, explanations, identifiers, digests, paths, validation excerpts, database values, and clipboard contents never enter logs, exception text, telemetry, CI output, screenshots, or GitHub artifacts. Fixed diagnostics may contain document kind, schema version, byte count, operation index, a known-field JSON Pointer, fixed error code, bounded counts, and phase/result metadata only.

## Compatibility and follow-up boundaries

The published v1 schemas and semantics are immutable. A semantic or breaking structural change creates a new integer version. Unknown, newer, and unsupported older versions fail closed without mutation and direct the user to regenerate or update the proposal. A future version may accept and convert one real preceding version for a stated application-release window, but v1 has no fictional v0 converter.

The implementation remains split across the existing architecture:

- Core owns the planning snapshot model, proposal model, semantic validation, prospective plan, preview model, and atomic apply contract.
- SQLite owns atomic revision-bearing reads, forward-only migration, revision advancement, recovery-backed transaction application, and storage mapping.
- A JSON adapter owns strict parsing, immutable schemas, deterministic generation, JCS identity, and safe bounded failures.
- Desktop owns explicit copy, paste, file-picker, drag-and-drop, review, focus, accessibility, confirmation, and composition interactions.
- Future MCP owns transport and authentication only and cannot approve or call mutation below the same review boundary.

No interchange production module is scaffolded until the first implementation slice exercises a substantial boundary.

## Owner decision recorded

Version 1 uses the shared proposal text surface with all three explicit intake paths: paste or typing, **Load from file…**, and single-file drag-and-drop. All paths converge before parsing on the same bounded strict-UTF-8 intake and require the same **Review** and explicit whole-proposal **Apply** flow.
