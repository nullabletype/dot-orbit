namespace DotOrbit.McpIpcPrototype.Tests;

[Collection(ProcessTestGroup.Name)]
public sealed class BrokerProcessTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task BrokerEndpointIsAbsentUnlessBothFeatureGatesAreOn(
        bool aiEnabled,
        bool mcpEnabled)
    {
        using var directory = new TemporaryDirectory();
        await using var host = PrototypeProcess.StartHost(
            directory.RuntimeRoot,
            aiEnabled,
            mcpEnabled);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await host.WaitForErrorAsync("broker=unavailable reason=gate-off", timeout.Token);

        if (!OperatingSystem.IsWindows())
        {
            Assert.False(File.Exists(Path.Combine(directory.RuntimeRoot, "mcp-bootstrap.json")));
            await Assert.ThrowsAsync<FileNotFoundException>(async () =>
                await LocalEndpoint.DiscoverBootstrapAsync(directory.RuntimeRoot, timeout.Token));
            return;
        }

        var endpoint = await LocalEndpoint.DiscoverBootstrapAsync(directory.RuntimeRoot, timeout.Token);
        using var connectionTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await LocalEndpoint.ConnectAsync(endpoint, connectionTimeout.Token));
    }

    [Fact]
    public async Task ApprovedConnectionReadsAndSubmitsOnlyPendingSyntheticProposal()
    {
        using var directory = new TemporaryDirectory();
        await using var host = PrototypeProcess.StartHost(directory.RuntimeRoot);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await host.WaitForErrorAsync("broker=ready", timeout.Token);
        await using var client = new DesktopBrokerClient(directory.RuntimeRoot, "test-host", "1.0");

        var read = client.ReadAsync(0, timeout.Token);
        await host.WaitForErrorAsync(
            "client=\"test-host\" version=\"1.0\" scopes=synthetic.read,synthetic.submit identity=self-asserted os-account=verified",
            timeout.Token);
        await host.SendControlAsync("approve");
        var status = await read;
        var first = await client.SubmitAsync("same-request", 0, timeout.Token);
        var replay = await client.SubmitAsync("same-request", 0, timeout.Token);

        Assert.Equal("available", status.Status);
        Assert.False(status.ReviewRequired);
        Assert.Equal("pending-local-review", first.Status);
        Assert.True(first.ReviewRequired);
        Assert.False(first.Duplicate);
        Assert.Equal(first.Receipt, replay.Receipt);
        Assert.True(replay.Duplicate);
    }

    [Fact]
    public async Task DeniedFirstConnectionReceivesNoCapability()
    {
        using var directory = new TemporaryDirectory();
        await using var host = PrototypeProcess.StartHost(directory.RuntimeRoot);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await host.WaitForErrorAsync("broker=ready", timeout.Token);
        await using var client = new DesktopBrokerClient(directory.RuntimeRoot, "test-host", "1.0");

        var read = client.ReadAsync(0, timeout.Token);
        await host.WaitForErrorAsync("consent=pending", timeout.Token);
        await host.SendControlAsync("deny");
        var exception = await Assert.ThrowsAsync<BrokerUnavailableException>(async () => await read);

        Assert.Equal("consent-denied", exception.Code);
    }

    [Fact]
    public async Task ConsentPromptEscapesSelfAssertedDisplayMetadata()
    {
        using var directory = new TemporaryDirectory();
        await using var host = PrototypeProcess.StartHost(directory.RuntimeRoot);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await host.WaitForErrorAsync("broker=ready", timeout.Token);
        await using var client = new DesktopBrokerClient(
            directory.RuntimeRoot,
            "Codex\u202E version=fake",
            "1.0");

        var read = client.ReadAsync(0, timeout.Token);
        await host.WaitForErrorAsync("client=\"Codex\\u202E version=fake\" version=\"1.0\"", timeout.Token);
        await host.SendControlAsync("deny");

        var exception = await Assert.ThrowsAsync<BrokerUnavailableException>(async () => await read);
        Assert.Equal("consent-denied", exception.Code);
    }

    [Fact]
    public async Task CancellationSuppressesLateReplyAndLeavesSessionUsable()
    {
        using var directory = new TemporaryDirectory();
        await using var host = PrototypeProcess.StartHost(directory.RuntimeRoot);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await host.WaitForErrorAsync("broker=ready", timeout.Token);
        await using var client = new DesktopBrokerClient(directory.RuntimeRoot, "test-host", "1.0");

        var initial = client.ReadAsync(0, timeout.Token);
        await host.WaitForErrorAsync("consent=pending", timeout.Token);
        await host.SendControlAsync("approve");
        _ = await initial;

        using var requestCancellation = new CancellationTokenSource();
        var delayed = client.ReadAsync(5000, requestCancellation.Token);
        requestCancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await delayed);
        var next = await client.ReadAsync(0, timeout.Token);
        Assert.Equal("available", next.Status);
    }

    [Fact]
    public async Task AdapterRejectsRequestsBeyondItsOutstandingLimit()
    {
        using var directory = new TemporaryDirectory();
        await using var host = PrototypeProcess.StartHost(directory.RuntimeRoot);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await host.WaitForErrorAsync("broker=ready", timeout.Token);
        await using var client = new DesktopBrokerClient(directory.RuntimeRoot, "test-host", "1.0");
        var initial = client.ReadAsync(0, timeout.Token);
        await host.WaitForErrorAsync("consent=pending", timeout.Token);
        await host.SendControlAsync("approve");
        _ = await initial;

        using var requestCancellation = new CancellationTokenSource();
        var pending = Enumerable.Range(0, PrototypeProtocol.MaximumOutstandingRequests)
            .Select(_ => client.ReadAsync(5000, requestCancellation.Token))
            .ToArray();

        var exception = await Assert.ThrowsAsync<BrokerUnavailableException>(
            () => client.ReadAsync(0, timeout.Token));
        Assert.Equal("busy", exception.Code);

        requestCancellation.Cancel();
        foreach (var request in pending)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await request);
        }
    }

    [Fact]
    public async Task ConcurrentSameKeySubmissionMarksExactlyOneDuplicate()
    {
        using var directory = new TemporaryDirectory();
        await using var host = PrototypeProcess.StartHost(directory.RuntimeRoot);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await host.WaitForErrorAsync("broker=ready", timeout.Token);
        await using var client = new DesktopBrokerClient(directory.RuntimeRoot, "test-host", "1.0");
        var initial = client.ReadAsync(0, timeout.Token);
        await host.WaitForErrorAsync("consent=pending", timeout.Token);
        await host.SendControlAsync("approve");
        _ = await initial;

        var results = await Task.WhenAll(
            client.SubmitAsync("concurrent-key", 0, timeout.Token),
            client.SubmitAsync("concurrent-key", 0, timeout.Token));

        Assert.Single(results, result => !result.Duplicate);
        Assert.Single(results, result => result.Duplicate);
        Assert.Equal(results[0].Receipt, results[1].Receipt);
        Assert.All(results, result => Assert.Equal("pending-local-review", result.Status));
    }

    [Fact]
    public async Task RevocationDisconnectsExistingSessionAndRequiresFreshConsent()
    {
        using var directory = new TemporaryDirectory();
        await using var host = PrototypeProcess.StartHost(directory.RuntimeRoot);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await host.WaitForErrorAsync("broker=ready", timeout.Token);
        await using var firstClient = new DesktopBrokerClient(directory.RuntimeRoot, "test-host", "1.0");

        var initial = firstClient.ReadAsync(0, timeout.Token);
        await host.WaitForErrorAsync("consent=pending", timeout.Token);
        await host.SendControlAsync("approve");
        Assert.Equal("available", (await initial).Status);

        var delayed = firstClient.ReadAsync(5000, timeout.Token);
        await host.SendControlAsync("revoke");
        await host.WaitForErrorAsync("sessions=revoked", timeout.Token);

        var revoked = await Assert.ThrowsAsync<BrokerUnavailableException>(async () => await delayed);
        Assert.Equal("broker-disconnected", revoked.Code);

        var reconnected = firstClient.ReadAsync(0, timeout.Token);
        await host.WaitForErrorAsync("consent=pending", timeout.Token);
        await host.SendControlAsync("approve");
        Assert.Equal("available", (await reconnected).Status);
    }

    [Fact]
    public async Task IdleBootstrapConnectionTimesOutWithoutBlockingNextClient()
    {
        using var directory = new TemporaryDirectory();
        await using var host = PrototypeProcess.StartHost(directory.RuntimeRoot);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await host.WaitForErrorAsync("broker=ready", timeout.Token);
        var endpoint = await LocalEndpoint.DiscoverBootstrapAsync(directory.RuntimeRoot, timeout.Token);
        await using (var idle = await LocalEndpoint.ConnectAsync(endpoint, timeout.Token))
        {
            var response = await JsonLineCodec.ReadAsync(
                idle.Stream,
                PrototypeJsonContext.Default.BootstrapResponse,
                timeout.Token);
            Assert.Equal("bootstrap-timeout", response?.Error);
            Assert.Null(response?.Token);
        }

        await host.WaitForErrorAsync("broker=ready", timeout.Token);
        await using var client = new DesktopBrokerClient(directory.RuntimeRoot, "next-client", "1.0");
        var read = client.ReadAsync(0, timeout.Token);
        await host.WaitForErrorAsync("consent=pending", timeout.Token);
        await host.SendControlAsync("approve");

        Assert.Equal("available", (await read).Status);
    }

    [Fact]
    public async Task BootstrapDisconnectDoesNotStopBroker()
    {
        using var directory = new TemporaryDirectory();
        await using var host = PrototypeProcess.StartHost(directory.RuntimeRoot);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await host.WaitForErrorAsync("broker=ready", timeout.Token);
        var endpoint = await LocalEndpoint.DiscoverBootstrapAsync(directory.RuntimeRoot, timeout.Token);
        await using (var abandoned = await LocalEndpoint.ConnectAsync(endpoint, timeout.Token))
        {
        }

        await host.WaitForErrorAsync("broker=ready", timeout.Token);
        await using var client = new DesktopBrokerClient(directory.RuntimeRoot, "next-client", "1.0");
        var read = client.ReadAsync(0, timeout.Token);
        await host.WaitForErrorAsync("consent=pending", timeout.Token);
        await host.SendControlAsync("approve");

        Assert.Equal("available", (await read).Status);
    }

    [Fact]
    public async Task ReplacementHostRecoversAfterPreviousHostCrashes()
    {
        using var directory = new TemporaryDirectory();
        await using var crashedHost = PrototypeProcess.StartHost(directory.RuntimeRoot);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await crashedHost.WaitForErrorAsync("broker=ready", timeout.Token);
        await crashedHost.CrashAsync();

        await using var replacement = PrototypeProcess.StartHost(directory.RuntimeRoot);
        await replacement.WaitForErrorAsync("broker=ready", timeout.Token);
        await using var client = new DesktopBrokerClient(directory.RuntimeRoot, "test-host", "1.0");
        var read = client.ReadAsync(0, timeout.Token);
        await replacement.WaitForErrorAsync("consent=pending", timeout.Token);
        await replacement.SendControlAsync("approve");

        Assert.Equal("available", (await read).Status);
    }

    [Fact]
    public async Task DisablingBrokerClosesSessionAndRequiresUnavailableReconnect()
    {
        using var directory = new TemporaryDirectory();
        await using var host = PrototypeProcess.StartHost(directory.RuntimeRoot);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await host.WaitForErrorAsync("broker=ready", timeout.Token);
        await using var client = new DesktopBrokerClient(directory.RuntimeRoot, "test-host", "1.0");
        var initial = client.ReadAsync(0, timeout.Token);
        await host.WaitForErrorAsync("consent=pending", timeout.Token);
        await host.SendControlAsync("approve");
        Assert.Equal("available", (await initial).Status);

        await host.SendControlAsync("disable");
        await host.WaitForErrorAsync("broker=stopped", timeout.Token);

        var exception = await Assert.ThrowsAsync<BrokerUnavailableException>(async () =>
            await client.ReadAsync(0, timeout.Token));
        Assert.True(
            exception.Code is "broker-disconnected" or "broker-unavailable",
            $"Unexpected unavailable code: {exception.Code}");
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(File.Exists(Path.Combine(directory.RuntimeRoot, "mcp-bootstrap.json")));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            var name = $"mp-{Guid.NewGuid():N}"[..11];
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), name);
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
