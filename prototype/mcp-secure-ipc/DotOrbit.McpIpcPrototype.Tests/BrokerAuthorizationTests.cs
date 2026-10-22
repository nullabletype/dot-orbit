namespace DotOrbit.McpIpcPrototype.Tests;

[Collection(ProcessTestGroup.Name)]
public sealed class BrokerAuthorizationTests
{
    [Fact]
    public async Task UnsupportedBootstrapScopeIsDeniedWithoutConsentOrCapability()
    {
        using var directory = new TemporaryDirectory();
        await using var host = PrototypeProcess.StartHost(directory.RuntimeRoot);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await host.WaitForErrorAsync("broker=ready", timeout.Token);
        var endpoint = await LocalEndpoint.DiscoverBootstrapAsync(directory.RuntimeRoot, timeout.Token);
        await using var bootstrap = await LocalEndpoint.ConnectAsync(endpoint, timeout.Token);

        await JsonLineCodec.WriteAsync(
            bootstrap.Stream,
            new BootstrapRequest(
                PrototypeProtocol.Version,
                "request-capability",
                "raw-test",
                "1.0",
                ["workspace.apply"]),
            PrototypeJsonContext.Default.BootstrapRequest,
            timeout.Token);
        var response = await JsonLineCodec.ReadAsync(
            bootstrap.Stream,
            PrototypeJsonContext.Default.BootstrapResponse,
            timeout.Token);

        Assert.NotNull(response);
        Assert.Equal("error", response.Type);
        Assert.Equal("bootstrap-invalid", response.Error);
        Assert.Null(response.Endpoint);
        Assert.Null(response.Token);
    }

    [Fact]
    public async Task UnsupportedBootstrapProtocolVersionIsRejected()
    {
        using var directory = new TemporaryDirectory();
        await using var host = PrototypeProcess.StartHost(directory.RuntimeRoot);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await host.WaitForErrorAsync("broker=ready", timeout.Token);
        var endpoint = await LocalEndpoint.DiscoverBootstrapAsync(directory.RuntimeRoot, timeout.Token);
        await using var bootstrap = await LocalEndpoint.ConnectAsync(endpoint, timeout.Token);

        await JsonLineCodec.WriteAsync(
            bootstrap.Stream,
            new BootstrapRequest(
                PrototypeProtocol.Version + 1,
                "request-capability",
                "raw-test",
                "1.0",
                ["synthetic.read"]),
            PrototypeJsonContext.Default.BootstrapRequest,
            timeout.Token);
        var response = await JsonLineCodec.ReadAsync(
            bootstrap.Stream,
            PrototypeJsonContext.Default.BootstrapResponse,
            timeout.Token);

        Assert.Equal("protocol-version-unsupported", response?.Error);
        Assert.Null(response?.Endpoint);
        Assert.Null(response?.Token);
    }

    [Fact]
    public async Task MissingBootstrapScopesReturnFixedErrorAndBrokerContinues()
    {
        using var directory = new TemporaryDirectory();
        await using var host = PrototypeProcess.StartHost(directory.RuntimeRoot);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await host.WaitForErrorAsync("broker=ready", timeout.Token);
        var endpoint = await LocalEndpoint.DiscoverBootstrapAsync(directory.RuntimeRoot, timeout.Token);
        await using (var bootstrap = await LocalEndpoint.ConnectAsync(endpoint, timeout.Token))
        {
            await bootstrap.Stream.WriteAsync(
                "{\"version\":1,\"type\":\"request-capability\",\"clientName\":\"raw-test\",\"clientVersion\":\"1.0\"}\n"u8.ToArray(),
                timeout.Token);
            var response = await JsonLineCodec.ReadAsync(
                bootstrap.Stream,
                PrototypeJsonContext.Default.BootstrapResponse,
                timeout.Token);
            Assert.Equal("bootstrap-invalid", response?.Error);
        }

        await host.WaitForErrorAsync("broker=ready", timeout.Token);
        await using var client = new DesktopBrokerClient(directory.RuntimeRoot, "next-client", "1.0");
        var read = client.ReadAsync(0, timeout.Token);
        await host.WaitForErrorAsync("consent=pending", timeout.Token);
        await host.SendControlAsync("approve");
        Assert.Equal("available", (await read).Status);
    }

