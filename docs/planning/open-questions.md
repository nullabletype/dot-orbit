# Open questions

These decisions should be resolved before their dependent implementation issues are marked `ready-for-agent`.

## Product

Decided: Markdown uses a CommonMark-style subset with raw HTML disabled. Unsafe URI schemes and executable or embedded content are rejected, remote images are not fetched automatically, and preview plus rich copy use the same sanitised render model for manual and imported text.

Decided: Archive search covers archived Project and Task titles, plain description text, Category names, and Participant labels. It is case-insensitive with word and prefix matching, excludes Bin, and returns contextual excerpts. Fuzzy search, learned ranking, saved searches, and advanced query syntax are deferred.

Decided: archiving is never an automatic background transition. The Completed view provides a confirmed bulk action whose user-selected threshold is from 1 to 30 completed calendar days. It archives eligible completed Tasks only, never their Projects. Archived Tasks remain visible under their Project and still contribute to derived Project status, but are excluded from Backlog and Completed. An individual Task must be complete before archival. A Project may be manually archived in any state without completing or otherwise changing it or its Tasks.

Decided: archiving a Project clears Today membership from its Tasks. Restoring a Project returns its incomplete, non-archived Tasks to Backlog without adding them to Today. Restoring an individually archived completed Task returns it to Completed, and previously archived Tasks remain archived through Project restoration.

Decided: Today membership persists across midnight and application restarts until a Task is removed, completed, or archived. Due dates never change Today membership. Today-only organisation does not create another durable Task status or affect Project completion. Incomplete Today Tasks have a persisted Planned or In progress lane; that lane is cleared when the Task leaves Today and does not affect Backlog order, Project status, or history.

Decided: Today shows every Task completed on the current local calendar day, regardless of prior Today membership. At midnight those Tasks leave Today but remain in Completed. Reopening removes a Task from the daily completion section and returns it to Backlog without adding it to Today.

Decided: completing a Task records its latest completion timestamp. Reopening clears that timestamp, and completing it again records a new one. The first release does not retain a full completion and reopening audit trail.

Decided: completion also captures the local calendar date at that moment. Historical day grouping uses that captured date, so travelling or changing the computer's time zone does not move completed Tasks between days; the absolute completion instant remains available for ordering.

Decided: changing a Task between Planned and In progress preserves shared Backlog order. Each lane displays Tasks in shared order; reordering within a lane changes the relative shared order of those lane Tasks while preserving other positions. Completed today is ordered newest first and is not manually reordered.

Decided: Participants are reusable local records containing only a stable identifier and user-provided label, normally initials or a nickname. Labels are unique after trim, Unicode compatibility, and case normalisation. They have no contact, account, assignment, or permissions data. Renaming updates all references, and referenced Participants cannot be deleted.

Decided: recurring Tasks are outside the first useful release. A future recurrence design must model templates and occurrences explicitly rather than repeatedly resetting one Task and losing completion history.

Decided: operating-system notifications and reminders are outside the first useful release. Upcoming, its badge, and overdue grouping provide in-application visibility; due dates do not schedule background notifications initially.

Decided: every reorderable item provides accessible Move up, Move down, Move to top, and Move to bottom actions within its current scope, announces the new position, and may also offer optional keyboard shortcuts that are never the sole alternative to dragging.

Decided: Task due dates and Project target dates are optional. Undated Tasks remain available in Backlog, Projects, and Today but do not appear in Upcoming. Agent proposals may leave dates null rather than inventing deadlines.

Decided: Task due dates and Project target dates are date-only values with no time of day or time zone. A Task becomes overdue when its due date is before the current local calendar date. Timed deadlines, reminders, and notifications are deferred.

Decided: a Task may be standalone or belong to one Project. Standalone Tasks require an explicit Category, participate in shared ordering and normal Task views, appear directly in Categories, and do not appear in Projects or have a per-Project order. They can later be attached to a Project, and agent proposals may create them rather than inventing a Project.

Decided: detaching a Task materialises its effective Category explicitly. Attaching a standalone Task with the same Category as the Project switches to inheritance. If Categories differ, the UI or agent proposal must explicitly preserve the Task Category as an override or adopt the Project Category; silent recategorisation is prohibited.

Decided: Projects may contain zero Tasks. An empty Project is Not started, shows 0 of 0 progress, has no completion date, and never becomes Complete merely because it has no incomplete Tasks.

