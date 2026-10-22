using System.Net.Sockets;

namespace DotOrbit.McpIpcPrototype.Tests;

[Collection(ProcessTestGroup.Name)]
public sealed class DesktopBrokerClientTests
{
    [Fact]
    public async Task ClientRejectsUnsupportedBootstrapResponseVersion()
    {
        using var directory = new TemporaryDirectory();
        await using var listener = await LocalEndpoint.CreateBootstrapAsync(
            directory.RuntimeRoot,
            TestContext.Current.CancellationToken);
        await using var client = new DesktopBrokerClient(directory.RuntimeRoot, "test-client", "1.0");
        var read = client.ReadAsync(0, TestContext.Current.CancellationToken);
        await using var connection = await listener.AcceptAsync(TestContext.Current.CancellationToken);
        var request = await JsonLineCodec.ReadAsync(
            connection.Stream,
            PrototypeJsonContext.Default.BootstrapRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(PrototypeProtocol.Version, request?.Version);
        await JsonLineCodec.WriteAsync(
            connection.Stream,
            new BootstrapResponse(
                PrototypeProtocol.Version + 1,
                "error",
                null,
                null,
                "future-error"),
            PrototypeJsonContext.Default.BootstrapResponse,
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<BrokerUnavailableException>(async () => await read);
        Assert.Equal("protocol-version-unsupported", exception.Code);
    }

    [Fact]
    public async Task FailedSessionConnectionZeroesCapability()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var socketPath = System.IO.Path.Combine(directory.Path, "refused.sock");
        using (var refused = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            refused.Bind(new UnixDomainSocketEndPoint(socketPath));
            refused.Listen(1);
        }

        var capability = Enumerable.Repeat((byte)0x5a, 32).ToArray();
        var endpoint = new EndpointAddress("unix", socketPath);

        await Assert.ThrowsAsync<SocketException>(
            () => DesktopBrokerClient.ConnectSessionAsync(
                endpoint,
                capability,
                TestContext.Current.CancellationToken));

        Assert.All(capability, value => Assert.Equal(0, value));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bc-{Guid.NewGuid():N}"[..11]);
            Directory.CreateDirectory(Path);
            RuntimeRoot = System.IO.Path.Combine(Path, "r");
        }

        public string Path { get; }
        public string RuntimeRoot { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
