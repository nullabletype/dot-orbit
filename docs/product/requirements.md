# Product requirements

Status: working specification derived from the interactive prototype.

## Navigation

- **NAV-001** The primary navigation order is Today, Upcoming, Backlog, Projects, Categories, Completed, Archive.
- **NAV-002** Today is the default view at application launch.
- **NAV-003** Navigating away from an inspector with unsaved field edits prompts the user to save, discard, or remain on the current item.

## Projects and Tasks

- **PROJ-001** A Project has a title, Markdown description, Category, optional target date, manual position, and Tasks.
- **PROJ-002** Project status is derived from Task completion: Not started, In progress, or Complete.
- **PROJ-003** A Project becomes Complete when it has at least one Task and every Task is complete.
- **PROJ-004** A completed Project's completion date is the latest completion date among its Tasks.
- **PROJ-005** A Project may contain zero Tasks; an empty Project is Not started, reports 0 of 0 progress, and has no completion date.
- **PROJ-006** An active, incomplete Project is overdue when its target date is earlier than the current local calendar date.
- **PROJ-007** An overdue Project displays a warning icon and accessible Overdue text on its Project row; the Projects navigation item has no overdue count or warning badge.
- **PROJ-008** Becoming overdue never reorders a Project or otherwise changes its manual position.
- **TASK-001** A Task is standalone or belongs to one Project and can be completed independently.
- **TASK-002** A Task has a title, Markdown description, optional due date, Today membership, optional participants, and completion date.
- **TASK-003** Reopening a Task in a completed Project recalculates the Project to In progress and clears the derived Project completion date.
- **TASK-004** A Task's durable workflow state is incomplete or complete; Today-only organisation does not add another durable Task status.
- **TASK-005** Completing a Task records the completion instant and the local calendar date at which completion occurs.
- **TASK-006** Reopening a Task clears its completion instant and captured local date; completing it again records new values, and the first release retains no completion/reopen audit trail.
- **TASK-007** A later time-zone change does not recalculate the captured completion date or move a Task between historical calendar-day groups.
- **TASK-008** Task due dates and Project target dates are date-only calendar values with no time of day or time zone.

## Editing

- **EDIT-001** Inspector changes to titles, descriptions, dates, Categories, and Participants remain a local draft until the user explicitly chooses Save.
- **EDIT-002** Cancel discards the current inspector draft and restores the last saved values.
- **EDIT-003** Immediate workflow actions such as completion, Today membership, lane movement, archive, Bin, restore, and reorder are committed independently and never implicitly save or discard an inspector draft.
- **EDIT-004** If an immediate action would make the drafted item unavailable in its current view, the application first requires the draft to be saved or discarded.
- **EDIT-005** Creating a Project or Task opens a transient inspector draft that becomes a persisted item only after the user explicitly chooses Create.
- **EDIT-006** Cancelling a creation draft leaves no partial item in lists, counts, backups, search, or other projections.
- **EDIT-007** A Task draft opened from a Project defaults to that Project and inherited Category.
- **EDIT-008** A Task draft opened from a Category defaults to a standalone Task with that Category.
- **EDIT-009** A Task draft opened from Today defaults to Today membership in Planned and allows an optional Project to be selected before creation.
- **EDIT-010** A Task draft opened from Backlog or the global New Task action defaults to standalone and requires an explicit Category.
- **EDIT-011** Every contextual default is visible and editable before Create; defaults never silently constrain the resulting Task.
- **EDIT-012** Each Project provides an inline Quick add task field for immediate title-only Task creation.
- **EDIT-013** Enter or Tab with a non-empty quick-add title creates the Task, attaches it to the Project with inherited Category, and focuses a fresh empty quick-add field.
- **EDIT-014** Tab on an empty quick-add field follows normal focus navigation, and Escape clears unsubmitted text, so rapid entry never traps keyboard focus.
- **EDIT-015** A quick-added Task has no description, due date, Participants, or Today membership until edited, and selecting it opens the full inspector.
- **EDIT-016** Backlog provides a Quick add task field with an explicit Category selector for immediate title-only standalone Task creation.
- **EDIT-017** The selected Backlog quick-add Category remains selected only for the current rapid-entry session, allowing several related standalone Tasks to be entered without becoming a hidden persistent default.
- **EDIT-018** Backlog quick-add uses the same Enter, Tab, empty-Tab, Escape, and full-inspector follow-up behaviour as Project quick-add.

