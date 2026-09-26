# Issue tracker

Status: active.

- Tracker: [GitHub Issues](https://github.com/nullabletype/dot-orbit/issues)
- Planning surface: [dot-orbit GitHub Project](https://github.com/users/nullabletype/projects/3/views/1)
- External pull requests as a request surface: no
- Delivery unit: one bounded issue per branch or worktree

Issues whose specifications are complete but whose listed dependencies remain open use the `blocked` label. Replace `blocked` with `ready-for-agent` only after every blocking issue is complete; agents must not start dependency-blocked work merely because it is present in the project Todo column.

Feature requests should start as issues. Collaborator pull requests may implement an existing issue, but external pull requests are not automatically pulled into the feature-triage queue.

External feature or behavioural pull requests should implement an accepted, linked issue. Small documentation corrections and clearly isolated bug fixes may be opened directly. Internal APIs have no compatibility guarantee before the first stable release. Security reports follow `SECURITY.md` and must not be filed publicly.
