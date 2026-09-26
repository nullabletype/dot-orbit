# Dependency and runner baseline

Checked: 2026-09-26

This file records the support check for the runnable desktop shell, encrypted workspace lifecycle, and self-contained release packaging. Package lock files remain the reproducible record of the complete transitive graph.

## Runtime and SDK

- .NET 10 is the active LTS release through 2028-11-14. The repository pins SDK 10.0.401, which includes the current 10.0.12 runtime security patch. Source: [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy) and [.NET 10 downloads](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).
- `net10.0` is the only target framework in this slice. Preview SDKs and runtimes are not used.

## Direct packages

- Avalonia desktop, Fluent theme, Inter font, and headless xUnit integration are pinned to 12.1.3. Avalonia 12.1 lists Windows 11 24H2, macOS 26, Ubuntu, and Debian 13 among its supported desktop targets. Source: [Avalonia supported platforms](https://docs.avaloniaui.net/docs/supported-platforms) and the [Avalonia.Desktop package](https://www.nuget.org/packages/Avalonia.Desktop/12.1.3).
- `xunit.v3` is pinned to 3.2.2 because Avalonia.Headless.XUnit 12.1.3 is compiled against the xUnit 3.2 discovery API. xUnit 4.0.1 was evaluated and rejected after it caused `MissingMethodException` during Avalonia test discovery. This pin should be revisited when Avalonia publishes compatible headless integration.
- `Microsoft.Data.Sqlite.Core` is pinned to 10.0.12, the current stable .NET 10 servicing release. The `.Core` package is required so the application can supply one SQLite native bundle rather than pulling in the default SQLitePCLRaw bundle. Source: [Microsoft.Data.Sqlite.Core 10.0.12](https://www.nuget.org/packages/Microsoft.Data.Sqlite.Core/10.0.12).
- `SQLite3MC.PCLRaw.bundle` is pinned to 2.4.0, the current maintained NuGet release, published in July 2026 with SQLite3 Multiple Ciphers 2.4.0 based on SQLite 3.53.4. It is MIT licensed and supplies the native sqleet ChaCha20-Poly1305 implementation for the supported desktop runtimes. Source: [SQLite3MC.PCLRaw.bundle 2.4.0](https://www.nuget.org/packages/SQLite3MC.PCLRaw.bundle/2.4.0) and [SQLite3 Multiple Ciphers sqleet parameters](https://utelle.github.io/SQLite3MultipleCiphers/docs/ciphers/cipher_chacha20/).
- The upstream SQLite3 Multiple Ciphers source project has since tagged 2.5.1 while the current .NET native bundle remains 2.4.0; both use SQLite 3.53.4. The application depends on the maintained .NET bundle and must re-evaluate this gap when a later bundle is published or the native changes affect a supported runtime. Source: [SQLite3 Multiple Ciphers 2.5.1](https://github.com/utelle/SQLite3MultipleCiphers/releases/tag/v2.5.1).
- Central package management and per-project `packages.lock.json` files pin the complete graph. NuGet audit is enabled for all dependencies and vulnerability severities fail the build.
- The complete locked graph was checked with `dotnet package list --vulnerable --include-transitive` and `--deprecated --include-transitive`; NuGet reported no known vulnerable or deprecated direct or transitive packages on 2026-09-26. Transitive packages inherit their support boundary from the current Avalonia and xUnit release lines and must be rechecked with those direct dependencies.

## Continuous integration

- Runner labels are explicit: `ubuntu-24.04`, `windows-2025`, and `macos-26`. These were listed as generally available by the [GitHub runner-images project](https://github.com/actions/runner-images/blob/main/README.md) on the check date.
- Release packaging uses `macos-26` for Apple Silicon and `macos-26-intel` for Intel. The runner-images catalogue lists those as the current macOS 26 ARM64 and x64 labels respectively.
- `actions/checkout` is pinned to the full commit SHA for v7.0.1.
- `actions/setup-dotnet` is pinned to the full commit SHA for v6.0.0 and installs SDK 10.0.401 exactly.
- The manual package upload uses `actions/upload-artifact` v7.0.1 pinned to commit `043fb46d1a93c77aae656e7c1c64a875d1fc6a0a`. This is the current supported Node 24 release; pull-request runs never execute the upload step.
- The workflow performs locked restore, formatting verification, Release build, and tests. It uploads no artifacts.
- The same explicit matrix launches the real Release-built desktop application. Linux uses the `xvfb` package included in the pinned Ubuntu 24.04 runner image; macOS and Windows use their native desktop environments. The smoke runner adds no package or action dependency and uploads no artifacts.
- The native journey locates standard Avalonia controls by their automation IDs and checks their built-in automation peers before routing keyboard input. Avalonia exposes those peers through UI Automation on Windows, NSAccessibility on macOS, and AT-SPI2 on Linux. Source: [Avalonia accessibility](https://docs.avaloniaui.net/docs/app-development/accessibility) and the [Ubuntu 24.04 runner software inventory](https://github.com/actions/runner-images/blob/main/images/ubuntu/Ubuntu2404-Readme.md).

## Packaging tools

- The repository packager targets the pinned .NET 10 SDK and uses only supported base-class-library APIs for Zip, POSIX tar, GZip, SHA-256, and JSON. It introduces no additional package dependency or floating host tool.
- Package restore is locked for all four explicit runtime identifiers. The final archive contains one self-contained executable plus the repository licence, third-party notices, and the generated integrity manifest.
- Relevant pull requests assemble, inspect, extract, and run every archive on its matching architecture. Only a manual dispatch uploads the final validated archive, using the action pin recorded above.

## Review trigger

Recheck this baseline when changing any dependency, SDK, runner image, GitHub Action, target framework, or packaging tool, and before a supported release.