## Categories

- **CAT-001** A Project belongs to one Category.
- **CAT-002** A Project Task inherits its Project's Category by default.
- **CAT-003** A Project Task may override its inherited Category and may later return to inheritance.
- **CAT-004** Categories have a user-controlled manual order.
- **CAT-005** The Categories view groups Projects and standalone Tasks beneath their Category in Category order.
- **CAT-006** A standalone Task has an explicit Category because it has no Project from which to inherit.
- **CAT-007** Detaching a Task from a Project materialises its current effective Category as the standalone Task's explicit Category.
- **CAT-008** Attaching a standalone Task whose Category matches the Project switches the Task to inheritance.
- **CAT-009** When attaching a standalone Task whose Category differs from the Project, the operation must explicitly keep the Task Category as an override or adopt the Project Category; the application never changes it silently.
- **CAT-010** Category labels are unique case-insensitively.
- **CAT-011** An unreferenced Category can be deleted directly.
- **CAT-012** Deleting a referenced Category requires selecting a replacement and transactionally reassigns affected Projects, standalone Tasks, and Task overrides.
- **CAT-013** The domain has no hidden Uncategorised value and always retains at least one Category.
- **CAT-014** First-run setup creates the first Category before Projects or Tasks can be added.
- **CAT-015** Within each Category, Projects appear in global Project order and standalone Tasks appear in shared Backlog order; the Categories view has no category-local item order.

## Ordering

- **ORD-001** Incomplete Tasks have one shared manual order used by Today and Backlog.
- **ORD-002** Today is a filtered projection of the shared order; dragging a Task in Today changes its relative position in Backlog.
- **ORD-003** Dragging a Task in Backlog changes its relative position in Today when that Task is a Today member.
- **ORD-004** Projects have a manual order independent of Task order.
- **ORD-005** Tasks inside each Project have a per-Project order independent of the Today/Backlog order.
- **ORD-006** Categories have a manual order independent of Project and Task order.
- **ORD-007** Reordering must be possible using a visible drag handle and an accessible non-pointer alternative.
- **ORD-008** Every reorderable item provides keyboard-accessible Move up, Move down, Move to top, and Move to bottom actions within its current ordering scope.
- **ORD-009** Reorder actions announce the item's new position and scope to assistive technology.
- **ORD-010** Optional keyboard shortcuts may accelerate reorder actions but are never the only non-pointer method.
- **ORD-011** A standalone Task participates in shared Task order and has no per-Project order until attached to a Project.
- **ORD-012** A newly created Task is placed at the top of shared Backlog order.
- **ORD-013** A newly created attached Task is also placed at the end of its Project-local Task order.
- **ORD-014** Newly created Projects and Categories are placed at the end of their respective manual orders.

## Views

- **VIEW-001 Today** shows incomplete Tasks explicitly selected for Today in Today lanes, plus every Task completed on the current local calendar day.
- **VIEW-002 Backlog** shows all incomplete Tasks in shared Task order.
- **VIEW-003 Projects** shows Projects in manual Project order, with expandable attached Tasks in per-Project order; standalone Tasks do not appear there.
- **VIEW-004 Categories** shows all Projects and standalone Tasks grouped by manually ordered Category, with separate Project and standalone Task sections that preserve their existing global orders.
- **VIEW-005 Completed** groups recent completed Tasks by day for the last three days, then by week.
- **VIEW-006 Archive** provides full-text search across archived Projects and Tasks.
- **VIEW-007 Upcoming** shows incomplete, non-archived Tasks that are overdue or due within the seven-day upcoming window.

## Archive

