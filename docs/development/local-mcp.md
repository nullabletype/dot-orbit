# Local MCP operator and security guide

dot-orbit ships a separate `dot-orbit-mcp` executable for local MCP hosts. It speaks MCP over standard input/output and connects to the running desktop through a private same-user endpoint. It does not listen on TCP, HTTP, SSE, loopback, LAN, VPN, container, tunnel, cloud, or public interfaces, and it cannot unlock a workspace.

## Enable and connect

1. Unlock the intended workspace in the desktop application.
2. In **Settings**, enable **AI features**. This exposes only the provider-neutral manual interchange surface.
3. Enable **local MCP** separately. Settings reports **Running** only after the private broker endpoint is available.
4. Configure the MCP host to launch the `dot-orbit-mcp` executable from the same release archive as the desktop. Pass one or both scopes and use a stable profile name for that host installation.

The generic stdio configuration shape is:

```json
{
  "command": "/absolute/path/to/dot-orbit-mcp",
  "args": [
    "--stdio",
    "--profile", "personal",
    "--client-name", "My MCP host",
    "--client-version", "1.0",
    "--scope", "read",
    "--scope", "submit"
  ]
}
```

`--profile` accepts 1–64 ASCII letters, digits, hyphens, or underscores. `--client-name` and `--client-version` are display metadata only; dot-orbit labels them as unverified. The adapter creates a random 32-byte credential for the profile and stores it in Windows Credential Manager, macOS Keychain, or Linux Secret Service. Credential-store startup has a 10-second deadline and bounded helper output. The adapter sends only the SHA-256 verifier to the desktop. There is no plaintext-file fallback and no credential, workspace passphrase, or endpoint override in arguments or environment variables.

Current Codex, VS Code, and Claude MCP configuration fields change independently of dot-orbit. Use each host's documented local stdio configuration and point it at the packaged executable; dot-orbit does not install or edit third-party configuration.

## Approval and remembered trust

The first connection opens an in-app approval window. Check the OS-verified account and available process ID separately from the self-asserted client name and version, then review the exact scopes:

- `read` permits reusable instructions/schema reads and complete current Planning snapshots without a prompt on every read;
- `submit` permits a v1 New-work or Change proposal to enter the local review surface, but cannot approve or apply it.

**Allow once** lasts only for the live connection. **Always allow** stores an encrypted workspace-local record bound to the exact credential verifier, verified OS account, and scope set. A restart may reuse that remembered trust, but creates a new connection and capability. Adding a scope, changing profile credentials or OS account, using another workspace, or revoking the record requires approval again.

Remembered clients appear in Settings with a **Revoke** action. Revocation removes the stored record, closes all live MCP sessions immediately, and forces a new approval. Turning local MCP off also disconnects clients. Turning AI features off atomically persists local MCP off, closes the endpoint, invalidates live capabilities, and prevents MCP from silently restarting if AI is enabled later.

## Tools and proposal review

The adapter exposes exactly three tools:

- `orbit_get_instructions` returns immutable reusable v1 instructions and schemas;
- `orbit_get_planning_snapshot` returns one atomic revision-bearing snapshot, excluding archived and Bin Project/Task content while retaining the complete Category and Participant catalogues;
- `orbit_submit_proposal` accepts one strict UTF-8 v1 proposal and a caller-generated idempotency key.

Reads are observational and do not flush an editor, navigate the UI, mutate the workspace, or advance its revision. Responses are complete or rejected before output; the limit is 16 MiB.

Proposal submission uses the same 4 MiB schema and semantic planning boundary as typing, Paste, file load, and drop. Validated inbound proposals are encrypted in stable arrival order in a ten-item queue that is separate from manual text and manual review. An eleventh proposal fails with `proposal-queue-full` without replacement or merge. A bounded encrypted receipt ledger makes an exact client/key/payload retry return the original receipt after disconnect, restart, Review, or discard, including while the queue is full; reusing the key for different bytes fails as a conflict. A retry reports `pending-local-review` while that receipt is still pending, or `already-processed` with review no longer required after Apply or discard. An accepted inbound proposal is encrypted before its bounded receipt is returned and appears through a prominent `Review N` navigation pill and content-free, polite in-app banner; it never navigates, opens a window, or steals focus. Restored proposals do not replay the banner. Disabling AI hides but does not delete the durable queue. Explicit **Discard** removes only the selected encrypted row and never applies anything. A row opens the existing modal Review with the self-reported local-client source. Only whole-proposal approval, final stale check, recovery, and atomic Apply can change data. A successful Apply consumes only the selected receipt in the same transaction as the workspace changes, so interruption after commit cannot replay that proposal and cannot consume another queued item.

## Trust boundary and failure behaviour

On macOS and Linux, the desktop uses a private `0700` application runtime directory, `0600` Unix-domain sockets, symlink/pre-created-path checks, and peer-user verification. On Windows it uses a one-instance named pipe with an explicit current-user ACL, Network SID denial, owner/rule verification, and client impersonation to verify the SID. Session endpoints are unpredictable. The bootstrap grants a fresh random 256-bit capability that expires after 15 seconds, binds once to one connection and exact scopes, and is never persisted or logged.

This boundary excludes other ordinary OS accounts; it does not prove that a same-user program is really the brand named in its metadata. A compromised user session, administrator/root access, process injection, or a compromised operating system remains outside the application guarantee. Approve only local software you trust with the displayed scopes.

Private IPC frames are capped at 64 KiB and larger payloads use ordered bounded chunks. Proposal input is capped at 4 MiB, response output at 16 MiB, live sessions at four, and outstanding calls per session at four. The adapter separately caps each outer MCP stdio JSON request line at 32 MiB, allowing for worst-case JSON escaping of a valid 4 MiB proposal while still bounding SDK input. Version mismatch, malformed data, missing or reordered chunks, excess work, deadline expiry, cancellation, disconnect, shutdown, workspace replacement, either disabled gate, or broker failure fails closed with fixed content-free status or error metadata.

If Settings reports **Error**, turn local MCP off, close the MCP host, and enable it again after confirming no stale process is running. The adapter fails closed when its platform credential store is unavailable; dot-orbit never falls back to a plaintext secret or a network transport.
