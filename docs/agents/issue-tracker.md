# Issue tracker

Status: active.

- Tracker: [GitHub Issues](https://github.com/nullabletype/dot-orbit/issues)
- Planning surface: [dot-orbit GitHub Project](https://github.com/users/nullabletype/projects/3/views/1)
- External pull requests as a request surface: no
- Delivery unit: one bounded issue per branch or worktree

## Readiness contract

New implementation-slice issues begin at `needs-triage`. The issue form emits a `Readiness contract` section whose selected version distinguishes new structured issues from legacy tracker records. A maintainer admits a supported-contract issue to automatic dependency management by replacing `needs-triage` with either `blocked` or `ready-for-agent`. Legacy issues without that section and issues carrying any manual triage label remain under manual control.

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
