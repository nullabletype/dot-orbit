using System.Net.Sockets;

namespace DotOrbit.McpIpcPrototype.Tests;

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
}
