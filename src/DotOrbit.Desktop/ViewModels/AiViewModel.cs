using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using DotOrbit.Core.Interchange;
using DotOrbit.Desktop.Clipboard;
using DotOrbit.Interchange.Json;

namespace DotOrbit.Desktop.ViewModels;

public sealed class PendingProposalRowViewModel : INotifyPropertyChanged
{
    private bool _isLast;

    internal PendingProposalRowViewModel(PendingAgentProposal proposal, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        Receipt = proposal.Receipt;
        SourceLabel = proposal.ClientName;
        var localReceivedAt = TimeZoneInfo.ConvertTime(proposal.ReceivedAt, timeProvider.LocalTimeZone);
        ReceivedText = $"Received {localReceivedAt.ToString("dd MMM yyyy, HH:mm", CultureInfo.InvariantCulture)}";
        Title = proposal.ProposalKind switch
        {
            ProposalKind.NewWork => "New-work proposal",
            ProposalKind.WorkspaceChange => "Change proposal",
            _ => "Proposal",
        };
        KindLabel = proposal.ProposalKind switch
        {
            ProposalKind.NewWork => "Adds new work",
            ProposalKind.WorkspaceChange => "Updates existing work",
            _ => "Review to inspect",
        };
        Summary = proposal.OperationCount == 0 && proposal.EffectCount == 0
            ? "Review to inspect the validated changes"
            : $"{proposal.OperationCount.ToString(CultureInfo.InvariantCulture)} "
              + $"{Plural(proposal.OperationCount, "instruction", "instructions")} · "
              + $"{proposal.EffectCount.ToString(CultureInfo.InvariantCulture)} validated "
              + Plural(proposal.EffectCount, "effect", "effects");
        AccessibleName = $"{Title} from {SourceLabel}, {Summary}";
    }

    public string Receipt { get; }
    public string Title { get; }
    public string KindLabel { get; }
    public string Summary { get; }
    public string SourceLabel { get; }
    public string ReceivedText { get; }
    public string AccessibleName { get; }
    public string ReviewAccessibleName => $"Review {Title.ToLowerInvariant()} from {SourceLabel}";
    public string DiscardAccessibleName => $"Discard {Title.ToLowerInvariant()} from {SourceLabel} without applying it";
    public bool IsLast
    {
        get => _isLast;
        internal set
        {
            if (value == _isLast)
            {
                return;
            }

            _isLast = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLast)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static string Plural(int count, string singular, string plural) =>
        count == 1 ? singular : plural;
}

public sealed class AiViewModel : INotifyPropertyChanged
{
    private readonly IInterchangePreferenceService _interchangePreferenceService;
    private readonly PendingProposalCoordinator? _pendingProposalCoordinator;
    private readonly TimeProvider _timeProvider;
    private readonly ObservableCollection<PendingProposalRowViewModel> _pendingProposals = [];
    private bool _includeAgentInstructionsAndSchemaWithSnapshot;
    private string _interchangeStatus = string.Empty;
    private string _proposalText = string.Empty;
    private int _proposalByteCount;
    private string _proposalStatus = string.Empty;
    private string _pendingProposalReviewStatus = string.Empty;
    private string? _pendingProposalReviewStatusReceipt;
    private bool _proposalActionInFlight;

    public AiViewModel(IInterchangePreferenceService? interchangePreferenceService = null)
        : this(interchangePreferenceService, null, TimeProvider.System)
    {
    }

