using System.IO.Pipes;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace DotOrbit.Mcp.Ipc;

internal sealed record LocalPeerIdentity(string OperatingSystemAccount, int? ProcessId);

internal sealed class LocalConnection(
    Stream stream,
    LocalPeerIdentity peerIdentity,
    Func<ValueTask>? dispose = null) : IAsyncDisposable
{
    public Stream Stream { get; } = stream;
    public LocalPeerIdentity PeerIdentity { get; } = peerIdentity;

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

internal static partial class LocalEndpoint
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
            return await CreateWindowsBootstrapListenerAsync(runtimeRoot, cancellationToken).ConfigureAwait(false);
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
            return new EndpointAddress("pipe", GetWindowsBootstrapName(runtimeRoot));
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
            LocalMcpJsonContext.Default.EndpointDescriptor,
            cancellationToken).ConfigureAwait(false)
            ?? throw new ProtocolException("descriptor-invalid");
        if (descriptor.Version != LocalMcpProtocol.Version || descriptor.Transport != "unix")
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
                var peer = UnixPeerCredentials.VerifySameUser(socket);
                return new LocalConnection(
                    new NetworkStream(socket, ownsSocket: true),
                    UnixPeerIdentity(peer));
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

                socket.Listen(LocalMcpProtocol.MaximumSessions);
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
                    new EndpointDescriptor(LocalMcpProtocol.Version, endpoint.Transport, endpoint.Address),
                    LocalMcpJsonContext.Default.EndpointDescriptor,
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
            || File.GetUnixFileMode(endpoint.Address) != PrivateFileMode
            || !IsUnixSocket(endpoint.Address))
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

    [UnsupportedOSPlatform("windows")]
    private static bool IsUnixSocket(string path)
    {
        const int fileTypeMask = 0xf000;
        const int socketFileType = 0xc000;
        var status = Marshal.AllocHGlobal(256);
        try
        {
            int result;
            int mode;
            if (OperatingSystem.IsLinux())
            {
                const int currentWorkingDirectory = -100;
                const int doNotFollowLinks = 0x100;
                const uint requestFileType = 0x1;
                result = StatX(
                    currentWorkingDirectory,
                    path,
                    doNotFollowLinks,
                    requestFileType,
                    status);
                mode = unchecked((ushort)Marshal.ReadInt16(status, 28));
            }
            else if (OperatingSystem.IsMacOS())
            {
                result = LStat(path, status);
                var modeOffset = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 8 : 4;
                mode = unchecked((ushort)Marshal.ReadInt16(status, modeOffset));
            }
            else
            {
                throw new PlatformNotSupportedException();
            }

            if (result != 0)
            {
                throw new IOException("stale-endpoint-untrusted");
            }

            return (mode & fileTypeMask) == socketFileType;
        }
        finally
        {
            Marshal.FreeHGlobal(status);
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<LocalEndpointListener> CreateWindowsBootstrapListenerAsync(
        string runtimeRoot,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return CreateWindowsListener(GetWindowsBootstrapName(runtimeRoot));
            }
            catch (IOException) when (attempt < 39)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new IOException("bootstrap-endpoint-unavailable");
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
            LocalMcpProtocol.MaximumFrameBytes,
            LocalMcpProtocol.MaximumFrameBytes,
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
            await client.ConnectAsync(
                    checked((int)LocalMcpProtocol.BootstrapHandshakeLifetime.TotalMilliseconds),
                    cancellationToken)
                .ConfigureAwait(false);
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            var currentUser = identity.User ?? throw new UnauthorizedAccessException("current-user-unavailable");
            VerifyWindowsPipeAcl(client, currentUser);
            return new LocalConnection(client, WindowsPeerIdentity(currentUser, null));
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
    private static string GetWindowsBootstrapName(string runtimeRoot)
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var sid = identity.User?.Value ?? throw new UnauthorizedAccessException("current-user-unavailable");
        return BuildWindowsBootstrapName(sid, runtimeRoot);
    }

    internal static string BuildWindowsBootstrapName(string operatingSystemSid, string runtimeRoot)
    {
        if (string.IsNullOrWhiteSpace(operatingSystemSid)
            || string.IsNullOrWhiteSpace(runtimeRoot)
            || !Path.IsPathFullyQualified(runtimeRoot))
        {
            throw new ProtocolException("runtime-root-invalid");
        }

        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtimeRoot))
            .ToUpperInvariant();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{operatingSystemSid}\0{canonicalRoot}"));
        return $"dot-orbit-mcp-bootstrap-{Convert.ToHexStringLower(digest.AsSpan(0, 8))}";
    }

    [UnsupportedOSPlatform("windows")]
    private static LocalPeerIdentity UnixPeerIdentity(VerifiedUnixPeer peer)
    {
        var platform = OperatingSystem.IsLinux() ? "linux" : "macos";
        return new(
            $"{platform}-uid:{peer.UserId.ToString(CultureInfo.InvariantCulture)}",
            peer.ProcessId);
    }

    [SupportedOSPlatform("windows")]
    private static LocalPeerIdentity WindowsPeerIdentity(
        SecurityIdentifier user,
        int? processId) =>
        new($"windows-sid:{user.Value}", processId);

    [SupportedOSPlatform("windows")]
    private static int? GetWindowsClientProcessId(NamedPipeServerStream pipe) =>
        GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId)
            ? checked((int)processId)
            : null;

    [DllImport("libc", EntryPoint = "statx", SetLastError = true,
        BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int StatX(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        uint mask,
        IntPtr status);

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true,
        BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int LStat(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        IntPtr status);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static partial bool GetNamedPipeClientProcessId(
        Microsoft.Win32.SafeHandles.SafePipeHandle pipe,
        out uint clientProcessId);

    [UnsupportedOSPlatform("windows")]
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
                var peer = UnixPeerCredentials.VerifySameUser(accepted);
                return new LocalConnection(
                    new NetworkStream(accepted, ownsSocket: true),
                    UnixPeerIdentity(peer));
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
                        LocalMcpJsonContext.Default.EndpointDescriptor);
                    if (descriptor?.Address == path)
                    {
                        File.Delete(descriptorPath);
                    }
                }
                catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
                {
                }
            }

            if (File.Exists(path) && IsUnixSocket(path))
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
            try
            {
                pipe.RunAsClient(() => peerUser = WindowsIdentity.GetCurrent(ifImpersonating: true)?.User);
            }
            catch (Exception exception)
                when ((exception is IOException or InvalidOperationException) && !pipe.IsConnected)
            {
                throw new IOException("peer-disconnected", exception);
            }

            if (peerUser is null || !currentUser.Equals(peerUser))
            {
                pipe.Disconnect();
                throw new UnauthorizedAccessException("peer-user-mismatch");
            }

            return new LocalConnection(
                pipe,
                WindowsPeerIdentity(peerUser, GetWindowsClientProcessId(pipe)));
        }

        public override ValueTask DisposeAsync()
        {
            pipe.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
