# Avalonia and .NET for the cross-platform application

- Status: Accepted
- Date: 2026-09-24

## Context

dot-orbit must support macOS, Linux, and Windows in its first release. A mobile application may be wanted later, but mobile delivery is not part of the first release. The application needs a desktop-first interface without making the domain and persistence layers depend on a single operating system.

## Decision

Use Avalonia UI on .NET for the desktop application. Keep the domain and persistence layers independent of Avalonia so a future mobile client can reuse them without constraining the initial desktop experience.

## Consequences

- macOS, Linux, and Windows are required verification targets from the first runnable slice.
- Mobile compatibility influences boundaries but does not add mobile acceptance criteria to the first release.
- Use the current .NET LTS release on its latest supported patch.
- Support serviced Windows 11 releases, current supported macOS releases covered by Avalonia, Ubuntu LTS, and current Debian stable.
- X11 is the supported initial Linux display server. Wayland and other Linux distributions are best-effort until Avalonia's native support reaches the required maturity.
- Re-evaluate and record exact supported OS versions and architectures for every release rather than making an indefinite version promise.