    [Fact]
    public async Task ReadOnlyCapabilityCannotSubmit()
    {
        using var directory = new TemporaryDirectory();
        await using var host = PrototypeProcess.StartHost(directory.RuntimeRoot);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await host.WaitForErrorAsync("broker=ready", timeout.Token);
        var endpoint = await LocalEndpoint.DiscoverBootstrapAsync(directory.RuntimeRoot, timeout.Token);
        await using var bootstrap = await LocalEndpoint.ConnectAsync(endpoint, timeout.Token);

        await JsonLineCodec.WriteAsync(
            bootstrap.Stream,
            new BootstrapRequest(
                PrototypeProtocol.Version,
                "request-capability",
                "raw-test",
                "1.0",
                ["synthetic.read"]),
            PrototypeJsonContext.Default.BootstrapRequest,
            timeout.Token);
        await host.WaitForErrorAsync("consent=pending", timeout.Token);
        await host.SendControlAsync("approve");
        var grant = await JsonLineCodec.ReadAsync(
            bootstrap.Stream,
            PrototypeJsonContext.Default.BootstrapResponse,
            timeout.Token);

        Assert.NotNull(grant);
        Assert.NotNull(grant.Endpoint);
        Assert.NotNull(grant.Token);
        await using var session = await LocalEndpoint.ConnectAsync(grant.Endpoint, timeout.Token);
        await JsonLineCodec.WriteAsync(
            session.Stream,
            new SessionMessage(PrototypeProtocol.Version, "bind", Token: grant.Token),
            PrototypeJsonContext.Default.SessionMessage,
            timeout.Token);
        var bound = await JsonLineCodec.ReadAsync(
            session.Stream,
            PrototypeJsonContext.Default.SessionMessage,
            timeout.Token);
        Assert.Equal("bound", bound?.Type);

        await JsonLineCodec.WriteAsync(
            session.Stream,
            new SessionMessage(
                PrototypeProtocol.Version,
                "request",
                "1",
                "submit",
                "must-not-submit"),
            PrototypeJsonContext.Default.SessionMessage,
            timeout.Token);
        var denied = await JsonLineCodec.ReadAsync(
            session.Stream,
            PrototypeJsonContext.Default.SessionMessage,
            timeout.Token);

        Assert.Equal("operation-denied", denied?.Error);
        Assert.Null(denied?.Result);
    }

