using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using DotOrbit.Core.Workspaces;

namespace DotOrbit.Desktop.ViewModels;

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly IWorkspaceWork _work;
    private readonly Action _participantsChanged;
    private readonly IApplicationThemeService _applicationThemeService;
    private readonly IAiFeaturePreferenceService _aiFeaturePreferenceService;
    private readonly Action<bool> _aiFeaturesChanged;
    private readonly Action<bool> _localMcpChanged;
    private string _announcement = string.Empty;
    private string _focusAutomationId = string.Empty;
    private string _securityConfirmation = string.Empty;
    private string _localMcpRuntimeStatus = "Stopped";

    public SettingsViewModel(
        IWorkspaceWork work,
        Action participantsChanged,
        IApplicationThemeService? applicationThemeService = null,
        IAiFeaturePreferenceService? aiFeaturePreferenceService = null,
        Action<bool>? aiFeaturesChanged = null,
        Action<bool>? localMcpChanged = null)
    {
        _work = work;
        _participantsChanged = participantsChanged;
        _applicationThemeService = applicationThemeService ?? new TransientApplicationThemeService();
        _aiFeaturePreferenceService = aiFeaturePreferenceService
            ?? new TransientAiFeaturePreferenceService();
        _aiFeaturesChanged = aiFeaturesChanged ?? (_ => { });
        _localMcpChanged = localMcpChanged ?? (_ => { });
        ThemeOptions =
        [
            new(ApplicationTheme.Dark, "Dark"),
            new(ApplicationTheme.Light, "Light"),
        ];
        _selectedTheme = ThemeOptions.Single(option =>
            option.Value == _applicationThemeService.CurrentTheme);
        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<ParticipantSettingViewModel> Participants { get; } = [];
    public ObservableCollection<AuthorizedMcpClientViewModel> AuthorizedMcpClients { get; } = [];
    public IReadOnlyList<ApplicationThemeOption> ThemeOptions { get; }
    private ApplicationThemeOption _selectedTheme;
    public ApplicationThemeOption SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value == _selectedTheme)
            {
                return;
            }

            if (!_applicationThemeService.TrySetTheme(value.Value))
            {
                Announcement = "Could not save the theme preference. The previous theme is still active.";
                Notify();
                return;
            }

            _selectedTheme = value;
            Notify();
            Notify(nameof(ThemeStatus));
            Announcement = $"{value.Name} theme selected.";
        }
    }
    public string ThemeStatus => $"{SelectedTheme.Name} theme selected";
    public bool AiFeaturesEnabled
    {
        get => _aiFeaturePreferenceService.AiFeaturesEnabled;
        set
        {
            if (value == AiFeaturesEnabled)
            {
                return;
            }

            if (!_aiFeaturePreferenceService.TrySetAiFeaturesEnabled(value))
            {
                Announcement =
                    "Could not save the AI features preference. The previous setting is still active.";
                Notify();
                return;
            }

            Notify();
            Notify(nameof(AiFeaturesStatus));
            Notify(nameof(ShowLocalMcpSettings));
            Notify(nameof(LocalMcpEnabled));
            Notify(nameof(LocalMcpStatus));
            _aiFeaturesChanged(value);
            Announcement = value
                ? "AI features enabled. Manual agent interchange is now available."
                : "AI features disabled. Agent interchange is unavailable.";
        }
    }
    public string AiFeaturesStatus => AiFeaturesEnabled
        ? "AI features enabled"
        : "AI features disabled";
    public bool ShowLocalMcpSettings => AiFeaturesEnabled;
    public bool LocalMcpEnabled
    {
        get => _aiFeaturePreferenceService.LocalMcpEnabled;
        set
        {
            if (value == LocalMcpEnabled)
            {
                return;
            }

            if (!_aiFeaturePreferenceService.TrySetLocalMcpEnabled(value))
            {
                Announcement =
                    "Could not save the local MCP preference. The previous setting is still active.";
                Notify();
                return;
            }

            Notify();
            Notify(nameof(LocalMcpStatus));
            _localMcpChanged(value);
            Announcement = value
                ? "Local MCP enabled. Approved local clients can connect while this workspace is unlocked."
                : "Local MCP disabled. Local clients have been disconnected.";
        }
    }
    public string LocalMcpStatus => LocalMcpEnabled
        ? $"Local MCP enabled · {_localMcpRuntimeStatus}"
        : "Local MCP disabled";
    public bool HasAuthorizedMcpClients => AuthorizedMcpClients.Count > 0;
    public bool HasNoAuthorizedMcpClients => !HasAuthorizedMcpClients;

    internal void ReportLocalMcpRuntimeStatus(LocalMcpServiceStatus status)
    {
        _localMcpRuntimeStatus = status switch
        {
            LocalMcpServiceStatus.Running => "Running",
            LocalMcpServiceStatus.Error => "Error",
            _ => "Stopped",
        };
        Notify(nameof(LocalMcpStatus));
    }

    internal void ReportAuthorizedMcpClients(
        IReadOnlyList<DotOrbit.Core.Interchange.AgentClientAuthorization> authorizations,
        Action<string> revoke)
    {
        AuthorizedMcpClients.Clear();
        foreach (var authorization in authorizations)
        {
            AuthorizedMcpClients.Add(new(authorization, revoke));
        }

        Notify(nameof(HasAuthorizedMcpClients));
        Notify(nameof(HasNoAuthorizedMcpClients));
    }

    internal void ReportLocalMcpRevocation(bool removed, string focusAutomationId)
    {
        Announcement = removed
            ? "Remembered local MCP access revoked. Connected clients were disconnected."
            : "Remembered local MCP access could not be revoked. It may still be active.";
        FocusAutomationId = focusAutomationId;
    }
    public bool HasParticipants => Participants.Count > 0;
    public bool HasNoParticipants => !HasParticipants;
    public string Announcement
    {
        get => _announcement;
        private set { _announcement = value; Notify(); }
    }
    public string FocusAutomationId
    {
        get => _focusAutomationId;
        private set { _focusAutomationId = value; Notify(); }
    }
    public string SecurityConfirmation
    {
        get => _securityConfirmation;
        private set
        {
            _securityConfirmation = value;
            Notify();
            Notify(nameof(HasSecurityConfirmation));
        }
    }
    public bool HasSecurityConfirmation => !string.IsNullOrEmpty(SecurityConfirmation);
    public void Refresh()
    {
        var snapshot = _work.Read();
        var counts = snapshot.Tasks
            .SelectMany(task => task.Participants)
            .GroupBy(id => id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        Participants.Clear();
        for (var index = 0; index < snapshot.Participants.Count; index++)
        {
            var participant = snapshot.Participants[index];
            Participants.Add(new ParticipantSettingViewModel(
                this,
                participant.Id,
                participant.Label,
                counts.GetValueOrDefault(participant.Id),
                index == snapshot.Participants.Count - 1));
        }
        Notify(nameof(HasParticipants));
        Notify(nameof(HasNoParticipants));
    }

    internal void SaveRename(ParticipantSettingViewModel participant)
    {
        string label;
        try { label = ParticipantLabel.Normalize(participant.DraftLabel); }
        catch (ArgumentException)
        {
            participant.ValidationMessage = "Enter a Participant label.";
            FocusAutomationId = participant.LabelAutomationId;
            return;
        }

        try
        {
            _work.RenameParticipant(participant.Id, label);
            _participantsChanged();
            Refresh();
            Announcement = $"Renamed Participant to {label}.";
            FocusAutomationId = $"settings-participant-rename-{participant.Id}";
        }
        catch (ArgumentException)
        {
            participant.ValidationMessage = "A Participant with this label already exists.";
            FocusAutomationId = participant.LabelAutomationId;
        }
        catch (WorkspaceWorkException)
        {
            participant.ValidationMessage = "Could not rename this Participant. Try again.";
            FocusAutomationId = participant.LabelAutomationId;
        }
    }

    internal void BeginRename(ParticipantSettingViewModel participant)
    {
        foreach (var row in Participants)
            if (!ReferenceEquals(row, participant)) row.ResetRename();
        participant.StartRename();
        RequestFocus(participant.LabelAutomationId);
    }

    internal void Delete(ParticipantSettingViewModel participant)
    {
        var index = Participants.IndexOf(participant);
        try
        {
            _work.DeleteParticipant(participant.Id);
            _participantsChanged();
            Refresh();
            Announcement = $"Deleted Participant {participant.Label}.";
            FocusAutomationId = Participants.Count == 0
                ? "settings-recovery-open"
                : Participants[Math.Min(index, Participants.Count - 1)].RenameAutomationId;
        }
        catch (InvalidOperationException)
        {
            participant.ValidationMessage = "Remove this Participant from every Task before deleting it.";
            Announcement = $"{participant.Label} is still used by a Task and was not deleted.";
            FocusAutomationId = participant.DeleteAutomationId;
        }
        catch (WorkspaceWorkException)
        {
            participant.ValidationMessage = "Could not delete this Participant. Try again.";
            FocusAutomationId = participant.DeleteAutomationId;
        }
    }

    internal void RequestFocus(string automationId) => FocusAutomationId = automationId;

    internal void ConfirmPassphraseChanged() =>
        SecurityConfirmation =
            "Passphrase changed. Use the new passphrase the next time you unlock this workspace.";

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ParticipantSettingViewModel : INotifyPropertyChanged
{
    private readonly SettingsViewModel _owner;
    private string _draftLabel;
    private string _validationMessage = string.Empty;
    private bool _isEditing;

    internal ParticipantSettingViewModel(
        SettingsViewModel owner,
        string id,
        string label,
        int usageCount,
        bool isLast)
    {
        _owner = owner;
        Id = id;
        Label = label;
        _draftLabel = label;
        UsageCount = usageCount;
        IsLast = isLast;
        BeginRenameCommand = new(() => _owner.BeginRename(this));
        SaveRenameCommand = new(() => _owner.SaveRename(this));
        CancelRenameCommand = new(CancelRename);
        DeleteCommand = new(() => _owner.Delete(this));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id { get; }
    public string Label { get; }
    public int UsageCount { get; }
    public bool IsLast { get; }
    public string UsageText => UsageCount == 0 ? "Not in use" : $"Used by {UsageCount} {(UsageCount == 1 ? "task" : "tasks")}";
    public bool IsEditing
    {
        get => _isEditing;
        private set { _isEditing = value; Notify(); Notify(nameof(IsReadOnly)); }
    }
    public bool IsReadOnly => !IsEditing;
    public string DraftLabel
    {
        get => _draftLabel;
        set { _draftLabel = value; ValidationMessage = string.Empty; Notify(); }
    }
    public string ValidationMessage
    {
        get => _validationMessage;
        internal set { _validationMessage = value; Notify(); Notify(nameof(HasValidationError)); }
    }
    public bool HasValidationError => !string.IsNullOrEmpty(ValidationMessage);
    public string RenameAutomationId => $"settings-participant-rename-{Id}";
    public string DeleteAutomationId => $"settings-participant-delete-{Id}";
    public string LabelAutomationId => $"settings-participant-label-{Id}";
    public string RenameAccessibleName => $"Rename Participant {Label}";
    public string DeleteAccessibleName => $"Delete Participant {Label}";
    public RelayCommand BeginRenameCommand { get; }
    public RelayCommand SaveRenameCommand { get; }
    public RelayCommand CancelRenameCommand { get; }
    public RelayCommand DeleteCommand { get; }

    public void BeginRename()
    {
        _owner.BeginRename(this);
    }

    internal void StartRename()
    {
        DraftLabel = Label;
        IsEditing = true;
    }

    public void CancelRename()
    {
        ResetRename();
        _owner.RequestFocus(RenameAutomationId);
    }

    internal void ResetRename()
    {
        DraftLabel = Label;
        ValidationMessage = string.Empty;
        IsEditing = false;
    }

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
