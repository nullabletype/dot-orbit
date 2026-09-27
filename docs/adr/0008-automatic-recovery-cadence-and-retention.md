# ADR 0008: Automatic recovery cadence and retention

## Status

Accepted.

## Context

The encrypted recovery module already creates and validates portable SQLite3MC recovery points before atomic publication. Automatic recovery additionally needs a durable destination, deterministic hourly coalescing, and bounded retention without allowing automatic maintenance to delete manual or pre-operation safety points.

Schema version 1 predates automatic-recovery configuration, while numbered schema migrations and their required pre-migration recovery are a later delivery slice. Storing machine filesystem configuration in the encrypted schema now would either change a released schema without a migration or make automatic recovery depend circularly on the migration work.

## Decision

Store a versioned, workspace-specific automatic-recovery state sidecar beside the working database. The sidecar contains only the canonical recovery directory, a random recovery-set identifier, a monotonic committed-change generation, and the UTC instant and generation of a pending committed change. It never contains the passphrase or workspace content, and its values are never written to diagnostics. Publish state changes through a flushed same-directory candidate so a failed update leaves the previous state usable.

A committed stored-data change durably marks recovery pending. The first pending change creates a validated encrypted recovery point immediately when none exists. Later changes coalesce behind a one-shot `TimeProvider` timer until one hour after the most recent successful validated automatic point. A timestamp ahead of the current clock is treated as current, so clock rollback cannot postpone recovery by more than one hour. Closing the application cancels work that has not started and waits for an in-flight recovery operation to drain before clearing the in-memory passphrase; persisted pending state resumes on the next unlock. A creation failure leaves pending state for the next commit or unlock and does not start an unbounded retry loop. Failed or rolled-back data changes never notify the recovery module.

Automatic points use a strict, Windows-portable filename containing the workspace recovery-set identifier, the committed-change generation they cover, and UTC timestamp. Recovery clears pending work only when a validated point from the same set carries that generation or a later one; timestamp equality can never make an older snapshot cover a later commit. On unlock, the generation is reconciled with validated points so an interrupted sidecar update cannot cause a later commit to reuse an already-published generation. If the directory is temporarily unavailable, reconciliation is retried before allocating the next generation; continued unavailability rotates to a fresh recovery-set identity before persisting the new pending obligation. If marking a commit pending cannot be persisted, a configured workspace attempts an immediate recovery rather than leaving the only evidence in volatile scheduled work. Retention considers only exact-shape points from the current recovery set that validate with the current workspace passphrase. Workspaces can therefore share a destination and passphrase without affecting one another's cadence or retention. The policy keeps the union of the newest point in each of the 24 newest UTC-hour buckets, 30 newest UTC-day buckets, and 12 newest UTC-month buckets. Bucket overlap is deduplicated. Invalid, corrupt, unknown, lookalike, manual, and pre-operation recovery files are not deletion candidates. Pruning begins only after a new point is validated and published, deletes surplus files individually from oldest to newest, and stops on failure; interruption can therefore leave extra valid points but cannot remove the newest or final known-valid point.

The SQLite storage module owns an internal committed-change notification boundary. The first user-data write slice must call it only after its transaction commits. Session transactions, recovery snapshots, restores, timer callbacks, and close share one serialization gate, so a committed transaction cannot race its recovery snapshot or deadlock against session shutdown. SQLite connections and generic transaction delegates do not cross the Core boundary.

## Consequences

- Automatic cadence and retention are deterministic across local time-zone changes and testable with `TimeProvider`.
- The configured recovery directory survives application launches without prematurely changing schema version 1.
- Separate workspace recovery sets remain isolated even when they share a directory and passphrase.
- A copied workspace database does not carry machine-specific destination configuration unless its sidecar is copied deliberately.
- Filesystem or validation failures favour retaining too many encrypted recovery points over deleting uncertain data.
- Application code added after this decision must route successful stored-data commits through the storage module's notification boundary; bypassing it would bypass automatic recovery.