- **ARCH-001** Work is never moved to the Archive automatically in the background.
- **ARCH-002** The Completed view provides an explicit bulk-archive action with a selectable completed-age threshold from 1 to 30 days.
- **ARCH-003** The bulk action previews or clearly states the number of records it will archive and requires user confirmation before changing them.
- **ARCH-004** Age is measured from the record's completion date using calendar days in the user's local time zone.
- **ARCH-005** Bulk archive applies only to completed Tasks older than the selected threshold and never archives their Projects.
- **ARCH-006** An archived Task remains visible beneath its Project and continues to contribute to the Project's derived completion status and completion date.
- **ARCH-007** Archived Tasks are excluded from Backlog and Completed.
- **ARCH-008** A Project can enter the Archive only through an explicit Project archive action.
- **ARCH-009** An individual Task must be complete before it can be archived.
- **ARCH-010** A Project may be manually archived in any completion state, preserving its status, completion data, and Tasks unchanged.
- **ARCH-011** Archiving an incomplete Project does not complete the Project or any of its Tasks.
- **ARCH-012** Archiving a Project clears Today membership from all of its Tasks.
- **ARCH-013** Restoring a Project returns its incomplete, non-archived Tasks to Backlog but does not add any Task to Today automatically.
- **ARCH-014** Restoring an individually archived completed Task returns it to Completed.
- **ARCH-015** Tasks that were individually archived before their Project was archived remain archived when the Project is restored.

## Today planning

- **TODAY-001** Today membership persists across calendar-day changes and application restarts until the Task is removed, completed, or archived.
- **TODAY-002** Due dates never add or remove Today membership automatically.
- **TODAY-003** The Today view provides an explicit action to clear its incomplete Tasks rather than clearing them automatically.
- **TODAY-004** Today may organise selected Tasks into working lanes without changing their durable completion state or Project derivation.
- **TODAY-005** An incomplete Today Task has a Today-only lane of Planned or In progress.
- **TODAY-006** Today lane placement persists across calendar-day changes and application restarts while the Task remains an incomplete Today member.
- **TODAY-007** Today lane placement does not affect shared Backlog order, Project status, or the Task's completion state and timestamp.
- **TODAY-008** Removing, completing, or archiving a Task clears its Today lane placement.
- **TODAY-009** Today shows every Task completed during the current calendar day in the user's local time zone, whether or not it was previously a Today member.
- **TODAY-010** At local midnight, completed Tasks from the previous day leave Today but remain available in Completed until archived.
- **TODAY-011** Reopening a Task removes it from the completed-today section and returns it to Backlog without adding it to Today automatically.
- **TODAY-012** Moving a Task between Planned and In progress changes only its Today lane and preserves its shared Backlog position.
- **TODAY-013** Each incomplete Today lane displays its Tasks in shared Backlog order.
- **TODAY-014** Reordering Tasks within a Today lane changes their relative positions in the shared order while preserving the positions of Tasks outside that lane.
- **TODAY-015** Completed-today Tasks are ordered by completion time, newest first, and are not manually reorderable.

## Upcoming

- **UPCOMING-001** Upcoming is the second primary navigation item, between Today and Backlog.
- **UPCOMING-002** Its sidebar badge counts incomplete, non-archived Tasks that are overdue or due within the upcoming window.
- **UPCOMING-011** A Task becomes overdue only when its due date is earlier than the current local calendar date; a Task due today is not overdue.
- **UPCOMING-003** Overdue Tasks appear in a separate section above Tasks due Today, Tomorrow, and later dates.
- **UPCOMING-004** Upcoming is a read-only date projection with respect to membership; it never adds a Task to Today or changes shared Task order automatically.
- **UPCOMING-005** The upcoming window includes due dates from the current local calendar day through the end of the calendar day seven days later.
- **UPCOMING-006** Overdue Tasks are ordered by due date from oldest to newest; future sections are ordered chronologically by date.
- **UPCOMING-007** Tasks sharing a due date appear in shared Backlog order.
- **UPCOMING-008** Upcoming does not support drag or manual reordering.
- **UPCOMING-009** An Upcoming row supports selection and editing, completion, and Add to Today.
- **UPCOMING-010** Add to Today places a newly added Task in the Planned lane without changing its shared Backlog position.
- **UPCOMING-012** Upcoming contains Tasks only; Project target dates remain visible in Project surfaces rather than being mixed into the actionable Task list.
- **UPCOMING-013** Changing a due date immediately moves the Task to the appropriate Upcoming section or removes it from the view when it no longer qualifies.
- **UPCOMING-014** An undated Task does not appear in Upcoming and remains fully available in Backlog, Projects, and Today.

