# Local MCP stdio and same-user IPC boundary

- Status: Proposed
- Date: 2026-10-20

## Context

dot-orbit needs an opt-in local MCP integration without giving an external agent direct access to the encrypted store or a second mutation path. The global AI gate is off by default and the MCP server has a further independent gate. Reads should not prompt for every call, while write-like calls must enter the proposal review boundary defined by ADR 0011 and ADR 0012.

MCP stdio servers run as child processes. The process cannot safely own the unlocked workspace merely because it was launched by a recognised host: MCP client metadata and executable names are self-asserted, and a malicious process running as the same operating-system account can imitate them. A local broker can exclude other accounts, but it cannot prove that the connecting program is Codex, VS Code, or Claude.

The prototype compared a dedicated adapter executable with a stand-alone `WinExe`-shaped surrogate. Both use the official C# MCP SDK and communicate with a separately running desktop broker. The surrogate proves redirected-stdio mechanics for a GUI-shaped executable, but it does not exercise the real desktop entry point or prove that an embedded route can start before Avalonia, workspace, persistence, application logging, or other desktop services.

## Decision

Adopt a local-only MCP stdio process backed by a private same-user desktop broker. Use a dedicated adapter executable: it is the only shape this prototype exercises without desktop initialization, and it keeps the child process's dependency and privilege surface explicit. An embedded `--mcp-stdio` route may be reconsidered only after the real desktop executable proves, on every supported runtime, that it branches before Avalonia, workspace, persistence, application logging, and all other desktop services. The surrogate package check is not evidence for that invariant.

The stdio process:

- uses the official `ModelContextProtocol` package for MCP framing, negotiation, cancellation, and EOF handling;
- writes only MCP messages to stdout and fixed, content-free diagnostics to stderr;
- does not unlock or read the workspace, receive encryption material, initialize desktop UI, or access persistence;
- holds no durable state and receives no secret through argv, environment variables, files, URLs, or logs; and
- maps each tool call to one bounded private-broker operation and propagates cancellation.

The desktop broker exists only while the workspace is unlocked and both the global AI gate and MCP gate are enabled. Lock, gate disablement, desktop exit, or explicit revocation closes listeners and active sessions. Restart never restores approval.

Use a two-stage broker connection:

1. A same-user bootstrap endpoint accepts one bounded request containing a self-asserted client name, version, and requested scopes. The desktop visibly distinguishes that metadata from the operating-system-verified same-account fact and asks for explicit approval.
2. Approval creates a random private session endpoint and a 256-bit, 15-second, single-use capability. The capability is returned only over the bootstrap connection, bound once on the session connection, zeroed after use, and never persisted or logged.

An approved live session grants only its named scopes. Synthetic read calls require no further prompt. A proposal-submission scope may create a pending proposal for the existing local Review flow, but cannot approve or apply it. Requests are versioned newline-delimited UTF-8 JSON, limited to 64 KiB, closed to unknown properties, bounded in depth and concurrency, and use fixed errors that never echo content. Cancellation suppresses late replies; submissions use an idempotency key.

On macOS and Linux, use Unix-domain sockets below a trusted canonical platform runtime parent. Reject a symlink at that immediate parent or the app-owned runtime leaf, and use exact `0700` directory and `0600` socket/descriptor modes. Verify the peer effective user ID on both client and server. Use unpredictable session names, refuse active endpoints, and remove stale paths only after the exact owned endpoint is proven inactive. Production integration must supply the platform-derived parent rather than accept an arbitrary path containing earlier user-controlled ancestors.

On Windows, use one-instance named pipes with an explicit protected DACL owned by and granting only the current user SID, plus an explicit deny rule for the Network SID so an authenticated remote session cannot reuse that identity over SMB. Verify the resulting owner and access rules at both ends and impersonate the client to verify its SID. Do not combine an explicit `PipeSecurity` with `PipeOptions.CurrentUserOnly` on .NET 10 because the runtime ignores the supplied security in that combination and has an open current-user identity defect.

## Consequences

- Enabling MCP is deliberate twice: the global AI gate and MCP gate must both be on, followed by consent for each new adapter connection.
- Approval is session-local rather than remembered. This costs one prompt on reconnect but avoids a durable trust record that same-user malware could reuse.
- Same-account malware remains a documented residual risk. The capability narrows timing, scope, and routing; it is not application authentication.
- The prototype accepts a runtime-root argument for testing. Its symlink check covers the immediate trusted parent and app-owned leaf; production must derive that trusted parent from the platform location rather than treating arbitrary earlier path components as trusted.
- The dedicated adapter adds a second packaged executable and therefore needs version-alignment and packaging checks, but avoids making a security-sensitive early-startup branch part of the desktop executable.
- The prototype exposes synthetic data only. Real read DTOs and proposal delivery remain later work and must reuse the existing snapshot/proposal contracts rather than expand this transport.
- Loopback TCP, remote MCP, OAuth, automatic host-configuration edits, persistent approval, direct workspace access, and autonomous Apply remain out of scope.
- Before this decision becomes Accepted, the package and process matrix must pass on Windows, macOS, and Linux and the consent/revocation interaction must receive rendered human review.
