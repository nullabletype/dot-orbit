using System.Text;
using System.Text.Json;
using DotOrbit.Core.Interchange;
using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.ViewModels;
using DotOrbit.Interchange.Json;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class PendingProposalCoordinatorTests
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
    public async Task ConcurrentInboundSubmissionsAreSerialisedAndQueuedWithoutReplacement()
    {
        using var source = new BlockingProposalSource();
        var store = new MemoryAgentIntegrationStore();
        using var coordinator = new PendingProposalCoordinator(store, source, TimeProvider.System);

        var first = coordinator.TrySubmitAsync(
            Submission("receipt-1", "key-1"),
            TestContext.Current.CancellationToken).AsTask();
        await source.ReadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = coordinator.TrySubmitAsync(
            Submission("receipt-2", "key-2"),
            TestContext.Current.CancellationToken).AsTask();
        Assert.False(second.IsCompleted);

        source.ReleaseRead.Set();
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.Equal(InboundProposalSubmissionStatus.Accepted, result.Status));
        Assert.Equal(["receipt-1", "receipt-2"], coordinator.PendingProposals.Select(item => item.Receipt));
    }

    [Fact]
    public async Task DisposalClosesWaitingAndInFlightSubmissionsWithoutDisposingTheirGate()
    {
        using var source = new BlockingProposalSource();
        var store = new MemoryAgentIntegrationStore();
        var coordinator = new PendingProposalCoordinator(store, source, TimeProvider.System);
        var first = coordinator.TrySubmitAsync(
            Submission("receipt-1", "key-1"), TestContext.Current.CancellationToken).AsTask();
        await source.ReadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = coordinator.TrySubmitAsync(
            Submission("receipt-2", "key-2"), TestContext.Current.CancellationToken).AsTask();

        coordinator.Dispose();
        source.ReleaseRead.Set();
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result =>
            Assert.Equal(InboundProposalSubmissionStatus.WorkspaceUnavailable, result.Status));
        Assert.Empty(store.ReadPendingProposals());
    }

    [Fact]
    public async Task OlderDiscardSnapshotCannotHideAConcurrentlyAcceptedProposal()
    {
        var store = new MemoryAgentIntegrationStore();
        store.Seed(Pending("receipt-1", "key-1"));
        using var coordinator = new PendingProposalCoordinator(store, new StaticProposalSource(), TimeProvider.System);
        store.BlockNextPendingRead();

        var discard = Task.Run(() => coordinator.Discard("receipt-1"), TestContext.Current.CancellationToken);
        await store.PendingReadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var accepted = await coordinator.TrySubmitAsync(
            Submission("receipt-2", "key-2"), TestContext.Current.CancellationToken);
        store.ReleasePendingRead.Set();

        Assert.True(await discard);
        Assert.Equal(InboundProposalSubmissionStatus.Accepted, accepted.Status);
        Assert.Equal("receipt-2", Assert.Single(coordinator.PendingProposals).Receipt);
    }

    [Fact]
    public async Task QueueAcceptsTenThenRejectsNewWorkWhileExactRetryKeepsItsReceipt()
    {
        var store = new MemoryAgentIntegrationStore();
        using var coordinator = new PendingProposalCoordinator(store, new StaticProposalSource(), TimeProvider.System);
        var acceptedEvents = 0;
        coordinator.ProposalAccepted += (_, _) => acceptedEvents++;

        for (var index = 0; index < AgentInterchangeV1.MaximumPendingProposals; index++)
        {
            var accepted = await coordinator.TrySubmitAsync(
                Submission($"receipt-{index}", $"key-{index}"),
                TestContext.Current.CancellationToken);
            Assert.Equal(InboundProposalSubmissionStatus.Accepted, accepted.Status);
        }

        var full = await coordinator.TrySubmitAsync(
            Submission("receipt-full", "key-full"),
            TestContext.Current.CancellationToken);
        var duplicate = await coordinator.TrySubmitAsync(
            Submission("unused", "key-0"),
            TestContext.Current.CancellationToken);

        Assert.Equal(InboundProposalSubmissionStatus.Occupied, full.Status);
        Assert.Equal(InboundProposalSubmissionStatus.Duplicate, duplicate.Status);
        Assert.Equal("receipt-0", duplicate.Receipt);
        Assert.True(duplicate.ReviewRequired);
        Assert.Equal(10, coordinator.PendingProposalCount);
        Assert.Equal(10, acceptedEvents);
    }

    [Fact]
    public async Task InvalidSubmissionReleasesGateAndDiscardedRetryIsAlreadyProcessed()
    {
        var store = new MemoryAgentIntegrationStore();
        using var coordinator = new PendingProposalCoordinator(store, new StaticProposalSource(), TimeProvider.System);

        var invalid = await coordinator.TrySubmitAsync(
            Submission("invalid", "invalid", "{}"u8.ToArray()),
            TestContext.Current.CancellationToken);
        var accepted = await coordinator.TrySubmitAsync(
            Submission("receipt-1", "key-1"),
            TestContext.Current.CancellationToken);
        var conflict = await coordinator.TrySubmitAsync(
            Submission("unused", "key-1", "different"u8.ToArray()),
            TestContext.Current.CancellationToken);
        Assert.True(coordinator.Discard("receipt-1"));
        var duplicate = await coordinator.TrySubmitAsync(
            Submission("unused-again", "key-1"),
            TestContext.Current.CancellationToken);

        Assert.Equal(InboundProposalSubmissionStatus.Invalid, invalid.Status);
        Assert.Equal("unsupported-version", invalid.FailureCode);
        Assert.Equal(InboundProposalSubmissionStatus.Accepted, accepted.Status);
        Assert.Equal(InboundProposalSubmissionStatus.IdempotencyConflict, conflict.Status);
        Assert.Equal(InboundProposalSubmissionStatus.Duplicate, duplicate.Status);
        Assert.False(duplicate.ReviewRequired);
        Assert.Empty(coordinator.PendingProposals);
    }

    [Fact]
    public async Task ManualTextRemainsEditableBesideInboundQueueAndNeverReceivesInboundJson()
    {
        var store = new MemoryAgentIntegrationStore();
        using var coordinator = new PendingProposalCoordinator(store, new StaticProposalSource(), TimeProvider.System);
        _ = await coordinator.TrySubmitAsync(
            Submission("receipt-1", "key-1"),
            TestContext.Current.CancellationToken);
        var viewModel = new AiViewModel(null, coordinator);

        viewModel.ProposalText = "manual proposal text";

        Assert.Equal("manual proposal text", viewModel.ProposalText);
        Assert.True(viewModel.CanReplaceProposalText);
        Assert.True(viewModel.HasPendingInboundProposal);
        Assert.Single(viewModel.PendingProposals);
        Assert.Equal("Unverified client", viewModel.PendingProposals[0].SourceLabel);
    }

    [Fact]
    public async Task AcceptedQueuedChangeRetainsItsCompatibilityBasis()
    {
        var store = new MemoryAgentIntegrationStore();
        var source = new StaticProposalSource();
        using var coordinator = new PendingProposalCoordinator(store, source, TimeProvider.System);
        var state = source.Read();
        using var snapshot = JsonDocument.Parse(PlanningSnapshotJsonGenerator.Generate(
            PlanningSnapshotProjection.Create(state.Revision, state.Workspace)).Payload!.Value);
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            format = "dot-orbit-change-proposal",
            schemaVersion = 1,
            sourceSnapshotId = snapshot.RootElement.GetProperty("snapshotId").GetString(),
            sourceWorkspaceRevision = state.Revision.ToString(),
            operations = new[]
            {
                new
                {
                    operation = "createTask",
                    operationId = "create-task",
                    localId = "task",
                    title = "Queued task",
                    description = string.Empty,
                    project = (object?)null,
                    category = new { mode = "explicit", category = new { existingId = "c" } },
                    dueDate = (string?)null,
                    participants = Array.Empty<object>(),
                },
            },
        }));

        var result = await coordinator.TrySubmitAsync(
            Submission("receipt-change", "key-change", payload),
            TestContext.Current.CancellationToken);

        Assert.Equal(InboundProposalSubmissionStatus.Accepted, result.Status);
        var pending = Assert.Single(coordinator.PendingProposals);
        Assert.Equal(ProposalKind.WorkspaceChange, pending.ProposalKind);
        Assert.False(pending.ReviewBasis.IsEmpty);
    }

    [Fact]
    public async Task FreshArrivalPreservesExistingRowIdentityAndUsesTheInjectedLocalTimeZone()
    {
        var store = new MemoryAgentIntegrationStore();
        using var coordinator = new PendingProposalCoordinator(store, new StaticProposalSource(), TimeProvider.System);
        _ = await coordinator.TrySubmitAsync(
            Submission("receipt-1", "key-1"), TestContext.Current.CancellationToken);
        var localZone = TimeZoneInfo.CreateCustomTimeZone(
            "Synthetic +02", TimeSpan.FromHours(2), "Synthetic +02", "Synthetic +02");
        var viewModel = new AiViewModel(
            null,
            coordinator,
            new FixedTimeProvider(DateTimeOffset.UnixEpoch, localZone));
        var originalRow = Assert.Single(viewModel.PendingProposals);

        _ = await coordinator.TrySubmitAsync(
            Submission("receipt-2", "key-2"), TestContext.Current.CancellationToken);
        viewModel.RefreshPendingProposals();

        Assert.Same(originalRow, viewModel.PendingProposals[0]);
        Assert.Equal("Received 21 Oct 2026, 11:00", originalRow.ReceivedText);
        Assert.False(originalRow.IsLast);
        Assert.True(viewModel.PendingProposals[1].IsLast);
    }

    [Fact]
    public async Task TargetedDiscardFailureRetainsOnlyTheSelectedPendingRow()
    {
        var store = new MemoryAgentIntegrationStore();
        using var coordinator = new PendingProposalCoordinator(store, new StaticProposalSource(), TimeProvider.System);
        _ = await coordinator.TrySubmitAsync(
            Submission("receipt-1", "key-1"), TestContext.Current.CancellationToken);
        _ = await coordinator.TrySubmitAsync(
            Submission("receipt-2", "key-2"), TestContext.Current.CancellationToken);
        var viewModel = new AiViewModel(null, coordinator);
        store.RemovePendingProposalSucceeds = false;

        Assert.False(viewModel.DiscardInboundProposal("receipt-2"));
        Assert.Equal(2, coordinator.PendingProposalCount);

        store.RemovePendingProposalSucceeds = true;
        Assert.True(viewModel.DiscardInboundProposal("receipt-2"));
        Assert.Equal("receipt-1", Assert.Single(coordinator.PendingProposals).Receipt);
        Assert.Equal("Inbound proposal discarded. Nothing was applied.", viewModel.ProposalStatus);
    }

    [Fact]
    public void RestoredQueueDoesNotRaiseFreshArrivalNotification()
    {
        var store = new MemoryAgentIntegrationStore();
        store.Seed(Pending("receipt-1", "key-1"));
        using var coordinator = new PendingProposalCoordinator(store, new StaticProposalSource(), TimeProvider.System);
        var acceptedEvents = 0;
        coordinator.ProposalAccepted += (_, _) => acceptedEvents++;

        Assert.Single(coordinator.PendingProposals);
        Assert.Equal(0, acceptedEvents);
    }

    [Fact]
    public async Task FreshArrivalShowsContentFreeNotificationAndReviewPillWithoutTakingNavigation()
    {
        var store = new MemoryAgentIntegrationStore();
        using var coordinator = new PendingProposalCoordinator(store, new StaticProposalSource(), TimeProvider.System);
        var preferences = new TransientAiFeaturePreferenceService(aiFeaturesEnabled: true);
        var shell = new ShellViewModel(
            new MemoryWorkspaceWork(),
            TimeProvider.System,
            null,
            uiDispatcher: new InlineUiDispatcher(),
            aiFeaturePreferenceService: preferences,
            pendingProposalCoordinator: coordinator);
        var originalSelection = shell.SelectedItem;

        _ = await coordinator.TrySubmitAsync(
            Submission("receipt-1", "key-1"),
            TestContext.Current.CancellationToken);

        Assert.Same(originalSelection, shell.SelectedItem);
        Assert.True(shell.HasProposalArrivalNotification);
        Assert.Equal("1 AI proposal needs review. Nothing has been applied.", shell.ProposalArrivalMessage);
        Assert.Equal("Review 1", shell.AiNavigation.AttentionText);
        Assert.Equal("1 proposal awaiting review", shell.AiNavigation.AccessibleCount);
        Assert.DoesNotContain("Task", shell.ProposalArrivalMessage, StringComparison.Ordinal);

        shell.OpenPendingProposals();

        Assert.True(shell.ShowAI);
        Assert.False(shell.HasProposalArrivalNotification);
    }

    [Fact]
    public async Task FreshArrivalShowsNotificationWhileAiViewIsAlreadySelected()
    {
        var store = new MemoryAgentIntegrationStore();
        using var coordinator = new PendingProposalCoordinator(store, new StaticProposalSource(), TimeProvider.System);
        var shell = new ShellViewModel(
            new MemoryWorkspaceWork(),
            TimeProvider.System,
            null,
            uiDispatcher: new InlineUiDispatcher(),
            aiFeaturePreferenceService: new TransientAiFeaturePreferenceService(aiFeaturesEnabled: true),
            pendingProposalCoordinator: coordinator);
        shell.AiNavigation.SelectCommand.Execute(null);

        _ = await coordinator.TrySubmitAsync(
            Submission("receipt-1", "key-1"), TestContext.Current.CancellationToken);

        Assert.True(shell.ShowAI);
        Assert.True(shell.HasProposalArrivalNotification);
        Assert.Equal("1 AI proposal needs review. Nothing has been applied.", shell.ProposalArrivalMessage);
    }

    private static InboundProposalSubmission Submission(
        string receipt,
        string key,
        byte[]? payload = null) => new(
        receipt,
        key,
        "authorization-1",
        new string('b', 64),
        "Unverified client",
        new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero),
        payload ?? ValidProposal);

    private static PendingAgentProposal Pending(string receipt, string key) => new(
        receipt,
        key,
        "authorization-1",
        new string('b', 64),
        "Unverified client",
        DateTimeOffset.UnixEpoch,
        ProposalKind.NewWork,
        2,
        2,
        ValidProposal);

    private sealed class StaticProposalSource : IProposalWorkspaceSource
    {
        public ProposalWorkspaceState Read() => State();
    }

    private sealed class BlockingProposalSource : IProposalWorkspaceSource, IDisposable
    {
        public TaskCompletionSource ReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim ReleaseRead { get; } = new(false);

        public ProposalWorkspaceState Read()
        {
            ReadStarted.TrySetResult();
            ReleaseRead.Wait(TestContext.Current.CancellationToken);
            return State();
        }

        public void Dispose() => ReleaseRead.Dispose();
    }

    private static ProposalWorkspaceState State() => new(
        new(1),
        new([new("c", "Synthetic", 0)], [], [], []),
        [],
        []);

    private sealed class MemoryAgentIntegrationStore : IAgentIntegrationStore
    {
        private readonly object _gate = new();
        private readonly List<PendingAgentProposal> _pending = [];
        private readonly Dictionary<(string ClientIdentity, string Key), (string Digest, string Receipt)> _receipts = [];
        private int _blockNextPendingRead;

        public bool RemovePendingProposalSucceeds { get; set; } = true;
        public TaskCompletionSource PendingReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim ReleasePendingRead { get; } = new(false);

        public void BlockNextPendingRead() => Interlocked.Exchange(ref _blockNextPendingRead, 1);

        public void Seed(PendingAgentProposal proposal)
        {
            lock (_gate)
            {
                _pending.Add(proposal);
                _receipts[(proposal.ClientIdentity, proposal.IdempotencyKey)] =
                    (proposal.PayloadDigest, proposal.Receipt);
            }
        }

        public IReadOnlyList<AgentClientAuthorization> ReadAuthorizations() => [];
        public AgentClientAuthorization? FindAuthorization(
            string credentialVerifier,
            string operatingSystemAccount,
            AgentClientScopes exactScopes) => null;
        public void RememberAuthorization(AgentClientAuthorization authorization) =>
            throw new NotSupportedException();
        public bool RevokeAuthorization(string authorizationId) => false;
        public IReadOnlyList<PendingAgentProposal> ReadPendingProposals()
        {
            PendingAgentProposal[] snapshot;
            lock (_gate)
            {
                snapshot = _pending.ToArray();
            }

            if (Interlocked.Exchange(ref _blockNextPendingRead, 0) == 1)
            {
                PendingReadStarted.TrySetResult();
                ReleasePendingRead.Wait(TestContext.Current.CancellationToken);
            }

            return Array.AsReadOnly(snapshot);
        }

        public PendingAgentProposal? FindPendingProposal(string receipt)
        {
            lock (_gate)
            {
                return _pending.SingleOrDefault(proposal => proposal.Receipt == receipt);
            }
        }

        public AgentProposalReceipt? FindProposalReceipt(string clientIdentity, string idempotencyKey)
        {
            lock (_gate)
            {
                return _receipts.TryGetValue((clientIdentity, idempotencyKey), out var prior)
                    ? new(prior.Digest, prior.Receipt)
                    : null;
            }
        }

        public PendingAgentProposalStoreResult TryStorePendingProposal(PendingAgentProposal proposal)
        {
            lock (_gate)
            {
                var key = (proposal.ClientIdentity, proposal.IdempotencyKey);
                if (_receipts.TryGetValue(key, out var prior))
                {
                    return prior.Digest == proposal.PayloadDigest
                        ? new(PendingAgentProposalStoreStatus.Duplicate, prior.Receipt)
                        : new(PendingAgentProposalStoreStatus.IdempotencyConflict, null);
                }

                if (_pending.Count >= AgentInterchangeV1.MaximumPendingProposals)
                {
                    return new(PendingAgentProposalStoreStatus.Occupied, null);
                }

                _pending.Add(proposal);
                _receipts[key] = (proposal.PayloadDigest, proposal.Receipt);
                return new(PendingAgentProposalStoreStatus.Stored, proposal.Receipt);
            }
        }

        public bool RemovePendingProposal(string receipt)
        {
            lock (_gate)
            {
                return RemovePendingProposalSucceeds
                       && _pending.RemoveAll(proposal => proposal.Receipt == receipt) == 1;
            }
        }
    }
}
