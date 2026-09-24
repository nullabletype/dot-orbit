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

Today is a filtered projection of the shared Task order. Reordering its visible subset changes the relative positions of those Tasks in the shared order while preserving the positions of non-Today Tasks.

## Consequences

- There is no High/Medium/Low priority field in the current model.
- Persistence must store stable ordered identifiers for each scope.
- Moving a Task between Projects must update both Projects' subtask orders without losing its shared Task position.
- Removing a Task from Today does not change shared ordering.
- Every drag interaction needs a keyboard-accessible equivalent.