    internal AiViewModel(
        IInterchangePreferenceService? interchangePreferenceService,
        PendingProposalCoordinator? pendingProposalCoordinator = null,
        TimeProvider? timeProvider = null)
    {
        _interchangePreferenceService = interchangePreferenceService
            ?? new TransientInterchangePreferenceService();
        _pendingProposalCoordinator = pendingProposalCoordinator;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _includeAgentInstructionsAndSchemaWithSnapshot =
            _interchangePreferenceService.IncludeAgentInstructionsAndSchemaWithSnapshot;
        PendingProposals = new ReadOnlyObservableCollection<PendingProposalRowViewModel>(_pendingProposals);
        RefreshPendingProposals();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ReadOnlyObservableCollection<PendingProposalRowViewModel> PendingProposals { get; }

    public bool IncludeAgentInstructionsAndSchemaWithSnapshot
    {
        get => _includeAgentInstructionsAndSchemaWithSnapshot;
        set
        {
            if (value == _includeAgentInstructionsAndSchemaWithSnapshot)
            {
                return;
            }

            if (!_interchangePreferenceService.TrySetIncludeAgentInstructionsAndSchemaWithSnapshot(value))
            {
                InterchangeStatus =
                    "Could not save the snapshot output preference. The previous setting is still active.";
                Notify();
                return;
            }

            _includeAgentInstructionsAndSchemaWithSnapshot = value;
            Notify();
            Notify(nameof(InterchangePreferenceStatus));
            InterchangeStatus = value
                ? "Agent instructions and schemas will be included with Planning snapshots."
                : "Planning snapshots will be copied as JSON only.";
        }
    }

    public string InterchangePreferenceStatus => IncludeAgentInstructionsAndSchemaWithSnapshot
        ? "Agent instructions and schemas included"
        : "Snapshot JSON only";

    public string InterchangeStatus
    {
        get => _interchangeStatus;
        private set
        {
            _interchangeStatus = value;
            Notify();
            Notify(nameof(HasInterchangeStatus));
        }
    }

    public bool HasInterchangeStatus => !string.IsNullOrEmpty(InterchangeStatus);

    public string ProposalText
    {
        get => _proposalText;
        set
        {
            value ??= string.Empty;
            if (string.Equals(value, _proposalText, StringComparison.Ordinal))
            {
                return;
            }

            if (!ProposalTextRules.TryMeasure(value, out var byteCount))
            {
                ProposalStatus = ProposalInputRejectedStatus;
                return;
            }

            _proposalText = value;
            _proposalByteCount = byteCount;
            ManualProposalSource = ProposalReviewSource.TextImport;
            Notify();
            Notify(nameof(ProposalByteCountText));
            Notify(nameof(CanReviewProposal));
        }
    }

    public string ProposalByteCountText => _proposalByteCount switch
    {
        < 1_024 => $"{_proposalByteCount:N0} bytes",
        < 1_048_576 => $"{_proposalByteCount / 1_024d:0.#} KB",
        _ => $"{_proposalByteCount / 1_048_576d:0.#} MB",
    };

    public bool CanReviewProposal =>
        !_proposalActionInFlight && !string.IsNullOrWhiteSpace(ProposalText);
    public bool HasPendingInboundProposal => PendingProposals.Count > 0;
    public bool CanReplaceProposalText => !_proposalActionInFlight;
    public int PendingProposalCount => PendingProposals.Count;
    public string PendingProposalHeading => PendingProposalCount == 1
        ? "1 proposal needs review"
        : $"{PendingProposalCount.ToString(CultureInfo.InvariantCulture)} proposals need review";
    public string PendingCapacityText =>
        $"{PendingProposalCount.ToString(CultureInfo.InvariantCulture)} of {AgentInterchangeV1.MaximumPendingProposals}";
    public int PendingNewWorkCount => _pendingProposalCoordinator?.PendingProposals.Count(proposal =>
        proposal.ProposalKind == ProposalKind.NewWork) ?? 0;
    public int PendingChangeCount => _pendingProposalCoordinator?.PendingProposals.Count(proposal =>
        proposal.ProposalKind == ProposalKind.WorkspaceChange) ?? 0;

    public string ProposalStatus
    {
        get => _proposalStatus;
        private set
        {
            if (string.Equals(value, _proposalStatus, StringComparison.Ordinal))
            {
                return;
            }

            _proposalStatus = value;
            Notify();
            Notify(nameof(HasProposalStatus));
        }
    }

    public bool HasProposalStatus => !string.IsNullOrEmpty(ProposalStatus);
    public string PendingProposalReviewStatus => _pendingProposalReviewStatus;
    public bool HasPendingProposalReviewStatus => !string.IsNullOrEmpty(PendingProposalReviewStatus);

    internal ProposalReviewSource ManualProposalSource { get; private set; } =
        ProposalReviewSource.TextImport;

    internal const string ProposalInputRejectedStatus =
        "That change was rejected because proposal text must be valid UTF-8 and no larger than 4 MiB.";

    internal async Task CopyReusableInstructionsAsync(IPlainTextClipboard clipboard)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        var result = AgentInterchangeDocumentComposer.ComposeReusableInstructionsAndSchemas();
        if (!result.Succeeded)
        {
            InterchangeStatus = PlanningSnapshotCopyViewModel.TooLargeStatus;
            return;
        }

        try
        {
            await clipboard.WriteTextAsync(result.Payload!.Value);
            InterchangeStatus = "Reusable agent instructions and v1 schemas copied.";
        }
        catch (Exception)
        {
            InterchangeStatus = "Reusable agent instructions and schemas could not be copied. Try again.";
        }
    }

    internal void ReportInterchangeStatus(string status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        InterchangeStatus = status;
    }

