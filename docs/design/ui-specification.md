# UI specification

The historical `prototype/index.html` remains useful exploratory evidence. This document records the product-level UI decisions that survived the design conversation; [`style-guide.md`](style-guide.md) is the detailed authority for component anatomy, visual states, accessibility and Avalonia reuse.

## Recommended direction

**Workbench** is the current recommendation. It uses:

- a persistent navigation rail;
- a primary working canvas;
- a contextual inspector for direct editing; and
- a dense but calm desktop information hierarchy.

Canvas and Ledger remain useful comparison directions in the prototype. They are not implementation commitments.

## Visual language

- Near-black background with layered charcoal surfaces.
- Hot pink is the primary interactive accent.
- Green is reserved for completion, progress, and positive state hints.
- Category colours provide local identification without becoming global status colours.
- Rounded corners are restrained in Workbench, softer in Canvas, and nearly square in Ledger.
- Main copy should remain readable at normal desktop distance; metadata is visually quieter but not hidden.

## Navigation

1. Today
2. Upcoming
3. Backlog
4. Projects
5. Categories
6. Completed
7. Archive

Today is both the first item and the launch view.

Upcoming displays a badge counting incomplete, non-archived Tasks that are overdue or due through the end of the same weekday next week. The view contains Tasks only and groups Overdue first, then Today, Tomorrow, and subsequent dates. Overdue Tasks are oldest first; Tasks sharing a date retain shared Backlog order. Upcoming is not manually reorderable, but its rows support editing, completion, and Add to Today. Project target dates remain on Project surfaces. This accepted decision postdates the current HTML prototype and is not yet represented there.

Bin is a secondary utility item at the bottom of the sidebar, visually separated from the seven primary views. Its count appears only while non-empty. The Bin view orders removed items newest first and contains per-item Restore actions and the confirmed Empty Bin action.

## Ordering interactions

- A six-dot handle communicates draggable rows and sections.
- Today and Backlog expose the same Task order.
- Projects exposes an independent Project order.
- Expanded Project subtasks expose their own per-Project order.
- Category headings expose an independent Category order.
- Within each Category, Projects and standalone Tasks appear in separate sections. Projects retain global Project order and standalone Tasks retain shared Backlog order; neither section adds a category-local order.
- Drop position uses a pink insertion rule.
- Every reorderable item exposes Move up, Move down, Move to top, and Move to bottom through an accessible action menu scoped to the current list.
- Optional shortcuts such as Alt+Up and Alt+Down may accelerate reordering, but the action menu remains available and the new position is announced to assistive technology.

## List-row interaction contract

Current list surfaces compose the same semantic row states rather than defining surface-specific hover, completion, disclosure or reorder treatments. The authoritative row rules, component anatomy, semantic class names, state matrix and regression expectations are in the [desktop UI style guide](style-guide.md#rows-and-titles).

## Inspector

- Opens on row selection and keeps list context visible.
- Title, dates, Category, participants, and Markdown are edited as a local draft with explicit Save and Cancel actions.
- Navigating away from a dirty inspector prompts to save, discard, or stay. Immediate row actions remain independently committed; an action that would remove the drafted item from the current view first resolves the draft.
- New Project and Task actions open a transient inspector with Create and Cancel. The item does not appear elsewhere until Create succeeds.
- New Task defaults reflect its launch surface and remain visible and editable: a Project supplies attachment and inherited Category; a Category supplies a standalone Category; Today supplies Today membership and Planned while allowing optional Project selection; Backlog and global creation default to standalone and require a Category.
- Each Project includes an inline Quick add task field. Enter or Tab on a non-empty title creates an attached title-only Task and focuses a fresh field; Tab on an empty field exits normally, and Escape clears unsubmitted text. Created rows can be selected for full inspector editing.
- Backlog offers the same quick-add flow for standalone Tasks, with a required Category selector whose selection is retained only for the current rapid-entry session.
- Project status and completion date are read-only derived fields.
- An active, incomplete Project past its target date shows a warning icon with accessible Overdue text on its row. The Projects navigation item does not show an overdue badge.
- Task Category indicates inherited versus overridden state.
- Markdown source and rendered preview are visible together.
- **Copy rendered** writes `text/html` and `text/plain` clipboard formats.

## Project status presentation

- Not started: neutral dot.
- An empty Project is Not started and shows 0 of 0 Tasks with no completion date.
- In progress: amber dot with completed/total Task count.
- Complete: green status and the derived final completion date.

## Responsive boundary

The first release is desktop-first. The inspector may become an overlay at narrower desktop widths, but mobile navigation and mobile editing are separate design work rather than compressed desktop UI.
