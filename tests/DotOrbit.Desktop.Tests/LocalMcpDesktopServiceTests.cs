using System.Text;
using DotOrbit.Core.Interchange;
using DotOrbit.Core.Workspaces;
using DotOrbit.Mcp.Ipc;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class LocalMcpDesktopServiceTests
{
    private static readonly byte[] ValidProposal = Encoding.UTF8.GetBytes(
        """
        {
          "format": "dot-orbit-new-work-proposal",
          "schemaVersion": 1,
          "operations": [
            { "operation": "createCategory", "operationId": "1", "localId": "category", "name": "Fresh" },
            { "operation": "createTask", "operationId": "2", "localId": "task", "title": "Task", "description": "", "project": null, "category": { "mode": "explicit", "category": { "localId": "category" } }, "dueDate": null, "participants": [] }
          ]
        }
        """);

    [Fact]
    public async Task ApprovalCanBeRememberedThenReusedOnlyForExactAccountCredentialAndScopes()
    {
        using var directory = new TemporaryDirectory();
        var store = new MemoryAgentIntegrationStore();
        var session = new AgentWorkspaceSession(store);
        var coordinator = new PendingProposalCoordinator(store, session.Proposals, TimeProvider.System);
        await using var service = new LocalMcpDesktopService(
            session.Planning,
            store,
            coordinator,
            TimeProvider.System,
            directory.Path);
        await service.StartAsync();
        void Remember(object? sender, LocalMcpApprovalRequest request) =>
            request.Resolve(LocalMcpApprovalChoice.AlwaysAllow);
        service.ApprovalRequested += Remember;
        var client = Client(LocalMcpScopes.ReadInterchangeContext);

        var first = await service.AuthorizeAsync(client, TestContext.Current.CancellationToken);
        service.ApprovalRequested -= Remember;
        var second = await service.AuthorizeAsync(client, TestContext.Current.CancellationToken);
        var expanded = await AuthorizeAndDenyAsync(
            service,
            Client(LocalMcpScopes.ReadInterchangeContext | LocalMcpScopes.SubmitProposals));

        Assert.True(first.Approved, first.Error);
        Assert.True(first.RevokeOnAbandon);
        Assert.Equal(first.AuthorizationId, second.AuthorizationId);
        Assert.False(second.RevokeOnAbandon);
        Assert.False(expanded.Approved);
        Assert.Single(store.ReadAuthorizations());
        Assert.True(service.IsAuthorizationActive(
            first.AuthorizationId!,
            LocalMcpScopes.ReadInterchangeContext), "Remembered authorization was not active.");

        service.AbandonAuthorization(first.AuthorizationId!);
        Assert.Empty(store.ReadAuthorizations());
        Assert.False(service.IsAuthorizationActive(
            first.AuthorizationId!,
            LocalMcpScopes.ReadInterchangeContext));
    }

    [Fact]
    public async Task ReadsAndSubmissionUseTransportNeutralBoundariesWithoutApplying()
    {
        using var directory = new TemporaryDirectory();
        var store = new MemoryAgentIntegrationStore();
        var session = new AgentWorkspaceSession(store);
        var coordinator = new PendingProposalCoordinator(store, session.Proposals, TimeProvider.System);
        await using var service = new LocalMcpDesktopService(
            session.Planning,
            store,
            coordinator,
            TimeProvider.System,
            directory.Path);
        await service.StartAsync();
        service.ApprovalRequested += (_, request) => request.Resolve(LocalMcpApprovalChoice.AllowOnce);
        var authorization = await service.AuthorizeAsync(
            Client(LocalMcpScopes.ReadInterchangeContext | LocalMcpScopes.SubmitProposals),
            TestContext.Current.CancellationToken);

        var instructions = await service.HandleAsync(
            authorization.AuthorizationId!,
            new(LocalMcpOperation.ReadReusableInstructions, null, ReadOnlyMemory<byte>.Empty),
            TestContext.Current.CancellationToken);
        var snapshot = await service.HandleAsync(
            authorization.AuthorizationId!,
            new(LocalMcpOperation.ReadPlanningSnapshot, null, ReadOnlyMemory<byte>.Empty),
            TestContext.Current.CancellationToken);
        var submitted = await service.HandleAsync(
            authorization.AuthorizationId!,
            new(LocalMcpOperation.SubmitProposal, "key-1", ValidProposal),
            TestContext.Current.CancellationToken);

        Assert.Equal("ok", instructions.Status);
        Assert.Contains("dot-orbit planning interchange v1", Encoding.UTF8.GetString(instructions.Payload.Span));
        Assert.Equal("ok", snapshot.Status);
        Assert.Contains("workspaceRevision", Encoding.UTF8.GetString(snapshot.Payload.Span));
        Assert.Equal(1, session.PlanningSource.ReadCount);
        Assert.Equal("pending-local-review", submitted.Status);
        Assert.True(coordinator.HasPendingProposal);
        Assert.Equal(0, session.ApplyCount);

        coordinator.Discard(submitted.Receipt!);
        var completedRetry = await service.HandleAsync(
            authorization.AuthorizationId!,
            new(LocalMcpOperation.SubmitProposal, "key-1", ValidProposal),
            TestContext.Current.CancellationToken);

        Assert.Equal("already-processed", completedRetry.Status);
        Assert.True(completedRetry.Duplicate);
        Assert.Equal(submitted.Receipt, completedRetry.Receipt);
        Assert.False(coordinator.HasPendingProposal);
    }

    [Fact]
    public async Task SubmissionQueueRejectsOnlyTheEleventhAndKeepsPendingDuplicateSemantics()
    {
        using var directory = new TemporaryDirectory();
        var store = new MemoryAgentIntegrationStore();
        var session = new AgentWorkspaceSession(store);
        using var coordinator = new PendingProposalCoordinator(store, session.Proposals, TimeProvider.System);
        await using var service = new LocalMcpDesktopService(
            session.Planning,
            store,
            coordinator,
            TimeProvider.System,
            directory.Path);
        await service.StartAsync();
        service.ApprovalRequested += (_, request) => request.Resolve(LocalMcpApprovalChoice.AllowOnce);
        var authorization = await service.AuthorizeAsync(
            Client(LocalMcpScopes.SubmitProposals),
            TestContext.Current.CancellationToken);
        var accepted = new List<LocalMcpOperationResult>();
        for (var index = 0; index < AgentInterchangeV1.MaximumPendingProposals; index++)
        {
            accepted.Add(await service.HandleAsync(
                authorization.AuthorizationId!,
                new(LocalMcpOperation.SubmitProposal, $"key-{index}", ValidProposal),
                TestContext.Current.CancellationToken));
        }

        var full = await service.HandleAsync(
            authorization.AuthorizationId!,
            new(LocalMcpOperation.SubmitProposal, "key-full", ValidProposal),
            TestContext.Current.CancellationToken);
        var duplicate = await service.HandleAsync(
            authorization.AuthorizationId!,
            new(LocalMcpOperation.SubmitProposal, "key-0", ValidProposal),
            TestContext.Current.CancellationToken);

        Assert.All(accepted, result => Assert.Equal("pending-local-review", result.Status));
        Assert.Equal("rejected", full.Status);
        Assert.Equal("proposal-queue-full", full.Error);
        Assert.Equal("pending-local-review", duplicate.Status);
        Assert.True(duplicate.Duplicate);
        Assert.Equal(accepted[0].Receipt, duplicate.Receipt);
        Assert.Equal(AgentInterchangeV1.MaximumPendingProposals, coordinator.PendingProposalCount);
    }

    [Fact]
    public async Task ClosingAdmissionRejectsAnAuthorizedRequestBeforeItReachesPlanning()
    {
        using var directory = new TemporaryDirectory();
        var store = new MemoryAgentIntegrationStore();
        var session = new AgentWorkspaceSession(store);
        var coordinator = new PendingProposalCoordinator(store, session.Proposals, TimeProvider.System);
        await using var service = new LocalMcpDesktopService(
            session.Planning,
            store,
            coordinator,
            TimeProvider.System,
            directory.Path);
        await service.StartAsync();
        service.ApprovalRequested += (_, request) => request.Resolve(LocalMcpApprovalChoice.AllowOnce);
        var authorization = await service.AuthorizeAsync(
            Client(LocalMcpScopes.ReadInterchangeContext),
            TestContext.Current.CancellationToken);

        service.CloseAdmission();
        var result = await service.HandleAsync(
            authorization.AuthorizationId!,
            new(LocalMcpOperation.ReadPlanningSnapshot, null, ReadOnlyMemory<byte>.Empty),
            TestContext.Current.CancellationToken);

        Assert.Equal("unavailable", result.Status);
        Assert.Equal("mcp-disabled", result.Error);
        Assert.Equal(0, session.PlanningSource.ReadCount);
        Assert.False(service.IsAuthorizationActive(
            authorization.AuthorizationId!,
            LocalMcpScopes.ReadInterchangeContext));
    }

    [Fact]
    public async Task ClosingAdmissionBeforeStartIsStickyAndNeverPublishesAnEndpoint()
    {
        using var directory = new TemporaryDirectory();
        var store = new MemoryAgentIntegrationStore();
        var session = new AgentWorkspaceSession(store);
        var coordinator = new PendingProposalCoordinator(store, session.Proposals, TimeProvider.System);
        await using var service = new LocalMcpDesktopService(
            session.Planning,
            store,
            coordinator,
            TimeProvider.System,
            directory.Path);

        service.CloseAdmission();
        await service.StartAsync();
        var decision = await service.AuthorizeAsync(
            Client(LocalMcpScopes.ReadInterchangeContext),
            TestContext.Current.CancellationToken);

        Assert.Equal(LocalMcpServiceStatus.Stopped, service.Status);
        Assert.False(decision.Approved);
        Assert.Equal("mcp-disabled", decision.Error);
    }

    [Fact]
    public async Task ClosingAdmissionDuringRememberedLookupCannotReactivateAuthorization()
    {
        using var directory = new TemporaryDirectory();
        var lookupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var continueLookup = new ManualResetEventSlim(initialState: false);
        var store = new MemoryAgentIntegrationStore
        {
            FindAuthorizationStarted = lookupStarted,
            ContinueFindAuthorization = continueLookup,
        };
        var client = Client(LocalMcpScopes.ReadInterchangeContext);
        const string authorizationId = "remembered-authorization";
        store.RememberAuthorization(new(
            authorizationId,
            client.CredentialVerifier,
            client.OperatingSystemAccount,
            client.ClientName,
            client.ClientVersion,
            AgentClientScopes.ReadInterchangeContext,
            DateTimeOffset.UnixEpoch));
        var session = new AgentWorkspaceSession(store);
        var coordinator = new PendingProposalCoordinator(store, session.Proposals, TimeProvider.System);
        await using var service = new LocalMcpDesktopService(
            session.Planning,
            store,
            coordinator,
            TimeProvider.System,
            directory.Path);
        await service.StartAsync();

        var authorization = Task.Run(
            async () => await service.AuthorizeAsync(client, CancellationToken.None));
        await lookupStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        service.CloseAdmission();
        continueLookup.Set();
        var decision = await authorization;

        Assert.False(decision.Approved);
        Assert.Equal("mcp-disabled", decision.Error);
        Assert.False(service.IsAuthorizationActive(
            authorizationId,
            LocalMcpScopes.ReadInterchangeContext));
    }

    [Fact]
    public async Task RevokingDuringRememberedLookupCannotIssueAStaleCapability()
    {
        using var directory = new TemporaryDirectory();
        var lookupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var continueLookup = new ManualResetEventSlim(initialState: false);
        var store = new MemoryAgentIntegrationStore
        {
            FindAuthorizationStarted = lookupStarted,
            ContinueFindAuthorization = continueLookup,
        };
        var client = Client(LocalMcpScopes.ReadInterchangeContext);
        const string authorizationId = "remembered-authorization";
        store.RememberAuthorization(new(
            authorizationId,
            client.CredentialVerifier,
            client.OperatingSystemAccount,
            client.ClientName,
            client.ClientVersion,
            AgentClientScopes.ReadInterchangeContext,
            DateTimeOffset.UnixEpoch));
        var session = new AgentWorkspaceSession(store);
        var coordinator = new PendingProposalCoordinator(store, session.Proposals, TimeProvider.System);
        await using var service = new LocalMcpDesktopService(
            session.Planning,
            store,
            coordinator,
            TimeProvider.System,
            directory.Path);
        await service.StartAsync();
        var authorization = Task.Run(
            async () => await service.AuthorizeAsync(client, CancellationToken.None));
        await lookupStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(await service.RevokeAsync(authorizationId));
        continueLookup.Set();
        var decision = await authorization;

        Assert.False(decision.Approved);
        Assert.Equal("authorization-revoked", decision.Error);
        Assert.False(service.IsAuthorizationActive(
            authorizationId,
            LocalMcpScopes.ReadInterchangeContext));
    }

    [Fact]
    public async Task ClosingAdmissionCancelsPendingApprovalAsDisabled()
    {
        using var directory = new TemporaryDirectory();
        var store = new MemoryAgentIntegrationStore();
        var session = new AgentWorkspaceSession(store);
        var coordinator = new PendingProposalCoordinator(store, session.Proposals, TimeProvider.System);
        await using var service = new LocalMcpDesktopService(
            session.Planning,
            store,
            coordinator,
            TimeProvider.System,
            directory.Path);
        await service.StartAsync();
        var approvalRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.ApprovalRequested += (_, _) => approvalRequested.TrySetResult();

        var authorization = service.AuthorizeAsync(
            Client(LocalMcpScopes.ReadInterchangeContext),
            CancellationToken.None).AsTask();
        await approvalRequested.Task.WaitAsync(TestContext.Current.CancellationToken);

        service.CloseAdmission();
        var decision = await authorization;

        Assert.False(decision.Approved);
        Assert.Equal("mcp-disabled", decision.Error);
    }

    [Fact]
    public async Task AuthorizationStoreFailureReturnsFixedUnavailableResult()
    {
        using var directory = new TemporaryDirectory();
        var store = new MemoryAgentIntegrationStore
        {
            FindAuthorizationFailure = new WorkspaceWorkException(),
        };
        var session = new AgentWorkspaceSession(store);
        var coordinator = new PendingProposalCoordinator(store, session.Proposals, TimeProvider.System);
        await using var service = new LocalMcpDesktopService(
            session.Planning,
            store,
            coordinator,
            TimeProvider.System,
            directory.Path);
        await service.StartAsync();

        var decision = await service.AuthorizeAsync(
            Client(LocalMcpScopes.ReadInterchangeContext),
            TestContext.Current.CancellationToken);

        Assert.False(decision.Approved);
        Assert.Equal("workspace-unavailable", decision.Error);
    }

    [Fact]
    public async Task FailedEndpointPublicationReportsErrorInsteadOfRunning()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var invalidRuntimeRoot = Path.Combine(directory.Path, "not-a-directory");
        File.WriteAllText(invalidRuntimeRoot, "synthetic obstruction");
        var store = new MemoryAgentIntegrationStore();
        var session = new AgentWorkspaceSession(store);
        var coordinator = new PendingProposalCoordinator(store, session.Proposals, TimeProvider.System);
        await using var service = new LocalMcpDesktopService(
            session.Planning,
            store,
            coordinator,
            TimeProvider.System,
            invalidRuntimeRoot);

        await service.StartAsync();

        Assert.Equal(LocalMcpServiceStatus.Error, service.Status);
        var decision = await service.AuthorizeAsync(
            Client(LocalMcpScopes.ReadInterchangeContext),
            TestContext.Current.CancellationToken);
        Assert.False(decision.Approved);
        Assert.Equal("mcp-disabled", decision.Error);
    }

    private static async Task<LocalMcpAuthorizationDecision> AuthorizeAndDenyAsync(
        LocalMcpDesktopService service,
        LocalMcpClientIdentity client)
    {
        void Deny(object? sender, LocalMcpApprovalRequest request) =>
            request.Resolve(LocalMcpApprovalChoice.Deny);

        service.ApprovalRequested += Deny;
        try
        {
            return await service.AuthorizeAsync(client, TestContext.Current.CancellationToken);
        }
        finally
        {
            service.ApprovalRequested -= Deny;
        }
    }

    private static LocalMcpClientIdentity Client(LocalMcpScopes scopes) => new(
        "verified-account",
        123,
        "Unverified client",
        "1.0",
        new string('a', 64),
        scopes);

    private sealed class AgentWorkspaceSession : IWorkspaceSession
    {
        public AgentWorkspaceSession(IAgentIntegrationStore store)
        {
            AgentIntegration = store;
        }

        public int SchemaVersion => 16;
        public string FirstCategoryName => "Home";
        public IWorkspaceRecovery Recovery { get; } = new RecoveryViewModelTests.StubWorkspaceRecovery();
        public RecordingPlanningSource PlanningSource { get; } = new();
        public IPlanningSnapshotSource Planning => PlanningSource;
        public IProposalWorkspaceSource Proposals { get; } = new StaticProposalSource();
        public IAgentIntegrationStore AgentIntegration { get; }
        public IWorkspaceWork Work { get; } = new MemoryWorkspaceWork();
        public int ApplyCount { get; private set; }

        public PassphraseRotationResult RotatePassphrase(
            WorkspacePassphrase currentPassphrase,
            WorkspacePassphrase newPassphrase) => PassphraseRotationResult.Failed();

        public void Dispose()
        {
        }
    }

    private sealed class RecordingPlanningSource : IPlanningSnapshotSource
    {
        public int ReadCount { get; private set; }

        public PlanningSnapshotContent Read()
        {
            ReadCount++;
            return new(
                new(7),
                [new("home", "Home")],
                [],
                [],
                [],
                new(["home"], [], []));
        }
    }

    private sealed class StaticProposalSource : IProposalWorkspaceSource
    {
        public ProposalWorkspaceState Read() => new(
            new(7),
            new([new("home", "Home", 0)], [], [], []),
            [],
            []);
    }

    private sealed class MemoryAgentIntegrationStore : IAgentIntegrationStore
    {
        private readonly List<AgentClientAuthorization> _authorizations = [];
        private readonly Dictionary<(string ClientIdentity, string Key), (string Digest, string Receipt)> _receipts = [];
        private readonly List<PendingAgentProposal> _pending = [];

        public TaskCompletionSource? FindAuthorizationStarted { get; init; }

        public ManualResetEventSlim? ContinueFindAuthorization { get; init; }

        public Exception? FindAuthorizationFailure { get; init; }

        public IReadOnlyList<AgentClientAuthorization> ReadAuthorizations() => _authorizations.AsReadOnly();

        public AgentClientAuthorization? FindAuthorization(
            string credentialVerifier,
            string operatingSystemAccount,
            AgentClientScopes exactScopes)
        {
            if (FindAuthorizationFailure is { } failure)
            {
                throw failure;
            }

            var result = _authorizations.SingleOrDefault(item =>
                item.CredentialVerifier == credentialVerifier
                && item.OperatingSystemAccount == operatingSystemAccount
                && item.Scopes == exactScopes);
            FindAuthorizationStarted?.TrySetResult();
            if (ContinueFindAuthorization is { } continueFind
                && !continueFind.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("The authorization lookup test gate was not released.");
            }

            return result;
        }

        public void RememberAuthorization(AgentClientAuthorization authorization)
        {
            _authorizations.RemoveAll(item =>
                item.CredentialVerifier == authorization.CredentialVerifier
                && item.OperatingSystemAccount == authorization.OperatingSystemAccount);
            _authorizations.Add(authorization);
        }

        public bool RevokeAuthorization(string authorizationId) =>
            _authorizations.RemoveAll(item => item.Id == authorizationId) == 1;

        public IReadOnlyList<PendingAgentProposal> ReadPendingProposals() => _pending.AsReadOnly();

        public PendingAgentProposal? FindPendingProposal(string receipt) =>
            _pending.SingleOrDefault(proposal => proposal.Receipt == receipt);

        public AgentProposalReceipt? FindProposalReceipt(string clientIdentity, string idempotencyKey) =>
            _receipts.TryGetValue((clientIdentity, idempotencyKey), out var prior)
                ? new(prior.Digest, prior.Receipt)
                : null;

        public PendingAgentProposalStoreResult TryStorePendingProposal(PendingAgentProposal proposal)
        {
            var key = (proposal.ClientIdentity, proposal.IdempotencyKey);
            if (_receipts.TryGetValue(key, out var prior))
            {
                return prior.Digest == proposal.PayloadDigest
                    ? new(PendingAgentProposalStoreStatus.Duplicate, prior.Receipt)
                    : new(PendingAgentProposalStoreStatus.IdempotencyConflict, null);
            }

            if (_pending.Count < AgentInterchangeV1.MaximumPendingProposals)
            {
                _pending.Add(proposal);
                _receipts[key] = (proposal.PayloadDigest, proposal.Receipt);
                return new(PendingAgentProposalStoreStatus.Stored, proposal.Receipt);
            }

            return new(PendingAgentProposalStoreStatus.Occupied, null);
        }

        public bool RemovePendingProposal(string receipt)
        {
            return _pending.RemoveAll(proposal => proposal.Receipt == receipt) == 1;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"mcpd-{Guid.NewGuid():N}"[..12]);
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
