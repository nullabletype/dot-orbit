# Issue tracker

Status: active.

- Tracker: [GitHub Issues](https://github.com/nullabletype/dot-orbit/issues)
- Planning surface: [dot-orbit GitHub Project](https://github.com/users/nullabletype/projects/3/views/1)
- External pull requests as a request surface: no
- Delivery unit: one bounded issue per dedicated branch; concurrent writers also use separate worktrees or clones

## Readiness contract

New implementation-slice issues begin with the `implementation-slice` scope label and the manual `needs-triage` state. The issue form also emits a versioned `Readiness contract` section. A maintainer admits a supported-contract issue to automatic dependency management by replacing `needs-triage` with either `blocked` or `ready-for-agent`. Legacy issues without the scope label and issues carrying any manual triage label remain under manual control. Once admitted, removing or corrupting a mandated body section returns the issue to `needs-triage`; deleting body text cannot bypass validation.

A managed issue is fully specified only when each of these exact Markdown sections appears once and contains a nonblank value:

- `Readiness contract` (exactly `dot-orbit-issue-readiness:v1`)
- `User-visible outcome`
- `Acceptance criteria`
- `Non-goals`
- `Domain and decision context`
- `Known constraints`
- `Verification`
- `Open decisions`
- `Blocked by`

`Open decisions` must be `None`. `Blocked by` is the only machine-readable dependency source: it must be `None` or contain one local issue reference per line in the form `- #123`. Issue references in any other section have no dependency meaning. Missing issues, pull-request references, self-references, malformed references, and direct or transitive cycles fail closed by returning the managed issue to `needs-triage`.

The issue-readiness workflow reevaluates managed open issues after relevant issue changes. A valid issue with any open dependency is `blocked`; when every dependency is closed it becomes `ready-for-agent`; reopening a dependency restores `blocked`. Transitions add the replacement label before removing the previous managed label, preserving the issue body and unrelated labels. Repeated evaluation is a no-op.

Use the workflow's manual dispatch to preview deterministic issue-number, reason, and label deltas with read-only permissions. The issue-event job has `issues: write` only for targeted label operations. Neither path interpolates or executes issue content.

Feature requests should start as issues. Collaborator pull requests may implement an existing issue, but external pull requests are not automatically pulled into the feature-triage queue.

External feature or behavioural pull requests should implement an accepted, linked issue. Small documentation corrections and clearly isolated bug fixes may be opened directly. Internal APIs have no compatibility guarantee before the first stable release. Security reports follow `SECURITY.md` and must not be filed publicly.

## Ownership for concurrent work

Readiness and ownership are separate. `ready-for-agent` describes specification and dependency readiness; the readiness workflow does not claim issues or prevent duplicate implementation. Keep its managed labels under the existing readiness contract.

For the initial two-chat workflow, the human assigns a different issue to each implementing chat in its launch prompt. Before editing, read the issue, its comments, and linked open pull requests. Record an active claim in an issue comment with the implementing chat's display name or identifier, branch, non-sensitive checkout alias, and base commit SHA. Keep the absolute local directory in the private chat or local handoff, not a public GitHub comment. A GitHub account assignee alone cannot distinguish two chats using the same account.

A claim comment is advisory, not an atomic lock. Re-read the issue after posting. If another active claim or implementation PR exists, stop implementation and return the ownership conflict to the human; do not pick a winner by timestamp or remove the other claim. Explicit human reassignment must identify the prior owner and preserve its branch, changes, and handoff. A new chat resuming an existing implementation continues that branch and updates its ownership record instead of starting a duplicate implementation.

Update the claim when paused, handed off, opened for review, or finished. Link the PR and resumable handoff where applicable. A paused claim remains reserved until explicitly released or reassigned; a review claim remains reserved until the PR is merged or closed and the work is released. Label changes and agent inactivity are not releases.

For directory setup, integration, and cleanup, follow [`parallel-work.md`](../development/parallel-work.md).
