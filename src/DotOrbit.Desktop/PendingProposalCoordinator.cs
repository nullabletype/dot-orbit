using DotOrbit.Core.Interchange;
using DotOrbit.Core.Workspaces;
using DotOrbit.Interchange.Json;

namespace DotOrbit.Desktop;

internal enum InboundProposalSubmissionStatus
{
    Accepted,
    Duplicate,
    Occupied,
    Invalid,
    WorkspaceUnavailable,
    Cancelled,
    IdempotencyConflict,
}

internal sealed record InboundProposalSubmission(
    string Receipt,
    string IdempotencyKey,
    string AuthorizationId,
    string ClientIdentity,
    string ClientName,
    DateTimeOffset ReceivedAt,
    ReadOnlyMemory<byte> StrictUtf8);

internal sealed record InboundProposalSubmissionResult(
    InboundProposalSubmissionStatus Status,
    string? Receipt = null,
    string? FailureCode = null,
    bool ReviewRequired = false);

internal interface IInboundProposalSink
{
    ValueTask<InboundProposalSubmissionResult> TrySubmitAsync(
        InboundProposalSubmission submission,
        CancellationToken cancellationToken = default);
}

internal sealed class PendingProposalCoordinator : IInboundProposalSink, IDisposable
{
    private readonly object _gate = new();
    private readonly IAgentIntegrationStore _store;
    private readonly IProposalWorkspaceSource _proposalSource;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _submissionGate = new(1, 1);
    private IReadOnlyList<PendingAgentProposal> _pending;
    private long _queueVersion;
    private long _publishedQueueVersion;
    private bool _disposed;

    public PendingProposalCoordinator(
        IAgentIntegrationStore store,
        IProposalWorkspaceSource proposalSource,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(proposalSource);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _store = store;
        _proposalSource = proposalSource;
        _timeProvider = timeProvider;
        _pending = store.ReadPendingProposals();
    }

    public event EventHandler? StateChanged;
    public event EventHandler? ProposalAccepted;

    public IReadOnlyList<PendingAgentProposal> PendingProposals
    {
        get
        {
            lock (_gate)
            {
                return Array.AsReadOnly(_pending.ToArray());
            }
        }
    }

    public int PendingProposalCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    public bool HasPendingProposal => PendingProposalCount > 0;

    public PendingAgentProposal? FindPendingProposal(string receipt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receipt);
        lock (_gate)
        {
            return _pending.SingleOrDefault(item =>
                string.Equals(item.Receipt, receipt, StringComparison.Ordinal));
        }
    }

    public async ValueTask<InboundProposalSubmissionResult> TrySubmitAsync(
        InboundProposalSubmission submission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        try
        {
            await _submissionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(InboundProposalSubmissionStatus.Cancelled);
        }
        catch (ObjectDisposedException)
        {
            return new(InboundProposalSubmissionStatus.WorkspaceUnavailable);
        }

        try
        {
            if (Volatile.Read(ref _disposed))
            {
                return new(InboundProposalSubmissionStatus.WorkspaceUnavailable);
            }

            var digest = Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(submission.StrictUtf8.Span));
            var priorReceipt = _store.FindProposalReceipt(
                submission.ClientIdentity,
                submission.IdempotencyKey);
            if (priorReceipt is not null)
            {
                if (!string.Equals(priorReceipt.PayloadDigest, digest, StringComparison.Ordinal))
                {
                    return new(InboundProposalSubmissionStatus.IdempotencyConflict);
                }

                var pending = _store.FindPendingProposal(priorReceipt.Receipt);
                ReloadPending(Volatile.Read(ref _queueVersion));
                return new(
                    InboundProposalSubmissionStatus.Duplicate,
                    priorReceipt.Receipt,
                    ReviewRequired: pending is not null);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var boundary = new ProposalReviewBoundary(_proposalSource, _timeProvider);
            var reviewed = await Task.Run(
                () => boundary.PrepareQueued(submission.StrictUtf8),
                cancellationToken).ConfigureAwait(false);
            if (reviewed.Plan is not { } plan)
            {
                return new(
                    InboundProposalSubmissionStatus.Invalid,
                    FailureCode: reviewed.Failures.Count == 0
                        ? "proposal-invalid"
                        : reviewed.Failures[0].Code);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _disposed))
            {
                return new(InboundProposalSubmissionStatus.WorkspaceUnavailable);
            }

            var proposal = new PendingAgentProposal(
                submission.Receipt,
                submission.IdempotencyKey,
                submission.AuthorizationId,
                submission.ClientIdentity,
                submission.ClientName,
                submission.ReceivedAt,
                plan.Kind,
                plan.Operations.Count,
                plan.Preview.Count,
                reviewed.ReviewBasis.Span,
                submission.StrictUtf8.Span);
            var stored = _store.TryStorePendingProposal(proposal);
            var queueVersion = Interlocked.Increment(ref _queueVersion);
            ReloadPending(queueVersion);
            if (stored.Status == PendingAgentProposalStoreStatus.Occupied)
            {
                return new(InboundProposalSubmissionStatus.Occupied);
            }

            if (stored.Status == PendingAgentProposalStoreStatus.IdempotencyConflict)
            {
                return new(InboundProposalSubmissionStatus.IdempotencyConflict);
            }

            if (stored.Status == PendingAgentProposalStoreStatus.Duplicate)
            {
                return new(
                    InboundProposalSubmissionStatus.Duplicate,
                    stored.Receipt,
                    ReviewRequired: stored.Receipt is not null
                        && FindPendingProposal(stored.Receipt) is not null);
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
            ProposalAccepted?.Invoke(this, EventArgs.Empty);
            return new(
                InboundProposalSubmissionStatus.Accepted,
                stored.Receipt,
                ReviewRequired: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(InboundProposalSubmissionStatus.Cancelled);
        }
        catch (Exception exception) when (exception is WorkspaceWorkException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or InvalidOperationException
                                          or ObjectDisposedException)
        {
            return new(InboundProposalSubmissionStatus.WorkspaceUnavailable);
        }
        finally
        {
            _submissionGate.Release();
        }
    }

    public bool Discard(string receipt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receipt);
        if (Volatile.Read(ref _disposed))
        {
            return false;
        }

        try
        {
            if (!_store.RemovePendingProposal(receipt)
                && _store.FindPendingProposal(receipt) is not null)
            {
                return false;
            }

            ReloadPending(Interlocked.Increment(ref _queueVersion));
        }
        catch (Exception exception) when (exception is WorkspaceWorkException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or InvalidOperationException
                                          or ObjectDisposedException)
        {
            return false;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void CompleteApplied(string receipt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receipt);
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        var queueVersion = Interlocked.Increment(ref _queueVersion);
        try
        {
            ReloadPending(queueVersion);
        }
        catch (Exception exception) when (exception is WorkspaceWorkException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or InvalidOperationException
                                          or ObjectDisposedException)
        {
            lock (_gate)
            {
                if (queueVersion >= _publishedQueueVersion)
                {
                    _pending = Array.AsReadOnly(_pending
                        .Where(item => !string.Equals(item.Receipt, receipt, StringComparison.Ordinal))
                        .ToArray());
                    _publishedQueueVersion = queueVersion;
                }
            }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        Volatile.Write(ref _disposed, true);
    }

    private void ReloadPending(long queueVersion)
    {
        var pending = _store.ReadPendingProposals();
        lock (_gate)
        {
            if (queueVersion >= _publishedQueueVersion)
            {
                _pending = pending;
                _publishedQueueVersion = queueVersion;
            }
        }
    }
}
