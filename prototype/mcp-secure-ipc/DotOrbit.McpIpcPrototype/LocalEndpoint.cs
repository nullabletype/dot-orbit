using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace DotOrbit.McpIpcPrototype;

internal sealed class LocalConnection(Stream stream, Func<ValueTask>? dispose = null) : IAsyncDisposable
{
    public Stream Stream { get; } = stream;

    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync().ConfigureAwait(false);
        if (dispose is not null)
        {
            await dispose().ConfigureAwait(false);
        }
    }
}

internal abstract class LocalEndpointListener : IAsyncDisposable
{
    public abstract EndpointAddress Address { get; }
    public abstract ValueTask<LocalConnection> AcceptAsync(CancellationToken cancellationToken);
    public abstract ValueTask DisposeAsync();
}

internal static class LocalEndpoint
{
    private const string DescriptorName = "mcp-bootstrap.json";
    private static readonly UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static async Task<LocalEndpointListener> CreateBootstrapAsync(
        string runtimeRoot,
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateWindowsListener(GetWindowsBootstrapName());
        }

        PrepareUnixRuntimeRoot(runtimeRoot);
        await RemoveStaleUnixBootstrapAsync(runtimeRoot, cancellationToken).ConfigureAwait(false);
        var listener = CreateUnixListener(
            runtimeRoot,
            "b",
            Path.Combine(runtimeRoot, DescriptorName));
        try
        {
            await PublishDescriptorAsync(runtimeRoot, listener.Address, cancellationToken).ConfigureAwait(false);
            return listener;
        }
        catch
        {
            await listener.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static Task<LocalEndpointListener> CreateSessionAsync(string runtimeRoot)
    {
        if (OperatingSystem.IsWindows())
        {
            return Task.FromResult<LocalEndpointListener>(
                CreateWindowsListener($"dot-orbit-mcp-session-{RandomName()}"));
        }

        PrepareUnixRuntimeRoot(runtimeRoot);
        return Task.FromResult<LocalEndpointListener>(CreateUnixListener(runtimeRoot, "s", null));
    }

    public static async Task<EndpointAddress> DiscoverBootstrapAsync(
        string runtimeRoot,
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return new EndpointAddress("pipe", GetWindowsBootstrapName());
        }

        PrepareUnixRuntimeRoot(runtimeRoot);
        var descriptorPath = Path.Combine(runtimeRoot, DescriptorName);
        RejectLink(descriptorPath);
        await using var stream = new FileStream(
            descriptorPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > 4096)
        {
            throw new ProtocolException("descriptor-invalid");
        }

        var descriptor = await JsonSerializer.DeserializeAsync(
            stream,
            PrototypeJsonContext.Default.EndpointDescriptor,
            cancellationToken).ConfigureAwait(false)
            ?? throw new ProtocolException("descriptor-invalid");
        if (descriptor.Version != PrototypeProtocol.Version || descriptor.Transport != "unix")
        {
            throw new ProtocolException("descriptor-version-unsupported");
        }

        if (string.IsNullOrWhiteSpace(descriptor.Address))
        {
            throw new ProtocolException("descriptor-invalid");
        }

        ValidateUnixEndpointPath(runtimeRoot, descriptor.Address);
        return new EndpointAddress(descriptor.Transport, descriptor.Address);
    }

