using System.Collections.Concurrent;
using DotOrbit.Mcp.Ipc;

namespace DotOrbit.Mcp.Ipc.Tests;

public sealed class LocalMcpBrokerTests
{
    [Fact]
    public async Task SameUserClientReadsAndSubmitsThroughScopedChunkedBroker()
    {
        using var directory = new TemporaryDirectory();
        var runtimeRoot = Path.Combine(directory.Path, "runtime");
        var boundary = new RecordingBoundary();
        await using var host = new LocalMcpBrokerHost(runtimeRoot, boundary, boundary);
        host.Start();
        await WaitForEndpointAsync(runtimeRoot);
        await using var client = new LocalMcpBrokerClient(
            runtimeRoot,
            "test-client",
            "1.0",
            new string('a', 64),
            [LocalMcpProtocol.ReadScope, LocalMcpProtocol.SubmitScope]);
        var proposal = Enumerable.Range(0, 96 * 1024).Select(index => (byte)(index % 251)).ToArray();

        var snapshot = await client.ReadPlanningSnapshotAsync(TestContext.Current.CancellationToken);
        var submission = await client.SubmitProposalAsync(
            "idempotency-1",
            proposal,
            TestContext.Current.CancellationToken);

        Assert.Equal(boundary.Snapshot, snapshot.Payload.ToArray());
        Assert.Equal("pending-local-review", submission.Status);
        Assert.Equal("receipt-1", submission.Receipt);
        Assert.Equal(proposal, boundary.SubmittedPayload);
        Assert.Equal("idempotency-1", boundary.SubmittedKey);
        Assert.Equal(LocalMcpScopes.ReadInterchangeContext | LocalMcpScopes.SubmitProposals,
            Assert.Single(boundary.Clients).RequestedScopes);
        Assert.False(string.IsNullOrWhiteSpace(Assert.Single(boundary.Clients).OperatingSystemAccount));
    }

    [Fact]
    public async Task RevokedAuthorizationFailsBeforeCallingOperationBoundary()
    {
        using var directory = new TemporaryDirectory();
        var runtimeRoot = Path.Combine(directory.Path, "runtime");
        var boundary = new RecordingBoundary();
        await using var host = new LocalMcpBrokerHost(runtimeRoot, boundary, boundary);
        host.Start();
        await WaitForEndpointAsync(runtimeRoot);
        await using var client = new LocalMcpBrokerClient(
            runtimeRoot,
            "test-client",
            "1.0",
            new string('b', 64),
            [LocalMcpProtocol.ReadScope]);
        _ = await client.ReadReusableInstructionsAsync(TestContext.Current.CancellationToken);
        boundary.Active = false;

        var exception = await Assert.ThrowsAsync<LocalMcpBrokerException>(() =>
            client.ReadPlanningSnapshotAsync(TestContext.Current.CancellationToken));

        Assert.Equal("authorization-revoked", exception.Code);
        Assert.Equal(1, boundary.HandleCount);
    }

    [Fact]
    public async Task ScopeIsEnforcedBeforeCallingOperationBoundary()
    {
        using var directory = new TemporaryDirectory();
        var runtimeRoot = Path.Combine(directory.Path, "runtime");
        var boundary = new RecordingBoundary();
        await using var host = new LocalMcpBrokerHost(runtimeRoot, boundary, boundary);
        host.Start();
        await WaitForEndpointAsync(runtimeRoot);
        await using var client = new LocalMcpBrokerClient(
            runtimeRoot,
            "test-client",
            "1.0",
            new string('c', 64),
            [LocalMcpProtocol.ReadScope]);

        var exception = await Assert.ThrowsAsync<LocalMcpBrokerException>(() =>
            client.SubmitProposalAsync(
                "idempotency-1",
                "{}"u8.ToArray(),
                TestContext.Current.CancellationToken));

        Assert.Equal("scope-denied", exception.Code);
        Assert.Equal(0, boundary.HandleCount);
    }

