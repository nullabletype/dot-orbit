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

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