## Inspector, Participants, and Markdown

- **INSPECT-001** Selecting a Project or Task opens its details in an inspector without leaving the current view.
- **INSPECT-002** Editable fields can be changed directly in the inspector without opening a modal editor.
- **MARKDOWN-001** Markdown descriptions show a live rendered preview.
- **MARKDOWN-002** The rendered description can be copied as rich HTML with a plain-text fallback for Teams, Word, and similar applications.
- **PARTICIPANT-001** Participants are optional reusable local records identified by a user-provided label, typically initials or a nickname, and do not create an assignee or ownership model.
- **PARTICIPANT-002** A Participant stores only its label and stable identifier; it has no email address, contact details, account, permissions, or operating-system contact link.
- **PARTICIPANT-003** Selecting an existing Participant reuses that record; entering a new label creates a local Participant.
- **PARTICIPANT-004** Renaming a Participant updates its label everywhere it is referenced.
- **PARTICIPANT-005** A Participant cannot be deleted while Tasks reference it; the references must be removed first.
- **PARTICIPANT-006** Participant entry explains that labels should use initials or nicknames rather than personally identifying contact information.
- **MARKDOWN-003** Markdown follows a CommonMark-style subset with raw HTML disabled.
- **MARKDOWN-004** Markdown rendering rejects scripts, embedded objects, inline event handlers, and unsafe or custom URI schemes.
- **MARKDOWN-005** Remote images are not fetched automatically and render as links or placeholders.
- **MARKDOWN-006** Rich-copy HTML and its plain-text fallback are produced from the same sanitised render model used by preview.
- **MARKDOWN-007** Manually entered and imported or agent-proposed Markdown follow identical rendering and sanitisation rules.

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
- **DATA-008** The application creates and validates a recovery backup before a schema migration, import, restore, or Empty Bin operation changes working data.
- **DATA-009** The default retention policy keeps 24 hourly, 30 daily, and 12 monthly valid backups.
- **DATA-010** Retention pruning never deletes the last known-valid backup.
- **DATA-011** The automatic-backup destination is configurable and may be any writable filesystem directory, including one managed by an external cloud-sync tool.
- **DATA-012** A backup is completely written and validated before it is exposed at its configured destination as a restorable backup.
- **DATA-013** The first release requires the user's encryption passphrase whenever the application launches and does not persist that passphrase in files, settings, logs, or diagnostic evidence.
- **DATA-014** The encryption passphrase is retained only in process memory while the local data store is unlocked.
- **DATA-015** There is no password recovery mechanism or recovery backdoor; losing the passphrase means losing access to the working data and its backups.
- **DATA-016** Password rotation requires the current passphrase and produces a validated encrypted result before replacing working data or backups.
- **DATA-017** Passphrase creation and rotation require at least 12 characters after consistent Unicode normalisation, recommend 16 or more characters or a multi-word passphrase, and require confirmation entry.
- **DATA-018** Passphrase entry supports paste, password-manager generation, and an accessible show/hide control without imposing uppercase, number, symbol, or other composition rules.
- **DATA-019** Passphrase creation and rotation state clearly that no recovery mechanism exists.
- **DATA-020** The first release can explicitly export a versioned plaintext JSON document containing active and archived Projects, Tasks, Categories, Participant labels, ordering, and each Task's current completion state, latest completion instant, and captured local completion date.
- **DATA-021** Plaintext export excludes Bin contents, never runs automatically, and requires an unlocked data store plus confirmation that the output is unencrypted.
- **DATA-022** Plaintext export does not default to the configured automatic-backup directory.
- **DATA-023** Database schemas use numbered, forward-only migrations.
- **DATA-024** Before migration, the application exclusively locks the store and creates and validates an encrypted recovery backup.
- **DATA-025** A migration runs transactionally and validates the resulting schema version and database integrity before commit.
- **DATA-026** A failed or interrupted migration leaves the pre-migration database usable and offers restoration from the recovery backup.
- **DATA-027** An older application refuses a database created by a newer unsupported schema version without modifying it.
- **DATA-028** Migration tests cover upgrades from every previously released schema still in the supported upgrade path, including interrupted migration and recovery.