    [Fact]
    public async Task FifthLiveSessionIsDeniedByPublishedLimit()
    {
        using var directory = new TemporaryDirectory();
        await using var host = PrototypeProcess.StartHost(directory.RuntimeRoot);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await host.WaitForErrorAsync("broker=ready", timeout.Token);
        var clients = new List<DesktopBrokerClient>();
        try
        {
            for (var index = 0; index < PrototypeProtocol.MaximumSessions; index++)
            {
                var client = new DesktopBrokerClient(directory.RuntimeRoot, $"client-{index}", "1.0");
                clients.Add(client);
                var read = client.ReadAsync(0, timeout.Token);
                await host.WaitForErrorAsync("consent=pending", timeout.Token);
                await host.SendControlAsync("approve");
                Assert.Equal("available", (await read).Status);
                await host.WaitForErrorAsync("broker=ready", timeout.Token);
            }

            await using var excess = new DesktopBrokerClient(directory.RuntimeRoot, "client-excess", "1.0");
            var denied = excess.ReadAsync(0, timeout.Token);
            await host.WaitForErrorAsync("consent=pending", timeout.Token);
            await host.SendControlAsync("approve");
            var exception = await Assert.ThrowsAsync<BrokerUnavailableException>(async () => await denied);

            Assert.Equal("session-limit", exception.Code);
        }
        finally
        {
            foreach (var client in clients)
            {
                await client.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task FifthConcurrentRequestIsRejectedAsBusy()
    {
        using var directory = new TemporaryDirectory();
        await using var host = PrototypeProcess.StartHost(directory.RuntimeRoot);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await host.WaitForErrorAsync("broker=ready", timeout.Token);
        var endpoint = await LocalEndpoint.DiscoverBootstrapAsync(directory.RuntimeRoot, timeout.Token);
        await using var bootstrap = await LocalEndpoint.ConnectAsync(endpoint, timeout.Token);
        await JsonLineCodec.WriteAsync(
            bootstrap.Stream,
            new BootstrapRequest(
                PrototypeProtocol.Version,
                "request-capability",
                "raw-test",
                "1.0",
                ["synthetic.read"]),
            PrototypeJsonContext.Default.BootstrapRequest,
            timeout.Token);
        await host.WaitForErrorAsync("consent=pending", timeout.Token);
        await host.SendControlAsync("approve");
        var grant = await JsonLineCodec.ReadAsync(
            bootstrap.Stream,
            PrototypeJsonContext.Default.BootstrapResponse,
            timeout.Token);
        Assert.NotNull(grant?.Endpoint);
        Assert.NotNull(grant.Token);

        await using var session = await LocalEndpoint.ConnectAsync(grant.Endpoint, timeout.Token);
        await JsonLineCodec.WriteAsync(
            session.Stream,
            new SessionMessage(PrototypeProtocol.Version, "bind", Token: grant.Token),
            PrototypeJsonContext.Default.SessionMessage,
            timeout.Token);
        Assert.Equal(
            "bound",
            (await JsonLineCodec.ReadAsync(
                session.Stream,
                PrototypeJsonContext.Default.SessionMessage,
                timeout.Token))?.Type);

        for (var index = 1; index <= PrototypeProtocol.MaximumOutstandingRequests + 1; index++)
        {
            await JsonLineCodec.WriteAsync(
                session.Stream,
                new SessionMessage(
                    PrototypeProtocol.Version,
                    "request",
                    index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "read",
                    DelayMilliseconds: 5000),
                PrototypeJsonContext.Default.SessionMessage,
                timeout.Token);
        }

        var busy = await JsonLineCodec.ReadAsync(
            session.Stream,
            PrototypeJsonContext.Default.SessionMessage,
            timeout.Token);
        Assert.Equal("5", busy?.Id);
        Assert.Equal("busy", busy?.Error);
        Assert.Null(busy?.Result);

        for (var index = 1; index <= PrototypeProtocol.MaximumOutstandingRequests; index++)
        {
            await JsonLineCodec.WriteAsync(
                session.Stream,
                new SessionMessage(
                    PrototypeProtocol.Version,
                    "cancel",
                    index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                PrototypeJsonContext.Default.SessionMessage,
                timeout.Token);
        }
    }

    [Fact]
    public async Task UnboundGrantsExpireAndReleaseSessionCapacity()
    {
        using var directory = new TemporaryDirectory();
        await using var host = PrototypeProcess.StartHost(directory.RuntimeRoot);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await host.WaitForErrorAsync("broker=ready", timeout.Token);

        for (var index = 0; index < PrototypeProtocol.MaximumSessions; index++)
        {
            var grant = await RequestGrantAsync(host, directory.RuntimeRoot, timeout.Token);
            Assert.Equal("capability", grant.Type);
            Assert.NotNull(grant.Endpoint);
            Assert.NotNull(grant.Token);
            await host.WaitForErrorAsync("broker=ready", timeout.Token);
        }

        var limited = await RequestGrantAsync(host, directory.RuntimeRoot, timeout.Token);
        Assert.Equal("session-limit", limited.Error);
        await host.WaitForErrorAsync("broker=ready", timeout.Token);

        await Task.Delay(PrototypeProtocol.GrantLifetime + TimeSpan.FromSeconds(1), timeout.Token);
        var recovered = await RequestGrantAsync(host, directory.RuntimeRoot, timeout.Token);

        Assert.Equal("capability", recovered.Type);
        Assert.NotNull(recovered.Endpoint);
        Assert.NotNull(recovered.Token);
    }

    private static async Task<BootstrapResponse> RequestGrantAsync(
        PrototypeProcess host,
        string runtimeRoot,
        CancellationToken cancellationToken)
    {
        var endpoint = await LocalEndpoint.DiscoverBootstrapAsync(runtimeRoot, cancellationToken);
        await using var bootstrap = await LocalEndpoint.ConnectAsync(endpoint, cancellationToken);
        await JsonLineCodec.WriteAsync(
            bootstrap.Stream,
            new BootstrapRequest(
                PrototypeProtocol.Version,
                "request-capability",
                "abandoned-client",
                "1.0",
                ["synthetic.read"]),
            PrototypeJsonContext.Default.BootstrapRequest,
            cancellationToken);
        await host.WaitForErrorAsync("consent=pending", cancellationToken);
        await host.SendControlAsync("approve");
        return await JsonLineCodec.ReadAsync(
            bootstrap.Stream,
            PrototypeJsonContext.Default.BootstrapResponse,
            cancellationToken)
            ?? throw new InvalidOperationException("bootstrap-response-missing");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ba-{Guid.NewGuid():N}"[..11]);
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
