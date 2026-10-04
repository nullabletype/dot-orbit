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

Today presents persistent incomplete membership in **Planned** and **In progress** lanes, followed by Tasks completed on the captured current local date. Its New Task action opens a draft already assigned to Planned and lets the user keep it standalone or select an optional Project before creation. Today rows retain the shared Task-row anatomy: reorder and completion lead, title and Category remain central, due-date metadata is right-aligned, lane action and position follow, and the star is the final trailing action. The outlined star adds to Planned and the filled star removes from Today, with matching toggle state and accessible action text. Lane movement is explicit and does not imply a durable Task status. Clear Today removes all incomplete membership after a deliberate action; completion clears membership, and reopen returns the Task to Backlog without restoring it. Lane-local drag and accessible reorder actions update the relative shared order of the visible lane Tasks while preserving every hidden Task position.

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
- Project and Task title, date, Category, Participant associations, and Markdown changes save automatically. Text changes use a 600 ms inactivity delay; discrete changes save immediately. A persistent status beside the inspector heading reports saving, saved, or failed and exposes Retry without requiring scrolling.
- Navigating away, closing, or starting an immediate row action flushes pending valid Project or Task changes first. Invalid or failed changes remain in the inspector and block leaving until corrected, retried, or explicitly discarded. Category editing retains explicit Save and Cancel actions.
- New Project and Task actions open a transient inspector without permanent Create or Cancel buttons. The first valid automatic save creates the item; blank or incomplete drafts can be left without adding a partial record.
- New Task defaults reflect its launch surface and remain visible and editable: a Project supplies attachment and inherited Category; a Category supplies a standalone Category; Today supplies Today membership and Planned while allowing optional Project selection; Backlog and global creation default to standalone and require a Category.
- Each Project includes an inline Quick add task field. Enter or Tab on a non-empty title creates an attached title-only Task and focuses a fresh field; Tab on an empty field exits normally, and Escape clears unsubmitted text. Created rows can be selected for full inspector editing.
- Backlog offers the same quick-add flow for standalone Tasks, with a required Category selector whose selection is retained only for the current rapid-entry session.
- Project status and completion date are read-only derived fields.
- An active, incomplete Project past its target date shows a warning icon with accessible Overdue text on its row. The Projects navigation item does not show an overdue badge.
- Task Category indicates inherited versus overridden state.
- The Participant picker ends with **New participant…**. Selecting it reveals and focuses a temporary label field; Enter or Add creates and associates a unique label, reuses an equivalent existing Participant, or reports that the Participant is already on the Task. Escape or Cancel closes the field and returns to the picker.
- Participant rename and delete do not appear in each Task inspector. A count-free **Settings** utility destination sits after Bin at the bottom of navigation. Its Participants section uses the shared list panel and row recipes, shows usage counts, and makes only the active rename row editable with Save/Cancel. Its Recovery section launches the existing recovery window; there is no permanent Recovery action in the top bar. Its Security section launches a focused passphrase-change window that requires the current passphrase, repeats creation guidance and the no-recovery warning, and keeps existing recovery points under the passphrase that created them. Successful rotation returns to Settings and shows a visible, polite live confirmation that the new passphrase will be used at the next unlock.
- Markdown descriptions open as a rendered preview with semantic block spacing and an accessible name that includes the rendered plain-text content. Activating the preview with pointer, keyboard, or an accessibility Invoke action switches it to the source editor; moving focus away or clicking anywhere outside the editor returns to the rendered view without saving the inspector draft or stealing focus from the next control. Tab and Shift+Tab change Markdown list indentation; indenting a numeric or lower-alpha ordered item converts it to an unordered sub-point, while unordered markers and plain text retain their type. Enter continues the current marker outside code blocks, and Control+Tab preserves ordinary focus traversal. Ordered, unordered, and mixed nesting stays visibly distinct through at least four levels.
- **Copy rendered** writes `text/html` and `text/plain` clipboard formats.
- Approved absolute web and email links in the preview are pointer- and keyboard-operable. Unsafe destinations and local fragments without a preview navigation target remain inert text.

## Project status presentation

- Not started: neutral dot.
- An empty Project is Not started and shows 0 of 0 Tasks with no completion date.
- In progress: amber dot with completed/total Task count.
- Complete: green status and the derived final completion date.

## Responsive boundary

The first release is desktop-first. The inspector may become an overlay at narrower desktop widths, but mobile navigation and mobile editing are separate design work rather than compressed desktop UI.
