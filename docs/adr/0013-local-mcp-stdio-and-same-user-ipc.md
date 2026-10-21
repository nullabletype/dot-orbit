# Local MCP stdio and same-user IPC boundary

- Status: Proposed
- Date: 2026-10-21

## Context

dot-orbit needs an opt-in local MCP integration without giving an external agent direct access to the encrypted store or a second mutation path. The global AI gate is off by default and the MCP server has a further independent gate. Reads should not prompt for every call, while write-like calls must enter the proposal review boundary defined by ADR 0011 and ADR 0012.

MCP stdio servers run as child processes. The process cannot safely own the unlocked workspace merely because it was launched by a recognised host: MCP client metadata and executable names are self-asserted, and a malicious process running as the same operating-system account can imitate them. A local broker can exclude other accounts, but it cannot prove that the connecting program is Codex, VS Code, or Claude.

The prototype compared a dedicated adapter executable with a stand-alone `WinExe`-shaped surrogate. Both used the official C# MCP SDK and communicated with a separately running desktop broker. The dedicated executable was the only shape that exercised redirected stdio without desktop initialization, workspace access, persistence, application logging, or unrelated UI services.

## Decision

Adopt a local-only MCP stdio process backed by a private same-user desktop broker. Ship the dedicated `dot-orbit-mcp` adapter beside the desktop executable and keep the official MCP SDK out of the desktop process. The adapter and desktop must come from the same versioned archive. There is no embedded desktop route and no network transport.

The stdio process:

- uses the official `ModelContextProtocol` package for MCP framing, negotiation, cancellation, and EOF handling;
- writes only MCP messages to stdout and fixed, content-free diagnostics to stderr;
- does not unlock or read the workspace, receive encryption material, initialize desktop UI, or access persistence;
- holds no durable state and receives no secret through argv, environment variables, files, URLs, or logs; and
- maps each tool call to one bounded private-broker operation and propagates cancellation.

The desktop broker exists only while the workspace is unlocked and both the global AI gate and MCP gate are enabled. Lock, gate disablement, desktop exit, or explicit revocation closes listeners and active sessions. A remembered approval may survive restart, but no listener, connection, session, or capability does.

Use a two-stage broker connection:

1. A same-user bootstrap endpoint accepts one bounded request containing a self-asserted client name, version, requested scopes, and an opaque adapter-profile credential. The desktop visibly distinguishes self-asserted metadata from the operating-system-verified same-account fact. An exact remembered match may reconnect automatically; otherwise the desktop asks for explicit approval.
2. Approval can be one-time or remembered for that workspace, adapter credential, operating-system account, and exact scope set. Scope expansion, a different workspace or credential, or explicit revocation requires approval again. The production adapter stores its credential using the platform credential store; the desktop stores only the verifier and approval policy inside the encrypted workspace. Neither the display name nor executable path is the trust key.
3. Every approved connection creates a random private session endpoint and a fresh 256-bit, 15-second, single-use capability. The capability is returned only over the bootstrap connection, bound once on the session connection, zeroed after use, and never persisted or logged.

An approved live session grants only its named scopes. Synthetic read calls require no further prompt. A proposal-submission scope may create a pending proposal for the existing local Review flow, but cannot approve or apply it. Requests are versioned newline-delimited UTF-8 JSON, limited to 64 KiB, closed to unknown properties, bounded in depth and concurrency, and use fixed errors that never echo content. Cancellation suppresses late replies; submissions use an idempotency key.

Accepted proposal submissions enter an encrypted, insertion-ordered pending-review queue before the adapter receives a receipt. The queue is independent of manual text and review and holds at most ten proposals; a full queue rejects another submission without replacement or merge, after duplicate and idempotency-conflict checks. Exact admission of a Change proposal also stores a bounded, versioned compatibility basis containing encrypted hashes of the fields, relationships, records, and aggregates its semantics depend on, bound to the original payload and source. Content edits depend only on the fields they propose to change, so unrelated field changes and incidental ordering shifts from newly created work remain mergeable. Later Review may replan the unchanged operations against the current Workspace only when every dependency still matches; otherwise it fails closed and requires fresh context. This is sequential verified replanning, not proposal merging: operations are never rewritten, dropped, repaired, partially applied, or automatically approved. The desktop shows a prominent `Review N` pill on the AI navigation item and a content-free, polite in-app arrival banner. Arrivals never navigate, open a window, move focus, or expose proposal content. Review reuses the same modal validation, preview, approval, recovery, and atomic Apply boundary as typing, paste, file, and drop intake, while identifying the self-reported local-client source and visibly identifying a recalculated preview. Successful Apply deletes only the selected receipt within the same transaction as the workspace mutations and revision increment; post-commit maintenance and UI refresh never delete or replay it. Turning the global AI gate off hides pending-review attention without deleting the queue; attention returns when AI is enabled. Restart retains the encrypted queue and compatibility basis but no live session or capability and does not replay a new-arrival banner. Rows retained from an earlier schema have no compatibility basis and therefore fail closed once stale.

On macOS and Linux, use Unix-domain sockets below a trusted canonical platform runtime parent. Reject a symlink at that immediate parent or the app-owned runtime leaf, and use exact `0700` directory and `0600` socket/descriptor modes. Verify the peer effective user ID on both client and server. Use unpredictable session names, refuse active endpoints, and remove stale paths only after the exact owned endpoint is proven inactive. Production integration must supply the platform-derived parent rather than accept an arbitrary path containing earlier user-controlled ancestors.

On Windows, use one-instance named pipes with an explicit protected DACL owned by and granting only the current user SID, plus an explicit deny rule for the Network SID so an authenticated remote session cannot reuse that identity over SMB. Verify the resulting owner and access rules at both ends and impersonate the client to verify its SID. Do not combine an explicit `PipeSecurity` with `PipeOptions.CurrentUserOnly` on .NET 10 because the runtime ignores the supplied security in that combination and has an open current-user identity defect.

## Consequences

- Enabling MCP is deliberate twice: the global AI gate and MCP gate must both be on, followed by consent for each new adapter connection.
- Remembered approval avoids a prompt after every desktop or MCP-host restart. It persists only the scoped trust policy and credential verifier, never a live capability; explicit revoke removes the record and closes current sessions.
- A remembered adapter credential improves continuity, not application authentication. Same-account malware remains able to imitate display metadata and may be able to act through the user's local security context; this is a documented residual risk rather than a claim of host identity.
- Same-account malware remains a documented residual risk. The capability narrows timing, scope, and routing; it is not application authentication.
- Production derives its runtime root from the canonical per-user local application-data location. An arbitrary runtime-root override exists only as an internal test seam and is not exposed by either executable.
- The dedicated adapter adds a second packaged executable and therefore needs version-alignment and packaging checks, but avoids making a security-sensitive early-startup branch part of the desktop executable.
- The broker receives only the transport-neutral Planning source, proposal planner/coordinator, and encrypted agent-integration store. It has no reference to proposal application authority.
- Pending-review indication is coalesced and content-free. Proposal content, titles, workspace names, and client-supplied explanations never appear in diagnostics.
- Loopback TCP, remote MCP, OAuth, automatic host-configuration edits, direct workspace access, and autonomous Apply remain out of scope.
- Before this decision becomes Accepted, the package and process matrix must pass on Windows, macOS, and Linux and the consent/revocation interaction must receive rendered human review.
