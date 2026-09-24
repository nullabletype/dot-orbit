# dot-orbit domain context

This document defines the language and invariants agents should use. Update it when a domain decision changes; do not use implementation terminology as a substitute for a domain concept.

## Terms

### Project

A container for a purposeful piece of work. A Project has a title, Markdown description, category, target date, manually controlled position, and one or more Tasks.

### Task

An independently completable item belonging to exactly one Project. A Task has its own title, Markdown description, due date, completion state, participants, Today membership, category behaviour, and ordering positions.

### Category

A user-managed grouping for Projects. Tasks inherit their Project's Category unless explicitly overridden. Categories have their own manual order.

### Today

An explicit membership flag on a Task, not a calculation based on its due date. Today is a filtered projection of the task backlog.

### Backlog

All incomplete Tasks in one shared manual task order. Today shows a subset of this same order.

### Participant

A person associated with a Task for context. Participants are not assignees and do not imply ownership or workflow responsibility.

### Completion status

A derived Project state:

- **Not started** — no subtasks are complete.
- **In progress** — some, but not all, subtasks are complete.
- **Complete** — every subtask is complete.

### Completion date

For a Task, the date it was completed. For a Project, the latest completion date among its Tasks once every Task is complete.

### Archive

A searchable historical collection of closed or deliberately archived work. The exact transition into the Archive remains an open product decision.

## Invariants

1. Every Task belongs to one Project.
2. A Task can be completed independently of its Project.
3. Project status is derived; it is never edited directly.
4. A Project becomes Complete only when all its Tasks are complete.
5. A completed Project's completion date equals its last Task completion date.
6. A Task category is either inherited from its Project or explicitly overridden.
7. Today membership is explicit and independent of due date.
8. Today and Backlog share one manual Task order.
9. Project order, Category order, and per-Project subtask order are independent of the shared Task order.
