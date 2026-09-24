# Open questions

These decisions should be resolved before their dependent implementation issues are marked `ready-for-agent`.

## Product

- When does completed work move from Completed to Archive?
- Can a Project or Task be archived manually while incomplete?
- Are Today selections cleared automatically, carried forward, or always manual?
- Are participants free text, local contacts, or reusable named records?
- Are recurring Tasks part of the first release?

## Platform

Decided: the first release supports macOS, Linux, and Windows using Avalonia UI on .NET. Mobile is a possible future client, not a first-release target. Local storage and backups are encrypted at rest. The first release is local-only and does not synchronise or merge application data; stable identifiers and a persistence boundary preserve the option to design synchronisation later.

## Data and recovery

- What export formats are required?
- Is deletion immediate, recoverable, or archive-only?
- How are schema migrations tested and recovered?
- What threat model and operating-system credential-store design would be required for optional biometric unlock after the first release?

Decided: after stored data changes, routine backups are coalesced to at most one per hour. A validated recovery backup is required before migrations, imports, and restores. Retain 24 hourly, 30 daily, and 12 monthly backups, and never prune the last known-valid backup. The backup destination is configurable and may be a directory managed by an external cloud-sync tool; dot-orbit does not integrate with that provider.

Decided: the first release requires the encryption passphrase on every launch, holds it only in process memory while unlocked, provides no password recovery or backdoor, and requires the current passphrase for rotation. Biometric unlock is deferred beyond the first release.

## Open source

- What contribution policy is appropriate before the architecture stabilises?

Decided: the public product and repository name is `dot-orbit`; .NET identifiers use `DotOrbit`. The project is licensed under the MIT License.
