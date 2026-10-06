# GitHub artifact policy

GitHub artifact storage is reserved for clean application builds intended for release-style evaluation or distribution.

## Allowed

- Packaged application binaries built in Release configuration for an explicitly supported runtime identifier.
- PDB files accompanying those binaries, when useful for diagnostics.
- Files required for the application package to run or satisfy its licence obligations.

## Prohibited

- Docker or OCI images, exported container filesystems, image layers, and container build contexts.
- SDKs, toolchains, or runtime installations uploaded as standalone payloads.
- NuGet, npm, operating-system, or other dependency caches stored as artifacts.
- Source trees, `bin` or `obj` directories, intermediate outputs, and unfiltered publish directories.
- Test results, coverage files, logs, traces, screenshots, generated documentation, and general evidence bundles.
- Debug-configuration application builds.

CI should report tests and verification through job output, checks, pull-request text, or concise summaries. It must not preserve those materials through artifact upload.

Before adding or changing an artifact upload, verify that its input is a deliberately assembled Release package containing only the allowed files. A workflow must fail rather than upload an unfiltered directory or an unknown payload.

The sole workflow-artifact upload path is the gated upload step in `.github/workflows/release-packages.yml`. It runs only for pushes to `main` or for a `v`-prefixed SemVer tag whose commit is contained in `main`, and retains each supported runtime archive for seven days. Pull-request and manual runs build, inspect, extract, and smoke packages but upload nothing. The checked-in packager must validate the archive allowlist and versioned manifest before the workflow can reach the upload step.

For a valid version tag, the workflow may copy those same four archives from workflow-artifact storage to an unpublished GitHub Release draft after every native smoke job passes. The draft-release job must recheck the tag target and exact asset allowlist, may update only an existing draft, and must fail rather than alter a published release. Its generated release notes, release metadata, and final archives belong to the release; no CI evidence or intermediate output may be attached.
