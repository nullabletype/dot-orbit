using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using DotOrbit.Core.Workspaces;

namespace DotOrbit.Desktop.ViewModels;

public sealed class RecoveryViewModel : INotifyPropertyChanged
{
    private readonly IRecoveryPathPicker _pathPicker;
    private readonly Action<IWorkspaceSession> _sessionReplaced;
    private bool _isRestoreConfirmationVisible;
    private IWorkspaceRecovery _recovery;
    private string _recoveryDirectoryPath = string.Empty;
    private string _recoveryPointPath = string.Empty;
    private string _statusMessage = string.Empty;

    internal RecoveryViewModel(
        IWorkspaceRecovery recovery,
        IRecoveryPathPicker pathPicker,
        Action<IWorkspaceSession> sessionReplaced)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(pathPicker);
        ArgumentNullException.ThrowIfNull(sessionReplaced);
        _recovery = recovery;
        _pathPicker = pathPicker;
        _sessionReplaced = sessionReplaced;
        CreateRecoveryPointCommand = new RelayCommand(CreateRecoveryPoint);
        RequestRestoreCommand = new RelayCommand(RequestRestore);
        CancelRestoreCommand = new RelayCommand(CancelRestore);
        ConfirmRestoreCommand = new RelayCommand(ConfirmRestore);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? RestoreCancelled;

    public string RecoveryDirectoryPath
    {
        get => _recoveryDirectoryPath;
        private set
        {
            if (SetField(ref _recoveryDirectoryPath, value))
            {
                OnPropertyChanged(nameof(HasRecoveryDirectory));
            }
        }
    }

    public bool HasRecoveryDirectory => !string.IsNullOrEmpty(RecoveryDirectoryPath);

    public string RecoveryPointPath
    {
        get => _recoveryPointPath;
        private set
        {
            if (SetField(ref _recoveryPointPath, value))
            {
                OnPropertyChanged(nameof(HasRecoveryPoint));
                OnPropertyChanged(nameof(RecoveryPointName));
            }
        }
    }

    public string RecoveryPointName => HasRecoveryPoint
        ? Path.GetFileName(RecoveryPointPath)
        : string.Empty;

    public bool HasRecoveryPoint => !string.IsNullOrEmpty(RecoveryPointPath);

    public bool IsRestoreConfirmationVisible
    {
        get => _isRestoreConfirmationVisible;
        private set => SetField(ref _isRestoreConfirmationVisible, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetField(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(HasStatusMessage));
            }
        }
    }

    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    public ICommand CreateRecoveryPointCommand { get; }

    public ICommand RequestRestoreCommand { get; }

    public ICommand CancelRestoreCommand { get; }

    public ICommand ConfirmRestoreCommand { get; }

    public async Task SelectRecoveryDirectoryAsync()
    {
        var selectedPath = await _pathPicker.SelectRecoveryDirectoryAsync();
        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            RecoveryDirectoryPath = selectedPath;
            StatusMessage = string.Empty;
        }
    }

    public async Task SelectRecoveryPointAsync()
    {
        var selectedPath = await _pathPicker.SelectRecoveryPointAsync();
        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            RecoveryPointPath = selectedPath;
            IsRestoreConfirmationVisible = false;
            StatusMessage = string.Empty;
        }
    }

    private void CreateRecoveryPoint()
    {
        if (!HasRecoveryDirectory)
        {
            StatusMessage = "Choose a recovery directory first.";
            return;
        }

        var result = _recovery.CreateRecoveryPoint(RecoveryDirectoryPath);
        StatusMessage = result.Status == RecoveryPointCreationStatus.Created
            ? "Encrypted recovery point created and validated."
            : "The recovery point could not be created. Check directory access and try again.";
    }

    private void RequestRestore()
    {
        if (!HasRecoveryDirectory)
        {
            StatusMessage = "Choose a recovery directory for the current workspace recovery point.";
            return;
        }

        if (!HasRecoveryPoint)
        {
            StatusMessage = "Choose an encrypted recovery point to restore.";
            return;
        }

        StatusMessage = string.Empty;
        IsRestoreConfirmationVisible = true;
    }

    private void CancelRestore()
    {
        IsRestoreConfirmationVisible = false;
        StatusMessage = "Restore cancelled. The current workspace was not changed.";
        RestoreCancelled?.Invoke(this, EventArgs.Empty);
    }

    private void ConfirmRestore()
    {
        IsRestoreConfirmationVisible = false;
        var result = _recovery.Restore(RecoveryPointPath, RecoveryDirectoryPath);
        if (result.Session is not null)
        {
            _recovery = result.Session.Recovery;
            _sessionReplaced(result.Session);
        }

        StatusMessage = result.Status switch
        {
            WorkspaceRestoreStatus.Restored =>
                "Recovery point restored and the workspace reopened.",
            WorkspaceRestoreStatus.InvalidRecoveryPoint =>
                "The recovery point could not be validated with this workspace passphrase.",
            WorkspaceRestoreStatus.UnsupportedSchema =>
                "This recovery point was created by a newer version of dot-orbit.",
            WorkspaceRestoreStatus.PreRestoreRecoveryFailed =>
                "Restore stopped because the current workspace recovery point could not be created.",
            _ => "The workspace could not be restored. Known-valid files were kept.",
        };

    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
