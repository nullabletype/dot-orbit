# Forward-only encrypted workspace migrations

- Status: Accepted
- Date: 2026-09-27

## Context

Schema version 1 contains the initial encrypted Category store. Version 2 adds ordered Category lookup, version 3 adds Projects and attached Tasks, and version 4 adds standalone Task membership plus a dense shared Backlog order. Opening an older supported schema must not change it until the application has exclusive access and has published and validated an encrypted recovery point. A schema-1 workspace can predate automatic-recovery configuration, so it may not have a recovery destination sidecar and cannot configure one through the session UI before it opens.

SQLite online backup cannot run from a connection while that connection has an active write transaction. Releasing all locks to create the backup would introduce a race between the inspected database, its recovery point, and the later migration.

## Decision

Use explicit, contiguous, forward-only migration steps owned by the encrypted store lifecycle module. Schema version 2 adds a non-unique `ix_categories_position` index over exactly `categories(position)`, supporting the existing ordered Category read. Schema version 3 adds Projects and attached Tasks. Schema version 4 permits a Task to have no Project only when it has an explicit Category and no Project-local position, and normalises the shared Task order to dense non-negative positions. New workspaces are created directly at version 4. Versions 1, 2, and 3 remain in the supported upgrade path.

Migration first inspects the store read-only so a newer schema is refused without a write-capable open. For an older supported schema, the migration connection enters SQLite exclusive locking mode, begins an exclusive transaction, and rechecks the schema under that lock. It rolls back that no-op transaction while retaining the connection's exclusive lock, publishes and independently validates an encrypted recovery point through SQLite backup, then begins a new exclusive transaction for the migration chain. Each numbered step advances to its exact target version and validates that version's schema shape and database integrity before the chain commits. Any failure rolls back the whole chain to the pre-migration schema.

Pre-migration points use a distinct filename and are never automatic-retention candidates. The persisted automatic-recovery directory is used when configured. If schema 1 has no configured directory, the point is placed beside the working database so the workspace is not made permanently unopenable. A configured but unavailable destination blocks migration rather than silently publishing somewhere else. A failed open exposes an action to restore any validated point that was already published. Restoration validates the historical point, takes an exclusive lock on the working database, creates and validates a distinct pre-restore safety point beside it, then restores through SQLite online backup while retaining that lock. The restored historical schema remains closed for a later migration retry.

## Consequences

- Schema changes, validation, and recovery publication remain hidden behind the Core-owned workspace-open result; SQLite types do not cross the adapter boundary.
- One exclusive connection closes both the inspection-to-backup-to-migration race and the pre-restore-to-restore race while respecting SQLite's backup transaction restriction.
- Schema-1 workspaces can upgrade without prior machine-local configuration, but their first pre-migration point may share a filesystem failure domain with the working database.
- A failed upgrade can be restored before a session exists; the passphrase needed for that single action remains only in process memory and is discarded after the attempt.
- Adding another released schema requires a contiguous migration step, version-specific validation, and a synthetic fixture upgrade test from every schema retained in the support path.
- Downgrade, arbitrary repair, cipher changes, passphrase rotation, and plaintext import remain separate decisions.