## Synchronisation

- **SYNC-001** The first release does not automatically synchronise or merge application data between devices.
- **SYNC-002** A cloud-synchronised backup directory is a recovery and portability mechanism, not live application-data synchronisation.
- **SYNC-003** Domain records use stable identifiers, and persistence remains behind an application boundary, so future synchronisation can be designed without replacing the domain model.

## Planned agent interchange

- **AGENT-001** A future interchange boundary exports incomplete, non-archived current work for use by an external planning tool without exposing Bin or archived history.
- **AGENT-002** A future interchange boundary accepts structured proposed additions, updates, completions, reopenings, moves, archives, and moves to Bin without binding the domain to a particular AI provider or transport.
- **AGENT-003** Imported proposals are untrusted input: the application validates them, previews their effects, and applies only changes explicitly approved by the user.
- **AGENT-004** Agent interchange cannot permanently delete data or empty Bin.
- **AGENT-005** Future MCP support reuses the same application boundary rather than creating a second mutation model.
- **AGENT-006** Interchange uses versioned JSON documents validated against published JSON Schemas.
- **AGENT-007** A snapshot contains stable identifiers, relationships, ordering, Today placement, due dates, a snapshot identifier, and a data revision for incomplete, non-archived work.
- **AGENT-008** A proposal references its source snapshot and contains typed operations; optional agent explanations are review metadata and are never copied into Task content implicitly.
- **AGENT-009** A delete operation in a proposal means Move to Bin and can never request permanent deletion.
- **AGENT-010** If current data revision differs from the proposal's source snapshot revision, the application rejects the entire proposal without mutation and requires a fresh snapshot and proposal.
- **AGENT-011** The application does not automatically rebase or partially apply a stale proposal in the first interchange version.
- **AGENT-012** A future Interchange section in Settings generates a provider-neutral prompt or agent definition for explicit clipboard copy; it never sends data or reads meeting files itself.
- **AGENT-013** Interchange Settings provide a locally persisted Include agent instructions and schema with snapshot toggle.
- **AGENT-014** Copying a snapshot previews the included work and requires explicit confirmation that plaintext will be placed on the clipboard, regardless of the persisted include-instructions preference.
- **AGENT-015** When the include-instructions preference is enabled, the copied snapshot is prefixed with the provider-neutral role, constraints, schema, operation vocabulary, and output instructions; when disabled, only the versioned snapshot is copied.
- **AGENT-016** Settings also allow the reusable instructions and schema to be copied without a live snapshot.
- **AGENT-017** Include agent instructions and schema with snapshot is enabled by default.
- **AGENT-018** Agent proposals cannot add or remove Today membership, change Planned or In progress placement, or reorder the shared Backlog, Projects, or Categories.
- **AGENT-019** Agent proposals may reorder attached Tasks within a Project using stable relative placement such as before or after another Task, never numeric persistence positions.
- **AGENT-020** Daily focus and overall priority remain user-controlled and are not part of the agent-interchange operation vocabulary.
- **AGENT-021** The first interchange operation vocabulary can create and edit Projects, attached Tasks, and standalone Tasks; attach, detach, or move a Task between Projects; change titles, descriptions, dates, Categories, and Participant associations or labels; complete or reopen Tasks; reorder attached Tasks within a Project; archive eligible Projects or Tasks; and move Projects or Tasks to Bin.
- **AGENT-022** The first interchange operation vocabulary cannot restore archived or binned work, empty Bin, permanently delete data, or perform any operation prohibited elsewhere by the domain rules.
- **AGENT-023** Syntactically valid operations still undergo normal domain validation before preview or application.
- **AGENT-024** Published interchange JSON Schemas are immutable and each document declares an integer schema version.
- **AGENT-025** A semantic or breaking structural change creates a new schema version rather than modifying a published schema.
- **AGENT-026** The application generates the current schema version and accepts the current and immediately previous version for one application release.
- **AGENT-027** A supported previous-version document is converted to the current model before validation and preview.
- **AGENT-028** Unknown or unsupported schema versions are rejected without mutation with guidance to update or regenerate the document.
- **AGENT-029** Agent proposals may create Projects without target dates, Tasks without due dates, and standalone Tasks when no genuine Project applies; they never invent a Project or date merely to satisfy interchange validation.
- **AGENT-030** An agent attach operation with differing Task and Project Categories must explicitly select inheritance or preserve the Task Category as an override; ambiguous operations are rejected.