    [Fact]
    public async Task StalledChunkUploadExpiresAndReleasesTheSession()
    {
        using var directory = new TemporaryDirectory();
        var runtimeRoot = Path.Combine(directory.Path, "runtime");
        var boundary = new RecordingBoundary();
        await using var host = new LocalMcpBrokerHost(
            runtimeRoot,
            boundary,
            boundary,
            requestLifetime: TimeSpan.FromMilliseconds(100));
        host.Start();
        await WaitForEndpointAsync(runtimeRoot);

        var bootstrap = await LocalEndpoint.DiscoverBootstrapAsync(
            runtimeRoot,
            TestContext.Current.CancellationToken);
        await using var bootstrapConnection = await LocalEndpoint.ConnectAsync(
            bootstrap,
            TestContext.Current.CancellationToken);
        await JsonLineCodec.WriteAsync(
            bootstrapConnection.Stream,
            new BootstrapRequest(
                LocalMcpProtocol.Version,
                "request-capability",
                "stalling-client",
                "1.0",
                new string('d', 64),
                [LocalMcpProtocol.SubmitScope]),
            LocalMcpJsonContext.Default.BootstrapRequest,
            TestContext.Current.CancellationToken);
        var capability = Assert.IsType<BootstrapResponse>(await JsonLineCodec.ReadAsync(
            bootstrapConnection.Stream,
            LocalMcpJsonContext.Default.BootstrapResponse,
            TestContext.Current.CancellationToken));
        await using var session = await LocalEndpoint.ConnectAsync(
            Assert.IsType<EndpointAddress>(capability.Endpoint),
            TestContext.Current.CancellationToken);
        await JsonLineCodec.WriteAsync(
            session.Stream,
            new SessionMessage(LocalMcpProtocol.Version, "bind", Token: capability.Token),
            LocalMcpJsonContext.Default.SessionMessage,
            TestContext.Current.CancellationToken);
        _ = Assert.IsType<SessionMessage>(await JsonLineCodec.ReadAsync(
            session.Stream,
            LocalMcpJsonContext.Default.SessionMessage,
            TestContext.Current.CancellationToken));
        await JsonLineCodec.WriteAsync(
            session.Stream,
            new SessionMessage(
                LocalMcpProtocol.Version,
                "request-begin",
                "stalled",
                "submit.proposal",
                "idempotency-1",
                TotalLength: 32,
                ChunkCount: 1),
            LocalMcpJsonContext.Default.SessionMessage,
            TestContext.Current.CancellationToken);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var buffer = new byte[1];
        Assert.Equal(0, await session.Stream.ReadAsync(buffer, deadline.Token));

        await using var healthyClient = new LocalMcpBrokerClient(
            runtimeRoot,
            "healthy-client",
            "1.0",
            new string('e', 64),
            [LocalMcpProtocol.ReadScope]);
        var response = await healthyClient.ReadReusableInstructionsAsync(deadline.Token);
        Assert.Equal("ok", response.Status);
    }

    [Fact]
    public async Task IncompatibleProtocolVersionIsRejectedBeforeAuthorization()
    {
        using var directory = new TemporaryDirectory();
        var runtimeRoot = Path.Combine(directory.Path, "runtime");
        var boundary = new RecordingBoundary();
        await using var host = new LocalMcpBrokerHost(runtimeRoot, boundary, boundary);
        host.Start();
        await WaitForEndpointAsync(runtimeRoot);
        var endpoint = await LocalEndpoint.DiscoverBootstrapAsync(
            runtimeRoot,
            TestContext.Current.CancellationToken);
        await using var connection = await LocalEndpoint.ConnectAsync(
            endpoint,
            TestContext.Current.CancellationToken);

        await JsonLineCodec.WriteAsync(
            connection.Stream,
            new BootstrapRequest(
                LocalMcpProtocol.Version + 1,
                "request-capability",
                "future-client",
                "1.0",
                new string('f', 64),
                [LocalMcpProtocol.ReadScope]),
            LocalMcpJsonContext.Default.BootstrapRequest,
            TestContext.Current.CancellationToken);
        var response = Assert.IsType<BootstrapResponse>(await JsonLineCodec.ReadAsync(
            connection.Stream,
            LocalMcpJsonContext.Default.BootstrapResponse,
            TestContext.Current.CancellationToken));

        Assert.Equal("error", response.Type);
        Assert.Equal("protocol-version-unsupported", response.Error);
        Assert.Empty(boundary.Clients);
    }