    internal void ReportProposalTextAttempt(bool accepted, int byteCount)
    {
        if (!accepted)
        {
            ProposalStatus = ProposalInputRejectedStatus;
            return;
        }

        _proposalByteCount = byteCount;
        Notify(nameof(ProposalByteCountText));
        ProposalStatus = "Proposal text changed. Review is required before anything can be applied.";
    }

    internal void ReportProposalStatus(string status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        ProposalStatus = status;
    }

    internal void ClearPendingProposalReviewStatus(string receipt)
    {
        if (!string.Equals(_pendingProposalReviewStatusReceipt, receipt, StringComparison.Ordinal))
        {
            return;
        }

        _pendingProposalReviewStatusReceipt = null;
        _pendingProposalReviewStatus = string.Empty;
        Notify(nameof(PendingProposalReviewStatus));
        Notify(nameof(HasPendingProposalReviewStatus));
    }

    internal void ReportPendingProposalReviewStatus(string receipt, string status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        _pendingProposalReviewStatusReceipt = receipt;
        _pendingProposalReviewStatus = status;
        Notify(nameof(PendingProposalReviewStatus));
        Notify(nameof(HasPendingProposalReviewStatus));
    }

    internal void MarkManualProposalSource(ProposalReviewSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ManualProposalSource = source;
    }

    internal void SetProposalActionInFlight(bool inFlight)
    {
        if (_proposalActionInFlight == inFlight)
        {
            return;
        }

        _proposalActionInFlight = inFlight;
        Notify(nameof(CanReviewProposal));
        Notify(nameof(CanReplaceProposalText));
    }

    internal void RefreshPendingProposals()
    {
        var pending = _pendingProposalCoordinator?.PendingProposals ?? [];
        var pendingReceipts = pending.Select(proposal => proposal.Receipt).ToHashSet(StringComparer.Ordinal);
        for (var index = _pendingProposals.Count - 1; index >= 0; index--)
        {
            if (!pendingReceipts.Contains(_pendingProposals[index].Receipt))
            {
                _pendingProposals.RemoveAt(index);
            }
        }

        for (var index = 0; index < pending.Count; index++)
        {
            var proposal = pending[index];
            var existingIndex = _pendingProposals
                .Select((row, rowIndex) => (row, rowIndex))
                .FirstOrDefault(item => string.Equals(item.row.Receipt, proposal.Receipt, StringComparison.Ordinal))
                .rowIndex;
            if (existingIndex >= 0
                && existingIndex < _pendingProposals.Count
                && string.Equals(_pendingProposals[existingIndex].Receipt, proposal.Receipt, StringComparison.Ordinal))
            {
                if (existingIndex != index)
                {
                    _pendingProposals.Move(existingIndex, index);
                }
            }
            else
            {
                _pendingProposals.Insert(index, new PendingProposalRowViewModel(proposal, _timeProvider));
            }
        }

        for (var index = 0; index < _pendingProposals.Count; index++)
        {
            _pendingProposals[index].IsLast = index == _pendingProposals.Count - 1;
        }

        if (_pendingProposalReviewStatusReceipt is { } statusReceipt
            && !pendingReceipts.Contains(statusReceipt))
        {
            _pendingProposalReviewStatusReceipt = null;
            _pendingProposalReviewStatus = string.Empty;
            Notify(nameof(PendingProposalReviewStatus));
            Notify(nameof(HasPendingProposalReviewStatus));
        }

        Notify(nameof(HasPendingInboundProposal));
        Notify(nameof(PendingProposalCount));
        Notify(nameof(PendingProposalHeading));
        Notify(nameof(PendingCapacityText));
        Notify(nameof(PendingNewWorkCount));
        Notify(nameof(PendingChangeCount));
    }

    internal bool DiscardInboundProposal(string receipt)
    {
        if (_pendingProposalCoordinator is null
            || _pendingProposalCoordinator.FindPendingProposal(receipt) is null)
        {
            return false;
        }

        if (!_pendingProposalCoordinator.Discard(receipt))
        {
            ProposalStatus = "The inbound proposal could not be discarded. It remains pending for review.";
            return false;
        }

        ProposalStatus = "Inbound proposal discarded. Nothing was applied.";
        return true;
    }

    internal void ClearAppliedProposal()
    {
        _proposalText = string.Empty;
        _proposalByteCount = 0;
        ManualProposalSource = ProposalReviewSource.TextImport;
        Notify(nameof(ProposalText));
        Notify(nameof(ProposalByteCountText));
        Notify(nameof(CanReviewProposal));
    }

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
