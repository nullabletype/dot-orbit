# ADR 0001: Independent manual ordering scopes

- Status: Accepted for the product specification
- Date: 2026-09-24

## Context

A single priority field did not express how the user thinks about work. The desired order depends on context: daily execution, overall Project chronology, Project-local sequencing, and Category presentation.

## Decision

dot-orbit has four manual ordering scopes:

1. One shared incomplete-Task order used by Today and Backlog.
2. One Project order used by the Projects view.
3. One subtask order per Project.
4. One Category order used by the Categories view.

Today is a filtered and grouped projection of the shared Task order. Its incomplete Tasks appear in persisted Planned and In progress lanes. Changing a Task's lane does not change shared order. Reordering the visible subset within one lane changes the relative positions of those Tasks in the shared order while preserving the positions of Tasks outside that lane.

## Consequences

- There is no High/Medium/Low priority field in the current model.
- Persistence must store stable ordered identifiers for each scope.
- Attaching, detaching, or moving a Task between Projects must update affected per-Project orders without losing its shared Task position.
- Removing a Task from Today does not change shared ordering.
- Moving a Task between Today lanes does not change shared ordering.
- Every drag interaction needs a keyboard-accessible equivalent.
