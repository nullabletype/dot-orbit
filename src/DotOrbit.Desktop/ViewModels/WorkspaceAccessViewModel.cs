using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using DotOrbit.Core.Workspaces;
using DotOrbit.Storage.Sqlite;

namespace DotOrbit.Desktop.ViewModels;

public sealed class WorkspaceAccessViewModel : INotifyPropertyChanged
{
    private const string AdoptionConflictMessage =
        "dot-orbit found both workspace.db and workspace.orb, or conflicting companion files, in its local application-data folder. It left them unchanged. Close every dot-orbit instance and keep every file. After identifying the complete workspace file set to preserve, move the other complete set to a separate backup folder without deleting or merging files, then reopen dot-orbit.";

    private const string AdoptionFailureMessage =
        "dot-orbit could not safely finish adopting workspace.db as workspace.orb in its local application-data folder. It did not overwrite a workspace. Close dot-orbit, keep every file, check folder access, and try again.";

    private const string UnavailableWorkspaceMessage =
        "The selected workspace is missing, unavailable, or no longer a regular file. dot-orbit did not create or change anything there. Retry, choose another workspace, or return to the default location.";

    private readonly Action<IWorkspaceSession> _workspaceOpened;
    private readonly IWorkspacePathAvailability _workspacePathAvailability;
    private readonly IWorkspacePathPreferenceStore _workspacePathPreferenceStore;
    private readonly IWorkspaceStore _workspaceStore;
    private readonly RelayCommand _restoreMigrationRecoveryCommand;
    private readonly RelayCommand _submitCommand;
    private readonly RelayCommand _togglePassphraseVisibilityCommand;
    private string _blockingMessage = string.Empty;
    private string _confirmation = string.Empty;
    private string _firstCategoryName = string.Empty;
    private bool _isPassphraseVisible;
    private bool _isBlocked;
    private bool _isCompleted;
    private bool _isCreateMode;
    private bool _isWorkspaceUnavailable;
    private WorkspacePassphrase? _migrationPassphrase;
    private string? _migrationRecoveryPointPath;
    private string _passphrase = string.Empty;
    private WorkspacePathSelection _selection;
    private string _validationMessage = string.Empty;

    public WorkspaceAccessViewModel(
        IWorkspaceStore workspaceStore,
        string workspacePath,
        Action<IWorkspaceSession> workspaceOpened)
        : this(
            workspaceStore,
            WorkspacePathSelection.Default(
                DefaultWorkspaceResolution.Ready(workspacePath),
                workspacePath),
            new TransientWorkspacePathPreferenceStore(),
            workspaceOpened,
            new StoreWorkspacePathAvailability(workspaceStore))
    {
    }

    public WorkspaceAccessViewModel(
        IWorkspaceStore workspaceStore,
        DefaultWorkspaceResolution workspaceResolution,
        Action<IWorkspaceSession> workspaceOpened)
        : this(
            workspaceStore,
            WorkspacePathSelection.Default(
                workspaceResolution,
                workspaceResolution.WorkspacePath),
            new TransientWorkspacePathPreferenceStore(),
            workspaceOpened,
            new StoreWorkspacePathAvailability(workspaceStore))
    {
    }

    internal WorkspaceAccessViewModel(
        IWorkspaceStore workspaceStore,
        WorkspacePathSelection selection,
        IWorkspacePathPreferenceStore workspacePathPreferenceStore,
        Action<IWorkspaceSession> workspaceOpened,
        IWorkspacePathAvailability? workspacePathAvailability = null)
    {
        ArgumentNullException.ThrowIfNull(workspaceStore);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(workspacePathPreferenceStore);
        ArgumentNullException.ThrowIfNull(workspaceOpened);

        _workspaceStore = workspaceStore;
        _workspacePathAvailability = workspacePathAvailability
                                     ?? new StoreWorkspacePathAvailability(workspaceStore);
        _selection = selection;
        _workspacePathPreferenceStore = workspacePathPreferenceStore;
        _workspaceOpened = workspaceOpened;
        _submitCommand = new RelayCommand(Submit, () => IsWorkspaceAccessAvailable);
        _restoreMigrationRecoveryCommand = new RelayCommand(
            RestoreMigrationRecovery,
            () => IsWorkspaceAccessAvailable);
        _togglePassphraseVisibilityCommand = new RelayCommand(
            TogglePassphraseVisibility,
            () => IsWorkspaceAccessAvailable);
        ApplySelection(selection, clearSecrets: false);
    }

    public event EventHandler? PassphraseFocusRequested;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsCreateMode => _isCreateMode;

    public bool IsUnlockMode => IsWorkspaceAccessAvailable && !IsCreateMode;

    public bool IsBlocked => _isBlocked || IsWorkspaceUnavailable;

