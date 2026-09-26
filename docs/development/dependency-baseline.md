# Dependency and runner baseline

Checked: 2026-09-26

This file records the support check for the first runnable desktop shell. Package lock files remain the reproducible record of the complete transitive graph.

## Runtime and SDK

- .NET 10 is the active LTS release through 2028-11-14. The repository pins SDK 10.0.401, which includes the current 10.0.12 runtime security patch. Source: [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy) and [.NET 10 downloads](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).
- `net10.0` is the only target framework in this slice. Preview SDKs and runtimes are not used.

## Direct packages

- Avalonia desktop, Fluent theme, Inter font, and headless xUnit integration are pinned to 12.1.3. Avalonia 12.1 lists Windows 11 24H2, macOS 26, Ubuntu, and Debian 13 among its supported desktop targets. Source: [Avalonia supported platforms](https://docs.avaloniaui.net/docs/supported-platforms) and the [Avalonia.Desktop package](https://www.nuget.org/packages/Avalonia.Desktop/12.1.3).
- `xunit.v3` is pinned to 3.2.2 because Avalonia.Headless.XUnit 12.1.3 is compiled against the xUnit 3.2 discovery API. xUnit 4.0.1 was evaluated and rejected after it caused `MissingMethodException` during Avalonia test discovery. This pin should be revisited when Avalonia publishes compatible headless integration.
- Central package management and per-project `packages.lock.json` files pin the complete graph. NuGet audit is enabled for all dependencies and vulnerability severities fail the build.
- The complete locked graph was checked with `dotnet package list --vulnerable --include-transitive` and `--deprecated --include-transitive`; NuGet reported no known vulnerable or deprecated direct or transitive packages on 2026-09-26. Transitive packages inherit their support boundary from the current Avalonia and xUnit release lines and must be rechecked with those direct dependencies.

## Continuous integration

- Runner labels are explicit: `ubuntu-24.04`, `windows-2025`, and `macos-26`. These were listed as generally available by the [GitHub runner-images project](https://github.com/actions/runner-images/blob/main/README.md) on the check date.
- `actions/checkout` is pinned to the full commit SHA for v7.0.1.
- `actions/setup-dotnet` is pinned to the full commit SHA for v6.0.0 and installs SDK 10.0.401 exactly.
- The workflow performs locked restore, formatting verification, Release build, and tests. It uploads no artifacts.

## Review trigger

Recheck this baseline when changing any dependency, SDK, runner image, GitHub Action, target framework, or packaging tool, and before a supported release.
