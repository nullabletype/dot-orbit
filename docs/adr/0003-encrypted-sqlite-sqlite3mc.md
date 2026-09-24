# Encrypted SQLite via SQLite3MC for local persistence

- Status: Accepted
- Date: 2026-09-24

## Context

dot-orbit requires encrypted-at-rest working data and automatic encrypted backups that can be restored across macOS, Linux, and Windows. The storage model is relational and includes Projects, Tasks, Categories, ordering scopes, completion history, and archive search. The project will not accept deprecated, end-of-life, or unmaintained dependencies.

## Decision

Use SQLite through `Microsoft.Data.Sqlite.Core`, encrypted by `SQLite3MC.PCLRaw.bundle` with the sqleet ChaCha20-Poly1305 cipher scheme. Working data and backups use the same portable encrypted database format. Pin the cipher and key-derivation parameters explicitly rather than relying on mutable defaults.

Generate backups from the encrypted database without a plaintext intermediate file. Write, validate, and atomically replace backup and restore candidates so interruption, a wrong password, corruption, or tampering cannot replace a known-valid file.

## Rationale

- SQLite provides transactions, indexes, incremental writes, and full-text search without introducing a service dependency.
- SQLite3MC is the maintained, MIT-licensed option already researched and selected for dot-finance after its previous packaged SQLCipher dependency became deprecated.
- A bespoke encrypted snapshot format would add cryptographic protocol and whole-file-write responsibilities that SQLite3MC already addresses.
- Operating-system credential stores alone cannot provide one user-password-protected backup that moves unchanged across all three desktop platforms.

## Consequences

- The application carries and verifies native SQLite3MC binaries for each supported runtime identifier.
- Wrong-password and damaged-data failures should be deliberately indistinguishable to avoid unreliable diagnosis or information leakage.
- The first release requires a passphrase at every launch, retains it only in process memory while unlocked, provides no recovery backdoor, and requires the current passphrase for rotation.
- Optional biometric unlock is deferred and must not replace the portable passphrase used to open encrypted backups.
- Schema migrations and recovery remain separate decisions. The behavioural requirements define the accepted backup cadence, retention policy, and configurable filesystem destination.
- Encryption protects files at rest; it does not by itself protect process memory, swap, crash dumps, filesystem snapshots, or operating-system telemetry.