    public bool IsWorkspaceUnavailable => _isWorkspaceUnavailable;

    public bool IsWorkspaceAccessAvailable => !IsBlocked && !_isCompleted;

    public bool CanRetryWorkspace => IsWorkspaceUnavailable
                                     && _selection.Intent == WorkspacePathIntent.Existing;

    public bool CanReturnToDefault => IsWorkspaceUnavailable
                                      && _selection.Intent == WorkspacePathIntent.Existing;

    public string BlockingMessage => _blockingMessage;

    public string WorkspaceResolutionAnnouncementName => IsWorkspaceUnavailable
        ? "Workspace location unavailable"
        : "Workspace startup blocked";

    public string SelectedWorkspacePath => _selection.StoragePath;

    public string SelectedWorkspaceAccessibleName =>
        $"Selected workspace location: {SelectedWorkspacePath}";

    public string WorkspaceLocationStatus => _selection.Intent switch
    {
        WorkspacePathIntent.New => "New workspace location",
        WorkspacePathIntent.Existing when _selection.WasRemembered => "Remembered workspace location",
        WorkspacePathIntent.Existing => "Existing workspace location",
        WorkspacePathIntent.Development => "Development workspace location",
        _ => "Default workspace location",
    };

    public string Heading => IsBlocked
        ? "Workspace needs attention"
        : IsCreateMode
            ? "Create your workspace"
            : "Unlock your workspace";

    public string Intro => IsBlocked
        ? IsWorkspaceUnavailable
            ? "Choose how dot-orbit should reach an encrypted local workspace."
            : "Startup stopped before dot-orbit created or opened storage."
        : IsCreateMode
            ? "Create one encrypted local workspace and name the first Category that will organise your work."
            : "Enter your passphrase to open the encrypted local workspace.";

    public string SubmitActionName => IsCreateMode ? "Create workspace" : "Unlock workspace";

    public string FirstCategoryName
    {
        get => _firstCategoryName;
        set => SetField(ref _firstCategoryName, value);
    }

    public string Passphrase
    {
        get => _passphrase;
        set => SetField(ref _passphrase, value);
    }

    public string Confirmation
    {
        get => _confirmation;
        set => SetField(ref _confirmation, value);
    }

    public bool IsPassphraseVisible
    {
        get => _isPassphraseVisible;
        private set
        {
            if (SetField(ref _isPassphraseVisible, value))
            {
                OnPropertyChanged(nameof(PasswordCharacter));
                OnPropertyChanged(nameof(PassphraseVisibilityActionName));
            }
        }
    }

    public char PasswordCharacter => IsPassphraseVisible ? '\0' : '\u25cf';

    public string PassphraseVisibilityActionName =>
        IsPassphraseVisible ? "Hide passphrase" : "Show passphrase";