Decided: Category labels are unique case-insensitively. Empty Categories may be deleted directly; deleting a referenced Category requires a replacement and transactionally reassigns every reference. There is no hidden Uncategorised value, at least one Category always exists, and first-run setup creates it before work can be added.

Decided: each Category displays separate Projects and standalone Tasks sections. Projects retain global Project order, standalone Tasks retain shared Backlog order, and the Categories view introduces no additional ordering scope.

## Platform

Decided: the first release supports macOS, Linux, and Windows using Avalonia UI on .NET. Mobile is a possible future client, not a first-release target. Local storage and backups are encrypted at rest. The first release is local-only and does not synchronise or merge application data; stable identifiers and a persistence boundary preserve the option to design synchronisation later.

## Data and recovery

- What threat model and operating-system credential-store design would be required for optional biometric unlock after the first release?

Decided: after stored data changes, routine backups are coalesced to at most one per hour. A validated recovery backup is required before migrations, imports, and restores. Retain 24 hourly, 30 daily, and 12 monthly backups, and never prune the last known-valid backup. The backup destination is configurable and may be a directory managed by an external cloud-sync tool; dot-orbit does not integrate with that provider.

Decided: the first release requires the encryption passphrase on every launch, holds it only in process memory while unlocked, provides no password recovery or backdoor, and requires the current passphrase for rotation. Biometric unlock is deferred beyond the first release.

Decided: passphrases require at least 12 characters after consistent Unicode normalisation, with guidance to use 16 or more characters or multiple words. Creation and rotation require confirmation, support paste and show/hide controls, impose no composition rules, and repeat the no-recovery warning.

Decided: the first release provides explicit versioned plaintext JSON export in addition to encrypted backup. It includes active and archived work plus each Task's current completion state, latest completion instant, and captured local completion date; excludes Bin; requires an unlocked store and an unencrypted-output warning; never runs automatically; and does not default to the backup directory.

Decided: schemas use numbered forward-only migrations. A validated encrypted recovery backup and exclusive store lock precede transactional migration; schema and integrity checks precede commit. Failure leaves the prior database usable. Older applications refuse newer schemas without modification. Supported upgrade paths, interruption, and recovery are tested.

Decided: removal uses a recoverable Bin distinct from Archive. Bin never expires automatically and is permanently cleared only through an explicit confirmed Empty Bin action. Moving a Project to Bin moves it and all its Tasks as one unit; moving a Task affects only that Task. Binned Tasks do not contribute to Project derivation, and cannot be restored independently while their parent Project remains in Bin.

Decided: restoring from Bin recovers the exact pre-Bin domain, visibility, Today, and ordering state where possible. If an original position cannot be reproduced, the item returns beside its nearest surviving former neighbour or at the end of that ordering scope.

Decided: Empty Bin shows affected Project and Task counts, requires confirmation, and creates a validated encrypted recovery backup before deleting all Bin contents transactionally from the working database. It does not promise forensic erasure; retained backups may contain earlier copies until normal pruning removes them.

Decided: Bin is a secondary utility at the bottom of the sidebar, with a count shown only while non-empty. It lists binned Projects and individually binned Tasks newest removed first, offers per-item Restore, and keeps Empty Bin inside the Bin view.

## Open source

Decided: the public product and repository name is `dot-orbit`; .NET identifiers use `DotOrbit`. The project is licensed under the MIT License.

Decided: features and behavioural changes are issue-first, and external pull requests implement accepted linked issues. Small documentation corrections and isolated bug fixes may open directly. Internal APIs are unstable before the first stable release. All contributions follow the same dependency, privacy, verification, and artifact policies. Vulnerabilities are reported privately under `SECURITY.md`.

## Upcoming

Decided: Upcoming is the second navigation item. Its sidebar badge and view include incomplete, non-archived Tasks that are overdue or due from today through the end of the same weekday next week. Overdue Tasks appear oldest first in a separate top section; future sections are chronological, with same-date Tasks in shared Backlog order. Upcoming is not manually reorderable. Rows support editing, completion, and Add to Today; adding places a Task in Planned without changing shared order.

Decided: Upcoming remains an actionable Task-only list. Project target dates stay on Project surfaces rather than mixing Projects into Upcoming.

Decided: overdue Projects do not add a sidebar badge. An active, incomplete Project whose target date is before today shows a warning icon and accessible Overdue text on its Project row rather than relying on colour alone.

Decided: overdue status is informational and never changes the user-controlled Project order.

