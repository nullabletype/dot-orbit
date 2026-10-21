using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

using DotOrbit.Mcp.Ipc;

namespace DotOrbit.Mcp.Ipc.Tests;

public sealed class LocalEndpointTests
{
    [Fact]
    public async Task WindowsEndpointUsesVerifiedCurrentUserAcl()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var listener = await LocalEndpoint.CreateSessionAsync("unused-on-windows");
        var accepting = listener.AcceptAsync(TestContext.Current.CancellationToken).AsTask();
        await using var client = await LocalEndpoint.ConnectAsync(
            listener.Address,
            TestContext.Current.CancellationToken);
        await using var server = await accepting;

        Assert.Equal("pipe", listener.Address.Transport);
        using var current = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        Assert.Equal(
            $"windows-sid:{current.User?.Value}",
            server.PeerIdentity.OperatingSystemAccount);
    }

    [Fact]
    public async Task UnixEndpointUsesPrivateModesVerifiesPeerAndCleansOwnedFiles()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var runtimeRoot = Path.Combine(directory.Path, "r");
        var listener = await LocalEndpoint.CreateBootstrapAsync(
            runtimeRoot,
            TestContext.Current.CancellationToken);
        var socketPath = listener.Address.Address;
        var descriptorPath = Path.Combine(runtimeRoot, "mcp-bootstrap.json");

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(runtimeRoot));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(socketPath));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(descriptorPath));

        var accepting = listener.AcceptAsync(TestContext.Current.CancellationToken).AsTask();
        await using var client = await LocalEndpoint.ConnectAsync(
            listener.Address,
            TestContext.Current.CancellationToken);
        await using var server = await accepting;

        var platform = OperatingSystem.IsLinux() ? "linux" : "macos";
        Assert.Equal(
            $"{platform}-uid:{GetEffectiveUserId().ToString(CultureInfo.InvariantCulture)}",
            server.PeerIdentity.OperatingSystemAccount);

        await listener.DisposeAsync();

        Assert.False(File.Exists(socketPath));
        Assert.False(File.Exists(descriptorPath));
    }

    [Fact]
    public async Task UnixRuntimeRootSymlinkIsRejected()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var target = Path.Combine(directory.Path, "target");
        var link = Path.Combine(directory.Path, "r");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(link, target);

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await LocalEndpoint.CreateBootstrapAsync(link, TestContext.Current.CancellationToken));

        Assert.Equal("filesystem-link-rejected", exception.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    [Fact]
    public async Task UnixRegularFileNamedByStaleDescriptorIsNeverDeleted()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var runtimeRoot = Path.Combine(directory.Path, "r");
        Directory.CreateDirectory(
            runtimeRoot,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var target = Path.Combine(runtimeRoot, "bootstrap-collision.sock");
        await File.WriteAllTextAsync(target, "sentinel", TestContext.Current.CancellationToken);
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var descriptor = $$"""{"version":1,"transport":"unix","address":"{{target}}"}""";
        var descriptorPath = Path.Combine(runtimeRoot, "mcp-bootstrap.json");
        await File.WriteAllTextAsync(descriptorPath, descriptor, TestContext.Current.CancellationToken);
        File.SetUnixFileMode(descriptorPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await LocalEndpoint.CreateBootstrapAsync(runtimeRoot, TestContext.Current.CancellationToken));

        Assert.True(File.Exists(target));
        Assert.Equal("sentinel", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SecondUnixBootstrapCannotReplaceActiveEndpoint()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var runtimeRoot = Path.Combine(directory.Path, "r");
        await using var first = await LocalEndpoint.CreateBootstrapAsync(
            runtimeRoot,
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<IOException>(async () =>
            await LocalEndpoint.CreateBootstrapAsync(runtimeRoot, TestContext.Current.CancellationToken));

        Assert.Equal("broker-already-running", exception.Message);
        Assert.True(File.Exists(first.Address.Address));
    }

    [Fact]
    public async Task LinuxOtherUserCannotConnectEvenWithExactSocketPath()
    {
        if (!OperatingSystem.IsLinux()
            || !string.Equals(
                Environment.GetEnvironmentVariable("DOTORBIT_REQUIRE_OTHER_USER_TEST"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var runtimeRoot = Path.Combine(directory.Path, "r");
        await using var listener = await LocalEndpoint.CreateSessionAsync(runtimeRoot);
        using var probe = Process.Start(new ProcessStartInfo
        {
            FileName = "sudo",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList =
            {
                "-n",
                "-u",
                "nobody",
                "python3",
                "-c",
                "import socket,sys; s=socket.socket(socket.AF_UNIX); s.connect(sys.argv[1])",
                listener.Address.Address,
            },
        }) ?? throw new InvalidOperationException("other-user-probe-did-not-start");

        await probe.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.NotEqual(0, probe.ExitCode);

        var accepting = listener.AcceptAsync(TestContext.Current.CancellationToken).AsTask();
        await using var sameUser = await LocalEndpoint.ConnectAsync(
            listener.Address,
            TestContext.Current.CancellationToken);
        await using var accepted = await accepting;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"do-{Guid.NewGuid():N}"[..11]);
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    [UnsupportedOSPlatform("windows")]
    private static extern uint GetEffectiveUserId();
}