    [Fact]
    public async Task BootstrapDisconnectCancelsPendingAuthorization()
    {
        using var directory = new TemporaryDirectory();
        var runtimeRoot = Path.Combine(directory.Path, "runtime");
        var boundary = new PendingAuthorizationBoundary();
        await using var host = new LocalMcpBrokerHost(runtimeRoot, boundary, boundary);
        host.Start();
        await WaitForEndpointAsync(runtimeRoot);
        var endpoint = await LocalEndpoint.DiscoverBootstrapAsync(
            runtimeRoot,
            TestContext.Current.CancellationToken);
        var connection = await LocalEndpoint.ConnectAsync(
            endpoint,
            TestContext.Current.CancellationToken);
        await JsonLineCodec.WriteAsync(
            connection.Stream,
            new BootstrapRequest(
                LocalMcpProtocol.Version,
                "request-capability",
                "disconnecting-client",
                "1.0",
                new string('a', 64),
                [LocalMcpProtocol.ReadScope]),
            LocalMcpJsonContext.Default.BootstrapRequest,
            TestContext.Current.CancellationToken);
        await boundary.AuthorizationStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        await connection.DisposeAsync();

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await boundary.AuthorizationCancelled.Task.WaitAsync(deadline.Token);
        Assert.Equal(0, boundary.ReleaseCount);
    }

    [Fact]
    public async Task DisconnectRacingRememberedApprovalRevokesTheAbandonedAuthorization()
    {
        using var directory = new TemporaryDirectory();
        var runtimeRoot = Path.Combine(directory.Path, "runtime");
        var boundary = new RacingRememberedAuthorizationBoundary();
        await using var host = new LocalMcpBrokerHost(runtimeRoot, boundary, boundary);
        host.Start();
        await WaitForEndpointAsync(runtimeRoot);
        var endpoint = await LocalEndpoint.DiscoverBootstrapAsync(
            runtimeRoot,
            TestContext.Current.CancellationToken);
        var connection = await LocalEndpoint.ConnectAsync(
            endpoint,
            TestContext.Current.CancellationToken);
        await JsonLineCodec.WriteAsync(
            connection.Stream,
            new BootstrapRequest(
                LocalMcpProtocol.Version,
                "request-capability",
                "disconnecting-remembered-client",
                "1.0",
                new string('e', 64),
                [LocalMcpProtocol.ReadScope]),
            LocalMcpJsonContext.Default.BootstrapRequest,
            TestContext.Current.CancellationToken);
        await boundary.AuthorizationStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        await connection.DisposeAsync();
        boundary.CompleteApproval.TrySetResult();

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await boundary.AuthorizationAbandoned.Task.WaitAsync(deadline.Token);
        Assert.Equal(1, boundary.ReleaseCount);
        Assert.Equal(1, boundary.AbandonCount);
    }

    [Theory]
    [InlineData(LocalMcpProtocol.Version, "read.instructions")]
    [InlineData(LocalMcpProtocol.Version + 1, null)]
    public async Task InvalidCancelFrameClosesOnlyItsSession(int version, string? operation)
    {
        using var directory = new TemporaryDirectory();
        var runtimeRoot = Path.Combine(directory.Path, "runtime");
        var boundary = new RecordingBoundary();
        await using var host = new LocalMcpBrokerHost(runtimeRoot, boundary, boundary);
        host.Start();
        await WaitForEndpointAsync(runtimeRoot);
        await using var session = await OpenSessionAsync(runtimeRoot, boundary);

        await JsonLineCodec.WriteAsync(
            session.Stream,
            new SessionMessage(version, "cancel", "missing", Operation: operation),
            LocalMcpJsonContext.Default.SessionMessage,
            TestContext.Current.CancellationToken);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var buffer = new byte[1];
        Assert.Equal(0, await session.Stream.ReadAsync(buffer, deadline.Token));
        await using var healthy = new LocalMcpBrokerClient(
            runtimeRoot,
            "healthy-client",
            "1.0",
            new string('b', 64),
            [LocalMcpProtocol.ReadScope]);
        Assert.Equal("ok", (await healthy.ReadReusableInstructionsAsync(deadline.Token)).Status);
    }

