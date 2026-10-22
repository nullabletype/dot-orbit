# Context-free New-work proposals

- Status: Accepted
- Date: 2026-10-14

## Context

ADR 0011 required every agent proposal to be tied to a copied Planning snapshot. That is appropriate when an agent edits or connects existing work, but it makes a common capture flow unnecessarily dependent on disclosure: a user may give an external agent a transcript or notes and ask it to create entirely new work without first sending any dot-orbit data.

Removing the source fields from the existing Change proposal would weaken its stale-source contract and make one wire format carry two different authority models. Allowing names to silently resolve to existing records would also make duplicate titles and hidden catalogue state unsafe.

## Decision

Add a sibling version-1 `dot-orbit-new-work-proposal` document with its own immutable JSON schema. It contains no snapshot identity or Workspace revision and permits only Category, Participant, Project, and Task creation. Every relationship is a backward proposal-local reference; durable existing identifiers and all non-create operations are invalid in both schema and Core validation.

Review reads the current workspace once, validates the complete prospective graph, and retains an immutable execution plan bound to the current Workspace revision. Apply repeats the revision check under the same exclusive, validated-recovery, atomic, single-use boundary as a Change proposal. Fresh durable identities are generated only during Apply.

The existing `dot-orbit-change-proposal` remains snapshot-bound and retains both revision and snapshot-identity checks. Updating or connecting existing work therefore still requires explicitly shared workspace context.

The review UI identifies which proposal type was supplied. Human outcomes lead the review; source identity and revision where applicable, validation counts, and safety limits remain available under technical details. Local and operation identifiers remain internal because approval is whole-proposal rather than per-operation.

## Consequences

- Transcript-to-new-work capture can be configured once without disclosing a workspace.
- New-work cannot place a Task in an existing Category or Project; that is intentionally a Change proposal in version 1.
- A workspace mutation after Review invalidates a New-work plan even though the imported document did not contain a source revision.
- The schema catalogue, reusable instructions, examples, requirements, and tests now publish three version-1 document schemas.
- ADR 0011 continues to govern Planning snapshots, Change proposals, whole-proposal approval, complete internal effect modelling, recovery, and transport authority; this ADR supersedes only its claim that every proposal must carry snapshot identity.
