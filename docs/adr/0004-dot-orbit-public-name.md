# dot-orbit as the public name

- Status: Accepted
- Date: 2026-09-24

## Context

Orbit expresses the Project and Task model well, but several existing task-management products already use it. The project also benefits from a name that fits alongside dot-finance and signals its .NET implementation.

## Decision

Use `dot-orbit` as the public product and repository name. Use `DotOrbit` for .NET namespaces and `dot-orbit` for executable and package names.

## Consequences

- User-facing text, documentation, repository metadata, and future distribution listings use `dot-orbit` consistently.
- Storage and backup extensions should use a distinctive `dotorbit`-derived stem rather than the generic `.orbit` suffix.
