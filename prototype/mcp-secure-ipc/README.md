# Secure local MCP/IPC prototype

This throwaway slice tests the boundary selected by issue #125. It uses synthetic data only and does not reference the real desktop, encrypted workspace, proposal parser, or persistence projects.

## What it proves

- MCP is standard stdio owned by the official C# SDK.
- A dedicated console adapter is the selected stdio shape. A stand-alone `WinExe`-shaped surrogate checks redirected-stdio mechanics only; it does not prove an early route in the real desktop executable.
- Both shapes expose exactly `orbit_status` and `orbit_submit_synthetic_proposal`.
- The running desktop-shaped host owns consent and a private same-user broker.
- Reads need one connection approval, not one prompt per call.
- Submission returns `pending-local-review`; it cannot apply anything.
- Disablement, lock, revocation, cancellation, crash recovery, stale cleanup, frame limits, capability expiry, and capability single use have executable checks.

The prototype deliberately does not prove real workspace reads, real proposal ingress, application-identity authentication, remote MCP, or host configuration editing.

## Run locally

Restore and build from the repository root:

```sh
dotnet restore DotOrbit.slnx --configfile NuGet.Config
dotnet build prototype/mcp-secure-ipc/DotOrbit.McpIpcPrototype.Tests/DotOrbit.McpIpcPrototype.Tests.csproj --configuration Release --no-restore
```

Run the focused suite:

```sh
dotnet test --project prototype/mcp-secure-ipc/DotOrbit.McpIpcPrototype.Tests/DotOrbit.McpIpcPrototype.Tests.csproj --configuration Release --no-build
```

Compare the two self-contained single-file shapes for the current runtime, for example:

```sh
dotnet run --project prototype/mcp-secure-ipc/DotOrbit.McpIpcPrototype.PackageCheck/DotOrbit.McpIpcPrototype.PackageCheck.csproj --configuration Release --no-restore -- --repository-root . --runtime osx-arm64
```

The package check writes only ignored local build output below `artifacts/mcp-prototype-package-check/`. It launches each packaged binary first with immediate EOF, then through the official C# MCP client to initialize and list exactly the two synthetic tools. CI runs it on `win-x64`, `linux-x64`, `osx-x64`, and `osx-arm64` and uploads nothing.

## Manual broker exercise

Choose a short private temporary location and start the desktop-shaped host:

```sh
dotnet run --project prototype/mcp-secure-ipc/DotOrbit.McpIpcPrototype/DotOrbit.McpIpcPrototype.csproj -- host --runtime-root /tmp/dot-orbit-mcp --ai-on --mcp-on
```

The control input accepts `approve`, `deny`, `revoke`, `lock`, `disable`, and `exit`. The test client and MCP process intentionally cannot approve themselves.

Launch either stdio shape from an MCP host:

```text
dot-orbit-mcp-prototype --mcp-stdio --runtime-root /tmp/dot-orbit-mcp
dot-orbit-mcp-adapter-prototype --stdio --runtime-root /tmp/dot-orbit-mcp
```

No environment secret is required or supported. A host configuration should contain only an absolute command path and these arguments. Do not commit a machine-specific path or MCP enablement to the repository.

## Security boundary

On Unix, the runtime directory is exact mode `0700`; descriptors and sockets are `0600`; a symlink at the immediate trusted parent or app-owned runtime leaf and paths outside the private directory are rejected; and kernel peer credentials must match the current effective user. The prototype takes that parent through `--runtime-root`; production must derive it from a trusted platform location rather than accepting arbitrary earlier path components. On Windows, each pipe has a protected explicit current-user DACL plus a Network SID deny rule to exclude remote SMB clients. The ACL is verified on both ends, and the server verifies the impersonated client SID.

Client names are explicitly labelled self-asserted. Approval grants a live connection a 15-second single-use bootstrap capability and the requested synthetic scopes. It does not identify the calling application. Another process already running as the same user remains the principal residual risk.

The internal protocol is strict newline-delimited UTF-8 JSON with a 64 KiB frame limit, bounded depth, unknown-member rejection, fixed errors, maximum four sessions, four outstanding calls admitted by both the adapter and broker per session, and 128 retained idempotency receipts per session. Capabilities never enter files, process arguments, environment variables, URLs, logs, test output, or CI artifacts.

## Review the interaction model

Open `consent-state.html`. Guided scenarios cover first connection, denial, revocation, gate disablement, lock, and restart. Free play makes the important distinction visible: the OS verifies the account; the adapter only asserts its application name.
