using System.Security.Cryptography;

namespace DotOrbit.Core.Interchange;

[Flags]
public enum AgentClientScopes
{
    None = 0,
    ReadInterchangeContext = 1,
    SubmitProposals = 2,
}

public sealed record AgentClientAuthorization(
    string Id,
    string CredentialVerifier,
    string OperatingSystemAccount,
    string ClientName,
    string ClientVersion,
    AgentClientScopes Scopes,
    DateTimeOffset AuthorizedAt);

public sealed class PendingAgentProposal
{
    private readonly byte[] _strictUtf8;
    private readonly byte[] _reviewBasis;

    public PendingAgentProposal(
        string receipt,
        string idempotencyKey,
        string authorizationId,
        string clientIdentity,
        string clientName,
        DateTimeOffset receivedAt,
        ProposalKind? proposalKind,
        int operationCount,
        int effectCount,
        ReadOnlySpan<byte> strictUtf8)
        : this(
            receipt,
            idempotencyKey,
            authorizationId,
            clientIdentity,
            clientName,
            receivedAt,
            proposalKind,
            operationCount,
            effectCount,
            ReadOnlySpan<byte>.Empty,
            strictUtf8)
    {
    }

    public PendingAgentProposal(
        string receipt,
        string idempotencyKey,
        string authorizationId,
        string clientIdentity,
        string clientName,
        DateTimeOffset receivedAt,
        ProposalKind? proposalKind,
        int operationCount,
        int effectCount,
        ReadOnlySpan<byte> reviewBasis,
        ReadOnlySpan<byte> strictUtf8)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receipt);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);
        if (strictUtf8.IsEmpty || strictUtf8.Length > AgentInterchangeV1.MaximumProposalBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(strictUtf8));
        }
        if (proposalKind is not null && !Enum.IsDefined(proposalKind.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(proposalKind));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(operationCount);
        ArgumentOutOfRangeException.ThrowIfNegative(effectCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(operationCount, AgentInterchangeV1.MaximumOperations);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(effectCount, AgentInterchangeV1.MaximumPreviewEffects);
        if (reviewBasis.Length > AgentInterchangeV1.MaximumPendingReviewBasisBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(reviewBasis));
        }

        Receipt = receipt;
        IdempotencyKey = idempotencyKey;
        AuthorizationId = authorizationId;
        ClientIdentity = clientIdentity;
        ClientName = clientName;
        ReceivedAt = receivedAt;
        ProposalKind = proposalKind;
        OperationCount = operationCount;
        EffectCount = effectCount;
        _reviewBasis = reviewBasis.ToArray();
        _strictUtf8 = strictUtf8.ToArray();
        PayloadDigest = Convert.ToHexStringLower(SHA256.HashData(strictUtf8));
    }

    public string Receipt { get; }
    public string IdempotencyKey { get; }
    public string AuthorizationId { get; }
    public string ClientIdentity { get; }
    public string ClientName { get; }
    public DateTimeOffset ReceivedAt { get; }
    public ProposalKind? ProposalKind { get; }
    public int OperationCount { get; }
    public int EffectCount { get; }
    public string PayloadDigest { get; }
    public ReadOnlyMemory<byte> ReviewBasis => _reviewBasis;
    public ReadOnlyMemory<byte> StrictUtf8 => _strictUtf8;
}

public enum PendingAgentProposalStoreStatus
{
    Stored,
    Duplicate,
    Occupied,
    IdempotencyConflict,
}

public sealed record PendingAgentProposalStoreResult(
    PendingAgentProposalStoreStatus Status,
    string? Receipt);

public sealed record AgentProposalReceipt(
    string PayloadDigest,
    string Receipt);

public interface IAgentIntegrationStore
{
    IReadOnlyList<AgentClientAuthorization> ReadAuthorizations();

    AgentClientAuthorization? FindAuthorization(
        string credentialVerifier,
        string operatingSystemAccount,
        AgentClientScopes exactScopes);

    void RememberAuthorization(AgentClientAuthorization authorization);

    bool RevokeAuthorization(string authorizationId);

    IReadOnlyList<PendingAgentProposal> ReadPendingProposals();

    PendingAgentProposal? FindPendingProposal(string receipt);

    AgentProposalReceipt? FindProposalReceipt(string clientIdentity, string idempotencyKey);

    PendingAgentProposalStoreResult TryStorePendingProposal(PendingAgentProposal proposal);

    bool RemovePendingProposal(string receipt);
}
