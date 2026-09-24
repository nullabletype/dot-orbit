# Local-only first release with portable encrypted backups

- Status: Accepted
- Date: 2026-09-24

## Context

dot-orbit may gain a mobile client or multi-device synchronisation later, but synchronisation would introduce identity, conflict resolution, deletion propagation, and partial-failure behaviour before the core personal workflow has been proven. Users still need off-device recovery and a way to move their data between supported desktop platforms.

## Decision

The first release stores and edits one local data set and does not automatically synchronise or merge application data. Encrypted backups are the cross-platform recovery and portability mechanism. Their destination is configurable and may be a filesystem directory managed by an external cloud-sync service, but dot-orbit does not integrate with or depend on that provider.

Use stable identifiers for domain records and keep persistence behind an application boundary so future synchronisation can be designed without replacing the domain model.

## Consequences

- Opening the same backup independently on multiple devices creates divergent local data sets; dot-orbit will not merge them in the first release.
- Backup generation must publish only complete, validated recovery points to the configured destination.
- Cloud-provider availability, authentication, transfer status, version history, and retention remain outside dot-orbit's responsibility.
