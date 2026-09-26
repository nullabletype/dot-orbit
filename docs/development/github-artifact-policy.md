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

The sole current upload path is the manually dispatched upload step in `.github/workflows/release-packages.yml`. Pull-request runs of that workflow build, inspect, extract, and smoke packages but upload nothing. The checked-in packager must validate the archive allowlist and manifest before the workflow can reach the upload step.
