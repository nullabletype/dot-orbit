# Release packaging

Issue #3 adds reproducible self-contained Release archives for `win-x64`, `linux-x64`, `osx-x64`, and `osx-arm64`. It does not add installers, signing, notarisation, tags, or GitHub releases.

## Build one package

Run a locked restore from the repository root, then invoke the checked-in .NET packager with one supported runtime identifier:

```sh
dotnet restore DotOrbit.slnx --locked-mode
dotnet run --project tools/DotOrbit.Packaging/DotOrbit.Packaging.csproj --configuration Release --no-restore -- --repository-root . --runtime osx-arm64 --no-restore
```

The packager publishes a self-contained, single-file application, fails if the publish directory contains anything except the expected app executable and optional root-level PDBs, and assembles a separate archive containing only:

- the runtime-specific `dot-orbit` executable;
- `LICENSE.txt`;
- `THIRD-PARTY-NOTICES.md` and the complete bundled .NET, Apache-2.0, Inter OFL-1.1, ANGLE, SkiaSharp, and HarfBuzzSharp licence/notice texts;
- `package-manifest.json`, which records the runtime, Release configuration, self-contained status, size, and SHA-256 digest of every payload file.

Windows uses Zip; Linux and macOS use compressed tar archives so the executable bit is retained. The tool reopens the completed archive, rejects unexpected entries, extracts it to `artifacts/package-smoke/<rid>`, and verifies every manifested size and digest. Raw publish directories are never upload inputs.

## Smoke the extracted package

Run the self-contained apphost directly, matching the archive's target runtime and architecture:

```sh
# macOS
./artifacts/package-smoke/osx-arm64/dot-orbit --package-smoke

# Linux/X11
xvfb-run --auto-servernum --auth-file=artifacts/package-smoke/linux-x64/dot-orbit-xvfb.auth ./artifacts/package-smoke/linux-x64/dot-orbit --package-smoke

# Windows PowerShell
./artifacts/package-smoke/win-x64/dot-orbit.exe --package-smoke
```

The package smoke creates a synthetic encrypted SQLite3MC `workspace.orb` in a temporary directory, closes it, reopens and validates it, restores the external portable recovery fixture when `DOTORBIT_PACKAGE_SMOKE_RECOVERY` names it, removes the temporary workspace, then exercises the real Avalonia Today-to-Archive keyboard journey and closes cleanly. Its diagnostics contain fixed phase and result metadata only.

These packages are portable archives rather than installers or platform application bundles. They have no installation hook or package identity with which to register the `.orb` file association, so opening a workspace by double-clicking it is not part of this packaging slice. No cosmetic MIME metadata is included in the archive.

Before packaging, every runtime job restores the same checked-in synthetic encrypted recovery fixture through the storage integration test. After packaging, the extracted self-contained app restores those identical external bytes again through its bundled runtime before continuing the native UI journey. This proves Windows x64, Linux x64, macOS x64, and macOS arm64 compatibility rather than only proving separate same-host databases. The fixture is not included in the release archive; its provenance, public test-only passphrase, and pinned digest are recorded beside it under `tests/DotOrbit.Storage.Sqlite.Tests/Fixtures`.

## CI and artifact boundary

`.github/workflows/release-packages.yml` builds and smokes all four packages for relevant pull requests without uploading anything. Its manual `workflow_dispatch` path is the only workflow allowed to upload these already-validated archives. The uploaded file is the final archive itself, not a publish directory or a second archive containing evidence.

Do not dispatch the upload path until the selected source commit has passed the canonical exact-SHA evidence gate and all four package jobs. Confirm that the manual run's resolved commit SHA matches that recorded evidence before treating its archives as release candidates. A branch name or a prior successful run is not sufficient evidence for a different commit.
