# Product requirements

Status: working specification derived from the interactive prototype.

## Navigation

- **NAV-001** The primary navigation order is Today, Upcoming, Backlog, Projects, Categories, Completed, Archive.
- **NAV-002** Today is the default view at application launch.
- **NAV-003** Navigating away from a Project or Task inspector flushes valid pending automatic changes first. Invalid or failed changes remain visible and block leaving until they are corrected, retried, or explicitly discarded; Category drafts retain the save, discard, or stay prompt.

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

- **EDIT-001** Valid Project and Task inspector changes are persisted automatically: text entry is coalesced after 600 milliseconds of inactivity, while discrete selections and Participant association changes save immediately.
- **EDIT-002** Automatic saves are serialized and revision-aware so an older write cannot overwrite a newer inspector value. A persistent near-header status reports saving, saved, and failed states and provides an accessible retry action after failure.
- **EDIT-003** Navigating, closing, or starting an immediate workflow action first flushes valid pending Project or Task changes. Invalid or failed changes remain visible and block leaving until corrected, retried, or explicitly discarded.
- **EDIT-004** Category editing retains explicit Save and Cancel actions and the save, discard, or stay navigation decision.
- **EDIT-005** Creating a Project or Task opens a transient inspector. The first valid automatic save creates it; an incomplete or blank draft remains transient and can be left without creating a partial item.
- **EDIT-006** Discarding an invalid or failed creation draft leaves no partial item in lists, counts, backups, search, or other projections.
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
- **EDIT-019** Today membership, Planned/In progress, completion/reopen and Backlog/Today reorder activations are accepted in order through the same serialized persistence boundary as inspector saves. Pending actions do not optimistically alter persisted row state; repeated toggles and moves resolve against the committed result of preceding accepted actions.
- **EDIT-020** A polite pending count remains visible while immediate actions are queued. Navigation and close stop new action admission and await accepted work. Mutation failure is distinguished from a committed mutation whose refresh failed; retry after the latter refreshes only and never repeats the mutation.

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
- **CAT-016** Every Category has one required named colour from the checked-in identity palette. The stable palette key is persisted, colours need not be unique, and the Category name remains canonical.
- **CAT-017** Category colour is presentation metadata only. It does not affect membership, inheritance, ordering, completion, Today, or any other workflow behaviour.
- **CAT-018** Existing Categories receive deterministic persisted colours during forward migration; later rename or reorder does not recalculate them.

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
- **VIEW-006 Archive** groups the last three captured local archive dates by day and older archived work by week, and provides full-text search across archived Projects and Tasks.
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
- **INSPECT-003** Existing Task and Project inspectors expose a keyboard-operable Duplicate action. Bin surfaces do not expose duplication.
- **INSPECT-004** Duplicating a Task creates fresh active work titled `Copy of <source>`, copies description, due date, Category behaviour, and Participant references, resets completion, archive, Today, and Bin state, then opens the new Task with its title focused.
- **INSPECT-005** A Task duplicated from an active Project remains attached and retains inheritance or its explicit override. A Task duplicated from an archived Project becomes standalone and materialises its effective Category as an explicit Category.
- **INSPECT-006** Duplicating a Project first shows an editable suggested title, included Task count, and explicit archived and Bin exclusions. Confirmation creates a fresh active Project and copies only non-archived, non-binned Tasks; copied completion, archive, Today, and Bin state are reset.
- **INSPECT-007** Project duplication preserves Project content, Category, target date, identity colour, Task content, Category behaviour, Participant references, Project Task order, and relative shared order. The copied Tasks form a contiguous block at the top of shared Task order.
- **INSPECT-008** Project duplication rejects a stale confirmation preview and creates the Project and included Tasks atomically.
- **MARKDOWN-001** Markdown descriptions show a live rendered preview.
- **MARKDOWN-002** The rendered description can be copied as rich HTML with a plain-text fallback for Teams, Word, and similar applications.
- **PARTICIPANT-001** Participants are optional reusable local records identified by a user-provided label, typically initials or a nickname, and do not create an assignee or ownership model.
- **PARTICIPANT-002** A Participant stores only its label and stable identifier; it has no email address, contact details, account, permissions, or operating-system contact link.
- **PARTICIPANT-003** The Participant picker lists reusable records followed by a distinct New participant action. Choosing it reveals a focused label field with Enter/Add and Escape/Cancel behaviour. A new unique label is created and associated atomically with the Task's next automatic save; an equivalent existing label associates that existing Participant instead, and an already-associated match is rejected at the field.
- **PARTICIPANT-004** Participant labels are unique after trimming, Unicode compatibility normalisation, and case-insensitive comparison. Creating or renaming to an equivalent label never creates or merges duplicate identities.
- **PARTICIPANT-005** Settings lists all Participants using the shared list-row pattern. Renaming one row is an explicit Save/Cancel action and updates its label everywhere it is referenced.
- **PARTICIPANT-006** Settings can delete an unreferenced Participant. A Participant cannot be deleted while Tasks reference it; the references must be removed first.
- **PARTICIPANT-007** Participant entry explains that labels should use initials or nicknames rather than personally identifying contact information.
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
- **DESK-006** Settings offers Dark and Light colour themes. A change applies immediately across the main, unlock, recovery, and passphrase windows and is remembered on the local device without storing it in the encrypted workspace.
- **DESK-007** Every Task, Project, and Category identity heading uses one shared decorative work-type icon in a compact fixed-width slot immediately beside the title. Task icons are neutral; Project and Category icons carry their persisted identity colour rather than adding a second coloured marker. Interactive rows identify the work type and relationship in their accessible name so the icon is not announced twice and no action relies on icon or colour alone.
- **DESK-008** Task secondary text starts with its Project or `Standalone`, Project secondary text contains Category and total Task count, and Category secondary text contains Project and standalone Task counts. An explicit Task Category override is reinforced by a decorative fork symbol and named as a Category override in the row's accessible text.
- **DESK-009** Category creation and editing offer sixteen keyboard-operable named colour choices with visible selection, focus, and live preview. Colour participates in the Category's explicit Save, Cancel, and dirty-navigation draft.
- **DESK-010** Task rows in Today, Upcoming, Backlog, expanded Projects, Categories, Completed, Archive timeline and search, and Bin show one compact non-actionable identity pill. The effective Category appears first with its coloured Category glyph; an attached Project follows with its coloured Project glyph after an angled one-pixel internal boundary. Standalone Tasks collapse the Project segment and boundary. The existing decorative fork appears inside the Category segment only for an attached Task with an explicit Category override.
- **DESK-011** Category headings, Category choices, and Project Category metadata use the same non-actionable colour-and-name marker without adding a focus stop. Every containing row retains the full Category name and relationship in accessible text; no meaning relies on colour or the fork alone.
- **DESK-012** Project creation and editing offer the same sixteen keyboard-operable named colour choices with visible selection, focus, and live preview. Project colour is persisted presentation metadata, remains stable across lifecycle and ordering changes, and does not affect behaviour. Archive and Bin Project rows keep the Project identity once beside the title and collapse their secondary identity pill to Category only.
- **DESK-013** An accepted main-window close remembers the last valid normal width and height as a device-local preference after pending Workspace writes drain. The next main-window launch restores those logical dimensions in normal state, constrained to the platform-selected display's usable working area while retaining the supported 1120×600 minimum whenever that area permits it. Transient maximised, minimised, fullscreen, and display-scaling dimensions are not remembered; invalid, unavailable, or unwritable preferences and unavailable screen information fall back safely without blocking startup or shutdown. Position, monitor, window state, and auxiliary-window dimensions are not persisted, and geometry changes never alter Workspace data or revision.

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
- **DATA-029** Settings contains the persistent Recovery entry; recovery is not a permanent primary-work action.
- **DATA-030** The default live workspace is `<LocalApplicationData>/dot-orbit/workspace.orb`; `.orb` is a naming convention only, and every open validates encrypted SQLite3MC content, integrity, and schema rather than trusting the extension.
- **DATA-031** The exact legacy `workspace.db` name is adopted only after a successful validated unlock. Adoption uses same-directory no-overwrite name publication, is resumable through a flushed versioned manifest, preserves exact SQLite and automatic-recovery companions byte-for-byte, and fails closed without storage actions when old and new names are ambiguous.
- **DATA-032** A planned post-first-release Quick unlock with Windows Hello capability is optional, Windows 11-only, off by default, and enrolled explicitly only after the current passphrase has successfully opened and validated the workspace. macOS and Linux remain passphrase-only under the current distribution and provider-neutral boundaries.
- **DATA-033** Quick unlock starts one new HWND-owned Windows Hello or system-authentication operation for each current unlock attempt and proceeds only from that operation's `Verified` result. Windows does not guarantee that each operation displays a new prompt or requires a new gesture, and the product never claims factor recency, fingerprint-only, or biometric-only authentication.
- **DATA-034** The versioned, size-bounded device-local protected record uses DPAPI-NG `LOCAL=user` and contains the normalised portable passphrase plus bindings to the exact random encrypted workspace identity, Windows file-location identity digest, and passphrase-rotation generation. Copying, moving, renaming, replacing, restoring, or resolving the workspace through a different location requires its passphrase. The record is excluded from the workspace, recovery files, exports, logs, diagnostics, screenshots, real-data tests, CI output, and GitHub artifacts; secret-free fixtures may test strict parsing and failures, and there is no plaintext fallback.
- **DATA-035** The portable passphrase remains immediately reachable on every launch and is the only recovery and cross-device credential. Quick unlock never changes SQLite3MC encryption, opens a recovery point on another device, or recovers a forgotten passphrase.
- **DATA-036** Forgetting quick unlock idempotently deletes only the local protected record and cannot change, damage, re-encrypt, or lock the workspace; it does not revoke a previously copied blob. Passphrase rotation writes a new checked generation into its validated encrypted candidate and publishes only after confirmed record absence; an outcome that cannot confirm absence preserves the original workspace and aborts publication, while the record may safely be present or absent and is reconciled on the next status read. Re-enrolment is explicit only after the rotated workspace has been validated, and interruption leaves passphrase unlock authoritative. A stale record is rejected against the current post-rotation workspace, while paired rollback of both workspace and record is outside the accepted boundary.
- **DATA-037** Cancellation, denial, unavailable or unconfigured verification, policy disablement, busy or exhausted verification, a missing/corrupt/denied/mismatched record, native failure, stale completion, or unsuccessful workspace open fails closed to usable passphrase entry without automatic retry or weaker fallback.
- **DATA-038** Quick unlock claims protection only for a copied protected record outside its originating Windows user and computer. It does not claim protection from another same-user process, administrator or SYSTEM access, malware, application replacement, or a compromised operating system because verification and DPAPI-NG release are not cryptographically coupled.
- **DATA-039** Native quick-unlock interop stays in narrow Windows desktop adapters. Core owns platform-neutral lifecycle decisions and fixed outcomes; adapter-owned plaintext uses bounded mutable buffers and is zeroed and released immediately after handoff or failure. The ordinary unlocked session retains its managed passphrase under DATA-014, and no secret crosses diagnostic boundaries.

