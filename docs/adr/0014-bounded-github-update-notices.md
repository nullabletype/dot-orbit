# ADR 0014: Bounded GitHub update notices

## Status

Accepted

## Context

dot-orbit is local-first and has no account or hosted workspace service, but archive users need a trustworthy indication that a newer supported application release exists. GitHub release data is remote and mutable by default, and local, `main`, and CI packages can all carry valid-looking informational versions.

## Decision

`DotOrbit.Desktop` owns a notice-only update module. An official installed build requires both a canonical SemVer informational version and exact matching `DotOrbitReleaseTag=v<version>` assembly metadata supplied only by the admitted tag-packaging workflow. Stable builds select stable releases; prerelease builds select stable and prerelease releases by SemVer precedence. Development builds never check automatically.

The client anonymously queries the fixed numeric-repository GitHub Releases route using REST version `2026-03-10`, confirmed as supported in GitHub's API-version documentation on 2026-10-24. The pin must be reviewed when GitHub announces its removal or the request starts returning a version error; it must not float silently. Requests contain a generic user agent and no token, installed version, workspace content, account, path, device identifier, or telemetry. Redirects, pagination, response bytes, catalogue entries, JSON depth, and elapsed time are bounded. Only published, non-draft, immutable, canonical releases with a channel-consistent prerelease flag and exactly four uploaded non-empty runtime archives with SHA-256 digests are admitted. Navigation uses a locally constructed fixed-repository tag URL and ignores remote names, notes, Markdown, and URLs.

Automatic checking is best effort after unlock, defaults on, and attempts at most once per 24 hours from the last attempt. Manual checks bypass the cadence and concurrent checks coalesce. The atomic device preference file stores only the switch, last automatic attempt, last successful check, first-page ETag, last verified result, and dismissed exact version. A 304 requires that verified cache. Failure may preserve prior verified details but is reported as unavailable and never blocks workspace use.

## Consequences

- Core, encrypted workspace persistence, and workspace revisions remain unchanged.
- GitHub necessarily receives ordinary network metadata such as the source IP; Settings discloses that boundary.
- Release publication must make the release immutable and retain the exact four digest-bearing archives before clients can discover it.
- Update download, installation, signatures, and automatic application remain outside this decision.
