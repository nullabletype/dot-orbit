# Product requirements

Status: working specification derived from the interactive prototype.

## Navigation

- **NAV-001** The primary navigation order is Today, Backlog, Projects, Categories, Completed, Archive.
- **NAV-002** Today is the default view at application launch.
- **NAV-003** Navigation preserves unsaved inspector edits or explicitly prompts before discarding them.

## Projects and Tasks

- **PROJ-001** A Project has a title, Markdown description, Category, target date, manual position, and Tasks.
- **PROJ-002** Project status is derived from Task completion: Not started, In progress, or Complete.
- **PROJ-003** A Project becomes Complete when every Task is complete.
- **PROJ-004** A completed Project's completion date is the latest completion date among its Tasks.
- **TASK-001** A Task belongs to exactly one Project and can be completed independently.
- **TASK-002** A Task has a title, Markdown description, due date, Today membership, optional participants, and completion date.
- **TASK-003** Reopening a Task in a completed Project recalculates the Project to In progress and clears the derived Project completion date.

## Categories

- **CAT-001** A Project belongs to one Category.
- **CAT-002** A Task inherits its Project's Category by default.
- **CAT-003** A Task may override its inherited Category and may later return to inheritance.
- **CAT-004** Categories have a user-controlled manual order.
- **CAT-005** The Categories view groups all Projects beneath their Category in Category order.

## Ordering

- **ORD-001** Incomplete Tasks have one shared manual order used by Today and Backlog.
- **ORD-002** Today is a filtered projection of the shared order; dragging a Task in Today changes its relative position in Backlog.
- **ORD-003** Dragging a Task in Backlog changes its relative position in Today when that Task is a Today member.
- **ORD-004** Projects have a manual order independent of Task order.
- **ORD-005** Tasks inside each Project have a per-Project order independent of the Today/Backlog order.
- **ORD-006** Categories have a manual order independent of Project and Task order.
- **ORD-007** Reordering must be possible using a visible drag handle and an accessible non-pointer alternative.

## Views

- **VIEW-001 Today** shows incomplete Tasks explicitly selected for Today in shared Task order.
- **VIEW-002 Backlog** shows all incomplete Tasks in shared Task order.
- **VIEW-003 Projects** shows Projects in manual Project order, with expandable Tasks in per-Project order.
- **VIEW-004 Categories** shows all Projects grouped by manually ordered Category.
- **VIEW-005 Completed** groups recent completed Tasks by day for the last three days, then by week.
- **VIEW-006 Archive** provides full-text search across archived Projects and Tasks.

## Editing and Markdown

- **EDIT-001** Selecting a Project or Task opens its details in an inspector without leaving the current view.
- **EDIT-002** Editable fields can be changed directly in the inspector without opening a modal editor.
- **EDIT-003** Markdown descriptions show a live rendered preview.
- **EDIT-004** The rendered description can be copied as rich HTML with a plain-text fallback for Teams, Word, and similar applications.
- **EDIT-005** Participants are optional and do not create an assignee or ownership model.

## Desktop experience

- **DESK-001** The primary layout is designed for desktop widths and keyboard use.
- **DESK-002** Dark mode is the initial visual theme.
- **DESK-003** Core actions are available to keyboard and assistive technology users.
- **DESK-004** Supported releases run on serviced Windows 11 versions, current supported macOS versions covered by Avalonia, Ubuntu LTS, and current Debian stable.
- **DESK-005** X11 is the supported Linux display path for the first release. Native Wayland and other Linux distributions are best-effort until their upstream Avalonia support is suitable for a release commitment.

## Persistence and recovery

- **DATA-001** The application stores its working data in a local SQLite database encrypted with SQLite3MC using the sqleet ChaCha20-Poly1305 cipher scheme.
- **DATA-002** Encrypted storage and its backups are portable across supported macOS, Linux, and Windows installations.
- **DATA-003** The application generates encrypted backups automatically without creating a plaintext intermediate copy.
- **DATA-004** Backup creation and restoration do not replace a valid file until the candidate file has been completely written and validated.
- **DATA-005** A wrong password, damaged database, or tampered backup fails without replacing valid working data.
- **DATA-006** Secrets and user content are not written to application logs or diagnostic evidence.
- **DATA-007** After stored data changes, the application creates an automatic backup when needed, coalescing changes so no more than one routine backup is created per hour.
- **DATA-008** The application creates and validates a recovery backup before a schema migration, import, or restore changes working data.
- **DATA-009** The default retention policy keeps 24 hourly, 30 daily, and 12 monthly valid backups.
- **DATA-010** Retention pruning never deletes the last known-valid backup.
- **DATA-011** The automatic-backup destination is configurable and may be any writable filesystem directory, including one managed by an external cloud-sync tool.
- **DATA-012** A backup is completely written and validated before it is exposed at its configured destination as a restorable backup.
- **DATA-013** The first release requires the user's encryption passphrase whenever the application launches and does not persist that passphrase in files, settings, logs, or diagnostic evidence.
- **DATA-014** The encryption passphrase is retained only in process memory while the local data store is unlocked.
- **DATA-015** There is no password recovery mechanism or recovery backdoor; losing the passphrase means losing access to the working data and its backups.
- **DATA-016** Password rotation requires the current passphrase and produces a validated encrypted result before replacing working data or backups.

## Synchronisation

- **SYNC-001** The first release does not automatically synchronise or merge application data between devices.
- **SYNC-002** A cloud-synchronised backup directory is a recovery and portability mechanism, not live application-data synchronisation.
- **SYNC-003** Domain records use stable identifiers, and persistence remains behind an application boundary, so future synchronisation can be designed without replacing the domain model.

## Open requirements

- Archive entry and restoration rules.
- Recurring Tasks.
- Notifications and reminders.
- Export format beyond the encrypted portable backup.
- Passphrase creation and minimum-strength guidance.
- Optional biometric or operating-system credential-store unlock after the first release.
- Whether deleted records need a recoverable trash state.
- How an accessible keyboard-based reorder interaction should work.