    [Fact]
    public async Task BindFrameRejectsAFieldFromAnotherVariant()
    {
        using var directory = new TemporaryDirectory();
        var runtimeRoot = Path.Combine(directory.Path, "runtime");
        var boundary = new RecordingBoundary();
        await using var host = new LocalMcpBrokerHost(runtimeRoot, boundary, boundary);
        host.Start();
        await WaitForEndpointAsync(runtimeRoot);
        var bootstrap = await LocalEndpoint.DiscoverBootstrapAsync(
            runtimeRoot,
            TestContext.Current.CancellationToken);
        await using var bootstrapConnection = await LocalEndpoint.ConnectAsync(
            bootstrap,
            TestContext.Current.CancellationToken);
        await JsonLineCodec.WriteAsync(
            bootstrapConnection.Stream,
            new BootstrapRequest(
                LocalMcpProtocol.Version,
                "request-capability",
                "invalid-bind-client",
                "1.0",
                new string('d', 64),
                [LocalMcpProtocol.ReadScope]),
            LocalMcpJsonContext.Default.BootstrapRequest,
            TestContext.Current.CancellationToken);
        var capability = Assert.IsType<BootstrapResponse>(await JsonLineCodec.ReadAsync(
            bootstrapConnection.Stream,
            LocalMcpJsonContext.Default.BootstrapResponse,
            TestContext.Current.CancellationToken));
        await using var session = await LocalEndpoint.ConnectAsync(
            Assert.IsType<EndpointAddress>(capability.Endpoint),
            TestContext.Current.CancellationToken);

        await JsonLineCodec.WriteAsync(
            session.Stream,
            new SessionMessage(
                LocalMcpProtocol.Version,
                "bind",
                Id: "unexpected",
                Token: capability.Token),
            LocalMcpJsonContext.Default.SessionMessage,
            TestContext.Current.CancellationToken);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var buffer = new byte[1];
        Assert.Equal(0, await session.Stream.ReadAsync(buffer, deadline.Token));
    }

    [Fact]
    public async Task NullCredentialVerifierIsRejectedWithoutStoppingTheBroker()
    {
        using var directory = new TemporaryDirectory();
        var runtimeRoot = Path.Combine(directory.Path, "runtime");
        var boundary = new RecordingBoundary();
        await using var host = new LocalMcpBrokerHost(runtimeRoot, boundary, boundary);
        host.Start();
        await WaitForEndpointAsync(runtimeRoot);
        var endpoint = await LocalEndpoint.DiscoverBootstrapAsync(
            runtimeRoot,
            TestContext.Current.CancellationToken);
        await using (var connection = await LocalEndpoint.ConnectAsync(
                         endpoint,
                         TestContext.Current.CancellationToken))
        {
            var malformed = "{\"version\":1,\"type\":\"request-capability\",\"clientName\":\"bad\",\"clientVersion\":\"1.0\",\"credentialVerifier\":null,\"scopes\":[\"interchange.read\"]}\n"u8.ToArray();
            await connection.Stream.WriteAsync(malformed, TestContext.Current.CancellationToken);
            var response = Assert.IsType<BootstrapResponse>(await JsonLineCodec.ReadAsync(
                connection.Stream,
                LocalMcpJsonContext.Default.BootstrapResponse,
                TestContext.Current.CancellationToken));
            Assert.Equal("bootstrap-invalid", response.Error);
        }

        await using var healthy = new LocalMcpBrokerClient(
            runtimeRoot,
            "healthy-client",
            "1.0",
            new string('a', 64),
            [LocalMcpProtocol.ReadScope]);
        var result = await healthy.ReadReusableInstructionsAsync(TestContext.Current.CancellationToken);

        Assert.Equal("ok", result.Status);
        Assert.Single(boundary.Clients);
    }

