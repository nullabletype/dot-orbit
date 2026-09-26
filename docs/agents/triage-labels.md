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

`blocked` is a dependency-state label rather than a triage role. It marks a fully specified issue that cannot start until every issue listed under `Blocked by` is complete. Once those dependencies are complete, remove `blocked` and apply `ready-for-agent`.
