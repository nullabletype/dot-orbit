# Release packaging

Issue #3 adds reproducible self-contained Release archives for `win-x64`, `linux-x64`, `osx-x64`, and `osx-arm64`. Issue #73 adds automatic archive retention for `main` and version tags, plus unpublished draft releases. Neither slice adds installers, signing, notarisation, tag creation, or automatic publication.

## Build one package

Run a locked restore from the repository root, then invoke the checked-in .NET packager with one supported runtime identifier:

```sh
dotnet restore DotOrbit.slnx --locked-mode
dotnet run --project tools/DotOrbit.Packaging/DotOrbit.Packaging.csproj --configuration Release --no-restore -- --repository-root . --runtime osx-arm64 --version 0.0.0-local --no-restore
```

The packager requires a canonical SemVer value, passes it to the Release publish, publishes a self-contained single-file application, fails if the publish directory contains anything except the expected app executable and optional root-level PDBs, and assembles a separate archive containing only:

- the runtime-specific `dot-orbit` executable;
- `LICENSE.txt`;
- `THIRD-PARTY-NOTICES.md` and the complete bundled .NET, Apache-2.0, Inter OFL-1.1, ANGLE, SkiaSharp, and HarfBuzzSharp licence/notice texts;
- `package-manifest.json`, which records the version, runtime, Release configuration, self-contained status, size, and SHA-256 digest of every payload file.

The checked-in multi-resolution `src/DotOrbit.Desktop/Assets/dot-orbit.ico` is derived from the canonical `assets/orbit-mark.svg`. Its source hash, output hash, and required 16–256 pixel frame set are recorded in `assets/orbit-mark.native-icon.json`. Packaging validates that provenance, the icon structure, and the desktop project metadata before publishing. The icon is then embedded into the application single file: it is never a loose archive entry.

Windows uses Zip; Linux and macOS use compressed tar archives so the executable bit is retained. The tool reopens the completed archive, rejects unexpected entries, extracts it to `artifacts/package-smoke/<rid>`, and verifies every manifested size and digest. Raw publish directories are never upload inputs.

## Smoke the extracted package

Run the self-contained apphost directly, matching the archive's target runtime and architecture:

```sh
# macOS
DOTORBIT_PACKAGE_SMOKE_VERSION=0.0.0-local ./artifacts/package-smoke/osx-arm64/dot-orbit --package-smoke

# Linux/X11
DOTORBIT_PACKAGE_SMOKE_VERSION=0.0.0-local xvfb-run --auto-servernum --auth-file=artifacts/package-smoke/linux-x64/dot-orbit-xvfb.auth ./artifacts/package-smoke/linux-x64/dot-orbit --package-smoke

# Windows PowerShell
$env:DOTORBIT_PACKAGE_SMOKE_VERSION = "0.0.0-local"
./artifacts/package-smoke/win-x64/dot-orbit.exe --package-smoke
```

The package smoke first requires `DOTORBIT_PACKAGE_SMOKE_VERSION` to equal the executable's informational version. It then creates a synthetic encrypted SQLite3MC `workspace.orb` in a temporary directory, closes it, reopens and validates it, restores the external portable recovery fixture when `DOTORBIT_PACKAGE_SMOKE_RECOVERY` names it, removes the temporary workspace, then exercises the real Avalonia Today-to-Archive keyboard journey and closes cleanly. Its diagnostics contain fixed phase and result metadata only.

These packages are portable archives rather than installers or platform application bundles. They have no installation hook or package identity with which to register the `.orb` file association, so opening a workspace by double-clicking it is not part of this packaging slice. No cosmetic MIME metadata is included in the archive.

## Native application icon behaviour

- Windows embeds the multi-resolution icon in `dot-orbit.exe`; Explorer, executable properties, window chrome, the application switcher, and the taskbar can use it. Packaging also inspects the published PE resources and fails if the icon group or any required resolution is absent.
- Linux receives the same icon as Avalonia's embedded default for every top-level window. X11 window managers can use it for window chrome, switchers, and running-application surfaces, but launcher/menu artwork and desktop-entry identity are outside this portable archive format and vary by desktop environment.
- macOS also carries the embedded Avalonia window-icon resource, but the current bare executable has no `.app` bundle, `Info.plist`, or `.icns` artwork. Finder and Dock application artwork therefore remain host defaults until a separate bundle slice is implemented.

This distinction is intentional: the Windows executable icon and Avalonia running-window icon are part of the application binary, while installer artwork, Linux launcher integration, and macOS bundle artwork remain separate packaging concerns.

Before packaging, every runtime job restores the same checked-in synthetic encrypted recovery fixture through the storage integration test. After packaging, the extracted self-contained app restores those identical external bytes again through its bundled runtime before continuing the native UI journey. This proves Windows x64, Linux x64, macOS x64, and macOS arm64 compatibility rather than only proving separate same-host databases. The fixture is not included in the release archive; its provenance, public test-only passphrase, and pinned digest are recorded beside it under `tests/DotOrbit.Storage.Sqlite.Tests/Fixtures`.

## CI and artifact boundary

`.github/workflows/release-packages.yml` builds and smokes all four packages for relevant pull requests and manual runs without uploading anything. A push to `main` uploads the four already-validated archives as seven-day workflow artifacts. A pushed `v`-prefixed SemVer 2.0 tag, including a prerelease tag, can do the same only when the tagged commit is contained in `origin/main`. GitHub's glob trigger is deliberately treated as a broad candidate filter; the tested release control validates the complete tag grammar and ancestry before the native package jobs start.

Every upload input is the final allowlisted archive itself, not a publish directory or a second archive containing evidence. Pull requests and feature branches therefore cannot retain package artifacts, even when their packaging matrix passes. `main` builds use a traceable `0.0.0-main.<run>+<sha>` package version; other non-release validation uses `0.0.0-ci.<run>+<sha>`. A tagged build removes the `v` prefix, disables the SDK's default source-revision suffix, and records that exact SemVer in the executable and package manifest.

## Draft releases

After all four native jobs for a valid tag pass, a separate job downloads exactly those four workflow artifacts and calls the GitHub Releases API with a job-scoped `contents: write` token. It verifies that the tag still resolves to the workflow commit, asks GitHub to generate the title and changelog since the previous release, and creates an unpublished draft with the four archives attached. SemVer prerelease tags produce prerelease drafts.

A rerun may refresh generated notes and replace those four assets only while the release remains a draft. The automation fails if the tag resolves elsewhere, the asset set changes, or a release for the tag has already been published. It never creates a tag or publishes a release. Before a maintainer publishes a draft, the selected commit still requires the canonical exact-SHA gate and the full supported-release evidence; a successful packaging workflow alone is not release approval.

PDB generation remains disabled. Portable symbols are policy-permitted, but adding them without a symbolication workflow would enlarge the distributed payload without an operator benefit. Trimming, Native AOT, ReadyToRun, and single-file compression also remain unchanged until measured size, startup, and compatibility evidence supports a separate decision.