    [Fact]
    public async Task FifthConcurrentSessionIsRejectedWithoutDisturbingExistingClients()
    {
        using var directory = new TemporaryDirectory();
        var runtimeRoot = Path.Combine(directory.Path, "runtime");
        var boundary = new RecordingBoundary();
        await using var host = new LocalMcpBrokerHost(runtimeRoot, boundary, boundary);
        host.Start();
        await WaitForEndpointAsync(runtimeRoot);
        var clients = new List<LocalMcpBrokerClient>();
        try
        {
            for (var index = 0; index < LocalMcpProtocol.MaximumSessions; index++)
            {
                var client = new LocalMcpBrokerClient(
                    runtimeRoot,
                    $"client-{index}",
                    "1.0",
                    index.ToString("x64", System.Globalization.CultureInfo.InvariantCulture),
                    [LocalMcpProtocol.ReadScope]);
                clients.Add(client);
                Assert.Equal(
                    "ok",
                    (await client.ReadReusableInstructionsAsync(TestContext.Current.CancellationToken)).Status);
            }

            await using var excess = new LocalMcpBrokerClient(
                runtimeRoot,
                "client-excess",
                "1.0",
                new string('f', 64),
                [LocalMcpProtocol.ReadScope]);
            var exception = await Assert.ThrowsAsync<LocalMcpBrokerException>(() =>
                excess.ReadReusableInstructionsAsync(TestContext.Current.CancellationToken));

            Assert.Equal("session-limit", exception.Code);
            Assert.Equal(
                "ok",
                (await clients[0].ReadReusableInstructionsAsync(TestContext.Current.CancellationToken)).Status);
        }
        finally
        {
            foreach (var client in clients)
            {
                await client.DisposeAsync();
            }
        }
    }

    private static async Task WaitForEndpointAsync(string runtimeRoot)
    {
        if (OperatingSystem.IsWindows())
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
            return;
        }

        var descriptor = Path.Combine(runtimeRoot, "mcp-bootstrap.json");
        for (var attempt = 0; attempt < 100 && !File.Exists(descriptor); attempt++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(File.Exists(descriptor));
    }

    private static async Task<LocalConnection> OpenSessionAsync(
        string runtimeRoot,
        RecordingBoundary boundary)
    {
        var bootstrap = await LocalEndpoint.DiscoverBootstrapAsync(
            runtimeRoot,
            TestContext.Current.CancellationToken);
        await using var bootstrapConnection = await LocalEndpoint.ConnectAsync(
            bootstrap,
            TestContext.Current.CancellationToken);
        await JsonLineCodec.WriteAsync(
            bootstrapConnection.Stream,
            new BootstrapRequest(
                LocalMcpProtocol.Version,
                "request-capability",
                "raw-client",
                "1.0",
                new string('c', 64),
                [LocalMcpProtocol.ReadScope]),
            LocalMcpJsonContext.Default.BootstrapRequest,
            TestContext.Current.CancellationToken);
        var capability = Assert.IsType<BootstrapResponse>(await JsonLineCodec.ReadAsync(
            bootstrapConnection.Stream,
            LocalMcpJsonContext.Default.BootstrapResponse,
            TestContext.Current.CancellationToken));
        var session = await LocalEndpoint.ConnectAsync(
            Assert.IsType<EndpointAddress>(capability.Endpoint),
            TestContext.Current.CancellationToken);
        await JsonLineCodec.WriteAsync(
            session.Stream,
            new SessionMessage(LocalMcpProtocol.Version, "bind", Token: capability.Token),
            LocalMcpJsonContext.Default.SessionMessage,
            TestContext.Current.CancellationToken);
        var bound = Assert.IsType<SessionMessage>(await JsonLineCodec.ReadAsync(
            session.Stream,
            LocalMcpJsonContext.Default.SessionMessage,
            TestContext.Current.CancellationToken));
        Assert.Equal("bound", bound.Type);
        Assert.NotEmpty(boundary.Clients);
        return session;
    }

    private sealed class RecordingBoundary : ILocalMcpAuthorizationHandler, ILocalMcpOperationHandler
    {
        public byte[] Snapshot { get; } = Enumerable.Range(0, 160 * 1024)
            .Select(index => (byte)(index % 239))
            .ToArray();
        public ConcurrentBag<LocalMcpClientIdentity> Clients { get; } = [];
        public bool Active { get; set; } = true;
        public int HandleCount { get; private set; }
        public byte[]? SubmittedPayload { get; private set; }
        public string? SubmittedKey { get; private set; }

