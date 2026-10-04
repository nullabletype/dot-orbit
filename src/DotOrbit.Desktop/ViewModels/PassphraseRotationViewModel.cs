using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using DotOrbit.Core.Workspaces;

namespace DotOrbit.Desktop.ViewModels;

public sealed class PassphraseRotationViewModel : INotifyPropertyChanged
{
    private readonly Action<IWorkspaceSession> _sessionReplaced;
    private string _confirmation = string.Empty;
    private string _currentPassphrase = string.Empty;
    private bool _isPassphraseVisible;
    private bool _isWorkspaceUnavailable;
    private string _newPassphrase = string.Empty;
    private IWorkspaceSession _session;
    private string _validationMessage = string.Empty;

    public PassphraseRotationViewModel(
        IWorkspaceSession session,
        Action<IWorkspaceSession> sessionReplaced)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sessionReplaced);
        _session = session;
        _sessionReplaced = sessionReplaced;
        SubmitCommand = new RelayCommand(Submit);
        CancelCommand = new RelayCommand(Cancel);
        TogglePassphraseVisibilityCommand = new RelayCommand(TogglePassphraseVisibility);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action<string>? FocusRequested;

    public event EventHandler? CancelRequested;

    public event EventHandler? RotationCompleted;

    public event EventHandler? WorkspaceUnavailableExitRequested;

    public string CurrentPassphrase
    {
        get => _currentPassphrase;
        set => SetField(ref _currentPassphrase, value);
    }

    public string NewPassphrase
    {
        get => _newPassphrase;
        set => SetField(ref _newPassphrase, value);
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
        IsPassphraseVisible ? "Hide passphrases" : "Show passphrases";

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

    public bool IsWorkspaceUnavailable
    {
        get => _isWorkspaceUnavailable;
        private set
        {
            if (SetField(ref _isWorkspaceUnavailable, value))
            {
                OnPropertyChanged(nameof(CanEdit));
                OnPropertyChanged(nameof(CancelActionName));
            }
        }
    }

    public bool CanEdit => !IsWorkspaceUnavailable;

    public string CancelActionName =>
        IsWorkspaceUnavailable ? "Return to unlock" : "Cancel";

    public ICommand SubmitCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand TogglePassphraseVisibilityCommand { get; }

    internal void ClearSecrets()
    {
        CurrentPassphrase = string.Empty;
        NewPassphrase = string.Empty;
        Confirmation = string.Empty;
    }

    private void Submit()
    {
        ValidationMessage = string.Empty;
        var currentPassphrase = WorkspacePassphrase.ForUnlock(CurrentPassphrase);
        if (currentPassphrase is null)
        {
            FailValidation("Enter your current passphrase.", "CurrentPassphraseTextBox");
            return;
        }

        var newPassphrase = WorkspacePassphrase.Create(NewPassphrase, Confirmation);
        if (!newPassphrase.IsValid || newPassphrase.Passphrase is null)
        {
            var message = newPassphrase.Error switch
            {
                PassphraseValidationError.Required => "Enter a new passphrase.",
                PassphraseValidationError.TooShort =>
                    $"Use at least {WorkspacePassphrase.MinimumCharacterCount} characters for the new passphrase.",
                PassphraseValidationError.ConfirmationDoesNotMatch =>
                    "New passphrase confirmation does not match.",
                _ => "The new passphrase is not valid.",
            };
            var focusTarget = newPassphrase.Error == PassphraseValidationError.ConfirmationDoesNotMatch
                ? "ConfirmationTextBox"
                : "NewPassphraseTextBox";
            FailValidation(message, focusTarget);
            return;
        }

        if (newPassphrase.Passphrase.Matches(CurrentPassphrase))
        {
            FailValidation(
                "Choose a new passphrase that is different from the current passphrase.",
                "NewPassphraseTextBox");
            return;
        }

        PassphraseRotationResult result;
        try
        {
            result = _session.RotatePassphrase(currentPassphrase, newPassphrase.Passphrase);
        }
        catch (ObjectDisposedException)
        {
            ClearSecrets();
            FailValidation(
                "The workspace session changed. Close this window and try again.",
                "CancelPassphraseButton");
            return;
        }
        if (result.Session is not null)
        {
            _session = result.Session;
            _sessionReplaced(result.Session);
        }

        ClearSecrets();
        switch (result.Status)
        {
            case PassphraseRotationStatus.Rotated:
                RotationCompleted?.Invoke(this, EventArgs.Empty);
                break;
            case PassphraseRotationStatus.InvalidCurrentPassphraseOrStore:
                FailValidation(
                    "The current passphrase or workspace could not be verified.",
                    "CurrentPassphraseTextBox");
                break;
            case PassphraseRotationStatus.InvalidNewPassphrase:
                FailValidation(
                    "Enter and confirm a new passphrase with at least 12 characters.",
                    "NewPassphraseTextBox");
                break;
            case PassphraseRotationStatus.NewPassphraseMatchesCurrent:
                FailValidation(
                    "Choose a new passphrase that is different from the current passphrase.",
                    "NewPassphraseTextBox");
                break;
            case PassphraseRotationStatus.RecoveryPointCreationFailed:
                FailValidation(
                    "The passphrase was not changed because a validated recovery point could not be created.",
                    "CurrentPassphraseTextBox");
                break;
            case PassphraseRotationStatus.WorkspaceUnavailable:
                IsWorkspaceUnavailable = true;
                FailValidation(
                    "The workspace could not be reopened after replacement. Return to unlock and try the new passphrase. A validated recovery point was kept.",
                    "CancelPassphraseButton");
                break;
            default:
                FailValidation(
                    "The passphrase was not changed. The existing workspace remains available.",
                    "CurrentPassphraseTextBox");
                break;
        }
    }

    private void Cancel()
    {
        ClearSecrets();
        if (IsWorkspaceUnavailable)
        {
            WorkspaceUnavailableExitRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            CancelRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void FailValidation(string message, string focusTarget)
    {
        ValidationMessage = message;
        FocusRequested?.Invoke(focusTarget);
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
