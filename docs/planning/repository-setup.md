# Repository setup

The public repository is [`nullabletype/dot-orbit`](https://github.com/nullabletype/dot-orbit).

## Bootstrap

Clone the repository, then run commands from its root:

```sh
git clone https://github.com/nullabletype/dot-orbit.git
cd dot-orbit
```

## GitHub configuration

Create the labels defined in [`docs/agents/triage-labels.md`](../agents/triage-labels.md). Use GitHub Issues as the request surface and [GitHub Project 3](https://github.com/users/nullabletype/projects/3/views/1) for the delivery view.

Recommended project states:

- Inbox
- Needs information
- Ready
- In progress
- Review
- Done

External pull requests are contributions, not automatic requests for agent work.

The protected `main` merge boundary and its read-only drift audit are documented in [`docs/development/main-branch-protection.md`](../development/main-branch-protection.md). The ruleset is configured in GitHub Settings; the workflow resource and token limits are checked into the repository.

## Issue admission and automation

Use the implementation-slice issue form for proposed work. It starts at `needs-triage`; maintainers apply `blocked` or `ready-for-agent` only after checking the canonical readiness contract in [`docs/agents/issue-tracker.md`](../agents/issue-tracker.md). The issue-readiness workflow then maintains dependency state. Use its manual dispatch for a read-only preview before relying on new transition behaviour.