        public ValueTask<LocalMcpAuthorizationDecision> AuthorizeAsync(
            LocalMcpClientIdentity client,
            CancellationToken cancellationToken)
        {
            Clients.Add(client);
            return ValueTask.FromResult(new LocalMcpAuthorizationDecision(true, "authorization-1"));
        }

        public bool IsAuthorizationActive(string authorizationId, LocalMcpScopes exactScopes) =>
            Active && authorizationId == "authorization-1";

        public void ReleaseAuthorization(string authorizationId)
        {
        }

        public void AbandonAuthorization(string authorizationId)
        {
        }

        public ValueTask<LocalMcpOperationResult> HandleAsync(
            string authorizationId,
            LocalMcpOperationRequest request,
            CancellationToken cancellationToken)
        {
            HandleCount++;
            return request.Operation switch
            {
                LocalMcpOperation.ReadReusableInstructions =>
                    ValueTask.FromResult(new LocalMcpOperationResult("ok", "instructions"u8.ToArray())),
                LocalMcpOperation.ReadPlanningSnapshot =>
                    ValueTask.FromResult(new LocalMcpOperationResult("ok", Snapshot)),
                LocalMcpOperation.SubmitProposal => Submit(request),
                _ => throw new InvalidOperationException(),
            };
        }

        private ValueTask<LocalMcpOperationResult> Submit(LocalMcpOperationRequest request)
        {
            SubmittedPayload = request.Payload.ToArray();
            SubmittedKey = request.IdempotencyKey;
            return ValueTask.FromResult(new LocalMcpOperationResult(
                "pending-local-review",
                ReadOnlyMemory<byte>.Empty,
                "receipt-1"));
        }
    }

    private sealed class PendingAuthorizationBoundary : ILocalMcpAuthorizationHandler, ILocalMcpOperationHandler
    {
        public TaskCompletionSource AuthorizationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AuthorizationCancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ReleaseCount { get; private set; }

        public async ValueTask<LocalMcpAuthorizationDecision> AuthorizeAsync(
            LocalMcpClientIdentity client,
            CancellationToken cancellationToken)
        {
            _ = client;
            AuthorizationStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The authorization wait completed without cancellation.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                AuthorizationCancelled.TrySetResult();
                throw;
            }
        }

        public bool IsAuthorizationActive(string authorizationId, LocalMcpScopes exactScopes)
        {
            _ = authorizationId;
            _ = exactScopes;
            return false;
        }

        public void ReleaseAuthorization(string authorizationId)
        {
            _ = authorizationId;
            ReleaseCount++;
        }

        public void AbandonAuthorization(string authorizationId)
        {
            _ = authorizationId;
        }

        public ValueTask<LocalMcpOperationResult> HandleAsync(
            string authorizationId,
            LocalMcpOperationRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No operation should reach a disconnected authorization.");
    }

    private sealed class RacingRememberedAuthorizationBoundary :
        ILocalMcpAuthorizationHandler,
        ILocalMcpOperationHandler
    {
        public TaskCompletionSource AuthorizationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CompleteApproval { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AuthorizationAbandoned { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ReleaseCount { get; private set; }
        public int AbandonCount { get; private set; }

        public async ValueTask<LocalMcpAuthorizationDecision> AuthorizeAsync(
            LocalMcpClientIdentity client,
            CancellationToken cancellationToken)
        {
            _ = client;
            _ = cancellationToken;
            AuthorizationStarted.TrySetResult();
            await CompleteApproval.Task;
            return new(true, "remembered-race", RevokeOnAbandon: true);
        }

        public bool IsAuthorizationActive(string authorizationId, LocalMcpScopes exactScopes)
        {
            _ = authorizationId;
            _ = exactScopes;
            return false;
        }

        public void ReleaseAuthorization(string authorizationId)
        {
            _ = authorizationId;
            ReleaseCount++;
        }

        public void AbandonAuthorization(string authorizationId)
        {
            _ = authorizationId;
            AbandonCount++;
            AuthorizationAbandoned.TrySetResult();
        }

        public ValueTask<LocalMcpOperationResult> HandleAsync(
            string authorizationId,
            LocalMcpOperationRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No operation should reach an abandoned authorization.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mcp-{Guid.NewGuid():N}"[..12]);
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