## Synchronisation

- **SYNC-001** The first release does not automatically synchronise or merge application data between devices.
- **SYNC-002** A cloud-synchronised backup directory is a recovery and portability mechanism, not live application-data synchronisation.
- **SYNC-003** Domain records use stable identifiers, and persistence remains behind an application boundary, so future synchronisation can be designed without replacing the domain model.

## Planned agent interchange

- **AGENT-001** Agent interchange is a provider-neutral, human-reviewed planning exchange; dot-orbit does not invoke an AI provider, itself read meeting material or transcripts, or create an autonomous mutation route.
- **AGENT-002** A Planning snapshot contains every Category and Participant as a reusable reference catalogue, every Project that is neither archived nor in Bin, and every non-archived, non-binned Task that is standalone or belongs to an included Project, including completed Tasks.
- **AGENT-003** A Planning snapshot excludes archived and binned Project and Task content. Before copy, the application discloses that catalogue names or labels may still reveal context referenced only by excluded work.
- **AGENT-004** A Planning snapshot represents stable record identifiers, relationships, all applicable ordering scopes, Today membership and lane, dates, Category inheritance or override, Participant associations, completion state and captured completion data, plus its snapshot identity and Workspace revision. Domain-ordered arrays retain their manual order; unordered catalogues and set-valued associations sort by stable ID before canonicalisation.
- **AGENT-005** Agent interchange accepts either a creation-only New-work proposal with no workspace source fields or a structured Change proposal tied to one Planning snapshot, without binding the domain to a particular provider or transport.
- **AGENT-006** Every proposal is untrusted input. The application validates the whole document and all derived effects into an immutable plan. Review lists only work the proposal directly adds or changes and identifies routine knock-on behaviour as implied rather than expanding every affected item. Apply requires explicit whole-proposal approval.
- **AGENT-007** Agent interchange cannot restore archived or binned work, permanently delete data, empty Bin, duplicate work, or bypass any domain rule.
- **AGENT-008** Future MCP support must reuse the same snapshot, validation, preview, approval, and apply boundaries rather than create a second mutation model or approval route.
- **AGENT-009** Interchange uses strict JSON and JSON Schema 2020-12. Version 1 publishes exactly the Planning snapshot, Change proposal, and New-work proposal schemas under `schemas/interchange/v1/`, with shared definitions embedded. All three immutable schemas are checked in and packaged at matching paths, and validation uses a closed offline catalogue containing every transitively required meta-schema.
- **AGENT-010** A Planning snapshot identity is `sha256:` plus the SHA-256 digest of RFC 8785 JCS over the snapshot without its identity property. It binds content but does not authenticate its author or origin.
- **AGENT-011** Every Change proposal names its format and integer schema version, references the source snapshot identity in the exact lowercase SHA-256 form and Workspace revision in the same bounded canonical decimal-string form as the snapshot, contains ordered operations with unique operation identifiers, and keeps optional explanations as inert review metadata.
- **AGENT-012** The encrypted workspace stores a positive, monotonically increasing Workspace revision. Each transaction that actually changes user data advances it exactly once, including hidden, archived, or binned state and one multi-operation proposal; rejected, failed, rolled-back, or effective no-op mutations do not.
- **AGENT-013** Reads, snapshot or plaintext-export generation, recovery creation or retention, passphrase operations, schema migration, device-local preferences, and derived-index maintenance do not advance the Workspace revision. Recovery restore rebases restored working data to a checked successor greater than both the pre-restore and recovered revisions, carrying that successor through a later migration when the recovered schema predates revision storage. Increment and rebase fail closed before overflow.
- **AGENT-014** Snapshot data and revision are read atomically. Manual Change-proposal Review checks both source revision and recomputed snapshot identity. A queued Change proposal is admitted only after that same exact check and retains an encrypted, versioned compatibility basis bound to its payload and semantic dependencies. Later queued Review may bind the unchanged proposal operations to the current revision and snapshot only when the basis proves every depended-on field, relationship, record, or aggregate is unchanged; unrelated fields and incidental ordering metadata are excluded from content-edit dependencies. Review then reruns complete semantic planning and shows the recalculated preview. New-work Review captures the current revision internally. Apply repeats the reviewed kind-specific revision and snapshot checks. Both retain database-level exclusive ownership through recovery creation, final recheck, mutation, and commit.
- **AGENT-015** A stale manual Change proposal and a queued Change proposal with a missing, invalid, or changed compatibility basis are rejected completely without mutation. Queue reconciliation never rewrites, repairs, drops, automatically approves, or partially applies operations; a safely refreshed plan still requires ordinary whole-proposal Review and approval. Related changes require a fresh snapshot and proposal.
- **AGENT-016** When global AI features are enabled, the AI destination generates provider-neutral instructions and a live Planning snapshot for explicit clipboard copy; dot-orbit never sends them itself and reads clipboard content only in response to the user's explicit Paste action, never through monitoring or background access.
- **AGENT-017** Within the enabled AI destination, an enabled-by-default, device-local **Include agent instructions and schema with snapshot** preference controls composition and does not advance the Workspace revision. It never enables the global AI-features gate.
- **AGENT-018** Every live snapshot copy previews the included scope and requires explicit confirmation that sensitive unencrypted plaintext will be placed on the clipboard, regardless of the persisted preference.
- **AGENT-019** With the preference enabled, copied output includes the provider-neutral role, constraints, schemas, operation vocabulary, output instructions, and live snapshot. With it disabled, only snapshot JSON is copied; reusable instructions and schemas can also be copied without reading workspace content.
- **AGENT-020** A Change proposal cannot directly add or remove Today membership, change Today lanes, change derived Project status, or reorder the shared Backlog, Projects, or Categories. Daily focus and global priority remain user-controlled.
- **AGENT-021** An attached Task may be placed `first`, `last`, `before`, or `after` another included Task in the same Project at that point in proposal order. The included subsequence is permuted within its slots while excluded archived Tasks remain fixed in their absolute Project-order slots. The retained plan validates the full local interleaving, while Review presents the directly moved Task rather than every shifted row. Numeric persistence positions and shared-order changes are invalid.
- **AGENT-022** Version 1 operations may create Categories, Participants, Projects, attached Tasks, and standalone Tasks; edit Project or Task content and optional dates; set Project Category or Task Category behaviour; replace Task Participant associations; attach, detach, or move Tasks; complete or reopen Tasks; reorder attached Tasks within a Project; archive eligible Projects or Tasks; and explicitly move a Project aggregate or Task to Bin.
- **AGENT-023** Version 1 cannot rename, delete, merge, recolour, or reorder Categories or Participants. Existing reusable records are referenced by stable ID; duplicate normalised names or labels are rejected rather than silently reusing hidden records.
- **AGENT-024** A proposal may create a Project without a target date, a Task without a due date, and a standalone Task when no genuine Project applies. It never invents a Project or date to satisfy validation.
- **AGENT-025** Attaching or moving a Task whose effective Category differs from the destination Project must explicitly select `adoptProjectCategory` or `preserveAsOverride`; omission is invalid.
- **AGENT-026** Ordered validation simulates the complete proposal prospectively without persistence. Later operations may use backward-only typed local references to earlier created records; durable identities are always application-generated.
- **AGENT-027** After creation, at most one operation per target may write each mutation facet: content, Category, Participants, Project relationship, completion, Project-local order, or lifecycle. Attach, detach, and move each consume both relationship and Category because detachment materialises the effective Category. Duplicate, contradictory, exact no-op, already-true, unresolved, forward-reference, and last-write-wins operations are invalid.
- **AGENT-028** Creation establishes initial state without consuming later mutation facets and retains normal ordering effects. A newly created record may be edited, moved, reordered, or completed once where applicable, but cannot be archived or moved to Bin in the same proposal.
- **AGENT-029** Archive and Move to Bin are terminal for their target. A proposal cannot separately target a child and archive or bin its parent Project; the retained plan validates the complete aggregate and hidden-child effects, while Review presents the direct parent action and treats the normal child consequences as implied.
- **AGENT-030** Published schemas are immutable and each document declares an integer version. Version 1 generates and accepts only version 1; conversion begins only when a real later version explicitly retains an earlier version. Unknown or unsupported versions fail closed.
- **AGENT-031** Proposal intake uses one editable text surface populated by typing or paste, **Load from file…**, or single-file drag-and-drop. All three paths share the same bounded strict-UTF-8 pipeline and require Review plus explicit Apply.
- **AGENT-032** The application never monitors the clipboard or filesystem, watches or reloads a proposal file, or retains its path. File intake rejects multiple items, directories, non-regular files, links or reparse points, and unreadable files; it opens once without following links and without waiting for pipe/device readiness, validates the opened handle, and acquires and reads outside the UI thread under one cancellation/deadline only to end-of-file or the byte limit plus one.
- **AGENT-033** Every proposed text mutation is strict-UTF-8 measured before UI insertion. Typing is guarded incrementally; an explicit paste or successful file load replaces the complete editor contents only after validation; failed input preserves prior text and focus; and fixed non-sensitive outcomes are announced accessibly.
- **AGENT-034** Version 1 rejects a byte-order mark, comments, trailing commas, unpaired surrogates, fractional or exponent-form numbers, non-finite values, duplicate decoded member names, unknown fields, and unknown operation discriminators before domain planning.
- **AGENT-035** Proposal limits are 4 MiB UTF-8, 32 JSON nesting levels, 512 operations, 100,000 aggregate JSON tokens, 128 Unicode scalars per operation/local/stable identifier, 1,024 per title/Category name/Participant label, 65,536 per Markdown description or explanation, 4,096 CommonMark AST nodes and 32 container levels per proposed Markdown field, 65,536 retained Markdown AST nodes per preview, 256 Participant associations per Task, 100 collected validation failures with at most 20 displayed, 2,000 expanded effects, and 4 MiB strict UTF-8 of retained preview display text.
- **AGENT-036** A New-work proposal uses format `dot-orbit-new-work-proposal`, contains no source snapshot identity or Workspace revision, and permits only `createCategory`, `createParticipant`, `createProject`, and `createTask`.
- **AGENT-037** Every New-work reference contains exactly one proposal-local ID and points backward to an earlier create of the required kind. Durable existing IDs and operations that edit, relate, complete, archive, bin, restore, reorder, or otherwise target existing work are rejected by both schema and semantic validation.
- **AGENT-038** Review labels a New-work proposal as adding work, states that no existing item is referenced or edited directly, treats ordinary ordering consequences as implied, and hides source diagnostics that the document does not contain. Applying still uses fresh durable IDs, validated recovery, current-revision rejection, atomic commit, and single-use approval.
- **AGENT-039** Each final composed snapshot/instruction clipboard payload or future transport response is limited separately from proposal input to 16 MiB strict UTF-8. The producer never truncates or omits current domain-valid values; over-limit generation fails before output with fixed non-sensitive guidance.
- **AGENT-040** Approval is whole-proposal, session-local, and single-use. The pending state retains the immutable plan, complete internal effect model, canonical proposal digest, and the kind-specific source binding: snapshot identity plus revision for a Change proposal, or the internally captured Review revision for New-work. Navigation, lock, close, restart, or any attempted input mutation discards it and disables Apply.
- **AGENT-041** Apply never reparses input or reloads a source file. It uses only the retained plan, obeys `EDIT-003`, creates and validates the encrypted recovery point required by `DATA-008`, and commits every operation and one revision increment in a single transaction or changes nothing.
- **AGENT-042** Commit is the success boundary. A later refresh or recovery-scheduling failure reports that data committed and may retry only that maintenance; it never replays the proposal.
- **AGENT-043** Descriptions and explanations follow `MARKDOWN-007`. Proposal and snapshot content, labels, identifiers, digests, paths, validation excerpts, clipboard contents, and database values never enter logs, telemetry, exception text, screenshots, CI output, or GitHub artifacts; diagnostics contain only bounded fixed operational metadata.
- **AGENT-044** Settings owns one device-local **Enable AI features** gate which is off when absent, unreadable, malformed, or explicitly disabled. Changing it does not advance the Workspace revision and never contacts a provider, reads workspace content, or accesses the clipboard by itself.
- **AGENT-045** While AI features are disabled, the AI navigation destination is absent from visual, keyboard-focus, and accessibility traversal; no AI view model, proposal intake, retained approval, or direct UI handler can exercise agent interchange. Disabling cancels pending intake and discards all remaining review authority without interrupting an already approved atomic Apply.
- **AGENT-046** Enabling AI features restores the manual interchange destination only. Local MCP has a separate off-by-default opt-in and must stop, persist disabled, close connections, and invalidate live capabilities whenever the global gate is disabled.
- **AGENT-047** The packaged `dot-orbit-mcp` executable is a standard MCP stdio adapter using the pinned official C# SDK. It exposes no network listener and reaches the running unlocked desktop only through a same-user private Unix-domain socket or current-user-ACL named pipe.
- **AGENT-048** The desktop creates its private broker endpoint only while the workspace is unlocked and both gates are enabled. Local MCP disable, global AI disable, workspace close or replacement, shutdown, or broker failure closes admission and invalidates every live capability. An incompatible protocol or disconnect closes and invalidates only the affected connection so an untrusted client cannot disrupt other approved clients. Explicit user revocation removes only the selected remembered authorisation but recycles all live sessions so no in-flight request survives the policy change; other remembered authorisations may reconnect without another prompt.
- **AGENT-049** Settings shows only fixed stopped, running, or error status and explains that approved local software can read sensitive plaintext current-work context and submit untrusted proposals but cannot approve or apply them. It lists remembered clients and supports immediate revoke.
- **AGENT-050** A new local client requires in-app approval that separates the verified operating-system account and available process identifier from unverified client name and version, enumerates the exact requested scopes and powers, and offers Deny, Allow once, and remembered approval.
- **AGENT-051** Remembered approval is encrypted in the workspace and matches the adapter credential verifier, verified operating-system account, workspace, and exact scope set. Scope expansion or a different credential or account requires approval again; display metadata is never a trust key.
- **AGENT-052** Adapter credentials are generated per named profile, stored only in the supported platform credential store, and represented to the desktop by a one-way verifier. Workspace passphrases and adapter secrets never enter process arguments, environment variables, URLs, logs, exports, backups, diagnostics, screenshots, fixtures, CI output, or GitHub artifacts.
- **AGENT-053** Each approved connection receives a fresh random 256-bit capability that expires after 15 seconds if unused, binds exactly once to its private session endpoint and scope set, and cannot be replayed across connections or restarts. At most four sessions and four outstanding calls per session are admitted.
- **AGENT-054** The read scope exposes immutable reusable instructions and schemas plus one atomic revision-bearing Planning snapshot through the existing generators. Reads, discovery, retries, cancellation, and disconnect are observational and do not navigate, flush editors, mutate domain state, or advance Workspace revision.
- **AGENT-055** The submit scope accepts one complete v1 proposal through the same strict 4 MiB UTF-8, schema, and semantic planning boundary used by manual intake. It returns bounded content-free receipt/status metadata and exposes no approve or Apply operation.
- **AGENT-056** Validated inbound proposals are encrypted in stable arrival order in a queue of at most ten items, independently of manual text and an open manual review. An eleventh proposal fails without replacement or merge; an identical client and idempotency key receives its prior durable receipt even while the queue is full. Review, discard, and Apply address one receipt and do not consume another queued item.
- **AGENT-057** An accepted inbound proposal is durably encrypted before acknowledgement and appears through a prominent accessible `Review N` navigation pill plus a content-free, polite in-app arrival banner without navigating, opening a window, or stealing focus. Restored proposals do not replay arrival banners. Each fixed-height review row opens the existing Review modal, which identifies manual text/file input or the explicitly self-reported local-MCP client source. A safely refreshed queued Change proposal opens that modal with an explicit notice that its preview was recalculated against the current Workspace. A queued proposal that cannot be reconciled remains pending and shows fixed non-sensitive guidance in the aggregate queue summary without changing row geometry; it is never silently consumed. While AI is already selected, the banner Review action opens the oldest queued proposal; otherwise it selects AI and focuses that row. Human whole-proposal approval, final stale check, recovery, and atomic Apply remain exclusively in that desktop flow; successful Apply consumes only the selected receipt in the same transaction so post-commit interruption cannot replay it.
- **AGENT-058** IPC uses closed versioned JSON frames of at most 64 KiB and bounded ordered chunking for 4 MiB proposals and 16 MiB responses. Unknown fields, malformed or reordered chunks, excess clients or requests, deadline expiry, cancellation, and backpressure fail closed with fixed content-free errors.

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

- Provider-specific quick-unlock support beyond the accepted Windows Hello and DPAPI-NG contract requires a new threat model and ADR. The current macOS unsigned bare executable and generic Linux Secret Service boundary remain unsupported.
