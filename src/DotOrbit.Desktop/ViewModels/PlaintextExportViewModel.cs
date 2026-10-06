using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using DotOrbit.Core.Workspaces;
using DotOrbit.Export.Json;

namespace DotOrbit.Desktop.ViewModels;

internal sealed class PlaintextExportViewModel : INotifyPropertyChanged
{
    private readonly WorkspaceWorkSnapshot _snapshot;
    private readonly IPlaintextExportDestinationPicker _destinationPicker;
    private readonly IPlaintextWorkspaceExporter _exporter;
    private PlaintextExportDestination? _destination;
    private bool _isConfirmationVisible;
    private string _statusMessage = string.Empty;

    public PlaintextExportViewModel(
        IWorkspaceWork work,
        IPlaintextExportDestinationPicker destinationPicker,
        IPlaintextWorkspaceExporter exporter)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(destinationPicker);
        ArgumentNullException.ThrowIfNull(exporter);
        _snapshot = work.Read();
        _destinationPicker = destinationPicker;
        _exporter = exporter;
        RequestExportCommand = new RelayCommand(RequestExport);
        CancelExportCommand = new RelayCommand(CancelExport);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? ConfirmationRequested;
    public event EventHandler? ConfirmationCancelled;
    public event EventHandler? ExportFinished;

    public int CategoryCount => _snapshot.Categories.Count;
    public int ParticipantCount => _snapshot.Participants.Count;
    public int ActiveProjectCount => _snapshot.Projects.Count(item => !item.IsArchived);
    public int ArchivedProjectCount => _snapshot.Projects.Count(item => item.IsArchived);
    public int ActiveTaskCount => _snapshot.Tasks.Count(item => !item.IsArchived);
    public int ArchivedTaskCount => _snapshot.Tasks.Count(item => item.IsArchived);
    public string CategoryScopeText => $"Categories: {CategoryCount}";
    public string ParticipantScopeText => $"Participant labels: {ParticipantCount}";
    public string ProjectScopeText => $"Projects: {ActiveProjectCount} active, {ArchivedProjectCount} archived";
    public string TaskScopeText => $"Tasks: {ActiveTaskCount} active, {ArchivedTaskCount} archived";

    public string DestinationName => _destination?.Name ?? string.Empty;
    public string DestinationAccessibleName => _destination is null
        ? "No plaintext export destination selected"
        : $"Selected plaintext export destination: {_destination.Name}";
    public bool HasDestination => _destination is not null;

    public bool IsConfirmationVisible
    {
        get => _isConfirmationVisible;
        private set => SetField(ref _isConfirmationVisible, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetField(ref _statusMessage, value)) OnPropertyChanged(nameof(HasStatusMessage));
        }
    }

    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);
    public ICommand RequestExportCommand { get; }
    public ICommand CancelExportCommand { get; }

    public async Task SelectDestinationAsync()
    {
        PlaintextExportDestination? selected;
        try
        {
            selected = await _destinationPicker.SelectAsync();
        }
        catch (Exception)
        {
            StatusMessage = "The export destination could not be selected. Try again.";
            return;
        }
        if (selected is null) return;

        _destination = selected;
        IsConfirmationVisible = false;
        StatusMessage = string.Empty;
        OnPropertyChanged(nameof(DestinationName));
        OnPropertyChanged(nameof(DestinationAccessibleName));
        OnPropertyChanged(nameof(HasDestination));
    }

    public async Task ConfirmExportAsync()
    {
        if (!IsConfirmationVisible || _destination is null)
        {
            StatusMessage = "Review the unencrypted export before saving it.";
            return;
        }

        IsConfirmationVisible = false;
        try
        {
            await _exporter.ExportAsync(_snapshot, _destination.Path);
            StatusMessage = "Unencrypted workspace JSON exported.";
        }
        catch (Exception)
        {
            StatusMessage = "The unencrypted export could not be saved. Choose a writable destination and try again.";
        }
        ExportFinished?.Invoke(this, EventArgs.Empty);
    }

    private void RequestExport()
    {
        if (_destination is null)
        {
            StatusMessage = "Choose an export destination first.";
            return;
        }

        StatusMessage = string.Empty;
        IsConfirmationVisible = true;
        ConfirmationRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CancelExport()
    {
        IsConfirmationVisible = false;
        StatusMessage = "Export cancelled. No plaintext file was written.";
        ConfirmationCancelled?.Invoke(this, EventArgs.Empty);
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
