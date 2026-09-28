# dot-orbit

dot-orbit is a personal, open-source desktop task manager built around projects, deliberate daily focus, and manual ordering.

The repository contains the first runnable Avalonia shell and the earlier interactive product prototype. Nothing in `docs/` should be read as evidence of implemented behaviour unless it is explicitly marked as implemented and verified.

## Start here

- Run `dotnet run --project src/DotOrbit.Desktop/DotOrbit.Desktop.csproj` to create or unlock the local encrypted workspace and open the desktop shell.
- Build and validate self-contained Release archives with the commands in [`docs/development/release-packaging.md`](docs/development/release-packaging.md).
- Open `prototype/index.html` in a modern desktop browser to explore the broader product direction. It is self-contained and needs no build step.
- Read `docs/product/brief.md` for the product boundary.
- Read `docs/product/requirements.md` for the current behavioural specification.
- Read `docs/design/ui-specification.md` alongside the prototype.
- Read `docs/agents/issue-tracker.md` for the current issue-readiness contract and `docs/development/agent-loop.md` for delivery.
- Follow `docs/planning/repository-setup.md` when creating the local and GitHub repositories.

## Current status

- Product requirements: maintained in `docs/product/requirements.md`; implementation work is tracked as admitted GitHub issues rather than an embedded candidate list.
- UI prototype: interactive and locally verified for the original six views; it predates the accepted Upcoming-view decision and must be updated before serving as evidence for that view.
- Application platform: Avalonia UI on .NET, targeting macOS, Linux, and Windows.
- Local persistence: encrypted workspace creation, unlock, automatic recovery and retention, manual recovery, and forward-only migration are implemented with SQLite3MC.
- Production application: the cross-platform navigation shell, encrypted workspace access and recovery, Project capture, standalone Task capture, and persisted shared Backlog ordering are implemented. Later product slices remain specified rather than implemented.
- Licence: MIT.

## Build and test

The repository pins .NET SDK 10.0.401 in `global.json`. From the repository root, run the canonical repository gate:

```sh
dotnet run --project tools/DotOrbit.Verification/DotOrbit.Verification.csproj -p:RestoreLockedMode=true
```

It performs locked restore, formatting verification, a deterministic Release build, all tests, the native desktop smoke journey, and both smoke negative controls. Add `-- --evidence --expected-sha <full-commit-sha>` to validate a detached snapshot of an exact clean commit. The same entry point runs on pinned Ubuntu, Windows, and macOS GitHub-hosted runners. See `docs/development/dependency-baseline.md` for the checked dependency and runner baseline.

## Licence

dot-orbit is licensed under the [MIT License](LICENSE).

## Contributing and security

Read [CONTRIBUTING.md](CONTRIBUTING.md) before proposing changes. Report suspected vulnerabilities privately as described in [SECURITY.md](SECURITY.md), never through a public issue.

## Name

The public product and repository name is `dot-orbit`. .NET identifiers use `DotOrbit`; executable and package names use `dot-orbit`. The name keeps the preferred Orbit concept, distinguishes the project from existing task managers called Orbit, and signals its .NET roots.
