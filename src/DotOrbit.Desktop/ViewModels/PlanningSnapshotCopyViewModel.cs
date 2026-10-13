using System.ComponentModel;
using System.Runtime.CompilerServices;
using DotOrbit.Core.Interchange;
using DotOrbit.Desktop.Clipboard;
using DotOrbit.Interchange.Json;

namespace DotOrbit.Desktop.ViewModels;

internal sealed class PlanningSnapshotCopyViewModel : INotifyPropertyChanged
{
    internal const string TooLargeStatus =
        "This workspace is too large for v1 interchange. Nothing was copied.";

    private readonly IPlainTextClipboard _clipboard;
    private readonly ReadOnlyMemory<byte>? _payload;
    private string _statusMessage = string.Empty;
    private bool _copyCompleted;

    public PlanningSnapshotCopyViewModel(
        PlanningSnapshotContent snapshot,
        bool includeAgentInstructionsAndSchema,
        IPlainTextClipboard clipboard)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(clipboard);
        _clipboard = clipboard;
        IncludeAgentInstructionsAndSchema = includeAgentInstructionsAndSchema;
        CategoryCount = snapshot.Categories.Count;
        ParticipantCount = snapshot.Participants.Count;
        ProjectCount = snapshot.Projects.Count;
        TaskCount = snapshot.Tasks.Count;
        CompletedTaskCount = snapshot.Tasks.Count(task => task.IsComplete);
        ExcludedArchivedChildTaskCount = snapshot.Projects.Sum(project => project.ExcludedArchivedTaskCount);

        var result = PlanningInterchangeCopyComposer.ComposeSnapshot(
            snapshot,
            includeAgentInstructionsAndSchema);
        if (result.Succeeded)
        {
            _payload = result.Payload;
        }
        else
        {
            StatusMessage = TooLargeStatus;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? CopyFinished;
    public event EventHandler? CopyCancelled;

    public int CategoryCount { get; }
    public int ParticipantCount { get; }
    public int ProjectCount { get; }
    public int TaskCount { get; }
    public int CompletedTaskCount { get; }
    public int ExcludedArchivedChildTaskCount { get; }
    public bool IncludeAgentInstructionsAndSchema { get; }
    public string CategoryScopeText => $"Reusable Categories: {CategoryCount}";
    public string ParticipantScopeText => $"Reusable Participant labels: {ParticipantCount}";
    public string ProjectScopeText => $"Active Projects: {ProjectCount}";
    public string TaskScopeText => $"Included Tasks: {TaskCount}, including {CompletedTaskCount} completed";
    public string ExcludedChildScopeText =>
        $"Individually archived child Tasks excluded from included Projects: {ExcludedArchivedChildTaskCount}";
    public string OutputScopeText => IncludeAgentInstructionsAndSchema
        ? "One text document: provider-neutral instructions, exact v1 schemas, operation vocabulary, output instructions, and this live snapshot with its relationships."
        : "Snapshot-only mode: one canonical v1 Planning snapshot JSON document with its relationships.";
    public bool CanConfirmCopy => _payload is not null && !_copyCompleted;
    public bool HasCompletedCopy => _copyCompleted;

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (_statusMessage == value)
            {
                return;
            }

            _statusMessage = value;
            Notify();
            Notify(nameof(HasStatusMessage));
        }
    }

    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    public async Task ConfirmCopyAsync()
    {
        if (_payload is not { } payload || _copyCompleted)
        {
            if (!_copyCompleted)
            {
                StatusMessage = TooLargeStatus;
            }
            return;
        }

        try
        {
            await _clipboard.WriteTextAsync(payload);
            _copyCompleted = true;
            StatusMessage = "Planning snapshot copied as sensitive unencrypted plaintext.";
            Notify(nameof(CanConfirmCopy));
            CopyFinished?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            StatusMessage = "The Planning snapshot could not be copied. Try again.";
        }
    }

    public void CancelCopy()
    {
        if (_copyCompleted || _payload is null)
        {
            return;
        }

        StatusMessage = "Planning snapshot copy cancelled. Clipboard contents were not changed.";
        CopyCancelled?.Invoke(this, EventArgs.Empty);
    }

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
