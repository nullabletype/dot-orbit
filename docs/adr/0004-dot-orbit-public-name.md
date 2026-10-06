# dot-orbit as the public name

- Status: Accepted
- Date: 2026-09-24

## Context

Orbit expresses the Project and Task model well, but several existing task-management products already use it. The project also benefits from a name that fits alongside dot-finance and signals its .NET implementation.

## Decision

Use `dot-orbit` as the public product and repository name. Use `DotOrbit` for .NET namespaces and `dot-orbit` for executable and package names.

## Consequences

- User-facing text, documentation, repository metadata, and future distribution listings use `dot-orbit` consistently.
- The live encrypted workspace uses the concise `.orb` extension. This is an intentional dot-orbit abbreviation rather than a claim of global uniqueness: niche third-party uses already exist, and dot-orbit does not define or register a new media type in this slice. Portable recovery files keep the distinctive `.dotorbit-recovery` suffix so a recovery point cannot be mistaken for the live workspace.
