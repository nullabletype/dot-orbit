# Secure local MCP/IPC prototype

> Historical design evidence. The accepted implementation was promoted into
> `src/DotOrbit.Mcp.Ipc`, `src/DotOrbit.Mcp.Adapter`, and their production test
> projects. The throwaway prototype executables and duplicate test harness were
> removed so there is only one maintained implementation.

This throwaway slice tested the boundary selected by issue #125. Its surviving `consent-state.html` is historical interaction evidence only. The executable projects and duplicate test harness were removed when issue #92 promoted the design into production.

## What it proves

- MCP is standard stdio owned by the official C# SDK.
- A dedicated console adapter is the selected stdio shape. A stand-alone `WinExe`-shaped surrogate checks redirected-stdio mechanics only; it does not prove an early route in the real desktop executable.
- Both shapes expose exactly `orbit_status` and `orbit_submit_synthetic_proposal`.
- The running desktop-shaped host owns consent and a private same-user broker.
- Reads need no per-call prompt. The interaction model offers one-time approval or remembered trust for a matching workspace, adapter credential, account, and exact scope set.
- Submission returns `pending-local-review`; it cannot apply anything.
- The interaction model uses a persistent foreground banner and AI-navigation count, plus a content-free background system notification, without opening Review or stealing focus automatically.
- Disablement, lock, revocation, cancellation, crash recovery, stale cleanup, frame limits, capability expiry, and capability single use have executable checks.

The prototype deliberately does not prove real workspace reads, real proposal ingress, application-identity authentication, remote MCP, or host configuration editing.

For current build, test, packaging, configuration, and security instructions, use [`docs/development/local-mcp.md`](../../docs/development/local-mcp.md) and [`docs/development/release-packaging.md`](../../docs/development/release-packaging.md).

## Security boundary

On Unix, the runtime directory is exact mode `0700`; descriptors and sockets are `0600`; a symlink at the immediate trusted parent or app-owned runtime leaf and paths outside the private directory are rejected; and kernel peer credentials must match the current effective user. The prototype takes that parent through `--runtime-root`; production must derive it from a trusted platform location rather than accepting arbitrary earlier path components. On Windows, each pipe has a protected explicit current-user DACL plus a Network SID deny rule to exclude remote SMB clients. The ACL is verified on both ends, and the server verifies the impersonated client SID.

Client names are explicitly labelled self-asserted. Approval grants a live connection a 15-second single-use bootstrap capability and the requested synthetic scopes. It does not identify the calling application. Another process already running as the same user remains the principal residual risk.

The executable broker prototype keeps approval session-local and deliberately writes no trust state. The interaction prototype separately models the production decision: “Always allow” remembers only the scoped client policy, while restart destroys the connection and capability; a matching reconnect receives a fresh capability. Production persistence requires a platform-protected credential per configured client profile and an encrypted-workspace verifier, and is not implemented by this synthetic slice.

The internal protocol is strict newline-delimited UTF-8 JSON with a 64 KiB frame limit, bounded depth, unknown-member rejection, fixed errors, maximum four sessions, four outstanding calls admitted by both the adapter and broker per session, and 128 retained idempotency receipts per session. Capabilities never enter files, process arguments, environment variables, URLs, logs, test output, or CI artifacts.

## Review the interaction model

Open `consent-state.html`. Guided scenarios cover first connection, denial, remembered trust across restart, explicit revoke-and-forget, foreground proposal attention, background notification, and deferring Review while retaining the navigation count. Free play makes the important distinctions visible: the OS verifies the account; the adapter only asserts its application name; remembered trust is a scoped policy rather than a restored live capability; and proposal arrival never grants Apply or steals focus.
