using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using DotOrbit.Core.Workspaces;

namespace DotOrbit.Desktop.ViewModels;

public sealed class WorkspaceAccessViewModel : INotifyPropertyChanged
{
    private readonly Action<IWorkspaceSession> _workspaceOpened;
    private readonly string _workspacePath;
    private readonly IWorkspaceStore _workspaceStore;
    private string _confirmation = string.Empty;
    private string _firstCategoryName = string.Empty;
    private bool _isPassphraseVisible;
    private string _passphrase = string.Empty;
    private string _validationMessage = string.Empty;

    public WorkspaceAccessViewModel(
        IWorkspaceStore workspaceStore,
        string workspacePath,
        Action<IWorkspaceSession> workspaceOpened)
    {
        ArgumentNullException.ThrowIfNull(workspaceStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentNullException.ThrowIfNull(workspaceOpened);

        _workspaceStore = workspaceStore;
        _workspacePath = workspacePath;
        _workspaceOpened = workspaceOpened;
        IsCreateMode = !_workspaceStore.Exists(_workspacePath);
        SubmitCommand = new RelayCommand(Submit);
        TogglePassphraseVisibilityCommand = new RelayCommand(TogglePassphraseVisibility);
    }

    public event EventHandler? PassphraseFocusRequested;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsCreateMode { get; }

    public bool IsUnlockMode => !IsCreateMode;

    public string Heading => IsCreateMode ? "Create your workspace" : "Unlock your workspace";

    public string Intro => IsCreateMode
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

    public ICommand SubmitCommand { get; }

    public ICommand TogglePassphraseVisibilityCommand { get; }

    private void Submit()
    {
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

        ValidationMessage = result.Status == WorkspaceOpenStatus.UnsupportedSchema
            ? "This workspace was created by a newer version of dot-orbit. Update the application to open it."
            : "The workspace could not be unlocked. Check the passphrase and try again.";
        Passphrase = string.Empty;
        PassphraseFocusRequested?.Invoke(this, EventArgs.Empty);
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
