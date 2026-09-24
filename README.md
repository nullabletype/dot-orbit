# dot-orbit

dot-orbit is a personal, open-source desktop task manager built around projects, deliberate daily focus, and manual ordering.

The repository is currently at the specification and interactive-prototype stage. Nothing in `docs/` should be read as evidence of a production implementation unless it is explicitly marked as implemented and verified.

## Start here

- Open `prototype/index.html` in a modern desktop browser. It is self-contained and needs no build step.
- Read `docs/product/brief.md` for the product boundary.
- Read `docs/product/requirements.md` for the current behavioural specification.
- Read `docs/design/ui-specification.md` alongside the prototype.
- Read `docs/development/agent-loop.md` before turning the specification into issues.
- Follow `docs/planning/repository-setup.md` when creating the local and GitHub repositories.

## Current status

- Product requirements: drafted from the design conversation.
- UI prototype: interactive and locally verified.
- Application platform: Avalonia UI on .NET, targeting macOS, Linux, and Windows.
- Local persistence: SQLite encrypted with SQLite3MC; automatic backup policy remains to be specified.
- Production application: not started.
- Licence: MIT.

## Licence

dot-orbit is licensed under the [MIT License](LICENSE).

## Name

The public product and repository name is `dot-orbit`. .NET identifiers use `DotOrbit`; executable and package names use `dot-orbit`. The name keeps the preferred Orbit concept, distinguishes the project from existing task managers called Orbit, and signals its .NET roots.