    public static async Task<LocalConnection> ConnectAsync(
        EndpointAddress endpoint,
        CancellationToken cancellationToken)
    {
        if (endpoint.Transport == "pipe" && OperatingSystem.IsWindows())
        {
            return await ConnectWindowsAsync(endpoint.Address, cancellationToken).ConfigureAwait(false);
        }

        if (endpoint.Transport == "unix" && !OperatingSystem.IsWindows())
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint.Address), cancellationToken)
                    .ConfigureAwait(false);
                UnixPeerCredentials.VerifySameUser(socket);
                return new LocalConnection(new NetworkStream(socket, ownsSocket: true));
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new ProtocolException("endpoint-transport-invalid");
    }

    [UnsupportedOSPlatform("windows")]
    private static UnixEndpointListener CreateUnixListener(
        string runtimeRoot,
        string purpose,
        string? descriptorPath)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var socketPath = Path.Combine(runtimeRoot, $"{purpose}-{RandomSocketName()}.sock");
            ValidateUnixEndpointPath(runtimeRoot, socketPath);
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                socket.Bind(new UnixDomainSocketEndPoint(socketPath));
                File.SetUnixFileMode(socketPath, PrivateFileMode);
                if (File.GetUnixFileMode(socketPath) != PrivateFileMode)
                {
                    throw new UnauthorizedAccessException("endpoint-mode-invalid");
                }

                socket.Listen(PrototypeProtocol.MaximumSessions);
                return new UnixEndpointListener(socket, socketPath, descriptorPath);
            }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                socket.Dispose();
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new IOException("endpoint-name-collisions");
    }

    [UnsupportedOSPlatform("windows")]
    private static void PrepareUnixRuntimeRoot(string runtimeRoot)
    {
        if (string.IsNullOrWhiteSpace(runtimeRoot) || !Path.IsPathFullyQualified(runtimeRoot))
        {
            throw new ProtocolException("runtime-root-invalid");
        }

        var parent = Directory.GetParent(runtimeRoot)
            ?? throw new ProtocolException("runtime-root-invalid");
        if (!parent.Exists)
        {
            throw new DirectoryNotFoundException("runtime-parent-missing");
        }

        RejectLink(parent.FullName);
        if (!Directory.Exists(runtimeRoot))
        {
            Directory.CreateDirectory(runtimeRoot, PrivateDirectoryMode);
        }

        RejectLink(runtimeRoot);
        File.SetUnixFileMode(runtimeRoot, PrivateDirectoryMode);
        if (File.GetUnixFileMode(runtimeRoot) != PrivateDirectoryMode)
        {
            throw new UnauthorizedAccessException("runtime-root-mode-invalid");
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static async Task PublishDescriptorAsync(
        string runtimeRoot,
        EndpointAddress endpoint,
        CancellationToken cancellationToken)
    {
        var descriptorPath = Path.Combine(runtimeRoot, DescriptorName);
        RejectLink(descriptorPath);
        var temporaryPath = Path.Combine(runtimeRoot, $"descriptor-{RandomName()}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                File.SetUnixFileMode(temporaryPath, PrivateFileMode);
                await JsonSerializer.SerializeAsync(
                    stream,
                    new EndpointDescriptor(PrototypeProtocol.Version, endpoint.Transport, endpoint.Address),
                    PrototypeJsonContext.Default.EndpointDescriptor,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, descriptorPath, overwrite: false);
            if (File.GetUnixFileMode(descriptorPath) != PrivateFileMode)
            {
                throw new UnauthorizedAccessException("descriptor-mode-invalid");
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static async Task RemoveStaleUnixBootstrapAsync(
        string runtimeRoot,
        CancellationToken cancellationToken)
    {
        var descriptorPath = Path.Combine(runtimeRoot, DescriptorName);
        if (!File.Exists(descriptorPath))
        {
            return;
        }

        var endpoint = await DiscoverBootstrapAsync(runtimeRoot, cancellationToken).ConfigureAwait(false);
        RejectLink(endpoint.Address);
        if (File.GetUnixFileMode(descriptorPath) != PrivateFileMode
            || !File.Exists(endpoint.Address)
            || File.GetUnixFileMode(endpoint.Address) != PrivateFileMode)
        {
            throw new ProtocolException("stale-endpoint-untrusted");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(250));
        try
        {
            await using var active = await ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
            throw new IOException("broker-already-running");
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionRefused)
        {
            File.Delete(endpoint.Address);
            File.Delete(descriptorPath);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("broker-already-running");
        }
    }

    private static void ValidateUnixEndpointPath(string runtimeRoot, string endpointPath)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtimeRoot));
        var canonicalEndpoint = Path.GetFullPath(endpointPath);
        if (!string.Equals(Path.GetDirectoryName(canonicalEndpoint), canonicalRoot, StringComparison.Ordinal)
            || Encoding.UTF8.GetByteCount(canonicalEndpoint) > 100)
        {
            throw new ProtocolException("endpoint-path-invalid");
        }
    }

    private static void RejectLink(string path)
    {
        var information = Directory.Exists(path)
            ? (FileSystemInfo)new DirectoryInfo(path)
            : new FileInfo(path);
        if (information.Exists && information.LinkTarget is not null)
        {
            throw new UnauthorizedAccessException("filesystem-link-rejected");
        }
    }

    private static string RandomName()
    {
        Span<byte> bytes = stackalloc byte[12];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }

    private static string RandomSocketName()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }

    [SupportedOSPlatform("windows")]
    private static WindowsEndpointListener CreateWindowsListener(string pipeName)
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var currentUser = identity.User ?? throw new UnauthorizedAccessException("current-user-unavailable");
        var network = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
        var security = new PipeSecurity();
        security.SetOwner(currentUser);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            network,
            PipeAccessRights.FullControl,
            AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(
            currentUser,
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            AccessControlType.Allow));

        var pipe = NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            PrototypeProtocol.MaximumFrameBytes,
            PrototypeProtocol.MaximumFrameBytes,
            security,
            HandleInheritability.None);
        VerifyWindowsPipeAcl(pipe, currentUser);
        return new WindowsEndpointListener(pipe, pipeName, currentUser);
    }

    [SupportedOSPlatform("windows")]
    private static async Task<LocalConnection> ConnectWindowsAsync(
        string pipeName,
        CancellationToken cancellationToken)
    {
        var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            VerifyWindowsPipeAcl(client, identity.User!);
            return new LocalConnection(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyWindowsPipeAcl(PipeStream pipe, SecurityIdentifier currentUser)
    {
        var security = pipe.GetAccessControl();
        if (!currentUser.Equals(security.GetOwner(typeof(SecurityIdentifier))))
        {
            throw new UnauthorizedAccessException("pipe-owner-invalid");
        }

        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToArray();
        var expectedRights = PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize;
        var network = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
        var userRule = rules.SingleOrDefault(rule =>
            rule.AccessControlType == AccessControlType.Allow
            && currentUser.Equals(rule.IdentityReference));
        var remoteDeny = rules.SingleOrDefault(rule =>
            rule.AccessControlType == AccessControlType.Deny
            && network.Equals(rule.IdentityReference));
        if (rules.Length != 2
            || rules.Any(rule => rule.IsInherited)
            || userRule is null
            || (userRule.PipeAccessRights & PipeAccessRights.CreateNewInstance) != 0
            || (userRule.PipeAccessRights & expectedRights) != expectedRights
            || remoteDeny is null
            || (remoteDeny.PipeAccessRights & PipeAccessRights.FullControl) != PipeAccessRights.FullControl)
        {
            throw new UnauthorizedAccessException("pipe-acl-invalid");
        }
    }

    [SupportedOSPlatform("windows")]
    private static string GetWindowsBootstrapName()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var sid = identity.User?.Value ?? throw new UnauthorizedAccessException("current-user-unavailable");
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(sid));
        return $"dot-orbit-mcp-bootstrap-{Convert.ToHexStringLower(digest.AsSpan(0, 8))}";
    }

    private sealed class UnixEndpointListener(
        Socket listener,
        string path,
        string? descriptorPath) : LocalEndpointListener
    {
        public override EndpointAddress Address { get; } = new("unix", path);

        public override async ValueTask<LocalConnection> AcceptAsync(CancellationToken cancellationToken)
        {
            var accepted = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                UnixPeerCredentials.VerifySameUser(accepted);
                return new LocalConnection(new NetworkStream(accepted, ownsSocket: true));
            }
            catch
            {
                accepted.Dispose();
                throw;
            }
        }

        public override ValueTask DisposeAsync()
        {
            listener.Dispose();
            if (descriptorPath is not null && File.Exists(descriptorPath))
            {
                try
                {
                    RejectLink(descriptorPath);
                    var descriptor = JsonSerializer.Deserialize(
                        File.ReadAllBytes(descriptorPath),
                        PrototypeJsonContext.Default.EndpointDescriptor);
                    if (descriptor?.Address == path)
                    {
                        File.Delete(descriptorPath);
                    }
                }
                catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
                {
                }
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return ValueTask.CompletedTask;
        }
    }

    [SupportedOSPlatform("windows")]
    private sealed class WindowsEndpointListener(
        NamedPipeServerStream pipe,
        string name,
        SecurityIdentifier currentUser) : LocalEndpointListener
    {
        public override EndpointAddress Address { get; } = new("pipe", name);

        public override async ValueTask<LocalConnection> AcceptAsync(CancellationToken cancellationToken)
        {
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            SecurityIdentifier? peerUser = null;
            pipe.RunAsClient(() => peerUser = WindowsIdentity.GetCurrent(ifImpersonating: true)?.User);
            if (peerUser is null || !currentUser.Equals(peerUser))
            {
                pipe.Disconnect();
                throw new UnauthorizedAccessException("peer-user-mismatch");
            }

            return new LocalConnection(pipe);
        }

        public override ValueTask DisposeAsync()
        {
            pipe.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