Decided: Project and Task inspectors save valid changes automatically. Text entry is coalesced after 600 milliseconds; discrete selections and Participant association changes save immediately. Navigation, close, and immediate actions flush pending changes first. Invalid or failed changes remain visible and block leaving until corrected, retried, or explicitly discarded. A near-header accessible status and Retry action make persistence visible without scrolling. Category editing retains explicit Save and Cancel.

Decided: new Project and Task inspectors begin as transient drafts. Their first valid automatic save creates the record; blank or incomplete drafts can be left without adding partial data to lists, counts, backups, search, or other projections.

Decided: the Participant picker ends with a distinct New participant action which reveals a temporary label field. Enter/Add creates and associates a unique label with the Task's next atomic save, or associates the matching existing Participant; a match already on the Task is rejected inline. Escape/Cancel returns to the picker. Rename and delete live in Settings rather than every Task inspector.

Decided: Settings is a count-free utility destination after Bin. Participant management uses standard list rows, and Recovery launches from its own Settings section rather than occupying the primary-work top bar.

Decided: New Task drafts use visible, editable context defaults. Project creation attaches and inherits; Category creation defaults to a standalone Task in that Category; Today creation defaults to Today and Planned with optional Project selection; Backlog and global creation default to standalone with a required Category.

Decided: new Tasks enter at the top of shared Backlog order and, when attached, at the end of Project-local Task order. New Projects and Categories enter at the end of their respective orders. All remain manually reorderable.

Decided: Projects provide inline rapid Task entry. Enter or Tab on a non-empty title creates an immediate attached Task with inherited Category, no optional details, and focus in a fresh field. Tab on an empty field exits normally and Escape clears unsubmitted text. Created Tasks can then be selected for full editing.

Decided: Backlog provides the same rapid-entry flow for title-only standalone Tasks. It requires an explicit Category selection, retained only for the current entry session so multiple related Tasks can be captured without creating a hidden persistent default.

## Agent interchange

Decided: [agent interchange version 1](agent-interchange-v1-contract.md) is a tool-neutral, human-reviewed planning exchange. Its snapshot includes every reusable Category and Participant, each active Project, and every non-archived, non-binned standalone or active-Project Task, including completed Tasks. Archived and Bin Project/Task content remains local; the copy preview discloses that catalogue labels can still reveal hidden context.

Decided: snapshots and proposals are strict, versioned JSON documents validated with immutable packaged JSON Schema 2020-12 resources through a closed offline catalogue. Snapshot identity is the SHA-256 digest of RFC 8785 canonical content, and the durable Workspace revision advances exactly once per actual user-data transaction. Source identity and revision are checked during Review and again while Apply retains exclusive database ownership.

Decided: version 1 proposals are ordered and use stable existing-record IDs plus backward-only typed local references for created records. Per-target mutation facets reject duplicate, contradictory, no-op, and last-write-wins changes. Project archive and Move to Bin own their complete aggregate effects. New durable identities and colours remain application-generated.

Decided: version 1 may create Categories and Participants but cannot rename, merge, recolour, reorder, or delete them. It can create and edit Projects and Tasks; change dates, Category behaviour, Participant associations, and Project relationships; complete or reopen Tasks; reorder Tasks within one Project; archive eligible active work; and explicitly move active Projects or Tasks to Bin. It cannot directly change Today or global ordering, restore hidden work, duplicate, empty Bin, permanently delete, or bypass domain validation.

Decided: Settings provides one proposal text surface populated by typing or paste, **Load from file…**, or exactly one local file dropped onto it. All paths use the same bounded strict-UTF-8 intake. dot-orbit never monitors or silently reads the clipboard or filesystem, retains a source path, watches a file, ingests meeting material, or invokes an AI provider.

Decided: validation produces one immutable plan and its complete authoritative preview. Approval is whole-proposal, session-local, and single-use. Any attempted input change invalidates the pending plan. Apply never reparses or reloads input; after a final stale check and validated encrypted recovery point, it commits the retained plan and one revision increment in one transaction or changes nothing.

Decided: optional explanations remain inert review metadata and use the existing sanitised Markdown path. Published input, output, validation, and preview limits fail closed without truncating an effect set or echoing sensitive content. Diagnostics and evidence never contain proposal/snapshot content, identifiers, digests, paths, clipboard payloads, or database values.

Decided: version 1 generates and accepts only schema version 1. Compatibility conversion begins only when a real later schema explicitly retains an earlier version. Unknown and unsupported versions fail closed. Future MCP support reuses these application boundaries and cannot introduce a second mutation or approval route.