    public string ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (SetField(ref _validationMessage, value))
            {
                OnPropertyChanged(nameof(HasValidationMessage));
            }
        }
    }

    public bool HasValidationMessage => !string.IsNullOrEmpty(ValidationMessage);

    public bool CanRestoreMigrationRecovery =>
        _migrationPassphrase is not null && _migrationRecoveryPointPath is not null;

    public ICommand RestoreMigrationRecoveryCommand => _restoreMigrationRecoveryCommand;

    public ICommand SubmitCommand => _submitCommand;

    public ICommand TogglePassphraseVisibilityCommand => _togglePassphraseVisibilityCommand;

    internal void SelectExistingWorkspace(string path) =>
        ApplySelection(WorkspacePathSelection.Existing(path), clearSecrets: true);

    internal void SelectNewWorkspace(string path) =>
        ApplySelection(WorkspacePathSelection.New(path), clearSecrets: true);

    internal void SelectDefaultWorkspace(WorkspacePathSelection selection) =>
        ApplySelection(selection, clearSecrets: true);

    internal void RetrySelectedWorkspace() => ApplySelection(_selection, clearSecrets: true);

    internal void ReportWorkspaceLocationError() =>
        ValidationMessage = "A local workspace file could not be selected. Choose another location and try again.";

    private void Submit()
    {
        ClearMigrationRecovery();
        ValidationMessage = string.Empty;
        if (IsCreateMode)
        {
            CreateWorkspace();
        }
        else
        {
            OpenWorkspace();
        }
    }

    private void CreateWorkspace()
    {
        var categoryResult = CategoryName.Create(FirstCategoryName);
        if (!categoryResult.IsValid || categoryResult.CategoryName is null)
        {
            ValidationMessage = "Name your first Category.";
            return;
        }

        var passphraseResult = WorkspacePassphrase.Create(Passphrase, Confirmation);
        if (!passphraseResult.IsValid || passphraseResult.Passphrase is null)
        {
            ValidationMessage = passphraseResult.Error switch
            {
                PassphraseValidationError.Required => "Enter a passphrase.",
                PassphraseValidationError.TooShort =>
                    $"Use at least {WorkspacePassphrase.MinimumCharacterCount} characters.",
                PassphraseValidationError.ConfirmationDoesNotMatch =>
                    "Passphrase confirmation does not match.",
                _ => "The workspace could not be created.",
            };
            PassphraseFocusRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        var result = _workspaceStore.Create(
            _selection.StoragePath,
            passphraseResult.Passphrase,
            categoryResult.CategoryName);
        if (result.Status == WorkspaceCreationStatus.Created && result.Session is not null)
        {
            Complete(result.Session);
            return;
        }

        ValidationMessage = result.Status == WorkspaceCreationStatus.AlreadyExists
            ? "A file already exists at that location. Choose it as an existing workspace or choose another new location."
            : "The encrypted workspace could not be created.";
        PassphraseFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OpenWorkspace()
    {
        var passphrase = WorkspacePassphrase.ForUnlock(Passphrase);
        if (passphrase is null)
        {
            ValidationMessage = "Enter your passphrase.";
            PassphraseFocusRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        var result = _workspaceStore.Open(_selection.StoragePath, passphrase);
        if (result.Status == WorkspaceOpenStatus.Opened && result.Session is not null)
        {
            Complete(result.Session);
            return;
        }

        if (result.Status == WorkspaceOpenStatus.MigrationFailed
            && result.RecoveryPointPath is not null)
        {
            _migrationPassphrase = passphrase;
            _migrationRecoveryPointPath = result.RecoveryPointPath;
            OnPropertyChanged(nameof(CanRestoreMigrationRecovery));
        }

        if (result.Status is WorkspaceOpenStatus.AdoptionConflict or WorkspaceOpenStatus.AdoptionFailed)
        {
            BlockWorkspaceAccess(result.Status);
            return;
        }

        if (_selection.Intent == WorkspacePathIntent.Existing
            && (result.Status == WorkspaceOpenStatus.Failed
                || !SelectedExistingWorkspaceStillExists()))
        {
            MarkSelectedWorkspaceUnavailable();
            return;
        }

        ValidationMessage = result.Status switch
        {
            WorkspaceOpenStatus.UnsupportedSchema =>
                "This workspace was created by a newer version of dot-orbit. Update the application to open it.",
            WorkspaceOpenStatus.MigrationFailed when result.RecoveryPointPath is not null =>
                "The workspace upgrade could not be completed. The original remains usable and a validated recovery point is available.",
            WorkspaceOpenStatus.MigrationFailed =>
                "The workspace upgrade could not start because a validated recovery point could not be created. The original remains unchanged.",
            _ => "The workspace could not be unlocked. Check the passphrase and try again.",
        };
        Passphrase = string.Empty;
        PassphraseFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool SelectedExistingWorkspaceStillExists()
    {
        try
        {
            return _workspacePathAvailability.IsAvailableRegularFile(_selection.StoragePath);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            return false;
        }
    }

    private void BlockWorkspaceAccess(WorkspaceOpenStatus status)
    {
        Passphrase = string.Empty;
        _isBlocked = true;
        _blockingMessage = status == WorkspaceOpenStatus.AdoptionConflict
            ? AdoptionConflictMessage
            : AdoptionFailureMessage;
        OnPropertyChanged(nameof(IsBlocked));
        OnPropertyChanged(nameof(IsWorkspaceAccessAvailable));
        OnPropertyChanged(nameof(IsUnlockMode));
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(Intro));
        OnPropertyChanged(nameof(BlockingMessage));
        _submitCommand.RaiseCanExecuteChanged();
        _restoreMigrationRecoveryCommand.RaiseCanExecuteChanged();
        _togglePassphraseVisibilityCommand.RaiseCanExecuteChanged();
    }

    private void RestoreMigrationRecovery()
    {
        if (_migrationPassphrase is null || _migrationRecoveryPointPath is null)
        {
            return;
        }

        var result = _workspaceStore.RestoreMigrationRecovery(
            _selection.StoragePath,
            _migrationPassphrase,
            _migrationRecoveryPointPath);
        ClearMigrationRecovery();
        ValidationMessage = result.Status switch
        {
            MigrationRecoveryRestoreStatus.Restored =>
                "The pre-upgrade workspace was restored. Enter your passphrase to retry the upgrade.",
            MigrationRecoveryRestoreStatus.InvalidRecoveryPoint =>
                "The migration recovery point is no longer valid. The workspace was not replaced.",
            MigrationRecoveryRestoreStatus.PreRestoreRecoveryFailed =>
                "The workspace was not replaced because a pre-restore recovery point could not be created.",
            _ => "The migration recovery point could not be restored. The workspace may still be usable.",
        };
        PassphraseFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ClearMigrationRecovery()
    {
        _migrationPassphrase = null;
        _migrationRecoveryPointPath = null;
        OnPropertyChanged(nameof(CanRestoreMigrationRecovery));
    }

    private void Complete(IWorkspaceSession session)
    {
        if (_isCompleted)
        {
            session.Dispose();
            return;
        }

        _isCompleted = true;
        Passphrase = string.Empty;
        Confirmation = string.Empty;
        if (_selection.Intent != WorkspacePathIntent.Development)
        {
            try
            {
                _ = _workspacePathPreferenceStore.TrySaveWorkspacePath(_selection.RememberedPath);
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or ArgumentException
                                               or NotSupportedException)
            {
                // A device-preference failure must not invalidate an already opened workspace.
            }
        }

        _submitCommand.RaiseCanExecuteChanged();
        _restoreMigrationRecoveryCommand.RaiseCanExecuteChanged();
        _togglePassphraseVisibilityCommand.RaiseCanExecuteChanged();
        _workspaceOpened(session);
    }

    private void ApplySelection(WorkspacePathSelection selection, bool clearSecrets)
    {
        ArgumentNullException.ThrowIfNull(selection);
        _selection = selection;
        _isCompleted = false;
        _isBlocked = selection.ResolutionStatus != DefaultWorkspaceResolutionStatus.Ready;
        _isWorkspaceUnavailable = false;
        _blockingMessage = selection.ResolutionStatus switch
        {
            DefaultWorkspaceResolutionStatus.Conflict => AdoptionConflictMessage,
            DefaultWorkspaceResolutionStatus.Failed => AdoptionFailureMessage,
            _ => string.Empty,
        };

        if (!_isBlocked)
        {
            if (selection.Intent == WorkspacePathIntent.New)
            {
                _isCreateMode = true;
            }
            else
            {
                try
                {
                    var exists = _workspacePathAvailability.IsAvailableRegularFile(selection.StoragePath);
                    if (selection.Intent == WorkspacePathIntent.Existing && !exists)
                    {
                        _isWorkspaceUnavailable = true;
                        _blockingMessage = UnavailableWorkspaceMessage;
                        _isCreateMode = false;
                    }
                    else
                    {
                        _isCreateMode = !exists;
                    }
                }
                catch (Exception exception) when (exception is IOException
                                                   or UnauthorizedAccessException
                                                   or InvalidDataException
                                                   or ArgumentException
                                                   or NotSupportedException)
                {
                    _isWorkspaceUnavailable = true;
                    _blockingMessage = UnavailableWorkspaceMessage;
                    _isCreateMode = false;
                }
            }
        }
        else
        {
            _isCreateMode = false;
        }

        if (clearSecrets)
        {
            Passphrase = string.Empty;
            Confirmation = string.Empty;
            FirstCategoryName = string.Empty;
            ValidationMessage = string.Empty;
            ClearMigrationRecovery();
        }

        NotifyWorkspaceSelectionChanged();
    }

    private void MarkSelectedWorkspaceUnavailable()
    {
        _isBlocked = false;
        _isWorkspaceUnavailable = true;
        _isCreateMode = false;
        _blockingMessage = UnavailableWorkspaceMessage;
        Passphrase = string.Empty;
        Confirmation = string.Empty;
        ValidationMessage = string.Empty;
        ClearMigrationRecovery();
        NotifyWorkspaceSelectionChanged();
    }

    private void NotifyWorkspaceSelectionChanged()
    {
        foreach (var propertyName in new[]
                 {
                     nameof(IsCreateMode), nameof(IsUnlockMode), nameof(IsBlocked),
                     nameof(IsWorkspaceUnavailable), nameof(IsWorkspaceAccessAvailable),
                     nameof(CanRetryWorkspace), nameof(CanReturnToDefault),
                     nameof(BlockingMessage), nameof(WorkspaceResolutionAnnouncementName),
                     nameof(SelectedWorkspacePath), nameof(SelectedWorkspaceAccessibleName),
                     nameof(WorkspaceLocationStatus), nameof(Heading), nameof(Intro),
                     nameof(SubmitActionName),
                 })
        {
            OnPropertyChanged(propertyName);
        }

        _submitCommand.RaiseCanExecuteChanged();
        _restoreMigrationRecoveryCommand.RaiseCanExecuteChanged();
        _togglePassphraseVisibilityCommand.RaiseCanExecuteChanged();
    }

    private void TogglePassphraseVisibility() => IsPassphraseVisible = !IsPassphraseVisible;

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
