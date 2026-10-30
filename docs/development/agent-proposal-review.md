# Agent proposal review

The AI destination is the only place where dot-orbit accepts an agent proposal. Unlock a workspace, open **AI**, then provide exactly one proposal by typing into the editor, choosing **Paste**, choosing **Load from file…**, or dropping one local file on the editor. Paste, file load, and drop replace the complete editor contents so the proposal being reviewed is always visible.

A **New-work proposal** can create new Categories, Participants, Projects, and Tasks without a Planning snapshot. It cannot reference, update, or connect to existing work. A **Change proposal** can do those things only when it is tied to workspace context previously copied from dot-orbit.

Version 2 can also place a new Task in Today, or add, move, or remove an existing Task between Planned, In progress, and outside Today. Only active incomplete Tasks can remain in Today. These changes appear explicitly in Review and preserve shared Task order. Version 1 proposals remain supported with their original restrictions.

All four routes use the same boundary. The proposal must be strictly valid UTF-8, contain no byte-order mark, and be no larger than 4 MiB. A rejected paste, file, or drop leaves the current editor text unchanged. File intake accepts one ordinary local file only; it does not follow symbolic links, accept directories or devices, monitor the file, retain the path, or contact a provider.

Choose **Review** to validate the current editor contents. Validation runs once against the unlocked workspace's current revision. Invalid input shows bounded, non-sensitive guidance. A stale New-work proposal can be reviewed again; a stale Change proposal must be regenerated from fresh workspace context.

The review window is the human approval surface. It shows:

- the proposal type and a human-readable creation or change summary;
- only the records the proposal directly creates or changes, with readable target names and sanitised descriptions;
- new Projects with their directly created Tasks nested beneath them, new Tasks under existing Project context, standalone Tasks separately, and supporting Categories and Participants in their own section; a mixed workspace-bound proposal separates all of that new work from existing-item changes;
- the shared Project and Category colours and Task relationship pills that will identify the resulting work after application;
- direct version 2 Today additions, lane moves, and removals with current and proposed state in text;
- a short note when routine ordering, inheritance, lifecycle-driven Today removal, aggregate, or derived-status consequences are implied rather than expanded item by item; and
- under **Technical details**, the source identity and revision where applicable, the validation counts, and the powers proposals cannot exercise.

The retained plan still validates and bounds all direct and derived effects before this view opens. Grouping uses the stable local or existing reference, never the display title, so duplicate names remain distinct. Multiple direct changes to one existing record appear together in operation order. Optional agent explanations remain inert metadata and are not expanded into additional change rows.

Closing the review window, editing or replacing the proposal, locking or closing the workspace, or ending the session discards the reviewed plan. Returning to review therefore requires validation again.

Applying is an explicit whole-proposal action. Choose **Add new work** or **Apply changes** in the review window; that action is the approval and does not open another confirmation. dot-orbit first saves pending edits, creates and validates an encrypted recovery point, and then applies all operations atomically. It never offers partial approval.

The final result distinguishes an applied proposal from a pre-commit rejection or cancellation, a stale proposal, and a committed proposal whose post-commit maintenance needs attention. Only the last case offers **Retry maintenance**; retrying never replays the proposal.

Proposal text, task contents, database contents, recovery contents, and file paths are private. They must not appear in logs, diagnostics, CI output, screenshots, or GitHub artifacts.
