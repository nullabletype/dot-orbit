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

    private readonly Action<IWorkspaceSession> _workspaceOpened;
    private readonly string _workspacePath;
    private readonly IWorkspaceStore _workspaceStore;
    private readonly RelayCommand _restoreMigrationRecoveryCommand;
    private readonly RelayCommand _submitCommand;
    private readonly RelayCommand _togglePassphraseVisibilityCommand;
    private string _blockingMessage = string.Empty;
    private string _confirmation = string.Empty;
    private string _firstCategoryName = string.Empty;
    private bool _isPassphraseVisible;
    private bool _isBlocked;
    private WorkspacePassphrase? _migrationPassphrase;
    private string? _migrationRecoveryPointPath;
    private string _passphrase = string.Empty;
    private string _validationMessage = string.Empty;

    public WorkspaceAccessViewModel(
        IWorkspaceStore workspaceStore,
        string workspacePath,
        Action<IWorkspaceSession> workspaceOpened)
        : this(
            workspaceStore,
            DefaultWorkspaceResolution.Ready(workspacePath),
            workspaceOpened)
    {
    }

    public WorkspaceAccessViewModel(
        IWorkspaceStore workspaceStore,
        DefaultWorkspaceResolution workspaceResolution,
        Action<IWorkspaceSession> workspaceOpened)
    {
        ArgumentNullException.ThrowIfNull(workspaceStore);
        ArgumentNullException.ThrowIfNull(workspaceResolution);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceResolution.WorkspacePath);
        ArgumentNullException.ThrowIfNull(workspaceOpened);

        _workspaceStore = workspaceStore;
        _workspacePath = workspaceResolution.WorkspacePath;
        _workspaceOpened = workspaceOpened;
        _isBlocked = workspaceResolution.Status != DefaultWorkspaceResolutionStatus.Ready;
        _blockingMessage = workspaceResolution.Status switch
        {
            DefaultWorkspaceResolutionStatus.Conflict =>
                AdoptionConflictMessage,
            DefaultWorkspaceResolutionStatus.Failed =>
                AdoptionFailureMessage,
            _ => string.Empty,
        };
        IsCreateMode = !IsBlocked && !_workspaceStore.Exists(_workspacePath);
        _submitCommand = new RelayCommand(Submit, () => !IsBlocked);
        _restoreMigrationRecoveryCommand = new RelayCommand(
            RestoreMigrationRecovery,
            () => !IsBlocked);
        _togglePassphraseVisibilityCommand = new RelayCommand(
            TogglePassphraseVisibility,
            () => !IsBlocked);
    }

    public event EventHandler? PassphraseFocusRequested;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsCreateMode { get; }

    public bool IsUnlockMode => !IsBlocked && !IsCreateMode;

    public bool IsBlocked => _isBlocked;

    public bool IsWorkspaceAccessAvailable => !IsBlocked;

    public string BlockingMessage => _blockingMessage;

    public string Heading => IsBlocked
        ? "Workspace needs attention"
        : IsCreateMode
            ? "Create your workspace"
            : "Unlock your workspace";

    public string Intro => IsBlocked
        ? "Startup stopped before dot-orbit created or opened storage."
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
            _workspacePath,
            passphraseResult.Passphrase,
            categoryResult.CategoryName);
        if (result.Status == WorkspaceCreationStatus.Created && result.Session is not null)
        {
            Complete(result.Session);
            return;
        }

        ValidationMessage = result.Status == WorkspaceCreationStatus.AlreadyExists
            ? "A workspace already exists. Restart dot-orbit to unlock it."
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

        var result = _workspaceStore.Open(_workspacePath, passphrase);
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

        ValidationMessage = result.Status switch
        {
            WorkspaceOpenStatus.UnsupportedSchema =>
                "This workspace was created by a newer version of dot-orbit. Update the application to open it.",
            WorkspaceOpenStatus.MigrationFailed when result.RecoveryPointPath is not null =>
                $"The workspace upgrade could not be completed. The original remains usable and a recovery point is available at {result.RecoveryPointPath}.",
            WorkspaceOpenStatus.MigrationFailed =>
                "The workspace upgrade could not start because a validated recovery point could not be created. The original remains unchanged.",
            _ => "The workspace could not be unlocked. Check the passphrase and try again.",
        };
        Passphrase = string.Empty;
        PassphraseFocusRequested?.Invoke(this, EventArgs.Empty);
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
            _workspacePath,
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
        Passphrase = string.Empty;
        Confirmation = string.Empty;
        _workspaceOpened(session);
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
