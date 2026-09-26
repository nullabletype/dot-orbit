# dot-orbit

dot-orbit is a personal, open-source desktop task manager built around projects, deliberate daily focus, and manual ordering.

The repository contains the first runnable Avalonia shell and the earlier interactive product prototype. Nothing in `docs/` should be read as evidence of implemented behaviour unless it is explicitly marked as implemented and verified.

## Start here

- Run `dotnet run --project src/DotOrbit.Desktop/DotOrbit.Desktop.csproj` to open the desktop shell.
- Open `prototype/index.html` in a modern desktop browser to explore the broader product direction. It is self-contained and needs no build step.
- Read `docs/product/brief.md` for the product boundary.
- Read `docs/product/requirements.md` for the current behavioural specification.
- Read `docs/design/ui-specification.md` alongside the prototype.
- Read `docs/development/agent-loop.md` before turning the specification into issues.
- Follow `docs/planning/repository-setup.md` when creating the local and GitHub repositories.

## Current status

- Product requirements: drafted from the design conversation.
- UI prototype: interactive and locally verified for the original six views; it predates the accepted Upcoming-view decision and must be updated before serving as evidence for that view.
- Application platform: Avalonia UI on .NET, targeting macOS, Linux, and Windows.
- Local persistence: SQLite encrypted with SQLite3MC, with the automatic backup and retention policy specified.
- Production application: the cross-platform navigation shell is implemented; domain data and persistence are not.
- Licence: MIT.

## Build and test

The repository pins .NET SDK 10.0.401 in `global.json`. From the repository root:

```sh
dotnet restore DotOrbit.slnx --locked-mode
dotnet format DotOrbit.slnx --no-restore --verify-no-changes
dotnet build DotOrbit.slnx --configuration Release --no-restore
dotnet test --solution DotOrbit.slnx --configuration Release --no-build
```

The same gate runs on pinned Ubuntu, Windows, and macOS GitHub-hosted runners. See `docs/development/dependency-baseline.md` for the checked dependency and runner baseline.

## Licence

dot-orbit is licensed under the [MIT License](LICENSE).

## Contributing and security

Read [CONTRIBUTING.md](CONTRIBUTING.md) before proposing changes. Report suspected vulnerabilities privately as described in [SECURITY.md](SECURITY.md), never through a public issue.

## Name

The public product and repository name is `dot-orbit`. .NET identifiers use `DotOrbit`; executable and package names use `dot-orbit`. The name keeps the preferred Orbit concept, distinguishes the project from existing task managers called Orbit, and signals its .NET roots.
