using System.Text;
using System.Text.Json;
using DotOrbit.Core.Interchange;
using DotOrbit.Core.Workspaces;
using DotOrbit.Interchange.Json;
using DotOrbit.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DotOrbit.Storage.Sqlite.Tests;

public sealed class AgentIntegrationStoreTests : IDisposable
{
    private const string PassphraseText = "synthetic local agent store password";
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"dot-orbit-agent-store-{Guid.NewGuid():N}");
    private readonly EncryptedWorkspaceStore _store = new();
    private readonly WorkspacePassphrase _passphrase = WorkspacePassphrase.Create(
        PassphraseText,
        PassphraseText).Passphrase!;

    private string WorkspacePath => Path.Combine(_directory, "workspace.orb");

    [Fact]
    public void AgentSchemaHasTheCanonicalValidatedShape()
    {
        _ = new EncryptedWorkspaceStore();
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SqliteAgentIntegrationStore.Schema;
        command.ExecuteNonQuery();

        SqliteAgentIntegrationStore.ValidateShape(connection, null);
    }

    [Fact]
    public void RememberedAuthorizationRequiresAnExactAccountVerifierAndScopeMatch()
    {
        using var session = CreateWorkspace();
        var before = session.Planning.Read().WorkspaceRevision;
        var authorization = new AgentClientAuthorization(
            "authorization-1",
            new string('a', 64),
            "synthetic-os-account",
            "Unverified client",
            "1.0",
            AgentClientScopes.ReadInterchangeContext,
            new DateTimeOffset(2026, 10, 21, 7, 0, 0, TimeSpan.Zero));

        session.AgentIntegration.RememberAuthorization(authorization);

        Assert.Equal(authorization, session.AgentIntegration.FindAuthorization(
            authorization.CredentialVerifier,
            authorization.OperatingSystemAccount,
            AgentClientScopes.ReadInterchangeContext));
        Assert.Null(session.AgentIntegration.FindAuthorization(
            authorization.CredentialVerifier,
            authorization.OperatingSystemAccount,
            AgentClientScopes.ReadInterchangeContext | AgentClientScopes.SubmitProposals));
        Assert.Null(session.AgentIntegration.FindAuthorization(
            authorization.CredentialVerifier,
            "different-account",
            AgentClientScopes.ReadInterchangeContext));
        Assert.Equal(before, session.Planning.Read().WorkspaceRevision);

        Assert.True(session.AgentIntegration.RevokeAuthorization(authorization.Id));
        Assert.Empty(session.AgentIntegration.ReadAuthorizations());
        Assert.False(session.AgentIntegration.RevokeAuthorization(authorization.Id));
        Assert.Equal(before, session.Planning.Read().WorkspaceRevision);
    }

    [Fact]
    public void PendingProposalQueueIsDurableOrderedBoundedAndReceiptAddressedWithoutAdvancingRevision()
    {
        var receivedAt = new DateTimeOffset(2026, 10, 21, 8, 0, 0, TimeSpan.Zero);
        var identity = new string('b', 64);
        var proposals = Enumerable.Range(0, AgentInterchangeV1.MaximumPendingProposals)
            .Select(index => Proposal(
                $"receipt-{index}",
                $"idempotency-{index}",
                identity,
                receivedAt.AddMinutes(index),
                index % 2 == 0 ? ProposalKind.NewWork : ProposalKind.WorkspaceChange,
                index + 1,
                index + 2))
            .ToArray();
        using (var session = CreateWorkspace())
        {
            var before = session.Planning.Read().WorkspaceRevision;
            foreach (var proposal in proposals)
            {
                var stored = session.AgentIntegration.TryStorePendingProposal(proposal);
                Assert.Equal(PendingAgentProposalStoreStatus.Stored, stored.Status);
                Assert.Equal(proposal.Receipt, stored.Receipt);
            }

            var duplicate = session.AgentIntegration.TryStorePendingProposal(Proposal(
                "different-receipt",
                proposals[0].IdempotencyKey,
                identity,
                receivedAt.AddHours(1),
                proposals[0].ProposalKind,
                proposals[0].OperationCount,
                proposals[0].EffectCount));
            var conflict = session.AgentIntegration.TryStorePendingProposal(Proposal(
                "conflicting-receipt",
                proposals[0].IdempotencyKey,
                identity,
                receivedAt.AddHours(1),
                payload: "different payload"));
            var occupied = session.AgentIntegration.TryStorePendingProposal(Proposal(
                "receipt-full",
                "idempotency-full",
                identity,
                receivedAt.AddHours(1)));

            Assert.Equal(PendingAgentProposalStoreStatus.Duplicate, duplicate.Status);
            Assert.Equal(proposals[0].Receipt, duplicate.Receipt);
            Assert.Equal(PendingAgentProposalStoreStatus.IdempotencyConflict, conflict.Status);
            Assert.Null(conflict.Receipt);
            Assert.Equal(PendingAgentProposalStoreStatus.Occupied, occupied.Status);
            Assert.Null(occupied.Receipt);
            Assert.Equal(before, session.Planning.Read().WorkspaceRevision);
        }

        using var reopened = Assert.IsAssignableFrom<IWorkspaceSession>(
            _store.Open(WorkspacePath, _passphrase).Session);
        var pending = reopened.AgentIntegration.ReadPendingProposals();
        Assert.Equal(proposals.Select(item => item.Receipt), pending.Select(item => item.Receipt));
        Assert.Equal(proposals[1].ProposalKind, pending[1].ProposalKind);
        Assert.Equal(proposals[1].OperationCount, pending[1].OperationCount);
        Assert.Equal(proposals[1].EffectCount, pending[1].EffectCount);
        Assert.True(proposals[1].StrictUtf8.Span.SequenceEqual(pending[1].StrictUtf8.Span));
        var found = Assert.IsType<PendingAgentProposal>(
            reopened.AgentIntegration.FindPendingProposal(proposals[4].Receipt));
        Assert.Equal(proposals[4].Receipt, found.Receipt);
        Assert.Equal(proposals[4].PayloadDigest, found.PayloadDigest);

        Assert.True(reopened.AgentIntegration.RemovePendingProposal(proposals[4].Receipt));
        Assert.Null(reopened.AgentIntegration.FindPendingProposal(proposals[4].Receipt));
        var replacement = Proposal(
            "receipt-replacement",
            "idempotency-replacement",
            identity,
            receivedAt.AddHours(2));
        Assert.Equal(
            PendingAgentProposalStoreStatus.Stored,
            reopened.AgentIntegration.TryStorePendingProposal(replacement).Status);
        Assert.Equal(
            proposals.Where(item => item != proposals[4]).Select(item => item.Receipt).Append(replacement.Receipt),
            reopened.AgentIntegration.ReadPendingProposals().Select(item => item.Receipt));
        Assert.Equal(1, reopened.Planning.Read().WorkspaceRevision.Value);
    }

    [Fact]
    public void ReceiptRetentionUsesInsertionOrderWhenTheClockMovesBackward()
    {
        using var session = CreateWorkspace();
        var identity = new string('c', 64);
        var future = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var protectedPending = Proposal("receipt-pending", "key-pending", identity, future);
        Assert.Equal(
            PendingAgentProposalStoreStatus.Stored,
            session.AgentIntegration.TryStorePendingProposal(protectedPending).Status);
        for (var index = 0; index < LocalReceiptLimit + 8; index++)
        {
            var proposal = Proposal($"receipt-{index}", $"key-{index}", identity, future.AddMinutes(index + 1));
            Assert.Equal(
                PendingAgentProposalStoreStatus.Stored,
                session.AgentIntegration.TryStorePendingProposal(proposal).Status);
            Assert.True(session.AgentIntegration.RemovePendingProposal(proposal.Receipt));
        }

        var afterClockReset = Proposal("receipt-clock-reset", "key-clock-reset", identity, future.AddDays(-1));
        Assert.Equal(
            PendingAgentProposalStoreStatus.Stored,
            session.AgentIntegration.TryStorePendingProposal(afterClockReset).Status);
        Assert.True(session.AgentIntegration.RemovePendingProposal(afterClockReset.Receipt));

        var replay = session.AgentIntegration.TryStorePendingProposal(
            Proposal("unused-receipt", afterClockReset.IdempotencyKey, identity, future.AddDays(-2)));

        Assert.Equal(PendingAgentProposalStoreStatus.Duplicate, replay.Status);
        Assert.Equal(afterClockReset.Receipt, replay.Receipt);
        var protectedReplay = session.AgentIntegration.TryStorePendingProposal(
            Proposal("unused-pending-receipt", protectedPending.IdempotencyKey, identity, future.AddDays(-2)));
        Assert.Equal(PendingAgentProposalStoreStatus.Duplicate, protectedReplay.Status);
        Assert.Equal(protectedPending.Receipt, protectedReplay.Receipt);
        Assert.Equal(protectedPending.Receipt, Assert.Single(session.AgentIntegration.ReadPendingProposals()).Receipt);
    }

    [Fact]
    public void PendingProposalReviewBasisPersistsAcrossRestart()
    {
        var basis = Encoding.UTF8.GetBytes("synthetic opaque review basis");
        var payload = Encoding.UTF8.GetBytes("synthetic proposal payload");
        using (var session = CreateWorkspace())
        {
            var proposal = new PendingAgentProposal(
                "receipt-basis",
                "key-basis",
                "authorization-basis",
                new string('d', 64),
                "Synthetic client",
                DateTimeOffset.UnixEpoch,
                ProposalKind.WorkspaceChange,
                1,
                1,
                basis,
                payload);
            Assert.Equal(
                PendingAgentProposalStoreStatus.Stored,
                session.AgentIntegration.TryStorePendingProposal(proposal).Status);
        }

        using var reopened = Assert.IsAssignableFrom<IWorkspaceSession>(
            _store.Open(WorkspacePath, _passphrase).Session);
        var restored = Assert.Single(reopened.AgentIntegration.ReadPendingProposals());
        Assert.True(basis.AsSpan().SequenceEqual(restored.ReviewBasis.Span));
        Assert.True(payload.AsSpan().SequenceEqual(restored.StrictUtf8.Span));
    }

    [Fact]
    public void PersistedCompatibilityBasisRefreshesAfterRestartAndAnUnrelatedChange()
    {
        byte[] payload;
        using (var session = CreateWorkspace())
        {
            var state = session.Proposals.Read();
            var categoryId = Assert.Single(state.Workspace.Categories).Id;
            using var snapshot = JsonDocument.Parse(PlanningSnapshotJsonGenerator.Generate(
                PlanningSnapshotProjection.Create(state.Revision, state.Workspace)).Payload!.Value);
            payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
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
                        operationId = "queued-task",
                        localId = "queued-task",
                        title = "Queued task",
                        description = string.Empty,
                        project = (object?)null,
                        category = new { mode = "explicit", category = new { existingId = categoryId } },
                        dueDate = (string?)null,
                        participants = Array.Empty<object>(),
                    },
                },
            }));
            var admitted = new ProposalReviewBoundary(session.Proposals, TimeProvider.System)
                .PrepareQueued(payload);
            Assert.NotNull(admitted.Plan);
            var proposal = new PendingAgentProposal(
                "receipt-restart-basis",
                "key-restart-basis",
                "authorization-restart-basis",
                new string('e', 64),
                "Synthetic client",
                DateTimeOffset.UnixEpoch,
                ProposalKind.WorkspaceChange,
                1,
                admitted.Plan.Preview.Count,
                admitted.ReviewBasis.Span,
                payload);
            Assert.Equal(
                PendingAgentProposalStoreStatus.Stored,
                session.AgentIntegration.TryStorePendingProposal(proposal).Status);
        }

        using var reopened = Assert.IsAssignableFrom<IWorkspaceSession>(
            _store.Open(WorkspacePath, _passphrase).Session);
        var category = Assert.Single(reopened.Work.Read().Categories);
        reopened.Work.CreateStandaloneTask("Unrelated task", string.Empty, category.Id, null);
        var pending = Assert.Single(reopened.AgentIntegration.ReadPendingProposals());

        var reviewed = new ProposalReviewBoundary(reopened.Proposals, TimeProvider.System)
            .ReviewQueued(pending.StrictUtf8, pending.ReviewBasis);

        Assert.NotNull(reviewed.Plan);
        Assert.True(reviewed.WasRefreshed);
        Assert.Equal(new WorkspaceRevision(2), reviewed.Plan.SourceWorkspaceRevision);
    }

    private const int LocalReceiptLimit = 128;

    private static PendingAgentProposal Proposal(
        string receipt,
        string key,
        string identity,
        DateTimeOffset receivedAt,
        ProposalKind? kind = ProposalKind.NewWork,
        int operationCount = 1,
        int effectCount = 1,
        string? payload = null) => new(
        receipt,
        key,
        "authorization-1",
        identity,
        "Unverified client",
        receivedAt,
        kind,
        operationCount,
        effectCount,
        Encoding.UTF8.GetBytes(payload ?? $"payload-{key}"));

    private IWorkspaceSession CreateWorkspace()
    {
        Directory.CreateDirectory(_directory);
        return Assert.IsAssignableFrom<IWorkspaceSession>(_store.Create(
            WorkspacePath,
            _passphrase,
            CategoryName.Create("Home").CategoryName!).Session);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
