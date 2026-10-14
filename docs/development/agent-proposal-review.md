# Agent proposal review

The AI destination is the only place where dot-orbit accepts an agent proposal. Unlock a workspace, open **AI**, then provide exactly one proposal by typing into the editor, choosing **Paste**, choosing **Load file**, or dropping one local file on the editor.

All four routes use the same boundary. The proposal must be strictly valid UTF-8, contain no byte-order mark, and be no larger than 4 MiB. A rejected paste, file, or drop leaves the current editor text unchanged. File intake accepts one ordinary local file only; it does not follow symbolic links, accept directories or devices, monitor the file, retain the path, or contact a provider.

Choose **Review proposal** to validate the current editor contents. Validation runs once against the unlocked workspace's current revision. Invalid input shows bounded, non-sensitive guidance. A stale proposal must be regenerated from a fresh planning snapshot.

The review window is authoritative. It shows:

- the source workspace identity and revision;
- every proposed operation in execution order;
- every computed effect, including cascades;
- sanitised descriptions and inert Markdown explanations; and
- the powers that proposals cannot exercise.

Closing the review window, editing or replacing the proposal, locking or closing the workspace, or ending the session discards the reviewed plan. Returning to review therefore requires validation again.

Applying is an explicit whole-proposal action. Choose **Apply whole proposal**, then confirm in the decision overlay. dot-orbit first saves pending edits, creates and validates an encrypted recovery point, and then applies all operations atomically. It never offers partial approval.

The final result distinguishes an applied proposal from a pre-commit rejection or cancellation, a stale proposal, and a committed proposal whose post-commit maintenance needs attention. Only the last case offers **Retry maintenance**; retrying never replays the proposal.

Proposal text, task contents, database contents, recovery contents, and file paths are private. They must not appear in logs, diagnostics, CI output, screenshots, or GitHub artifacts.
