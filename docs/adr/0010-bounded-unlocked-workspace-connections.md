# Bounded unlocked-workspace connection reuse

- Status: Accepted
- Date: 2026-10-09

## Context

ADR 0007 disables SQLite provider pooling so an encrypted workspace has an explicit connection and passphrase lifetime. The original storage coordinator therefore opened, keyed, configured and closed a physical SQLite3MC connection for every ordinary read and write. Windows measurements after the recovery-path correction showed that repeated encrypted connection setup dominated the remaining command time.

Microsoft.Data.Sqlite connections, commands and readers are not safe for concurrent use. The application already serializes workspace transactions, automatic recovery callbacks and session close through one gate. Some maintenance workflows also depend on connection lifetime: Empty Bin retains an exclusive lock across recovery creation, while passphrase rotation and restore must close every working-database handle before replacing the file.

One reusable read/write connection would minimise handles, but it would remove the existing file-mode read-only enforcement or require mutable `PRAGMA query_only` state. Provider pooling would make physical handle lifetime implicit and extend it beyond the unlocked session unless every close, replacement and failure path also cleared the correct pool.

## Decision

An unlocked current-schema workspace session owns exactly two configured, pooling-disabled SQLite3MC connections:

- the read-only connection that authenticated and validated the current schema; and
- one read/write connection opened only after that validation succeeds.

Ordinary reads reuse the read-only connection and ordinary writes reuse the read/write connection. Both sit behind the existing re-entrant session gate and are never used concurrently. Every operation creates a short transaction, fully materialises its result, and disposes its commands, readers and transaction before the gate admits the next operation. SQLite connection and transaction types remain internal to the storage adapter.

Connection-role exceptions remain explicit and operation-bounded. Migration, default-workspace adoption, Empty Bin's retained exclusive lock, backup destinations, independent validation and workspace replacement use separately owned short-lived connections. A retained session connection may act as a backup source only while the session gate is held and no transaction, command or reader is active. Switching a retained connection into exclusive locking mode is prohibited.

Session disposal drains the operation inside the gate, closes both retained connections, stops recovery scheduling, and then clears passphrase references. Passphrase rotation and restore close the original session before replacing the working database and return a newly opened session when successful. Pooling, shared-cache mode, WAL and parallel connection use are not introduced by this decision.

## Consequences

- Ordinary warm reads and writes no longer repeat encrypted physical connection setup while preserving true read-only enforcement.
- Unlock performs one additional read/write open after successful current-schema validation. Invalid, damaged, wrong-passphrase and unsupported-newer-schema stores never receive a session read/write connection.
- The session has a small, auditable connection budget and a deterministic end that matches its in-memory passphrase lifetime.
- Every caller, including future local transports such as MCP, must enter through the serialized application/storage boundary rather than acquire a SQLite connection.
- Exclusive maintenance and file-replacement tests remain necessary on macOS, Windows and Linux because a successful ordinary read/write test cannot prove handle-lifetime correctness.
- If future requirements introduce multiple simultaneously unlocked workspaces or genuinely parallel database reads, they require a separate concurrency, encryption-lifetime and replacement decision rather than silently enabling pooling.
