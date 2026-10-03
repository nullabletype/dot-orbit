using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using DotOrbit.Core.Workspaces;

namespace DotOrbit.Desktop.ViewModels;

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly IWorkspaceWork _work;
    private readonly Action _participantsChanged;
    private string _announcement = string.Empty;
    private string _focusAutomationId = string.Empty;

    public SettingsViewModel(IWorkspaceWork work, Action participantsChanged)
    {
        _work = work;
        _participantsChanged = participantsChanged;
        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<ParticipantSettingViewModel> Participants { get; } = [];
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
