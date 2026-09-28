# Triage labels

The proposed label mapping uses the standard names:

| Role | Label | Meaning |
| --- | --- | --- |
| Needs evaluation | `needs-triage` | A maintainer must evaluate scope and direction. |
| Waiting on reporter | `needs-info` | More information or reproduction detail is required. |
| Agent-ready | `ready-for-agent` | Fully specified and safe for an unattended agent to pick up. |
| Human-ready | `ready-for-human` | Requires human judgement or implementation. |
| Declined | `wontfix` | Will not be actioned. |

These labels describe workflow state, not type or severity. Add separate `bug`, `feature`, `docs`, and severity labels if useful.

`blocked` is a dependency-state label rather than a triage role. The authoritative readiness, dependency, and transition contract is in [`issue-tracker.md`](issue-tracker.md).

`implementation-slice` is an automation-scope label applied by the structured implementation-slice form. The readiness workflow ignores legacy and unrelated issues without it.