## Bin and deletion

- **BIN-001** Removing a Project or Task places it in a recoverable Bin rather than deleting it immediately.
- **BIN-002** Binned work is excluded from Today, Upcoming, Backlog, Projects, Categories, Completed, and Archive.
- **BIN-003** Bin contents do not expire or clear automatically.
- **BIN-004** The user can explicitly restore items from Bin.
- **BIN-005** The user can explicitly empty Bin after confirmation, permanently deleting its contents from working storage.
- **BIN-006** Archive and Bin are separate states: archived work is intentional retained history, while binned work is pending deletion.
- **BIN-007** Moving a Project to Bin moves the Project and all of its Tasks as one recoverable unit.
- **BIN-008** Moving a Task to Bin moves only that Task and removes it from all active and historical projections.
- **BIN-009** A binned Task does not contribute to its Project's derived status, progress, or completion date.
- **BIN-010** Restoring a binned Project restores its full Task set together.
- **BIN-011** A Task cannot be restored independently while its parent Project remains in Bin.
- **BIN-012** Restoring from Bin recovers the item's pre-Bin archive state, completion data, Today membership and lane, relationships, category behaviour, participants, and ordering positions.
- **BIN-013** When an exact prior position can no longer be reproduced, restoration places the item beside its nearest surviving former neighbour, or at the end of the ordering scope when no former neighbour survives.
- **BIN-014** Empty Bin shows the number of Projects and Tasks to be deleted and requires explicit confirmation.
- **BIN-015** Immediately before Empty Bin deletes data, the application creates and validates an encrypted recovery backup.
- **BIN-016** Empty Bin permanently deletes all Bin contents from the current working database in one transaction.
- **BIN-017** Empty Bin does not claim forensic erasure from storage media, filesystem snapshots, or retained backups; confirmation explains that backups may retain earlier copies until normal pruning removes them.
- **BIN-018** Bin is a secondary utility item visually separated from the primary navigation and shows an item-count badge only while non-empty.
- **BIN-019** Bin lists binned Projects and individually binned Tasks by removal time, newest first.
- **BIN-020** Each Bin item provides a restore action; Empty Bin is available only from within the Bin view.

## Archive search

- **SEARCH-001** Archive search covers archived Project and Task titles, Markdown description text with formatting removed, Category names, and Participant labels.
- **SEARCH-002** Search is case-insensitive and supports ordinary whole-word and prefix matching.
- **SEARCH-003** The first release does not provide fuzzy matching, learned ranking, saved searches, or an advanced query language.
- **SEARCH-004** Results identify the record type, parent Project where applicable, completion or archive date, and a short matching excerpt.
- **SEARCH-005** Bin contents never appear in Archive search.

## Open requirements

- Optional biometric or operating-system credential-store unlock after the first release.
