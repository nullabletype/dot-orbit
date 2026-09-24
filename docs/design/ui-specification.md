# UI specification

The interactive source of truth is `prototype/index.html`. This document records the decisions that survived the design conversation; it is not a pixel-perfect replacement for the prototype.

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
2. Backlog
3. Projects
4. Categories
5. Completed
6. Archive

Today is both the first item and the launch view.

## Ordering interactions

- A six-dot handle communicates draggable rows and sections.
- Today and Backlog expose the same Task order.
- Projects exposes an independent Project order.
- Expanded Project subtasks expose their own per-Project order.
- Category headings expose an independent Category order.
- Drop position uses a pink insertion rule.
- The implementation must add a keyboard alternative before this becomes production UI.

## Inspector

- Opens on row selection and keeps list context visible.
- Title, dates, Category, participants, and Markdown are edited in place.
- Project status and completion date are read-only derived fields.
- Task Category indicates inherited versus overridden state.
- Markdown source and rendered preview are visible together.
- **Copy rendered** writes `text/html` and `text/plain` clipboard formats.

## Project status presentation

- Not started: neutral dot.
- In progress: amber dot with completed/total Task count.
- Complete: green status and the derived final completion date.

## Responsive boundary

The first release is desktop-first. The inspector may become an overlay at narrower desktop widths, but mobile navigation and mobile editing are separate design work rather than compressed desktop UI.
