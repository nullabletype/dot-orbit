# Pragmatic modular monolith with explicit I/O seams

- Status: Accepted
- Date: 2026-09-25

## Context

dot-orbit is a local-first desktop application with one domain context and several behaviours that must change atomically: Project and Task relationships, independent manual orders, Today placement, Category inheritance, Archive, and Bin. Its highest-risk implementation concerns are encrypted SQLite lifecycle, forward-only migrations, and recoverable encrypted backups.

The first release targets Avalonia on macOS, Linux, and Windows. A mobile application may be added later, but the initial desktop application must not carry speculative synchronisation, shared-user-interface, or mobile-platform abstractions. The architecture must also make domain behaviour inexpensive to unit test and storage and encryption behaviour possible to verify against the real implementation.

## Decision

Build dot-orbit as a pragmatic modular monolith. Use ports and adapters at real I/O seams, capability-oriented vertical slices within the reusable core, and MVVM only at the Avalonia presentation edge.

The initial production projects are:

- `DotOrbit.Core` — domain and application behaviour organised by capabilities such as Projects, Tasks, Today, Categories, Archive, and Bin. It depends only on the .NET base class libraries and exposes small interfaces for application commands, results, and query projections.
- `DotOrbit.Markdown` — the single sanitised Markdown render model and its plain-text projection, shared by desktop preview, clipboard, and archive search.
- `DotOrbit.Storage.Sqlite` — the SQLite3MC adapter, including mappings, atomic transactions, search indexes, schema migrations, encrypted store lifecycle, and encrypted backup and restore implementation.
- `DotOrbit.Desktop` — Avalonia views and view models plus the desktop composition root, file selection, clipboard, and application lifecycle integration.

Tests align with these projects: core unit tests, real SQLite3MC storage integration tests using temporary files, and focused desktop view-model, headless, and supported-runtime checks. Markdown rendering and explicit plaintext export are separate adapters consumed at the desktop or application edge; neither is part of `DotOrbit.Core`. Additional production projects are introduced only when behaviour is substantial enough to form a deep module with a small interface; deferred features are not scaffolded in advance.

Dependencies point inward:

```text
DotOrbit.Desktop --------> DotOrbit.Core
        |                        ^
        +--> DotOrbit.Markdown   |
        |             ^          |
        +--> DotOrbit.Storage.Sqlite
                      |
                      +----------+
```

`DotOrbit.Core` never references Avalonia or SQLite. `DotOrbit.Storage.Sqlite` implements core-owned persistence needs. `DotOrbit.Desktop` owns composition and connects the application module to the production adapters. A future mobile application can provide another presentation and composition root while reusing the core and, where suitable, the storage adapter; desktop user-interface reuse is not required.

The initial deep modules are:

- **Workspace application module** — owns domain actions and their invariants, including atomic changes across relationships and ordering scopes. Its interface does not expose mutable entities, SQL records, or `IQueryable`.
- **Encrypted store lifecycle module** — owns create, open, validate, migrate, rotate, and close behaviour. Its implementation hides cipher and key-derivation configuration, exclusive migration locking, secret handling, and failure classification.
- **Recovery module** — owns creation, validation, retention, publication, and restoration of encrypted recovery points. SQLite backup mechanics and filesystem publication remain implementation details.
- **Plaintext export adapter** — explicitly and manually writes the versioned unencrypted export selected by the user. It is separate from encrypted recovery, does not participate in automatic backup retention, and does not default to the recovery destination.
- **Markdown module** — a separate adapter accepts untrusted Markdown and produces one sanitised render model used for preview, rich copy, archive-search text, and plain-text fallback. It is consumed at the desktop, persistence, or application edge and may depend inward on core-owned contracts if needed; `DotOrbit.Core` never depends on a Markdown parser or renderer.

Interfaces are introduced where behaviour genuinely varies or callers must be isolated from I/O. Local-substitutable dependencies use their real implementation in integration tests where practical: SQLite3MC tests use temporary encrypted databases and recovery tests use temporary directories. Built-in seams such as `TimeProvider` are preferred to application-specific wrappers. Internal fault-injection seams may be used for interruption testing without becoming part of a module's external interface.

## Consequences

- Domain and application rules can be tested without Avalonia, SQLite, or an operating-system UI.
- Encryption, migration, backup, restoration, search, and persistence claims require integration coverage against the real SQLite3MC adapter; mocks alone are not acceptance evidence.
- Plaintext manual export is tested and presented as a separate operation from encrypted recovery; it never inherits the recovery destination or encryption claims.
- View models translate user intent and expose presentation state but do not own domain rules or execute SQL.
- One behavioural change should remain local to a capability-oriented slice instead of being spread across generic controller, service, and repository layers.
- Cross-module changes that affect domain state are coordinated through the Workspace application module and committed atomically by the storage adapter.
- The architecture does not use microservices, event sourcing, an internal command bus for every action, infrastructure-heavy CQRS, generic repositories, repositories per entity, or one project or interface per class.
- Synchronisation, agent interchange, a plugin system, biometric unlock, and mobile-specific abstractions remain deferred until their requirements create real seams.
- A future mobile client can reuse the core without requiring the desktop interface to be designed as a lowest-common-denominator cross-platform UI.
