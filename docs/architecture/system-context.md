# System context

dot-orbit is a cross-platform Avalonia/.NET desktop application implemented as a pragmatic modular monolith. Capability-oriented domain and application behaviour lives in a reusable core, real I/O is placed behind explicit seams, and Avalonia MVVM remains at the desktop presentation edge. SQLite3MC provides encrypted local persistence and encrypted, cross-platform backups. See [ADR 0006](../adr/0006-pragmatic-modular-monolith.md).

```mermaid
flowchart LR
    Owner[Owner] -->|plans and completes work| Desktop[DotOrbit.Desktop<br/>Avalonia and MVVM]
    Desktop --> Core[DotOrbit.Core<br/>domain and application capabilities]
    Desktop --> Storage[DotOrbit.Storage.Sqlite<br/>production adapter]
    Desktop --> Markdown[DotOrbit.Markdown<br/>sanitised render model]
    Desktop --> Export[DotOrbit.Export.Json<br/>versioned plaintext export adapter]
    Storage --> Core
    Storage --> Markdown
    Export --> Core
    Storage --> Store[(Encrypted SQLite3MC store)]
    Storage --> Recovery[Encrypted recovery module]
    Recovery --> BackupFolder[Configured recovery directory]
    Export --> ExportFile[User-selected plaintext JSON file]
```

## Module and dependency direction

- **`DotOrbit.Core`** contains capability-oriented domain and application slices. It depends only on the .NET base class libraries and owns the interfaces required from persistence and other I/O.
- **`DotOrbit.Storage.Sqlite`** implements the encrypted persistence interface, migrations, search, and recovery behaviour. It depends on `DotOrbit.Core`.
- **`DotOrbit.Markdown`** owns the single sanitised Markdown render model and plain-text projection used by preview, clipboard, and archive search.
- **`DotOrbit.Export.Json`** maps one transactional workspace snapshot to the documented, deterministic plaintext JSON contract and atomically publishes it to the selected local file. It depends only on `DotOrbit.Core` and owns no file picker, recovery path, schedule, or database access.
- **`DotOrbit.Desktop`** contains Avalonia views, view models, desktop integration, and the composition root. It owns destination selection, passes the selected local path to the export adapter, and wires production adapters into the application.
- **Markdown and plaintext export adapters** are consumed at the desktop, persistence, or application edge. They may depend inward on core-owned contracts, but `DotOrbit.Core` remains BCL-only and never depends on a Markdown parser, renderer, or output adapter.
- **Tests** align with the production projects: core unit tests, SQLite3MC integration tests using real temporary encrypted databases, and focused desktop tests plus supported-runtime verification.

No production module depends on the desktop presentation module. A future mobile application can provide a separate presentation and composition root over `DotOrbit.Core` without requiring the first-release desktop UI to be portable.

## Required boundaries

- **Presentation** uses Avalonia MVVM to render the seven primary views, the Bin utility, and the inspector. View models expose presentation state and translate intent but do not own domain rules or SQL.
- **Core** owns completion derivation, Category inheritance, Today membership, ordering invariants, and the application actions that change them atomically.
- **Persistence** is a core-owned seam implemented by the SQLite3MC adapter. It stores records and every independent ordering scope atomically without exposing SQL records or query providers to callers.
- **Encrypted store lifecycle** owns creation, opening, validation, migration, passphrase rotation, and closing while hiding cipher and key-derivation configuration from callers.
- **Recovery** automatically creates encrypted, validated, cross-platform recovery points without a plaintext intermediate copy. Its destination is a user-selected filesystem directory that may be managed by an external sync service.
- **Plaintext export** is a separate, explicit manual operation to a user-selected JSON file. It is unencrypted, follows the [versioned public contract](../data/plaintext-export-v3.md), does not use automatic-backup retention, and does not default to the configured recovery directory.
- **Synchronisation** is outside the first-release system boundary. A synced backup folder does not provide live record synchronisation or conflict resolution.
- **Interchange** is planned but is not part of the current implementation graph and is not scaffolded in advance. Core owns snapshot/proposal models, semantic simulation, the immutable execution plan, authoritative preview, and atomic apply contract. SQLite owns revision-bearing reads, checked revision advancement, and recovery-backed exclusive application. A distinct interchange JSON adapter owns strict parsing, deterministic generation, JCS identity, immutable packaged schemas, and bounded failures; it is not `DotOrbit.Export.Json`. Desktop owns explicit copy, paste/typing, proposal-file picker and single-file drop, review, accessibility, confirmation, and composition. Meeting-material or transcript file access and AI-provider integration remain outside the application boundary. Future MCP is a transport and authentication adapter only and cannot bypass in-app review and approval.
- **Markdown** treats all source as untrusted and produces one sanitised render model for preview, rich clipboard output, archive-search text, and plain-text fallback. Raw HTML and automatic remote-image loading are disabled.
- **Search** indexes archived Project and Task content.

## Planned agent-interchange boundary

The accepted version 1 boundary is described in [ADR 0011](../adr/0011-human-reviewed-agent-interchange-boundary.md) and the [agent-interchange contract](../planning/agent-interchange-v1-contract.md). The shapes below are responsibilities, not advance production scaffolding:

```mermaid
flowchart LR
    External[External planning tool] -->|structured proposal| Desktop[Desktop intake and review]
    Desktop --> Json[Interchange JSON adapter]
    Json --> Core[Core plan and preview]
    Core --> Storage[SQLite revision and atomic apply]
    Storage --> Recovery[Validated encrypted recovery]
    Core --> Generator[Core snapshot source]
    Generator --> Json
    Json -->|snapshot and instructions| Desktop
    MCP[Future local MCP transport] --> Json
    MCP -. cannot approve or apply .-> Desktop
```

The desktop and a future MCP transport share the same JSON and Core-owned application contracts. Neither transport owns schema semantics, domain validation, preview calculation, recovery, or mutation authority.

## Deferred architecture questions

- Optional post-first-release biometric unlock and operating-system credential-store integration.
- Agent-interchange compatibility and vocabulary evolution after a real second schema version exists.
