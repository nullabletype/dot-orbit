# dot-orbit domain context

This document defines the language and invariants agents should use. Update it when a domain decision changes; do not use implementation terminology as a substitute for a domain concept.

## Terms

### Project

A container for a purposeful piece of work. A Project has a title, Markdown description, category, optional target date, manually controlled position, and zero or more Tasks.

### Task

An independently completable item that is standalone or belongs to one Project. A Task has its own title, Markdown description, optional due date, completion state, participants, Today membership, category behaviour, and ordering positions.

### Category

A user-managed grouping for Projects and standalone Tasks. A Project Task inherits its Project's Category unless explicitly overridden; a standalone Task has an explicit Category. Categories have their own manual order and one required named colour for local visual identification. The name remains the canonical Category identity; colour does not affect behaviour and need not be unique.

### Today

An explicit working selection plus a daily completion projection. Incomplete membership persists across date changes and application restarts until the Task is removed, completed, or archived. Today can organise selected incomplete Tasks into working lanes without changing their durable completion state, and separately shows every Task completed on the current local calendar day.

### Backlog

All incomplete Tasks in one shared manual task order. Today shows a subset of this same order.

### Upcoming

A date-based projection of incomplete, non-archived Tasks that are overdue or due from the current local calendar day through the end of the same weekday next week. It does not contain Projects. Upcoming does not change Today membership or shared Task order.

### Due date

An optional date-only calendar value for a Task. It has no time of day or time zone and becomes overdue only when it is earlier than the current local calendar date. A Project target date has the same date-only semantics.

### Today lane

A persisted Today-only placement for an incomplete Today Task: Planned or In progress. It is not a Task completion state and does not affect Project status or Backlog ordering.

### Participant

A reusable local label associated with a Task for context, typically initials or a nickname. Its label is unique after whitespace, Unicode compatibility, and case normalisation. Participants contain no contact fields, are not assignees, and do not imply identity, ownership, permissions, or workflow responsibility.

### Completion status

A derived Project state:

- **Not started** — the Project has no Tasks, or none of its Tasks are complete.
- **In progress** — some, but not all, subtasks are complete.
- **Complete** — every subtask is complete.

### Completion date

For a Task, the local calendar date captured alongside its latest completion timestamp when completion occurs. For a Project, the latest captured completion date among its Tasks once every Task is complete. A later time-zone change does not move completed work between calendar days. Reopening a Task clears its completion timestamp and captured date; completing it again records new values rather than retaining a completion audit trail.

### Archive

A visibility state and searchable historical projection. Archiving captures both an instant and the user's local calendar date so the projection can retain stable daily and weekly groups after a later time-zone change. An individual Task must be complete before it can be archived; it remains part of its Project and still contributes to derived Project status, but is excluded from Backlog and Completed. A Project can be archived explicitly in any completion state without changing that state or its Tasks.

### Bin

A recoverable soft-deletion state for work the user intends to remove. Bin is distinct from Archive, records when an item was removed, is excluded from normal and archived-work projections, and is permanently cleared only through an explicit user action.

## Invariants

1. Every Task is standalone or belongs to one Project, never more than one.
2. A Task can be completed independently of its Project.
3. Project status is derived; it is never edited directly.
4. A Project becomes Complete only when it has at least one Task and all its Tasks are complete.
5. A completed Project's completion date equals its last Task completion date.
6. A Project Task category is inherited or explicitly overridden; a standalone Task category is explicit.
7. Today membership is explicit and independent of due date.
8. Today and Backlog share one manual Task order.
9. Project order, Category order, and per-Project subtask order are independent of the shared Task order.
10. Archiving a Task does not remove it from its Project or change its completion state.
11. Bulk archiving never archives a Project.
12. Archiving an incomplete Project does not complete it or any of its Tasks.
13. Restoring archived work never adds a Task to Today automatically.
14. Due dates and calendar rollover never add or remove Today membership.
15. Today lane placement exists only while an incomplete Task belongs to Today.
16. The completed-today projection is based on completion timestamp, not prior Today membership.
17. Changing a Today lane never changes shared Task order.
18. Reordering within a Today lane changes the relative shared order of the visible lane Tasks while preserving other Tasks' positions.
19. A Participant is referenced by identity rather than copied as Task free text; renaming its unique label updates every Task that references it.
20. Binned work is never treated as archived history.
21. A binned Task does not contribute to its Project's derived status or completion date.
22. Moving a Project to Bin moves its Tasks with it as one aggregate.
23. Restoring from Bin recovers the item's prior domain, visibility, Today, and ordering state where that state can still be represented.
24. A standalone Task participates in shared Task order but has no per-Project order.
25. Detaching a Task materialises its effective Category as an explicit Category.
26. Attaching a standalone Task never changes its Category silently: it inherits when Categories match, otherwise the operation explicitly chooses inheritance or an override.
27. An empty Project is Not started, has no completion date, and is never Complete.
28. Every Project and standalone Task has a Category, and at least one Category always exists.
29. A referenced Category cannot be deleted without atomically reassigning every reference.
30. Within a Category, Projects retain global Project order and standalone Tasks retain shared Task order; the Category view introduces no additional ordering scope.
31. A completed Task has one latest completion timestamp and the local calendar date captured with it; reopening clears both and later recompletion records new values.
32. Changing time zone does not recalculate a Task's captured completion date or move it between historical day groups.
33. Task due dates and Project target dates are date-only values; they do not imply a time of day, time zone, reminder, or notification.
34. An active, incomplete Project whose target date is before today is overdue; this is presented on the Project itself and is not counted in the sidebar.
35. Overdue state never changes the user-controlled Project order.
36. Valid Project and Task inspector edits persist automatically; navigation, closing, and immediate workflow actions flush pending changes before proceeding, while invalid or failed changes remain available for correction, retry, or explicit discard. Category drafts retain explicit confirmation.
37. A new Project or Task does not exist in the domain until its transient draft first becomes valid and is persisted automatically.
38. New Task drafts use visible, editable defaults from their launch context: Project, Category, Today, Backlog, or global creation.
39. A newly created Task starts at the top of shared Task order and, when attached, at the end of its Project order; new Projects and Categories start at the end of their respective orders.
40. Quick-add is an explicit immediate action in Projects and Backlog that creates a title-only Task and returns focus to a fresh entry field for rapid sequential capture.
41. Backlog quick-add creates standalone Tasks using an explicitly selected Category that remains selected only for the current rapid-entry session.
42. A Category's persisted colour is identity metadata only; renaming or reordering the Category never recalculates it, and changing it never rewrites Project or Task references.
