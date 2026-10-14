using System.ComponentModel;
using System.Runtime.CompilerServices;
using DotOrbit.Desktop.Clipboard;
using DotOrbit.Interchange.Json;

namespace DotOrbit.Desktop.ViewModels;

public sealed class AiViewModel : INotifyPropertyChanged
{
    private readonly IInterchangePreferenceService _interchangePreferenceService;
    private bool _includeAgentInstructionsAndSchemaWithSnapshot;
    private string _interchangeStatus = string.Empty;
    private string _proposalText = string.Empty;
    private int _proposalByteCount;
    private string _proposalStatus = string.Empty;
    private bool _proposalActionInFlight;

    public AiViewModel(IInterchangePreferenceService? interchangePreferenceService = null)
    {
        _interchangePreferenceService = interchangePreferenceService
            ?? new TransientInterchangePreferenceService();
        _includeAgentInstructionsAndSchemaWithSnapshot =
            _interchangePreferenceService.IncludeAgentInstructionsAndSchemaWithSnapshot;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

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
            Notify();
            Notify(nameof(ProposalByteCountText));
            Notify(nameof(CanReviewProposal));
        }
    }

    public string ProposalByteCountText =>
        $"{_proposalByteCount:N0} of {ChangeProposalJsonParser.MaximumInputBytes:N0} UTF-8 bytes";

    public bool CanReviewProposal =>
        !_proposalActionInFlight && !string.IsNullOrWhiteSpace(ProposalText);

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

    internal void SetProposalActionInFlight(bool inFlight)
    {
        if (_proposalActionInFlight == inFlight)
        {
            return;
        }

        _proposalActionInFlight = inFlight;
        Notify(nameof(CanReviewProposal));
    }

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
