# Local MCP adapter and same-user IPC research

Research date: 2026-10-20

Issue: [#125 — Prototype secure local IPC for a standard MCP stdio adapter](https://github.com/nullabletype/dot-orbit/issues/125)

All linked sources were retrieved on 2026-10-20. This note distinguishes externally sourced facts from dot-orbit conclusions. A **sourced fact** is a statement supported by an official specification, project, vendor, or platform source. A **dot-orbit conclusion** is a proposed product or implementation choice; the cited standards do not require that choice.

## Scope

This note resolves the external dependencies and interoperability boundary for a local-only MCP prototype:

- the stable MCP protocol and official C# SDK versions to pin;
- stdio framing, version negotiation, cancellation, and shutdown behaviour;
- a private local IPC boundary between the MCP adapter and the running desktop application;
- representative configuration in current Codex, VS Code, and Claude hosts; and
- the smallest synthetic adapter that can test the architecture without exposing real workspace content or adding a second mutation path.

It does not design remote MCP, HTTP transport, OAuth, a public plugin package, or the final read/write tool vocabulary. It assumes the product decisions already made for this work: every AI-bounded feature is behind a global toggle that is off by default; the MCP server has a further opt-in toggle and can be completely disabled; reads do not require a prompt for every call; and proposed writes reuse the existing proposal review/apply boundary.

## Conclusions

The recommended direction for issue #125 is:

1. Pin the final **MCP 2026-07-28** protocol through the official C# SDK **`ModelContextProtocol` 2.2.0**. Use the SDK's dual-era support rather than implementing negotiation or legacy `initialize` fallback in dot-orbit.
2. Run a dedicated MCP adapter process that speaks MCP only over stdin/stdout. A `WinExe`-shaped surrogate demonstrates redirected-stdio mechanics but does not prove an early `--mcp-stdio` route in the real desktop executable. The dedicated process must not load the encrypted workspace, hold encryption material, mutate persistence, or host desktop UI.
3. Keep the desktop application as the authority. The adapter makes bounded requests to a private same-user desktop broker. Read operations return desktop-produced DTOs. A write-like MCP tool can only submit the same proposal format already accepted by paste/file ingress; it cannot approve or apply it.
4. Use a Windows named pipe with an explicit protected DACL containing only the current user, then verify the owner and access rules on both ends. Do not combine that DACL with `PipeOptions.CurrentUserOnly`: .NET 10 ignores the supplied `PipeSecurity` in that combination and its current implementation checks the token owner rather than the current user SID. On macOS and Linux under the repository's pinned .NET 10 runtime, prefer an explicit Unix-domain socket inside a validated app-owned directory with mode `0700`, set the socket to `0600` after bind, and verify peer credentials. Do not expose a loopback TCP port merely to avoid platform IPC differences.
5. Treat same-user IPC as an exclusion of other operating-system accounts, not as proof of which application connected. MCP `clientInfo`/`serverInfo` are self-reported metadata, not identities. Require the issue's explicit first-connection consent, then hand a short-lived, least-privilege capability to that connection over the already protected pipe/socket. Keep it only in memory and revoke it on disconnect, lock, timeout, gate disablement, or application exit.
6. When either the global AI gate or the MCP gate is off, the desktop must not listen for MCP broker traffic. Turning a gate off should close the broker and cancel adapter requests. It must not add a way to interrupt or bypass an atomic proposal apply that the user has already approved.
7. Keep adapter stdout protocol-clean, send operational diagnostics to stderr, and never log workspace content, proposal bodies, secrets, or content-derived identifiers.
8. Validate the prototype with one synthetic read and one synthetic proposal-submission tool before exposing real workspace reads. Exercise all three representative hosts, EOF, cancellation, timeout, unavailable desktop, denied connection, and both feature gates.

## 1. Current stable protocol and official C# SDK

### Sourced facts

The Model Context Protocol plan matrix marks **2026-07-28** as final and the later **2026-12-15** train as not ready ([official MCP plan matrix](https://plan.modelcontextprotocol.io/matrix), retrieved 2026-10-20). The release announcement calls 2026-07-28 a final specification release and says all Tier 1 SDKs, including C#, support it ([official MCP release announcement](https://blog.modelcontextprotocol.io/posts/2026-07-28/), retrieved 2026-10-20). The versioned specification is published at the [2026-07-28 MCP specification](https://modelcontextprotocol.io/specification/2026-07-28) (retrieved 2026-10-20).

The [official MCP C# SDK repository](https://github.com/modelcontextprotocol/csharp-sdk) is maintained in collaboration with Microsoft. Its package guidance distinguishes:

- `ModelContextProtocol.Core` for low-level protocol types and custom clients/servers;
- `ModelContextProtocol` for the standard client/server APIs, dependency injection, stdio, and attribute-based tool/resource/prompt discovery; and
- `ModelContextProtocol.AspNetCore` for HTTP servers.

The official release page identifies **v2.2.0**, released 2026-08-13, as the latest stable SDK release ([C# SDK releases](https://github.com/modelcontextprotocol/csharp-sdk/releases), retrieved 2026-10-20). NuGet publishes the corresponding stable [`ModelContextProtocol` 2.2.0 package](https://www.nuget.org/packages/ModelContextProtocol/2.2.0) and lists .NET 10 compatibility (retrieved 2026-10-20). The SDK's [versioning policy](https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/versioning.md) says the v2 line implements the 2026-07-28 protocol and retains compatibility with 2025-11-25 and earlier revisions (retrieved 2026-10-20).

The official C# [getting-started guide](https://csharp.sdk.modelcontextprotocol.io/v2/concepts/getting-started.html) uses `AddMcpServer()`, `WithStdioServerTransport()`, and tool discovery on a .NET Generic Host. It configures console logging to stderr so that stdout remains available for MCP messages (retrieved 2026-10-20). The [transport guide](https://csharp.sdk.modelcontextprotocol.io/v2/concepts/transports/transports.html) identifies stdio as the normal transport for local integrations and process-level security, and exposes `StreamServerTransport`/`StreamClientTransport` when an application genuinely needs a custom byte stream (retrieved 2026-10-20).

### dot-orbit conclusion

Use the high-level `ModelContextProtocol` package, not `ModelContextProtocol.Core`, for the adapter. The standard server host already supplies the required transport and compatibility behaviour. A hand-written JSON-RPC loop would increase protocol and shutdown risk without making the desktop IPC boundary safer.

The representative hosting shape is:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(options =>
    options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

[McpServerToolType]
internal sealed class OrbitTools(IOrbitDesktopBroker broker)
{
    [McpServerTool(Name = "orbit_status")]
    public Task<OrbitStatus> GetStatus(CancellationToken cancellationToken) =>
        broker.GetStatusAsync(cancellationToken);
}
```

`IOrbitDesktopBroker` here is a narrow adapter-owned IPC client, not a broad application service. The real implementation should use explicit DTOs and a per-operation timeout, and should pass the MCP request cancellation token all the way to the broker call.

## 2. Stdio framing, negotiation, cancellation, and shutdown

### Sourced facts

The current [stdio transport binding](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/stdio) requires the client to launch the server as a subprocess. The client writes requests and notifications to server stdin; the server writes responses and notifications to stdout. Each message is one JSON-RPC object on one line, with no embedded newline. A server may write diagnostics to stderr, but stdout must contain only valid MCP messages (retrieved 2026-10-20).

MCP 2026-07-28 changed the protocol to stateless, per-request version negotiation. A modern request declares its protocol version; a server that cannot serve it returns JSON-RPC error `-32022` with its supported versions. Servers must implement `server/discover`. For stdio interoperability with earlier clients, the specification defines a probe/fallback path rather than assuming that every host starts in the same era ([MCP versioning](https://modelcontextprotocol.io/specification/2026-07-28/basic/versioning) and [`server/discover`](https://modelcontextprotocol.io/specification/2026-07-28/server/discover), retrieved 2026-10-20).

For cancellation, a stdio client must send `notifications/cancelled`. The server should stop the operation and must not send further messages for that request. The general cancellation pattern recommends a timeout for every outgoing request and an absolute maximum timeout even when progress extends a deadline ([stdio cancellation](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/stdio) and [cancellation pattern](https://modelcontextprotocol.io/specification/2026-07-28/basic/patterns/cancellation), retrieved 2026-10-20).

For normal stdio shutdown, the client closes the server's input, waits a reasonable time, then may terminate it forcibly; the server should exit promptly on EOF. On unexpected termination, a client should restart the server, but in-flight requests are lost and may need to be retried ([stdio lifecycle](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/stdio), retrieved 2026-10-20).

The C# SDK maps `notifications/cancelled` to a request cancellation token. For stdio, it also cancels the handler token when stdin reaches EOF or the server's `RunAsync` token is cancelled; asynchronous disposal waits for in-flight handlers ([C# SDK stateless server lifecycle](https://csharp.sdk.modelcontextprotocol.io/v2/concepts/stateless/stateless.html), retrieved 2026-10-20). `McpServer.RunAsync` accepts a `CancellationToken` ([C# `McpServer` API](https://csharp.sdk.modelcontextprotocol.io/v2/api/ModelContextProtocol.Server.McpServer.html), retrieved 2026-10-20).

### dot-orbit conclusion

The adapter should let the SDK own MCP framing and negotiation. Every tool handler should accept a `CancellationToken`, apply a bounded timeout, and pass the linked token to IPC. The desktop broker must stop work and suppress a late reply when that token is cancelled. Read operations may be retried only where the operation is explicitly idempotent. Proposal submission needs a client-generated idempotency key or must report an indeterminate outcome rather than blindly duplicate a timed-out submission.

The adapter should keep no durable state and perform no durable cleanup during shutdown. That makes EOF, SIGINT/SIGTERM, and forced termination safe: the desktop remains the owner of broker sessions, pending proposals, and encrypted persistence.

## 3. Local IPC and the security boundary

### Sourced facts

On Windows, [`PipeOptions.CurrentUserOnly`](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.pipeoptions?view=net-10.0) requires a named-pipe server to accept only a client running under the same user and requires a client to connect only to a server running under the same user. Windows also checks elevation level (retrieved 2026-10-20).

The [`NamedPipeServerStreamAcl.Create` documentation](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.namedpipeserverstreamacl.create?view=net-10.0) states that `PipeOptions.CurrentUserOnly` ignores the supplied `PipeSecurity`. The open [.NET runtime issue #123903](https://github.com/dotnet/runtime/issues/123903) demonstrates that the .NET 10 implementation compares the pipe owner with the process token owner rather than the token's current user SID; its fix is targeted beyond .NET 10 (retrieved 2026-10-20).

Windows named pipes can accept network clients. The native [`CreateNamedPipe` documentation](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createnamedpipew) provides `PIPE_REJECT_REMOTE_CLIENTS` specifically to reject remote connections. The .NET ACL factory does not expose that flag, so an explicit deny ACE for the well-known Network SID is required in addition to the local-user allow rule (retrieved 2026-10-20).

.NET implements named pipes over Unix-domain sockets on Linux ([Microsoft named-pipe guidance](https://learn.microsoft.com/en-us/dotnet/standard/io/how-to-use-named-pipes-for-network-interprocess-communication), retrieved 2026-10-20). The stronger Unix filesystem behaviour for `CurrentUserOnly` is a .NET 11 change: before .NET 11, the socket inherited permissions from the process umask; in .NET 11 it is created with mode `0600`. The runtime still performs the same-user check before .NET 11, but the socket may be visible or connectable at the filesystem level before rejection ([.NET 11 named-pipe Unix permission change](https://learn.microsoft.com/en-gb/dotnet/core/compatibility/core-libraries/11/namedpipeserverstream-unix-permissions), retrieved 2026-10-20). .NET 10 exposes [`File.SetUnixFileMode`](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.setunixfilemode?view=net-10.0) for explicit Unix permission changes (retrieved 2026-10-20).

The MCP discovery specification says `clientInfo` and `serverInfo` are self-reported implementation metadata and must not be treated as security identity ([`server/discover`](https://modelcontextprotocol.io/specification/2026-07-28/server/discover), retrieved 2026-10-20).

The Windows API can retrieve the client process ID attached to a named-pipe instance ([`GetNamedPipeClientProcessId`](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeclientprocessid), retrieved 2026-10-20). Linux `SO_PEERCRED` returns the credentials captured for the peer of a connected Unix-domain socket ([Linux `unix(7)`](https://www.man7.org/linux/man-pages/man7/unix.7.html), retrieved 2026-10-20). On macOS, `getpeereid` returns the effective user and group IDs of a connected Unix-domain peer; Apple's manual explicitly describes the credentials as kernel-derived and not influenceable by the peer except by connecting under different effective credentials ([Apple `getpeereid(3)`](https://developer.apple.com/library/archive/documentation/System/Conceptual/ManPages_iPhoneOS/man3/getpeereid.3.html), retrieved 2026-10-20).

MCP leaves host UI details flexible, but its tool security guidance says a human should remain in the loop, clients should show exposed tools and tool invocations clearly, and confirmation should be obtained for sensitive operations. Servers must validate inputs, apply access controls and rate limits, and sanitize outputs; clients should apply timeouts and validate results ([MCP tools security](https://modelcontextprotocol.io/specification/2026-07-28/server/tools), retrieved 2026-10-20). The overall specification also makes explicit consent, data privacy, and tool safety foundational principles ([MCP security principles](https://modelcontextprotocol.io/specification/2026-07-28), retrieved 2026-10-20).

### dot-orbit conclusion

The desktop broker should use:

| Platform | Transport | Minimum protection |
| --- | --- | --- |
| Windows | `NamedPipeServerStreamAcl` | Protected explicit DACL owned by and granting only the current user SID, with Network SID denied; verify the resulting ACL on both ends; unpredictable per-session pipe name; one instance and bounded buffers. Do not set `CurrentUserOnly` with the explicit DACL on .NET 10. |
| macOS/Linux on .NET 10 | Explicit Unix-domain socket | Create it below a trusted canonical platform runtime parent; reject a symlink at the immediate parent or app-owned leaf; verify ownership; set directory mode `0700`; bind a per-session unpredictable socket name; set socket mode `0600`; remove only the exact owned socket on shutdown. |

The `0700` parent directory matters because changing a socket to `0600` only after bind otherwise leaves a creation window. The implementation must also refuse an existing non-socket path, a socket owned by another user, or an unexpectedly permissive parent. Root/administrator and already-compromised same-user processes remain outside what these permissions can exclude.

Same-user protection does not authenticate “Codex,” “VS Code,” or “Claude.” A process running as the signed-in user can impersonate names and MCP discovery metadata. Consequently:

- neither `clientInfo` nor an executable path should silently grant access;
- no API key or workspace secret should be inherited through host environment configuration;
- enabling AI and MCP establishes product intent but should not be described as application identity;
- the desktop must own an explicit, revocable first-connection approval and distinguish OS-verified facts (same account, and platform-specific peer process/credential data) from the adapter's self-asserted host name; and
- with the agreed no-per-read-prompt UX, approval grants only the documented read and proposal-submission scopes for that live connection. It never grants direct mutation.

After consent, the broker should generate a cryptographically random opaque capability, bind it to the exact transport connection and allowed synthetic operations, return it only across that connection, and require it on subsequent frames. It should expire quickly and be revoked on connection close, workspace lock, gate disablement, timeout, or desktop exit. The adapter keeps it only in process memory. It must never appear in argv, environment variables, URLs, endpoint descriptors, logs, exports, screenshots, fixtures, CI output, or artifacts. A rejected or expired connection must perform a new consent flow rather than silently restoring approval after restart.

A local bearer secret stored in a file readable by the same OS user does not defend against a malicious same-user process. It can still be useful as an unguessable session-routing capability, but must not be presented as a stronger identity guarantee.

The global AI gate and MCP-specific gate should be checked before creating the broker listener and again at request dispatch. If either gate is disabled, the endpoint should not exist and an already connected adapter should receive cancellation/closure. The adapter should return a fixed unavailable result on stderr/protocol without workspace-derived details. Endpoint discovery may publish a non-secret per-session pipe/socket locator in the private runtime directory, but never the granted capability. Discovery files need the same owner/mode, symlink, stale-cleanup, and exact-path protections as the endpoint.

## 4. Representative host support

These versions are representative current stable baselines on the research date, not proposed minimum-version promises. Host releases can move independently of dot-orbit, and none of the three vendor configuration references publishes an enduring promise to accept only one MCP wire revision. The C# SDK's documented dual-era compatibility is therefore part of the adapter requirement.

### Codex

The official Codex release feed identifies **`rust-v0.163.0`**, released 2026-10-15, as the current stable release on the research date ([OpenAI Codex releases](https://github.com/openai/codex/releases), retrieved 2026-10-20).

The official [Codex configuration reference](https://developers.openai.com/codex/config-reference) defines `mcp_servers.<id>.command` and `args` for a stdio launcher, `cwd` and `env` where needed, `enabled` to disable a configured server without deleting it, optional tool allow/deny lists, `required`, `startup_timeout_sec`, and `tool_timeout_sec` (retrieved 2026-10-20). OpenAI's [Docs MCP guide](https://developers.openai.com/learn/docs-mcp) says the CLI and IDE extension share MCP configuration and documents `codex mcp add`/`codex mcp list` (retrieved 2026-10-20).

Representative developer configuration:

```toml
[mcp_servers.dot_orbit]
command = "/absolute/path/to/dot-orbit-mcp"
args = ["--stdio"]
enabled = true
required = false
startup_timeout_sec = 10
tool_timeout_sec = 60
```

No environment secret is required. Codex approval settings are defence in depth for host-side tool invocation; they must not replace dot-orbit's proposal review/apply boundary.

### Visual Studio Code

The official VS Code release feed identifies **1.142.0**, released 2026-10-14, as the stable release on the research date; 1.143 was still an Insiders train ([VS Code releases](https://github.com/microsoft/vscode/releases), retrieved 2026-10-20).

VS Code's official [MCP server documentation](https://code.visualstudio.com/docs/agent-customization/mcp-servers) supports local stdio servers and describes the trust and tool-confirmation UI (retrieved 2026-10-20). Its [MCP configuration reference](https://code.visualstudio.com/docs/agents/reference/mcp-configuration) defines a workspace `.vscode/mcp.json` with a top-level `servers` object, and `type`, `command`, `args`, `cwd`, and `env` for stdio servers. It also documents import of portable `.mcp.json` files whose top-level key is `mcpServers`, plus `code --add-mcp` (retrieved 2026-10-20). The extension API guide says VS Code implements MCP and supports local stdio servers ([VS Code MCP extension guide](https://code.visualstudio.com/api/extension-guides/ai/mcp), retrieved 2026-10-20).

Representative workspace configuration:

```json
{
  "servers": {
    "dotOrbit": {
      "type": "stdio",
      "command": "/absolute/path/to/dot-orbit-mcp",
      "args": ["--stdio"]
    }
  }
}
```

The prototype should use user-level configuration for real workspace data, not commit a machine-specific executable path or server enablement to `.vscode/mcp.json`.

### Claude Code and Claude Desktop

The official Claude Code release feed identifies **v2.1.304**, released 2026-10-19, as the current release on the research date ([Claude Code releases](https://github.com/anthropics/claude-code/releases), retrieved 2026-10-20). Its release notes include fixes for servers negotiating protocol 2026-07-28, which is direct evidence that the current client line exercises the modern protocol.

Anthropic's official [Claude Code MCP documentation](https://code.claude.com/docs/en/mcp) supports local stdio servers and documents this command form (retrieved 2026-10-20):

```text
claude mcp add --transport stdio dot-orbit -- /absolute/path/to/dot-orbit-mcp --stdio
```

The same documentation defines a JSON `mcpServers` object with `command` and `args`. It also documents unusually tight process shutdown behaviour: on macOS/Linux, Claude Code sends SIGINT, waits 100 ms, sends SIGTERM, waits a further 400 ms, then sends SIGKILL; on Windows it closes stdin and kills the process tree 300 ms later. The documented graceful Windows stdin behaviour requires Claude Code 2.1.296 or later.

Claude Desktop's official local-server help now presents Desktop Extensions (`.mcpb`) as the supported installation experience for locally running servers and allows a custom extension to be installed from Settings ([Claude Desktop local MCP help](https://support.claude.com/en/articles/10949351-getting-started-with-local-mcp-servers-on-claude-desktop), retrieved 2026-10-20). It confirms support for local binary MCP servers but does not publish a current Desktop application version or an exact wire-protocol revision on that page.

For issue #125, use Claude Code's developer configuration to test the executable directly. Treat a Claude Desktop `.mcpb` as later packaging work, not part of the secure-IPC prototype.

## 5. Minimal synthetic adapter slice

### dot-orbit conclusion

Evaluate a dedicated console adapter against the possibility of an early MCP-only route in the existing executable:

| Shape | Benefit | Cost/risk |
| --- | --- | --- |
| Separate `dot-orbit-mcp` console executable | Natural stdio process model; no desktop UI bootstrap; smaller privilege and dependency surface; easier to keep stdout protocol-only; crash lifetime is independent of the desktop. | Packaging must carry and locate a second executable for every runtime. |
| Existing desktop executable with `--mcp-stdio` | One packaged executable and one version source. | The current desktop is a GUI `WinExe`; it would need a fully separate startup path, redirected-stdio proof on all platforms, and strict prevention of UI, workspace-key, logging, and stdout initialization. This expands the trusted child-process surface. |

The prototype packages and launches a dedicated adapter plus a stand-alone `WinExe`-shaped surrogate under redirected stdio. Local macOS arm64 evidence shows both self-contained shapes initialize through the official SDK, list the same two tools, keep stdout protocol-clean, and exit on EOF. That establishes cross-output-type mechanics only: the surrogate has no Avalonia, workspace, or persistence references and does not exercise `src/DotOrbit.Desktop/Program.cs`. Consequently the dedicated adapter is the selected route. An existing-executable route remains a possible later optimization only if the real desktop entry point and all supported packages prove the early-startup isolation invariant.

The smallest useful end-to-end prototype is:

```text
representative MCP host or official SDK client
        │ newline-delimited MCP over child-process stdio
        ▼
dot-orbit MCP stdio process
        │ bounded same-user pipe/socket RPC
        ▼
running dot-orbit desktop broker
        │
        ├── synthetic status DTO
        └── synthetic proposal receipt (no real proposal ingress)
```

The adapter should expose only two obviously synthetic tools:

1. `orbit_status`: returns availability, gate state, and fixed synthetic capability metadata. It must not return Task text, paths, database details, or keys.
2. `orbit_submit_synthetic_proposal`: submits a fixed or tightly bounded synthetic proposal to the desktop and returns a receipt saying it awaits local review. It must not apply anything and should not touch the real workspace model.

Use bounded newline-delimited UTF-8 JSON envelopes for the private broker. This is a private, versioned protocol rather than MCP, but the one-frame-per-line form is directly inspectable and avoids platform-dependent partial-read assumptions. The broker contract needs:

- a fixed protocol version and operation discriminator;
- non-secret endpoint/session locator plus the post-consent, connection-bound capability;
- request identifier and, for submission, idempotency identifier;
- strict maximum frame, string, and collection sizes before allocation;
- one response per request plus explicit cancellation;
- no arbitrary type names, reflection-based polymorphism, file paths, commands, or environment-variable expansion; and
- fixed error codes that do not echo request or workspace content.

The adapter's MCP tool handler maps one validated MCP call to one broker request. The desktop revalidates every broker request and constructs the returned DTO or proposal receipt itself. The adapter never forwards an MCP-supplied serialized object directly into persistence.

The isolated prototype evidence covers:

- both gate-off combinations create no broker endpoint and report unavailable;
- an official C# SDK client can initialize each packaged shape, list exactly the two synthetic tools, and call the synthetic read and submission;
- synthetic submission returns a bounded, idempotent `pending-local-review` receipt and has no apply operation or real proposal-ingress reference;
- MCP cancellation reaches the broker and leaves the session usable;
- stdin EOF exits cleanly, broker restart/revocation paths leave no durable adapter state, and stdout remains MCP-only;
- Unix modes and peer credentials plus Windows owner/DACL/client-SID checks enforce the same-user boundary; and
- a Linux CI test attempts the exact socket from another account when the runner supports passwordless `sudo`.

Production admission still requires evidence this isolated slice cannot supply: direct smoke tests from current Codex, VS Code, and Claude releases; delivery into the real proposal Review flow; explicit SIGINT/SIGTERM, forced-adapter-exit, and desktop-exit exercises; the supported Windows/macOS/Linux package matrix including the Linux other-user test; and rendered human review of the consent and revocation interaction. None of those unmet items is implied by the synthetic receipt or the local macOS SDK-client run.

## 6. Exact package additions and repository policy

### Sourced repository facts

The repository enables central package management and transitive pinning in `Directory.Packages.props`. It targets `net10.0`, enables nullable analysis and warnings-as-errors, generates lock files, and uses locked restore in continuous integration. `global.json` pins SDK `10.0.401` with no preview SDKs and no roll-forward. Existing projects check in `packages.lock.json` files.

The [`ModelContextProtocol` 2.2.0 NuGet metadata](https://www.nuget.org/packages/ModelContextProtocol/2.2.0) has an exact dependency on `ModelContextProtocol.Core` 2.2.0 and, for .NET 10, dependencies including Microsoft.Extensions hosting abstractions 10.0.10 or later (retrieved 2026-10-20). The official getting-started example uses the full Generic Host. The current stable [`Microsoft.Extensions.Hosting` package is 10.0.13](https://www.nuget.org/packages/Microsoft.Extensions.Hosting/10.0.13); the 11.x line is prerelease on the research date (retrieved 2026-10-20).

### dot-orbit conclusion

Add these central pins, without changing unrelated package versions:

```xml
<PackageVersion Include="ModelContextProtocol" Version="2.2.0" />
<PackageVersion Include="Microsoft.Extensions.Hosting" Version="10.0.13" />
```

The adapter project should reference both packages without local versions and check in its generated `packages.lock.json`. A direct pin for `ModelContextProtocol.Core` is unnecessary unless production code directly references that package; central transitive pinning and the exact dependency from `ModelContextProtocol` will lock it at 2.2.0.

Run locked restore, deterministic build, the adapter/broker tests, and the existing affected suite on the exact commit before publication. Package the adapter as part of the supported application distribution rather than requiring a global `dotnet` runtime or floating tool install.

## 7. Remaining uncertainty and decisions

1. **Host compatibility floors:** the cited vendor pages prove current representative releases and configuration, but do not define a durable minimum host version for dot-orbit. Establishing a supported floor requires a compatibility matrix against actual packaged builds.
2. **Exact host wire revisions:** Codex and VS Code product configuration pages do not pin a single MCP revision. Claude Code's current release notes prove 2026-07-28 negotiation work, not a perpetual compatibility contract. The official C# SDK's backward compatibility is the safer contract.
3. **Same-user application identity:** platform IPC permissions and peer credentials exclude or identify OS users/processes, not the human-meaningful host application. The desktop's peer is the adapter, not necessarily its parent host, and no cross-platform official API found in this research proves that the host is Codex, VS Code, or Claude. First-connection consent and revocable capabilities contain this risk but do not eliminate a malicious same-user process.
4. **Unix socket location:** choose the concrete per-user runtime directory only after checking dot-orbit's existing platform storage conventions and failure recovery. The security requirements are owner validation, a non-symlink private parent, restrictive modes, exact-path cleanup, and no silent fallback to a public directory.
5. **Read approval lifetime:** issue #125 requires first-connection consent and reads require no prompt per call. The prototype must still determine the short maximum lifetime, whether a reconnect in the same desktop session prompts again, and exactly what revocation UI is visible before real workspace reads are exposed.
6. **Proposal idempotency:** the synthetic slice should prove a request identifier and retry outcome before a host can submit real proposals. Timeout after desktop acceptance must not create an accidental duplicate proposal.
7. **Desktop packaging:** Claude Desktop's current extension packaging and the install/discovery story for all hosts are release work. They should not expand issue #125 beyond proving the secure local boundary.
