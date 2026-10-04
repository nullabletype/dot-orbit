using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.Markdown;

namespace DotOrbit.Desktop.ViewModels;

public sealed class ProjectCaptureViewModel : INotifyPropertyChanged
{
    private readonly IWorkspaceWork _work;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, TaskRowViewModel> _taskRows = new(StringComparer.Ordinal);
    private WorkspaceWorkSnapshot _snapshot = new([], [], []);
    private Action? _pendingNavigation;
    private string? _editingId;
    private bool _editingTask;
    private bool _editingCategory;
    private bool _creating;
    private bool _creatingStandaloneTask;
    private bool _creatingTodayTask;
    private string _title = string.Empty;
    private string _description = string.Empty;
    private string _date = string.Empty;
    private CategoryChoice? _category;
    private (string Title, string Description, string Date, string? CategoryId, string Participants) _original;
    private string _message = string.Empty;
    private bool _hasInspector;
    private string _backlogQuickTitle = string.Empty;
    private CategoryChoice? _backlogQuickCategory;
    private string _reorderAnnouncement = string.Empty;
    private bool _backlogActive;
    private bool _upcomingActive;
    private bool _completedActive;
    private bool _todayActive;
    private string _completionFocusAutomationId = string.Empty;
    private string _reorderFocusAutomationId = string.Empty;
    private DateOnly _presentationDate;
    private string _presentationTimeZoneId = string.Empty;
    private string _categoryNameValidationMessage = string.Empty;
    private string _workTitleValidationMessage = string.Empty;
    private string _dateValidationMessage = string.Empty;
    private string? _pendingCategoryDeleteId;
    private CategoryChoice? _categoryReplacement;
    private TaskContextChoice? _taskContextTarget;
    private string? _pendingAttachmentTaskId;
    private string? _pendingAttachmentProjectId;
    private bool _pendingNavigationClosesInspector = true;
    private string _dialogReturnFocusAutomationId = string.Empty;
    private bool _loadingDraft;
    private bool _editingMarkdown;
    private SanitisedMarkdownDocument _renderedDescription = SanitisedMarkdownDocument.Empty;
    private ParticipantChoice? _participantToAdd;
    private string _newParticipantLabel = string.Empty;
    private string _newParticipantValidationMessage = string.Empty;
    private string _participantSelectionMessage = string.Empty;
    private string _participantAnnouncement = string.Empty;
    private string _participantFocusAutomationId = string.Empty;
    private int _participantDraftSequence;
    private long _autosaveRevision;
    private bool _autosaveInProgress;
    private string _autosaveStatus = "Changes save automatically.";
    private bool _hasAutosaveError;
    private string _todayAnnouncement = string.Empty;
    private string _todayFocusAutomationId = string.Empty;

    public static TimeSpan AutosaveDelay { get; } = TimeSpan.FromMilliseconds(600);

    public ProjectCaptureViewModel(IWorkspaceWork work, TimeProvider? timeProvider = null)
    {
        _work = work;
        _timeProvider = timeProvider ?? TimeProvider.System;
        NewProjectCommand = new(() => Navigate(BeginProject));
        NewTaskCommand = new(() => Navigate(BeginStandaloneTask));
        NewTodayTaskCommand = new(() => Navigate(BeginTodayTask));
        NewCategoryCommand = new(() => Navigate(BeginCategory));
        ClearTodayCommand = new(ClearToday);
        AddParticipantCommand = new(AddParticipant);
        AddNewParticipantCommand = new(AddNewParticipant);
        CancelNewParticipantCommand = new(() =>
        {
            NewParticipantLabel = string.Empty;
            ParticipantToAdd = null;
            ParticipantFocusAutomationId = "participant-picker";
        });
        RetryAutosaveCommand = new(() => RunScheduledAutosave(force: true));
        DeleteCategoryCommand = new(() =>
        {
            if (_editingCategory && !_creating && _editingId is not null) BeginCategoryDeletion(_editingId);
        });
        ConfirmDeleteCategoryCommand = new(ConfirmDeleteCategory);
        CancelDeleteCategoryCommand = new(CancelDeleteCategory);
        ChangeTaskContextCommand = new(RequestTaskContextChange);
        PreserveTaskCategoryCommand = new(() => CompletePendingAttachment(TaskAttachmentCategoryChoice.PreserveEffectiveCategory));
        AdoptProjectCategoryCommand = new(() => CompletePendingAttachment(TaskAttachmentCategoryChoice.AdoptProjectCategory));
        CancelAttachmentCommand = new(CancelPendingAttachment);
        EditMarkdownCommand = new(() => IsEditingMarkdown = true);
        SaveCommand = new(() => Save());
        CancelCommand = new(Cancel);
        SaveAndLeaveCommand = new(() => { if (Save()) Leave(); });
        DiscardAndLeaveCommand = new(() => { Cancel(); Leave(); });
        StayCommand = new(() =>
        {
            _pendingNavigation = null;
            _pendingNavigationClosesInspector = true;
            Notify(nameof(NeedsDecision));
            Notify(nameof(HasBlockingDialog));
        });
        CapturePresentationClock();
        Reload();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<ProjectRowViewModel> Projects { get; } = [];
    public ObservableCollection<TaskRowViewModel> Backlog { get; } = [];
    public ObservableCollection<UpcomingTaskGroupViewModel> UpcomingGroups { get; } = [];
    public ObservableCollection<TaskRowViewModel> Completed { get; } = [];
    public ObservableCollection<TodayTaskRowViewModel> TodayPlanned { get; } = [];
    public ObservableCollection<TodayTaskRowViewModel> TodayInProgress { get; } = [];
    public ObservableCollection<CompletedTaskRowViewModel> CompletedToday { get; } = [];
    public ObservableCollection<CompletedTaskGroupViewModel> CompletedGroups { get; } = [];
    public ObservableCollection<CategoryChoice> Categories { get; } = [];
    public ObservableCollection<CategoryChoice> BacklogCategories { get; } = [];
    public ObservableCollection<CategoryGroupViewModel> CategoryGroups { get; } = [];
    public ObservableCollection<CategoryChoice> CategoryReplacementChoices { get; } = [];
    public ObservableCollection<TaskContextChoice> TaskContextChoices { get; } = [];
    public ObservableCollection<ParticipantChoice> AvailableParticipants { get; } = [];
    public ObservableCollection<ParticipantDraftViewModel> SelectedParticipants { get; } = [];
    public RelayCommand NewProjectCommand { get; }
    public RelayCommand NewTaskCommand { get; }
    public RelayCommand NewTodayTaskCommand { get; }
    public RelayCommand NewCategoryCommand { get; }
    public RelayCommand ClearTodayCommand { get; }
    public RelayCommand AddParticipantCommand { get; }
    public RelayCommand AddNewParticipantCommand { get; }
    public RelayCommand CancelNewParticipantCommand { get; }
    public RelayCommand RetryAutosaveCommand { get; }
    public RelayCommand DeleteCategoryCommand { get; }
    public RelayCommand ConfirmDeleteCategoryCommand { get; }
    public RelayCommand CancelDeleteCategoryCommand { get; }
    public RelayCommand ChangeTaskContextCommand { get; }
    public RelayCommand PreserveTaskCategoryCommand { get; }
    public RelayCommand AdoptProjectCategoryCommand { get; }
    public RelayCommand CancelAttachmentCommand { get; }
    public RelayCommand EditMarkdownCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand SaveAndLeaveCommand { get; }
    public RelayCommand DiscardAndLeaveCommand { get; }
    public RelayCommand StayCommand { get; }
    public bool HasInspector
    {
        get => _hasInspector;
        private set
        {
            _hasInspector = value;
            Notify();
            Notify(nameof(HasNoInspector));
            Notify(nameof(ShowTaskContext));
            Notify(nameof(ShowTaskContextAction));
            Notify(nameof(CanChangeTaskContext));
            Notify(nameof(TaskContextActionLabel));
            Notify(nameof(ShowWorkInspector));
            Notify(nameof(ShowCategoryInspector));
            Notify(nameof(ShowCategoryDelete));
            Notify(nameof(ShowParticipants));
            Notify(nameof(ShowMarkdownPreview));
            Notify(nameof(ShowMarkdownEditor));
        }
    }
    public bool HasNoInspector => !HasInspector;
    public bool HasProjects => Projects.Count > 0;
    public bool HasNoProjects => !HasProjects;
    public int UpcomingCount => UpcomingGroups.Sum(group => group.Rows.Count);
    public bool HasUpcoming => UpcomingCount > 0;
    public bool HasNoUpcoming => !HasUpcoming;
    public bool HasCompleted => Completed.Count > 0;
    public bool HasNoCompleted => !HasCompleted;
    public bool HasTodayTasks => TodayPlanned.Count + TodayInProgress.Count > 0;
    public bool HasNoTodayTasks => !HasTodayTasks;
    public bool HasCompletedToday => CompletedToday.Count > 0;
    public bool NeedsDecision => _pendingNavigation is not null;
    public bool NeedsCategoryReplacement => _pendingCategoryDeleteId is not null;
    public bool NeedsAttachmentChoice => _pendingAttachmentTaskId is not null;
    public bool HasBlockingDialog => NeedsDecision || NeedsCategoryReplacement || NeedsAttachmentChoice;
    public bool IsDirty => HasInspector && _original != Fingerprint();
    public bool ShowExplicitInspectorActions => HasInspector && _editingCategory;
    public bool ShowAutosaveStatus => HasInspector && !_editingCategory;
    public string AutosaveStatus
    {
        get => _autosaveStatus;
        private set { _autosaveStatus = value; Notify(); }
    }
    public bool HasAutosaveError
    {
        get => _hasAutosaveError;
        private set { _hasAutosaveError = value; Notify(); }
    }
    public bool ShowNewParticipantEntry => ParticipantToAdd?.IsNew == true;
    public bool ShowExistingParticipantAction => ParticipantToAdd is { IsNew: false, Id: not null };
    public event EventHandler<AutosaveRequestEventArgs>? AutosaveRequested;
    public bool ShowWorkInspector => HasInspector && !_editingCategory;
    public bool ShowMarkdownPreview => ShowWorkInspector && !IsEditingMarkdown;
    public bool ShowMarkdownEditor => ShowWorkInspector && IsEditingMarkdown;
    public bool ShowCategoryInspector => HasInspector && _editingCategory;
    public bool ShowCategoryDelete => ShowCategoryInspector && !_creating;
    public bool ShowParticipants => HasInspector && _editingTask && !_editingCategory;
    public const string ParticipantGuidance = "Use initials or a nickname. Do not enter email addresses or other contact details.";
    public ParticipantChoice? ParticipantToAdd
    {
        get => _participantToAdd;
        set
        {
            _participantToAdd = value;
            ParticipantSelectionMessage = string.Empty;
            Notify();
            Notify(nameof(ShowNewParticipantEntry));
            Notify(nameof(ShowExistingParticipantAction));
            if (value?.IsNew == true) ParticipantFocusAutomationId = "new-participant-label";
        }
    }
    public string NewParticipantLabel
    {
        get => _newParticipantLabel;
        set
        {
            _newParticipantLabel = value;
            NewParticipantValidationMessage = string.Empty;
            Notify();
        }
    }
    public string NewParticipantValidationMessage
    {
        get => _newParticipantValidationMessage;
        private set { _newParticipantValidationMessage = value; Notify(); Notify(nameof(HasNewParticipantValidationError)); }
    }
    public bool HasNewParticipantValidationError => !string.IsNullOrEmpty(NewParticipantValidationMessage);
    public string ParticipantSelectionMessage
    {
        get => _participantSelectionMessage;
        private set { _participantSelectionMessage = value; Notify(); Notify(nameof(HasParticipantSelectionError)); }
    }
    public bool HasParticipantSelectionError => !string.IsNullOrEmpty(ParticipantSelectionMessage);
    public string ParticipantAnnouncement
    {
        get => _participantAnnouncement;
        private set { _participantAnnouncement = value; Notify(); }
    }
    public string ParticipantFocusAutomationId
    {
        get => _participantFocusAutomationId;
        private set { _participantFocusAutomationId = value; Notify(); }
    }
    public string InspectorHeading => _editingCategory
        ? (_creating ? "New category" : "Category details")
        : _creating ? (_editingTask ? "New task" : "New project") : _editingTask ? "Task details" : "Project details";
    public string TitleAutomationName => _editingCategory ? "Category name" : "Title";
    public bool ShowProjectSummary => HasInspector && !_editingCategory && !_creating && !_editingTask;
    public string ProjectSummary => ShowProjectSummary
        ? Projects.Single(p => p.Id == _editingId).Summary
            + (string.IsNullOrEmpty(Projects.Single(p => p.Id == _editingId).CompletionDateText)
                ? " · No completion date"
                : $" · {Projects.Single(p => p.Id == _editingId).CompletionDateText}")
        : string.Empty;
    public bool ShowTaskCompletionDate => HasInspector && !_editingCategory && !_creating && _editingTask
        && _snapshot.Tasks.Single(task => task.Id == _editingId).CompletionDate is not null;
    public string TaskCompletionDateText => ShowTaskCompletionDate
        ? $"Completed {WorkDatePresentation.Relative(_snapshot.Tasks.Single(task => task.Id == _editingId).CompletionDate, Today)}"
        : string.Empty;
    public string TaskCompletionDateAccessibleText => ShowTaskCompletionDate
        ? WorkDatePresentation.Accessible(_snapshot.Tasks.Single(task => task.Id == _editingId).CompletionDate, "Completed")
        : string.Empty;
    public string InspectorMetaLabel => _editingTask ? "CATEGORY BEHAVIOUR" : "STATUS";
    public string InspectorMetaValue => _editingTask
        ? CategoryHint
        : ShowProjectSummary
            ? $"{Projects.Single(p => p.Id == _editingId).Status} · {Projects.Single(p => p.Id == _editingId).ProgressText}"
            : "Not started · 0/0 tasks";
    public string SaveLabel => _creating ? "Create" : "Save";
    public string DecisionHeading => _editingCategory ? "Save your changes before leaving?" : "Changes could not be saved";
    public string DecisionBody => _editingCategory
        ? "Your inspector contains changes that have not been saved."
        : "Correct the highlighted fields or retry saving before leaving. You can also discard these changes.";
    public string DecisionSaveLabel => _editingCategory ? "Save and leave" : "Retry and leave";
    public string DateLabel => _editingTask ? "Due date (YYYY-MM-DD, optional)" : "Target date (YYYY-MM-DD, optional)";
    public string CategoryHint => _editingTask
        ? IsStandaloneTaskDraft
            ? "Standalone Task category"
            : (_category?.Id is null ? "Inherited from project" : "Explicit category override")
        : "Project category";
    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public string Message { get => _message; private set { _message = value; Notify(); Notify(nameof(HasMessage)); } }
    public SanitisedMarkdownDocument RenderedDescription => _renderedDescription;
    public bool HasRenderedDescription => !_renderedDescription.IsEmpty;
    public string MarkdownPreviewAutomationName => _renderedDescription.IsEmpty
        ? "Empty rendered description. Activate to edit Markdown."
        : $"Rendered description: {_renderedDescription.ToPlainText().ReplaceLineEndings(" ")}. Activate to edit Markdown.";
    public bool IsEditingMarkdown
    {
        get => _editingMarkdown;
        private set
        {
            if (_editingMarkdown == value) return;
            _editingMarkdown = value;
            Notify();
            Notify(nameof(ShowMarkdownPreview));
            Notify(nameof(ShowMarkdownEditor));
        }
    }
    public string Title
    {
        get => _title;
        set
        {
            if (_loadingDraft) { _title = value; return; }
            if (string.Equals(_title, value, StringComparison.Ordinal)) return;
            _title = value;
            if (_editingCategory) CategoryNameValidationMessage = string.Empty;
            else
            {
                _workTitleValidationMessage = string.Empty;
                Notify(nameof(TitleValidationMessage));
                Notify(nameof(HasTitleValidationError));
            }
            Notify();
            DraftChanged();
        }
    }
    public string Description
    {
        get => _description;
        set
        {
            if (_loadingDraft) { _description = value; return; }
            if (string.Equals(_description, value, StringComparison.Ordinal)) return;
            _description = value;
            RefreshRenderedDescription();
            Notify();
            DraftChanged();
        }
    }
    public string Date
    {
        get => _date;
        set
        {
            if (_loadingDraft) { _date = value; return; }
            if (string.Equals(_date, value, StringComparison.Ordinal)) return;
            _date = value;
            DateValidationMessage = string.Empty;
            Notify();
            DraftChanged();
        }
    }
    public string BacklogQuickTitle { get => _backlogQuickTitle; set { _backlogQuickTitle = value; Notify(); } }
    public CategoryChoice? BacklogQuickCategory { get => _backlogQuickCategory; set { _backlogQuickCategory = value; Notify(); } }
    public string CategoryNameValidationMessage
    {
        get => _categoryNameValidationMessage;
        private set
        {
            _categoryNameValidationMessage = value;
            Notify();
            Notify(nameof(HasCategoryNameValidationError));
            Notify(nameof(TitleValidationMessage));
            Notify(nameof(HasTitleValidationError));
        }
    }
    public bool HasCategoryNameValidationError => !string.IsNullOrEmpty(CategoryNameValidationMessage);
    public string TitleValidationMessage => _editingCategory ? CategoryNameValidationMessage : _workTitleValidationMessage;
    public bool HasTitleValidationError => !string.IsNullOrEmpty(TitleValidationMessage);
    public string DateValidationMessage
    {
        get => _dateValidationMessage;
        private set
        {
            if (string.Equals(_dateValidationMessage, value, StringComparison.Ordinal)) return;
            _dateValidationMessage = value;
            Notify();
            Notify(nameof(HasDateValidationError));
        }
    }
    public bool HasDateValidationError => !string.IsNullOrEmpty(DateValidationMessage);
    internal void MarkdownCopySucceeded() => Message = "Rendered description copied as rich text and plain text.";
    internal void MarkdownCopyFailed() => Message = "Could not copy the rendered description. Try again.";
    internal void FinishMarkdownEditing() => IsEditingMarkdown = false;
    public CategoryChoice? CategoryReplacement { get => _categoryReplacement; set { _categoryReplacement = value; Notify(); } }
    public TaskContextChoice? TaskContextTarget
    {
        get => _taskContextTarget;
        set
        {
            _taskContextTarget = value;
            if (_creatingTodayTask && !_loadingDraft) RefreshCreatingTaskCategories();
            Notify();
            Notify(nameof(CanChangeTaskContext));
            Notify(nameof(TaskContextActionLabel));
        }
    }
    public bool ShowTaskContext => HasInspector && !_editingCategory && _editingTask && (!_creating || _creatingTodayTask);
    public bool ShowTaskContextAction => ShowTaskContext && !_creating;
    public bool CanChangeTaskContext => ShowTaskContextAction && TaskContextTarget?.ProjectId != _snapshot.Tasks.Single(task => task.Id == _editingId).ProjectId;
    public string TaskContextActionLabel => !ShowTaskContextAction || TaskContextTarget?.ProjectId == _snapshot.Tasks.Single(task => task.Id == _editingId).ProjectId
        ? "Choose a different context"
        : TaskContextTarget?.ProjectId is null
            ? "Detach to standalone"
            : _snapshot.Tasks.Single(task => task.Id == _editingId).ProjectId is null
                ? "Attach to project"
                : "Move to project";
    public string CategoryDeleteHeading => _pendingCategoryDeleteId is null
        ? string.Empty
        : $"Replace {_snapshot.Categories.Single(category => category.Id == _pendingCategoryDeleteId).Name}";
    public string PreserveCategoryLabel => _pendingAttachmentTaskId is null || _pendingAttachmentProjectId is null
        ? "Keep current category"
        : $"Keep {CategoryName(EffectiveCategoryId(_snapshot.Tasks.Single(task => task.Id == _pendingAttachmentTaskId)))} as override";
    public string AdoptCategoryLabel => _pendingAttachmentProjectId is null
        ? "Adopt project category"
        : $"Adopt {CategoryName(_snapshot.Projects.Single(project => project.Id == _pendingAttachmentProjectId).CategoryId)}";
    public string ReorderAnnouncement { get => _reorderAnnouncement; private set { _reorderAnnouncement = value; Notify(); } }
    public string TodayAnnouncement { get => _todayAnnouncement; private set { _todayAnnouncement = value; Notify(); } }
    public string TodayFocusAutomationId { get => _todayFocusAutomationId; private set { _todayFocusAutomationId = value; Notify(); } }
    public string CompletionFocusAutomationId { get => _completionFocusAutomationId; private set { _completionFocusAutomationId = value; Notify(); } }
    public string ReorderFocusAutomationId { get => _reorderFocusAutomationId; private set { _reorderFocusAutomationId = value; Notify(); } }
    public string DialogReturnFocusAutomationId { get => _dialogReturnFocusAutomationId; private set { _dialogReturnFocusAutomationId = value; Notify(); } }
    internal DateOnly Today => DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime);
    private bool IsStandaloneTaskDraft => _editingTask && ((_creatingStandaloneTask && (!_creatingTodayTask || TaskContextTarget?.ProjectId is null))
        || (_editingId is not null && _snapshot.Tasks.Single(task => task.Id == _editingId).ProjectId is null));
    public CategoryChoice? Category
    {
        get => _category;
        set
        {
            if (_loadingDraft) { _category = value; return; }
            if (_category == value) return;
            _category = value;
            Notify();
            Notify(nameof(CategoryHint));
            Notify(nameof(InspectorMetaValue));
            DraftChanged(immediate: true);
        }
    }

    private void DraftChanged(bool immediate = false)
    {
        Notify(nameof(IsDirty));
        if (!HasInspector || _editingCategory) return;
        _autosaveRevision++;
        HasAutosaveError = false;
        AutosaveStatus = IsDirty ? (immediate ? "Saving…" : "Saving soon…") : "Saved";
        if (immediate) RunScheduledAutosave();
        else AutosaveRequested?.Invoke(this, new(_autosaveRevision));
    }

    public bool RunScheduledAutosave(long? scheduledRevision = null, bool force = false)
    {
        if (!HasInspector || _editingCategory) return true;
        if (!force && scheduledRevision is not null && scheduledRevision != _autosaveRevision) return true;
        if (!IsDirty)
        {
            AutosaveStatus = _creating ? "Start typing to create." : "Saved";
            return true;
        }
        if (_autosaveInProgress) return false;
        var revision = _autosaveRevision;
        _autosaveInProgress = true;
        AutosaveStatus = "Saving…";
        var saved = SaveWork();
        _autosaveInProgress = false;
        if (saved)
        {
            HasAutosaveError = false;
            AutosaveStatus = "Saved";
        }
        else
        {
            HasAutosaveError = true;
            AutosaveStatus = string.IsNullOrEmpty(Message) ? "Could not save. Retry." : Message;
        }
        if (revision != _autosaveRevision && IsDirty) AutosaveRequested?.Invoke(this, new(_autosaveRevision));
        return saved;
    }

    public bool FlushPendingAutosave() => _editingCategory || !IsDirty || RunScheduledAutosave(force: true);

    public void CommitCalendarDate(DateTime? selectedDate)
    {
        if (!HasInspector || _editingCategory) return;
        var value = selectedDate is null
            ? string.Empty
            : DateOnly.FromDateTime(selectedDate.Value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!string.Equals(_date, value, StringComparison.Ordinal))
        {
            _date = value;
            DateValidationMessage = string.Empty;
            Notify(nameof(Date));
            DraftChanged(immediate: true);
            return;
        }
        RunScheduledAutosave(force: true);
    }

    public void Navigate(Action destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (NeedsDecision) return;
        if (IsDirty && (_editingCategory || !FlushPendingAutosave()))
        {
            _pendingNavigation = destination;
            _pendingNavigationClosesInspector = true;
            Notify(nameof(NeedsDecision));
            Notify(nameof(HasBlockingDialog));
            return;
        }
        CloseInspector();
        destination();
    }

    public void SelectProject(string id) => Navigate(() => LoadProject(id));
    public void SelectTask(string id) => Navigate(() => LoadTask(id));
    public void SelectCategory(string id) => Navigate(() => LoadCategory(id));

    private void AddParticipant()
    {
        if (ParticipantToAdd is null || ParticipantToAdd.IsNew || ParticipantToAdd.Id is null) return;
        if (SelectedParticipants.Any(item => item.Id == ParticipantToAdd.Id))
        {
            ParticipantSelectionMessage = "That Participant is already selected.";
            return;
        }
        SelectedParticipants.Add(new(this, ParticipantToAdd.Id, ParticipantToAdd.Label,
            ParticipantToAdd.Id!));
        ParticipantAnnouncement = $"Added {ParticipantToAdd.Label} to the Task draft.";
        ParticipantToAdd = null;
        ParticipantSelectionMessage = string.Empty;
        DraftChanged(immediate: true);
        ParticipantFocusAutomationId = "participant-picker";
    }

    private void AddNewParticipant()
    {
        string label;
        try { label = ParticipantLabel.Normalize(NewParticipantLabel); }
        catch (ArgumentException)
        {
            NewParticipantValidationMessage = "Enter a Participant label.";
            return;
        }

        var comparisonKey = ParticipantLabel.ComparisonKey(label);
        var existing = _snapshot.Participants.FirstOrDefault(participant =>
            string.Equals(ParticipantLabel.ComparisonKey(participant.Label), comparisonKey, StringComparison.Ordinal));
        if (existing is not null)
        {
            if (SelectedParticipants.Any(item => item.Id == existing.Id))
            {
                NewParticipantValidationMessage = "This Participant is already on this Task.";
                ParticipantFocusAutomationId = "new-participant-label";
                return;
            }

            SelectedParticipants.Add(new(this, existing.Id, existing.Label, existing.Id));
            ParticipantAnnouncement = $"Added existing Participant {existing.Label} to the Task draft.";
            NewParticipantLabel = string.Empty;
            ParticipantToAdd = null;
            DraftChanged(immediate: true);
            ParticipantFocusAutomationId = "participant-picker";
            return;
        }

        if (SelectedParticipants.Any(item => item.Id is null
            && string.Equals(ParticipantLabel.ComparisonKey(item.Label), comparisonKey, StringComparison.Ordinal)))
        {
            NewParticipantValidationMessage = "This Participant is already on this Task.";
            ParticipantFocusAutomationId = "new-participant-label";
            return;
        }
        var automationKey = $"draft-{_participantDraftSequence++}";
        SelectedParticipants.Add(new(this, null, label, automationKey));
        ParticipantAnnouncement = $"Created and added {label} to the Task draft.";
        NewParticipantLabel = string.Empty;
        NewParticipantValidationMessage = string.Empty;
        ParticipantToAdd = null;
        DraftChanged(immediate: true);
        ParticipantFocusAutomationId = "participant-picker";
    }

    internal void RemoveParticipant(ParticipantDraftViewModel participant)
    {
        var index = SelectedParticipants.IndexOf(participant);
        SelectedParticipants.Remove(participant);
        ParticipantAnnouncement = $"Removed {participant.Label} from the Task draft.";
        ParticipantFocusAutomationId = SelectedParticipants.Count == 0
            ? "participant-picker"
            : SelectedParticipants[Math.Min(index, SelectedParticipants.Count - 1)].RemoveAutomationId;
        DraftChanged();
    }

    public void SetBacklogActive(bool active) => _backlogActive = active;
    public void SetUpcomingActive(bool active) => _upcomingActive = active;
    public void SetCompletedActive(bool active) => _completedActive = active;
    public void SetTodayActive(bool active) => _todayActive = active;

    public bool RefreshDatePresentation()
    {
        var today = Today;
        var timeZoneId = _timeProvider.LocalTimeZone.Id;
        if (today == _presentationDate && string.Equals(timeZoneId, _presentationTimeZoneId, StringComparison.Ordinal)) return false;
        CapturePresentationClock();
        Reload();
        return true;
    }

    private void CapturePresentationClock()
    {
        _presentationDate = Today;
        _presentationTimeZoneId = _timeProvider.LocalTimeZone.Id;
    }

    public bool QuickAdd(string projectId, string title)
    {
        if (IsDirty && _editingCategory)
        {
            _pendingNavigation = () => QuickAdd(projectId, title);
            _pendingNavigationClosesInspector = false;
            Notify(nameof(NeedsDecision));
            Notify(nameof(HasBlockingDialog));
            return false;
        }
        if (IsDirty && !_editingCategory && !FlushPendingAutosave()) return false;
        if (string.IsNullOrWhiteSpace(title)) return false;
        return Attempt(() => { _work.CreateTask(projectId, title); Reload(); Message = "Task created."; });
    }

    public void BeginBacklogEntrySession()
    {
        BacklogQuickTitle = string.Empty;
        BacklogQuickCategory = null;
    }

    public void EndBacklogEntrySession()
    {
        BacklogQuickTitle = string.Empty;
        BacklogQuickCategory = null;
    }

    public bool SubmitBacklogQuickAdd()
    {
        if (IsDirty && (_editingCategory || !FlushPendingAutosave())) return false;
        if (string.IsNullOrWhiteSpace(BacklogQuickTitle)) return false;
        if (BacklogQuickCategory?.Id is null)
        {
            Message = "Choose a category before creating a standalone Task.";
            return false;
        }

        var title = BacklogQuickTitle;
        if (!Attempt(() => { _work.CreateStandaloneTask(title, string.Empty, BacklogQuickCategory.Id, null); Reload(); Message = "Task created."; }))
            return false;
        BacklogQuickTitle = string.Empty;
        return true;
    }

    internal void RequestDeleteCategory(string id)
    {
        if (_snapshot.Categories.Count == 1)
        {
            Message = "At least one Category must remain.";
            return;
        }

        var referenced = _snapshot.Projects.Any(project => project.CategoryId == id)
            || _snapshot.Tasks.Any(task => task.ExplicitCategoryId == id);
        if (!referenced)
        {
            Attempt(() =>
            {
                var categoryIndex = _snapshot.Categories.ToList().FindIndex(category => category.Id == id);
                var focusCategory = categoryIndex < _snapshot.Categories.Count - 1
                    ? _snapshot.Categories[categoryIndex + 1]
                    : _snapshot.Categories[categoryIndex - 1];
                _work.DeleteCategory(id);
                DialogReturnFocusAutomationId = $"category-selection-{focusCategory.Id}";
                CloseInspector();
                Reload();
                Message = "Category deleted.";
            }, "Could not delete the Category.");
            return;
        }

        DialogReturnFocusAutomationId = "category-delete";
        _pendingCategoryDeleteId = id;
        CategoryReplacementChoices.Clear();
        foreach (var category in _snapshot.Categories.Where(category => category.Id != id))
            CategoryReplacementChoices.Add(new(category.Id, category.Name));
        CategoryReplacement = CategoryReplacementChoices.FirstOrDefault();
        Notify(nameof(NeedsCategoryReplacement));
        Notify(nameof(CategoryDeleteHeading));
        Notify(nameof(HasBlockingDialog));
    }

    private void BeginCategoryDeletion(string id)
    {
        if (NeedsDecision) return;
        if (IsDirty)
        {
            _pendingNavigation = () => RequestDeleteCategory(id);
            _pendingNavigationClosesInspector = false;
            Notify(nameof(NeedsDecision));
            Notify(nameof(HasBlockingDialog));
            return;
        }

        RequestDeleteCategory(id);
    }

    private void ConfirmDeleteCategory()
    {
        if (_pendingCategoryDeleteId is null || CategoryReplacement?.Id is null) return;
        var id = _pendingCategoryDeleteId;
        var replacementId = CategoryReplacement.Id;
        if (!Attempt(() =>
            {
                _work.DeleteCategory(id, replacementId);
                DialogReturnFocusAutomationId = $"category-selection-{replacementId}";
                CancelDeleteCategory();
                CloseInspector();
                Reload();
                Message = "Category replaced and deleted.";
            }, "Could not replace the Category. No changes were made.")) return;
    }

    private void CancelDeleteCategory()
    {
        _pendingCategoryDeleteId = null;
        CategoryReplacementChoices.Clear();
        CategoryReplacement = null;
        Notify(nameof(NeedsCategoryReplacement));
        Notify(nameof(CategoryDeleteHeading));
        Notify(nameof(HasBlockingDialog));
    }

    private void RequestTaskContextChange()
    {
        if (!CanChangeTaskContext || _editingId is null) return;
        var taskId = _editingId;
        var targetProjectId = TaskContextTarget?.ProjectId;
        ResolveDraftBeforeAction(() =>
        {
            if (targetProjectId is null)
            {
                Attempt(() =>
                {
                    _work.DetachTask(taskId);
                    Reload();
                    LoadTask(taskId);
                    Message = "Task detached as standalone work.";
                }, "Could not detach the Task. No changes were made.");
                return;
            }

            var task = _snapshot.Tasks.Single(item => item.Id == taskId);
            var effectiveCategoryId = EffectiveCategoryId(task);
            var projectCategoryId = _snapshot.Projects.Single(project => project.Id == targetProjectId).CategoryId;
            if (effectiveCategoryId == projectCategoryId)
            {
                AttachTask(taskId, targetProjectId, null);
                return;
            }

            _pendingAttachmentTaskId = taskId;
            _pendingAttachmentProjectId = targetProjectId;
            Notify(nameof(NeedsAttachmentChoice));
            Notify(nameof(PreserveCategoryLabel));
            Notify(nameof(AdoptCategoryLabel));
            Notify(nameof(HasBlockingDialog));
        });
    }

    private void ResolveDraftBeforeAction(Action action)
    {
        if (NeedsDecision) return;
        if (IsDirty && (_editingCategory || !FlushPendingAutosave()))
        {
            _pendingNavigation = action;
            _pendingNavigationClosesInspector = false;
            Notify(nameof(NeedsDecision));
            Notify(nameof(HasBlockingDialog));
            return;
        }

        action();
    }

    private void CompletePendingAttachment(TaskAttachmentCategoryChoice choice)
    {
        if (_pendingAttachmentTaskId is null || _pendingAttachmentProjectId is null) return;
        var taskId = _pendingAttachmentTaskId;
        var projectId = _pendingAttachmentProjectId;
        if (AttachTask(taskId, projectId, choice)) CancelPendingAttachment();
    }

    private bool AttachTask(string taskId, string projectId, TaskAttachmentCategoryChoice? choice)
    {
        return Attempt(() =>
        {
            _work.AttachTask(taskId, projectId, choice);
            Reload();
            LoadTask(taskId);
            Message = "Task moved to the Project.";
        }, "Could not move the Task. No changes were made.");
    }

    private void CancelPendingAttachment()
    {
        _pendingAttachmentTaskId = null;
        _pendingAttachmentProjectId = null;
        Notify(nameof(NeedsAttachmentChoice));
        Notify(nameof(PreserveCategoryLabel));
        Notify(nameof(AdoptCategoryLabel));
        Notify(nameof(HasBlockingDialog));
    }

    public bool DragTask(string taskId, string targetTaskId)
    {
        var targetPosition = Backlog.IndexOf(Backlog.Single(task => task.Id == targetTaskId));
        return MoveTask(taskId, targetPosition);
    }

    public bool DragTodayTask(string taskId, string targetTaskId)
    {
        var task = _snapshot.Tasks.Single(item => item.Id == taskId);
        var target = _snapshot.Tasks.Single(item => item.Id == targetTaskId);
        if (task.TodayLane is null || task.TodayLane != target.TodayLane) return false;
        var rows = task.TodayLane == TodayLane.Planned ? TodayPlanned : TodayInProgress;
        var targetPosition = rows.IndexOf(rows.Single(row => row.Task.Id == targetTaskId));
        return MoveTodayTask(taskId, targetPosition);
    }

    public bool DragProject(string projectId, string targetProjectId) =>
        MoveProject(projectId, Projects.IndexOf(Projects.Single(project => project.Id == targetProjectId)));

    public bool DragProjectTask(string projectId, string taskId, string targetTaskId)
    {
        var project = Projects.Single(item => item.Id == projectId);
        var target = project.Tasks.SingleOrDefault(task => task.Id == targetTaskId);
        return target is not null && MoveProjectTask(projectId, taskId, project.Tasks.IndexOf(target));
    }

    public bool DragCategory(string categoryId, string targetCategoryId) =>
        MoveCategory(categoryId, CategoryGroups.IndexOf(CategoryGroups.Single(category => category.Id == targetCategoryId)));

    private void BeginProject()
    {
        _creating = true;
        _editingTask = false;
        _editingCategory = false;
        _creatingStandaloneTask = false;
        _creatingTodayTask = false;
        _editingId = null;
        SetDraft(string.Empty, string.Empty, null, _snapshot.Categories.Count == 0 ? null : _snapshot.Categories[0].Id);
    }

    private void BeginStandaloneTask()
    {
        _creating = true;
        _editingTask = true;
        _editingCategory = false;
        _creatingStandaloneTask = true;
        _creatingTodayTask = false;
        _editingId = null;
        SetDraft(string.Empty, string.Empty, null, null);
    }

    private void BeginTodayTask()
    {
        _creating = true;
        _editingTask = true;
        _editingCategory = false;
        _creatingStandaloneTask = true;
        _creatingTodayTask = true;
        _editingId = null;
        RefreshTaskContextChoices(null);
        SetDraft(string.Empty, string.Empty, null, null);
    }

    private void LoadProject(string id)
    {
        var project = _snapshot.Projects.Single(p => p.Id == id);
        _editingId = id;
        _editingTask = false;
        _editingCategory = false;
        _creating = false;
        _creatingStandaloneTask = false;
        _creatingTodayTask = false;
        SetDraft(project.Title, project.Description, project.TargetDate, project.CategoryId);
    }

    private void LoadTask(string id)
    {
        var task = _snapshot.Tasks.Single(t => t.Id == id);
        _editingId = id;
        _editingTask = true;
        _editingCategory = false;
        _creating = false;
        _creatingStandaloneTask = false;
        _creatingTodayTask = false;
        SetDraft(task.Title, task.Description, task.DueDate, task.ExplicitCategoryId, task.Participants);
        RefreshTaskContextChoices(task);
    }

    private void BeginCategory()
    {
        _creating = true;
        _editingTask = false;
        _editingCategory = true;
        _creatingStandaloneTask = false;
        _creatingTodayTask = false;
        _editingId = null;
        SetCategoryDraft(string.Empty);
    }

    private void LoadCategory(string id)
    {
        var category = _snapshot.Categories.Single(item => item.Id == id);
        _editingId = id;
        _editingTask = false;
        _editingCategory = true;
        _creating = false;
        _creatingStandaloneTask = false;
        _creatingTodayTask = false;
        SetCategoryDraft(category.Name);
    }

    private void SetCategoryDraft(string name)
    {
        _loadingDraft = true;
        try
        {
            Categories.Clear();
            _title = name;
            _description = string.Empty;
            _renderedDescription = SanitisedMarkdownDocument.Empty;
            _editingMarkdown = false;
            _date = string.Empty;
            _category = null;
            SelectedParticipants.Clear();
            _newParticipantLabel = string.Empty;
            _participantToAdd = null;
            _original = Fingerprint();
            Notify(nameof(Title));
            Notify(nameof(Description));
            Notify(nameof(RenderedDescription));
            Notify(nameof(HasRenderedDescription));
            Notify(nameof(MarkdownPreviewAutomationName));
            Notify(nameof(Date));
            Notify(nameof(Category));
            CategoryNameValidationMessage = string.Empty;
            _workTitleValidationMessage = string.Empty;
            DateValidationMessage = string.Empty;
            HasInspector = true;
            Message = string.Empty;
            ResetAutosavePresentation();
            NotifyInspectorPresentation();
        }
        finally
        {
            _title = name;
            _description = string.Empty;
            _date = string.Empty;
            _category = null;
            _loadingDraft = false;
        }
    }

    private void RefreshTaskContextChoices(TaskRecord? task)
    {
        TaskContextChoices.Clear();
        TaskContextChoices.Add(new(null, "Standalone"));
        foreach (var project in _snapshot.Projects)
            TaskContextChoices.Add(new(project.Id, project.Title));
        TaskContextTarget = TaskContextChoices.Single(choice => choice.ProjectId == task?.ProjectId);
    }

    private void RefreshCreatingTaskCategories()
    {
        var selectedId = Category?.Id;
        _loadingDraft = true;
        try
        {
            Categories.Clear();
            if (TaskContextTarget?.ProjectId is { } projectId)
            {
                var project = _snapshot.Projects.Single(item => item.Id == projectId);
                Categories.Add(new(null, $"Inherit — {CategoryName(project.CategoryId)}"));
            }
            foreach (var category in _snapshot.Categories) Categories.Add(new(category.Id, category.Name));
            Category = Categories.FirstOrDefault(item => item.Id == selectedId)
                ?? Categories.FirstOrDefault(item => item.Id is null);
        }
        finally { _loadingDraft = false; }
        Notify(nameof(Category));
        Notify(nameof(CategoryHint));
        Notify(nameof(InspectorMetaValue));
        DraftChanged();
    }

    private void SetDraft(string title, string description, DateOnly? date, string? categoryId,
        IReadOnlyList<string>? participantIds = null)
    {
        var dateText = date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
        CategoryChoice? categoryChoice = null;
        _loadingDraft = true;
        try
        {
            Categories.Clear();
            if (_editingTask && !IsStandaloneTaskDraft)
            {
                var task = _snapshot.Tasks.Single(t => t.Id == _editingId);
                var project = _snapshot.Projects.Single(p => p.Id == task.ProjectId);
                Categories.Add(new(null, $"Inherit — {CategoryName(project.CategoryId)}"));
            }
            foreach (var category in _snapshot.Categories) Categories.Add(new(category.Id, category.Name));
            _title = title;
            _description = description;
            _renderedDescription = SanitisedMarkdownRenderer.Render(description);
            _editingMarkdown = false;
            _date = dateText;
            categoryChoice = Categories.FirstOrDefault(c => c.Id == categoryId);
            _category = categoryChoice;
            SelectedParticipants.Clear();
            foreach (var participantId in participantIds ?? [])
            {
                var participant = _snapshot.Participants.Single(item => item.Id == participantId);
                SelectedParticipants.Add(new(this, participant.Id, participant.Label, participant.Id));
            }
            _newParticipantLabel = string.Empty;
            _participantToAdd = null;
            NewParticipantValidationMessage = string.Empty;
            ParticipantSelectionMessage = string.Empty;
            ParticipantAnnouncement = string.Empty;
            RefreshParticipantChoices();
            _original = Fingerprint();
            Notify(nameof(Title));
            Notify(nameof(Description));
            Notify(nameof(RenderedDescription));
            Notify(nameof(HasRenderedDescription));
            Notify(nameof(MarkdownPreviewAutomationName));
            Notify(nameof(Date));
            Notify(nameof(Category));
            Notify(nameof(NewParticipantLabel));
            Notify(nameof(ParticipantToAdd));
            Notify(nameof(CategoryHint));
            DateValidationMessage = string.Empty;
            _workTitleValidationMessage = string.Empty;
            HasInspector = true;
            Message = string.Empty;
            ResetAutosavePresentation();
            NotifyInspectorPresentation();
        }
        finally
        {
            _title = title;
            _description = description;
            _date = dateText;
            _category = categoryChoice;
            _loadingDraft = false;
        }
    }

    private void NotifyInspectorPresentation()
    {
        Notify(nameof(InspectorHeading)); Notify(nameof(TitleAutomationName)); Notify(nameof(SaveLabel)); Notify(nameof(DateLabel));
        Notify(nameof(ShowWorkInspector)); Notify(nameof(ShowCategoryInspector)); Notify(nameof(ShowCategoryDelete));
        Notify(nameof(ShowParticipants)); Notify(nameof(ShowTaskContext)); Notify(nameof(ShowTaskContextAction));
        Notify(nameof(ShowProjectSummary)); Notify(nameof(ProjectSummary));
        Notify(nameof(ShowTaskCompletionDate)); Notify(nameof(TaskCompletionDateText)); Notify(nameof(TaskCompletionDateAccessibleText));
        Notify(nameof(InspectorMetaLabel)); Notify(nameof(InspectorMetaValue));
        Notify(nameof(ShowMarkdownPreview)); Notify(nameof(ShowMarkdownEditor));
        Notify(nameof(ShowExplicitInspectorActions)); Notify(nameof(ShowAutosaveStatus));
        Notify(nameof(DecisionHeading)); Notify(nameof(DecisionBody)); Notify(nameof(DecisionSaveLabel));
        Notify(nameof(TitleValidationMessage)); Notify(nameof(HasTitleValidationError));
    }

    private void ResetAutosavePresentation()
    {
        _autosaveRevision++;
        HasAutosaveError = false;
        AutosaveStatus = _editingCategory ? string.Empty : _creating ? "Start typing to create." : "Saved";
    }

    private void RefreshRenderedDescription()
    {
        _renderedDescription = SanitisedMarkdownRenderer.Render(_description);
        Notify(nameof(RenderedDescription));
        Notify(nameof(HasRenderedDescription));
        Notify(nameof(MarkdownPreviewAutomationName));
    }

    public bool Save()
    {
        if (!HasInspector) return true;
        if (_editingCategory) return SaveCategory();
        return SaveWork();
    }

    private bool SaveWork()
    {
        if (string.IsNullOrWhiteSpace(Title))
        {
            _workTitleValidationMessage = "Enter a title.";
            Notify(nameof(TitleValidationMessage));
            Notify(nameof(HasTitleValidationError));
            Message = _workTitleValidationMessage;
            return false;
        }
        DateOnly? date = null;
        if (!string.IsNullOrWhiteSpace(Date))
        {
            if (!DateOnly.TryParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                DateValidationMessage = "Enter a valid date as YYYY-MM-DD, or leave it empty.";
                Message = DateValidationMessage;
                return false;
            }
            date = parsed;
        }
        DateValidationMessage = string.Empty;
        if (Category is null || ((!_editingTask || IsStandaloneTaskDraft) && Category.Id is null)) { Message = "Choose a category."; return false; }
        if (_editingTask) return SaveTask(date);
        return Attempt(() =>
        {
            if (_creating) _editingId = _work.CreateProject(Title, Description, Category.Id!, date).Id;
            else _work.UpdateProject(_editingId!, Title, Description, Category.Id!, date);
            _creating = false;
            _creatingStandaloneTask = false;
            ReloadAndKeepInspector();
        });
    }

    private bool SaveTask(DateOnly? date)
    {
        var participantIds = SelectedParticipants.Where(item => item.Id is not null).Select(item => item.Id!).ToArray();
        var newLabels = SelectedParticipants.Where(item => item.Id is null).Select(item => item.Label).ToArray();
        var participantChange = new ParticipantDraftChange(participantIds, newLabels);
        try
        {
            if (_creating)
                _editingId = _creatingTodayTask
                    ? _work.CreateTaskDraft(TaskContextTarget?.ProjectId, Title, Description, Category?.Id, date,
                        participantChange, TodayLane.Planned).Id
                    : _work.CreateStandaloneTask(Title, Description, Category!.Id!, date,
                        participantChange).Id;
            else
                _work.UpdateTask(_editingId!, Title, Description, Category!.Id, date,
                    participantChange);
            _creating = false;
            _creatingStandaloneTask = false;
            _creatingTodayTask = false;
            ReloadAndKeepInspector();
            return true;
        }
        catch (ArgumentException)
        {
            Message = "Check the Participant labels and selections. No changes were made.";
            return false;
        }
        catch (WorkspaceWorkException)
        {
            Message = "Could not save workspace changes. Your draft is retained. Try again.";
            return false;
        }
    }

    private void ReloadAndKeepInspector()
    {
        Reload();
        if (_editingTask && _editingId is not null)
        {
            var task = _snapshot.Tasks.Single(item => item.Id == _editingId);
            SelectedParticipants.Clear();
            foreach (var participantId in task.Participants)
            {
                var participant = _snapshot.Participants.Single(item => item.Id == participantId);
                SelectedParticipants.Add(new(this, participant.Id, participant.Label, participant.Id));
            }
            RefreshParticipantChoices();
            RefreshTaskContextChoices(task);
        }
        _original = Fingerprint();
        Message = string.Empty;
        Notify(nameof(IsDirty));
        NotifyInspectorPresentation();
    }

    private bool SaveCategory()
    {
        if (string.IsNullOrWhiteSpace(Title))
        {
            CategoryNameValidationMessage = "Enter a category name.";
            Message = CategoryNameValidationMessage;
            return false;
        }

        string categoryId;
        try
        {
            categoryId = _creating
                ? _work.CreateCategory(Title).Id
                : _work.RenameCategory(_editingId!, Title).Id;
        }
        catch (ArgumentException)
        {
            CategoryNameValidationMessage = "Enter a unique category name.";
            Message = CategoryNameValidationMessage;
            return false;
        }
        catch (WorkspaceWorkException)
        {
            Message = "Could not save workspace changes. Your draft is retained. Try again.";
            return false;
        }

        _editingId = categoryId;
        _creating = false;
        Reload();
        LoadCategory(categoryId);
        Message = "Category saved.";

        return true;
    }

    private bool Attempt(Action action)
    {
        try { action(); return true; }
        catch (WorkspaceWorkException) { Message = "Could not save workspace changes. Your draft is retained. Try again."; return false; }
        catch (ArgumentException) { Message = "The item or category is no longer available. Check your draft and try again."; return false; }
    }

    public void Cancel()
    {
        if (_creating || _editingId is null) CloseInspector();
        else if (_editingCategory) LoadCategory(_editingId);
        else if (_editingTask) LoadTask(_editingId);
        else LoadProject(_editingId);
    }
    private void CloseInspector()
    {
        HasInspector = false;
        IsEditingMarkdown = false;
        _creating = false;
        _creatingStandaloneTask = false;
        _creatingTodayTask = false;
        _editingCategory = false;
        CategoryNameValidationMessage = string.Empty;
        _workTitleValidationMessage = string.Empty;
        Notify(nameof(TitleValidationMessage));
        Notify(nameof(HasTitleValidationError));
        DateValidationMessage = string.Empty;
        Message = string.Empty;
        HasAutosaveError = false;
    }
    private void Leave()
    {
        var destination = _pendingNavigation;
        _pendingNavigation = null;
        var closesInspector = _pendingNavigationClosesInspector;
        _pendingNavigationClosesInspector = true;
        Notify(nameof(NeedsDecision));
        Notify(nameof(HasBlockingDialog));
        if (closesInspector) CloseInspector();
        destination?.Invoke();
    }
    private (string Title, string Description, string Date, string? CategoryId, string Participants) Fingerprint() =>
        (Title, Description, Date, Category?.Id,
            string.Join('\u001f', SelectedParticipants.Select(item => item.Id is null ? $"new:{item.Label}" : $"id:{item.Id}")));
    private string CategoryName(string id) => _snapshot.Categories.Single(c => c.Id == id).Name;
    private string EffectiveCategoryId(TaskRecord task) => task.ExplicitCategoryId
        ?? _snapshot.Projects.Single(project => project.Id == task.ProjectId).CategoryId;
    private void Reload()
    {
        _snapshot = _work.Read();
        RefreshParticipantChoices();
        RefreshBacklogCategories();
        foreach (var removedId in _taskRows.Keys.Except(_snapshot.Tasks.Select(task => task.Id), StringComparer.Ordinal).ToArray())
            _taskRows.Remove(removedId);
        var existing = Projects.ToDictionary(p => p.Id, StringComparer.Ordinal);
        Projects.Clear();
        for (var projectIndex = 0; projectIndex < _snapshot.Projects.Count; projectIndex++)
        {
            var project = _snapshot.Projects[projectIndex];
            var row = existing.GetValueOrDefault(project.Id) ?? new ProjectRowViewModel(this, project.Id);
            row.Refresh(project, ProjectWorkSummary.From(_snapshot, project.Id), CategoryName(project.CategoryId), _snapshot.Tasks.Where(t => t.ProjectId == project.Id).OrderBy(t => t.ProjectPosition).Select(ToTaskRow));
            row.SetPosition(projectIndex + 1, _snapshot.Projects.Count);
            Projects.Add(row);
        }
        var categoryExpansion = CategoryGroups.ToDictionary(
            category => category.Id,
            category => category.IsExpanded,
            StringComparer.Ordinal);
        CategoryGroups.Clear();
        for (var categoryIndex = 0; categoryIndex < _snapshot.Categories.Count; categoryIndex++)
        {
            var category = _snapshot.Categories[categoryIndex];
            var projectsInCategory = Projects
                .Where(project => _snapshot.Projects.Single(item => item.Id == project.Id).CategoryId == category.Id)
                .ToArray();
            var categoryProjects = projectsInCategory
                .Select((project, index) => new CategoryProjectRowViewModel(project, index == projectsInCategory.Length - 1))
                .ToArray();
            var standaloneTasks = _snapshot.Tasks
                .Where(task => task.ProjectId is null && task.ExplicitCategoryId == category.Id)
                .OrderBy(task => task.SharedPosition)
                .Select(ToTaskRow)
                .ToArray();
            CategoryGroups.Add(new(
                this,
                category,
                categoryProjects,
                standaloneTasks,
                categoryIndex + 1,
                _snapshot.Categories.Count,
                categoryExpansion.GetValueOrDefault(category.Id, true)));
        }
        var desiredBacklog = _snapshot.Tasks.Where(task => !task.IsComplete).OrderBy(t => t.SharedPosition).Select(ToTaskRow).ToArray();
        SynchroniseBacklog(desiredBacklog);
        for (var index = 0; index < Backlog.Count; index++) Backlog[index].SetPosition(index + 1, Backlog.Count);
        RefreshUpcomingGroups();
        var desiredCompleted = _snapshot.Tasks.Where(task => task.IsComplete)
            .OrderByDescending(task => task.CompletedAt).ThenBy(task => task.SharedPosition).Select(ToTaskRow).ToArray();
        Synchronise(Completed, desiredCompleted);
        SynchroniseToday(TodayPlanned, _snapshot.Tasks
            .Where(task => !task.IsComplete && task.TodayLane == TodayLane.Planned)
            .OrderBy(task => task.SharedPosition).Select(ToTaskRow).ToArray(), TodayLane.Planned);
        SynchroniseToday(TodayInProgress, _snapshot.Tasks
            .Where(task => !task.IsComplete && task.TodayLane == TodayLane.InProgress)
            .OrderBy(task => task.SharedPosition).Select(ToTaskRow).ToArray(), TodayLane.InProgress);
        var completedToday = _snapshot.Tasks
            .Where(task => task.IsComplete && task.CompletionDate == Today)
            .OrderByDescending(task => task.CompletedAt).ThenBy(task => task.SharedPosition).Select(ToTaskRow).ToArray();
        CompletedToday.Clear();
        for (var index = 0; index < completedToday.Length; index++)
            CompletedToday.Add(new(completedToday[index], index == completedToday.Length - 1));
        RefreshCompletedGroups();
        Notify(nameof(ProjectSummary));
        Notify(nameof(InspectorMetaValue));
        Notify(nameof(ShowTaskCompletionDate));
        Notify(nameof(TaskCompletionDateText));
        Notify(nameof(TaskCompletionDateAccessibleText));
        Notify(nameof(HasProjects));
        Notify(nameof(HasNoProjects));
        Notify(nameof(UpcomingCount));
        Notify(nameof(HasUpcoming));
        Notify(nameof(HasNoUpcoming));
        Notify(nameof(HasCompleted));
        Notify(nameof(HasNoCompleted));
        Notify(nameof(HasTodayTasks));
        Notify(nameof(HasNoTodayTasks));
        Notify(nameof(HasCompletedToday));
        Notify(nameof(CanChangeTaskContext));
        Notify(nameof(TaskContextActionLabel));
    }

    private void RefreshParticipantChoices()
    {
        var selectedId = ParticipantToAdd?.Id;
        var selectedNew = ParticipantToAdd?.IsNew == true;
        AvailableParticipants.Clear();
        foreach (var participant in _snapshot.Participants)
            AvailableParticipants.Add(new(participant.Id, participant.Label));
        AvailableParticipants.Add(ParticipantChoice.New);
        ParticipantToAdd = selectedNew
            ? AvailableParticipants.Single(item => item.IsNew)
            : AvailableParticipants.FirstOrDefault(item => item.Id == selectedId && !item.IsNew);
    }

    private void RefreshAvailableParticipants() => RefreshParticipantChoices();

    public void RefreshFromStore() => Reload();
    private TaskRowViewModel ToTaskRow(TaskRecord task)
    {
        var inherited = task.ProjectId is not null && task.ExplicitCategoryId is null;
        var categoryId = task.ExplicitCategoryId
            ?? _snapshot.Projects.Single(project => project.Id == task.ProjectId).CategoryId;
        if (!_taskRows.TryGetValue(task.Id, out var row))
        {
            row = new(this, task.Id);
            _taskRows.Add(task.Id, row);
        }
        row.Refresh(task, CategoryName(categoryId), task.ProjectId is null ? "standalone" : inherited ? "inherited" : "override", Today);
        return row;
    }

    private void RefreshBacklogCategories()
    {
        var selectedId = BacklogQuickCategory?.Id;
        BacklogCategories.Clear();
        foreach (var category in _snapshot.Categories) BacklogCategories.Add(new(category.Id, category.Name));
        BacklogQuickCategory = BacklogCategories.FirstOrDefault(category => category.Id == selectedId);
    }

    private void SynchroniseBacklog(TaskRowViewModel[] desired) => Synchronise(Backlog, desired);

    private static void Synchronise(ObservableCollection<TaskRowViewModel> collection, TaskRowViewModel[] desired)
    {
        for (var index = collection.Count - 1; index >= 0; index--)
            if (!desired.Contains(collection[index])) collection.RemoveAt(index);
        for (var index = 0; index < desired.Length; index++)
        {
            var current = collection.IndexOf(desired[index]);
            if (current < 0) collection.Insert(index, desired[index]);
            else if (current != index) collection.Move(current, index);
        }
    }

    private void SynchroniseToday(
        ObservableCollection<TodayTaskRowViewModel> collection,
        TaskRowViewModel[] desired,
        TodayLane lane)
    {
        var existing = collection.ToDictionary(row => row.Task.Id, StringComparer.Ordinal);
        collection.Clear();
        for (var index = 0; index < desired.Length; index++)
        {
            var row = existing.GetValueOrDefault(desired[index].Id)
                ?? new TodayTaskRowViewModel(this, desired[index], lane);
            row.Refresh(desired[index], index, desired.Length);
            collection.Add(row);
        }
    }

    private void RefreshCompletedGroups()
    {
        CompletedGroups.Clear();
        foreach (var group in Completed.GroupBy(row => CompletedGroupFor(
                     _snapshot.Tasks.Single(task => task.Id == row.Id).CompletionDate!.Value))
                 .OrderByDescending(group => group.Key.Start))
            CompletedGroups.Add(new(group.Key.Heading, group.ToArray()));
    }

    private void RefreshUpcomingGroups()
    {
        UpcomingGroups.Clear();
        var tasks = UpcomingTaskProjection.Create(_snapshot.Tasks, Today);
        var overdue = tasks.Where(task => task.DueDate < Today).Select(ToTaskRow).ToArray();
        if (overdue.Length > 0) UpcomingGroups.Add(new("Overdue", overdue));
        foreach (var group in tasks.Where(task => task.DueDate >= Today).GroupBy(task => task.DueDate!.Value))
        {
            var heading = group.Key == Today
                ? "Today"
                : group.Key.DayNumber == Today.DayNumber + 1
                    ? "Tomorrow"
                    : group.Key.ToString("dddd, d MMMM yyyy", CultureInfo.InvariantCulture);
            UpcomingGroups.Add(new(heading, group.Select(ToTaskRow).ToArray()));
        }
    }

    private (DateOnly Start, string Heading) CompletedGroupFor(DateOnly completionDate)
    {
        var age = Today.DayNumber - completionDate.DayNumber;
        if (age < 0)
            return (completionDate, completionDate.ToString("dddd, d MMMM yyyy", CultureInfo.InvariantCulture));
        if (age is >= 0 and <= 2)
        {
            var heading = age switch
            {
                0 => "Today",
                1 => "Yesterday",
                _ => completionDate.ToString("dddd, d MMMM yyyy", CultureInfo.InvariantCulture),
            };
            return (completionDate, heading);
        }

        var daysSinceMonday = ((int)completionDate.DayOfWeek + 6) % 7;
        var weekStart = completionDate.AddDays(-daysSinceMonday);
        return (weekStart, $"Week of {weekStart.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}");
    }

    private bool MoveTask(string taskId, int targetPosition)
    {
        if (IsDirty && (_editingCategory || !FlushPendingAutosave())) return false;
        var currentPosition = Backlog.IndexOf(Backlog.Single(task => task.Id == taskId));
        if (currentPosition < 0 || Backlog.Count == 0) return false;
        targetPosition = Math.Clamp(targetPosition, 0, Backlog.Count - 1);
        if (!Attempt(() =>
            {
                var change = _work.MoveTaskInSharedOrder(taskId, targetPosition);
                Reload();
                var task = Backlog.Single(row => row.Id == taskId);
                ReorderAnnouncement = $"Moved {task.Title} to position {change.Position} of {change.Count} in Backlog.";
                Message = ReorderAnnouncement;
            }, "Could not reorder the Task. The Backlog order was not changed.")) return false;
        return true;
    }

    internal void ToggleToday(string taskId)
    {
        var task = _snapshot.Tasks.Single(item => item.Id == taskId);
        Action action = () => ApplyTodayLane(taskId, task.TodayLane is null ? TodayLane.Planned : null);
        if (IsDirty && (_editingCategory || !FlushPendingAutosave()))
        {
            _pendingNavigation = action;
            _pendingNavigationClosesInspector = false;
            Notify(nameof(NeedsDecision));
            Notify(nameof(HasBlockingDialog));
            return;
        }
        action();
    }

    private void ApplyTodayLane(string taskId, TodayLane? lane)
    {
        var task = _snapshot.Tasks.Single(item => item.Id == taskId);
        var source = task.TodayLane == TodayLane.Planned ? TodayPlanned : TodayInProgress;
        var sourceIndex = source.IndexOf(source.FirstOrDefault(row => row.Task.Id == taskId)!);
        if (!Attempt(() =>
            {
                _work.SetTaskTodayLane(taskId, lane);
                Reload();
                var title = _snapshot.Tasks.Single(item => item.Id == taskId).Title;
                TodayAnnouncement = lane switch
                {
                    TodayLane.Planned => $"Added {title} to Today in Planned.",
                    TodayLane.InProgress => $"Moved {title} to In progress.",
                    _ => $"Removed {title} from Today.",
                };
                if (lane is not null || !_todayActive)
                    TodayFocusAutomationId = $"task-today-{taskId}";
                else
                {
                    var remaining = task.TodayLane == TodayLane.Planned ? TodayPlanned : TodayInProgress;
                    TodayFocusAutomationId = remaining.Count == 0
                        ? "today-clear"
                        : remaining[Math.Min(Math.Max(sourceIndex, 0), remaining.Count - 1)].Task.TodayAutomationId;
                }
                Message = TodayAnnouncement;
            }, "Could not change Today membership.")) return;
    }

    internal void MoveToOtherTodayLane(string taskId)
    {
        var task = _snapshot.Tasks.Single(item => item.Id == taskId);
        if (task.TodayLane is null) return;
        ResolveDraftBeforeAction(() => ApplyTodayLane(taskId,
            task.TodayLane == TodayLane.Planned ? TodayLane.InProgress : TodayLane.Planned));
    }

    internal bool MoveTodayTask(string taskId, int targetPosition)
    {
        if (NeedsDecision) return false;
        var task = _snapshot.Tasks.Single(item => item.Id == taskId);
        if (task.TodayLane is null) return false;
        var laneRows = task.TodayLane == TodayLane.Planned ? TodayPlanned : TodayInProgress;
        if (laneRows.Count == 0) return false;
        targetPosition = Math.Clamp(targetPosition, 0, laneRows.Count - 1);
        var moved = false;
        Action action = () => moved = Attempt(() =>
        {
            var change = _work.MoveTaskInTodayLane(taskId, targetPosition);
            Reload();
            var title = _snapshot.Tasks.Single(item => item.Id == taskId).Title;
            var laneName = change.Lane == TodayLane.Planned ? "Planned" : "In progress";
            TodayAnnouncement = $"Moved {title} to position {change.Position} of {change.Count} in {laneName}.";
            TodayFocusAutomationId = $"today-reorder-{taskId}";
            Message = TodayAnnouncement;
        }, "Could not reorder the Today lane.");
        ResolveDraftBeforeAction(action);
        return moved;
    }

    private void ClearToday()
    {
        ResolveDraftBeforeAction(() => Attempt(() =>
        {
            var count = _work.ClearToday();
            Reload();
            TodayAnnouncement = count == 1 ? "Cleared 1 Task from Today." : $"Cleared {count} Tasks from Today.";
            TodayFocusAutomationId = "today-clear";
            Message = TodayAnnouncement;
        }, "Could not clear Today."));
    }

    private bool Attempt(Action action, string failureMessage)
    {
        try { action(); return true; }
        catch (WorkspaceWorkException) { Message = failureMessage; return false; }
        catch (ArgumentException) { Message = failureMessage; return false; }
    }

    internal void MoveUp(string taskId) => MoveTask(taskId, Backlog.IndexOf(Backlog.Single(task => task.Id == taskId)) - 1);
    internal void MoveDown(string taskId) => MoveTask(taskId, Backlog.IndexOf(Backlog.Single(task => task.Id == taskId)) + 1);
    internal void MoveToTop(string taskId) => MoveTask(taskId, 0);
    internal void MoveToBottom(string taskId) => MoveTask(taskId, Backlog.Count - 1);
    internal void ToggleCompletion(string taskId)
    {
        var task = _snapshot.Tasks.Single(item => item.Id == taskId);
        Action action = () => ApplyCompletion(taskId, !task.IsComplete);
        var removesDraftFromCurrentView = (!task.IsComplete && _backlogActive)
            || (!task.IsComplete && _upcomingActive)
            || (task.IsComplete && _completedActive)
            || (task.IsComplete && _todayActive && task.CompletionDate == Today);
        if (IsDirty && _editingCategory)
        {
            _pendingNavigation = action;
            _pendingNavigationClosesInspector = removesDraftFromCurrentView && _editingTask && _editingId == taskId;
            Notify(nameof(NeedsDecision));
            Notify(nameof(HasBlockingDialog));
            return;
        }
        if (IsDirty && !FlushPendingAutosave())
        {
            _pendingNavigation = action;
            _pendingNavigationClosesInspector = removesDraftFromCurrentView && _editingTask && _editingId == taskId;
            Notify(nameof(NeedsDecision));
            Notify(nameof(HasBlockingDialog));
            return;
        }
        if (removesDraftFromCurrentView && _editingTask && _editingId == taskId) CloseInspector();
        action();
    }

    private void ApplyCompletion(string taskId, bool complete)
    {
        var backlogIndex = Backlog.IndexOf(Backlog.FirstOrDefault(row => row.Id == taskId)!);
        var upcomingRows = UpcomingGroups.SelectMany(group => group.Rows).ToArray();
        var upcomingIndex = Array.FindIndex(upcomingRows, row => row.Task.Id == taskId);
        var completedIndex = Completed.IndexOf(Completed.FirstOrDefault(row => row.Id == taskId)!);
        var completedTodayIndex = CompletedToday.IndexOf(CompletedToday.FirstOrDefault(row => row.Task.Id == taskId)!);
        if (!Attempt(() =>
            {
                if (complete) _work.CompleteTask(taskId); else _work.ReopenTask(taskId);
                Reload();
                if (complete && _backlogActive)
                {
                    CompletionFocusAutomationId = Backlog.Count == 0
                        ? "backlog-quick-title"
                        : Backlog[Math.Min(Math.Max(backlogIndex, 0), Backlog.Count - 1)].CompletionAutomationId;
                }
                else if (complete && _upcomingActive)
                {
                    var remainingUpcoming = UpcomingGroups.SelectMany(group => group.Rows).ToArray();
                    CompletionFocusAutomationId = remainingUpcoming.Length == 0
                        ? "navigation-upcoming"
                        : remainingUpcoming[Math.Min(Math.Max(upcomingIndex, 0), remainingUpcoming.Length - 1)]
                            .Task.CompletionAutomationId;
                }
                else if (!complete && _completedActive)
                {
                    CompletionFocusAutomationId = Completed.Count == 0
                        ? "navigation-completed"
                        : Completed[Math.Min(Math.Max(completedIndex, 0), Completed.Count - 1)].CompletionAutomationId;
                }
                else if (!complete && _todayActive)
                {
                    CompletionFocusAutomationId = CompletedToday.Count == 0
                        ? "navigation-today"
                        : CompletedToday[Math.Min(Math.Max(completedTodayIndex, 0), CompletedToday.Count - 1)].Task.CompletionAutomationId;
                }
                else
                {
                    CompletionFocusAutomationId = $"task-completion-{taskId}";
                }
                Message = complete ? "Task completed." : "Task reopened.";
            }, complete ? "Could not complete the Task." : "Could not reopen the Task.")) return;
    }

    internal bool MoveProject(string projectId, int targetPosition)
    {
        if (IsDirty && (_editingCategory || !FlushPendingAutosave())) return false;
        if (Projects.Count == 0) return false;
        targetPosition = Math.Clamp(targetPosition, 0, Projects.Count - 1);
        if (!Attempt(() =>
            {
                var change = _work.MoveProject(projectId, targetPosition);
                Reload();
                var project = Projects.Single(row => row.Id == projectId);
                ReorderAnnouncement = $"Moved {project.Title} to position {change.Position} of {change.Count} in Projects.";
                Message = ReorderAnnouncement;
                ReorderFocusAutomationId = project.ReorderAutomationId;
            }, "Could not reorder the Project.")) return false;
        return true;
    }

    internal bool MoveCategory(string categoryId, int targetPosition)
    {
        if (IsDirty && (_editingCategory || !FlushPendingAutosave())) return false;
        if (CategoryGroups.Count == 0) return false;
        targetPosition = Math.Clamp(targetPosition, 0, CategoryGroups.Count - 1);
        if (!Attempt(() =>
            {
                var change = _work.MoveCategory(categoryId, targetPosition);
                Reload();
                var category = CategoryGroups.Single(row => row.Id == categoryId);
                ReorderAnnouncement = $"Moved {category.Name} to position {change.Position} of {change.Count} in Categories.";
                Message = ReorderAnnouncement;
                ReorderFocusAutomationId = category.ReorderAutomationId;
            }, "Could not reorder the Category.")) return false;
        return true;
    }

    internal bool MoveProjectTask(string projectId, string taskId, int targetPosition)
    {
        if (IsDirty && (_editingCategory || !FlushPendingAutosave())) return false;
        var count = Projects.Single(project => project.Id == projectId).Tasks.Count;
        if (count == 0) return false;
        targetPosition = Math.Clamp(targetPosition, 0, count - 1);
        if (!Attempt(() =>
            {
                var change = _work.MoveTaskInProject(projectId, taskId, targetPosition);
                Reload();
                var task = _taskRows[taskId];
                ReorderAnnouncement = $"Moved {task.Title} to position {change.Position} of {change.Count} in project {Projects.Single(project => project.Id == projectId).Title}.";
                Message = ReorderAnnouncement;
                ReorderFocusAutomationId = task.ProjectReorderAutomationId;
            }, "Could not reorder the Project Task.")) return false;
        return true;
    }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed record CategoryChoice(string? Id, string Name);
public sealed record TaskContextChoice(string? ProjectId, string Name);
public sealed record ParticipantChoice(string? Id, string Label, bool IsNew = false)
{
    public static ParticipantChoice New { get; } = new(null, "New participant…", true);
}

public sealed class AutosaveRequestEventArgs(long revision) : EventArgs
{
    public long Revision { get; } = revision;
}

public sealed class ParticipantDraftViewModel : INotifyPropertyChanged
{
    private readonly ProjectCaptureViewModel _owner;
    private string _label;
    public ParticipantDraftViewModel(ProjectCaptureViewModel owner, string? id, string label, string automationKey)
    {
        _owner = owner;
        Id = id;
        _label = label;
        RemoveAutomationId = $"participant-remove-{automationKey}";
        RemoveCommand = new(() => _owner.RemoveParticipant(this));
    }
    public string? Id { get; }
    public string Label => _label;
    public string RemoveAccessibleName => $"Remove {Label} from Task";
    public string RemoveAutomationId { get; }
    public RelayCommand RemoveCommand { get; }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void RefreshLabel(string label)
    {
        _label = label;
        PropertyChanged?.Invoke(this, new(nameof(Label)));
        PropertyChanged?.Invoke(this, new(nameof(RemoveAccessibleName)));
    }
}

public sealed record CategoryProjectRowViewModel(ProjectRowViewModel Project, bool IsLast);
public sealed record CategoryTaskRowViewModel(TaskRowViewModel Task, bool IsLast);

public sealed class CategoryGroupViewModel : INotifyPropertyChanged
{
    private readonly ProjectCaptureViewModel _owner;
    private readonly int _position;
    private readonly int _count;
    private bool _isExpanded;

    public CategoryGroupViewModel(
        ProjectCaptureViewModel owner,
        WorkspaceCategory category,
        IReadOnlyList<CategoryProjectRowViewModel> projects,
        IReadOnlyList<TaskRowViewModel> standaloneTasks,
        int position,
        int count,
        bool isExpanded)
    {
        _owner = owner;
        Id = category.Id;
        Name = category.Name;
        _position = position;
        _count = count;
        _isExpanded = isExpanded;
        Projects = projects;
        StandaloneTasks = standaloneTasks
            .Select((task, index) => new CategoryTaskRowViewModel(task, index == standaloneTasks.Count - 1))
            .ToArray();
        SelectCommand = new(() => owner.SelectCategory(Id));
        MoveUpCommand = new(() => owner.MoveCategory(Id, _position - 2));
        MoveDownCommand = new(() => owner.MoveCategory(Id, _position));
        MoveToTopCommand = new(() => owner.MoveCategory(Id, 0));
        MoveToBottomCommand = new(() => owner.MoveCategory(Id, _count - 1));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id { get; }
    public string Name { get; }
    public int ProjectCount => Projects.Count;
    public int StandaloneTaskCount => StandaloneTasks.Count;
    public bool HasProjects => ProjectCount > 0;
    public bool HasStandaloneTasks => StandaloneTaskCount > 0;
    public bool HasNoWork => !HasProjects && !HasStandaloneTasks;
    public IReadOnlyList<CategoryProjectRowViewModel> Projects { get; }
    public IReadOnlyList<CategoryTaskRowViewModel> StandaloneTasks { get; }
    public string PositionText => $"{_position} of {_count}";
    public string ReorderAutomationId => $"category-reorder-{Id}";
    public string SelectionAutomationId => $"category-selection-{Id}";
    public string ReorderAccessibleName => $"Reorder {Name} in Categories";
    public string MoveUpAccessibleName => $"Move {Name} up in Categories";
    public string MoveDownAccessibleName => $"Move {Name} down in Categories";
    public string MoveToTopAccessibleName => $"Move {Name} to top of Categories";
    public string MoveToBottomAccessibleName => $"Move {Name} to bottom of Categories";
    public string SelectionAccessibleName => $"Edit {Name} category";
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new(nameof(IsExpanded)));
            PropertyChanged?.Invoke(this, new(nameof(ExpansionAccessibleName)));
        }
    }
    public string ExpansionAccessibleName => $"{(IsExpanded ? "Collapse" : "Expand")} {Name} category";
    public string Summary => $"{ProjectCount} {(ProjectCount == 1 ? "project" : "projects")} · {StandaloneTaskCount} standalone {(StandaloneTaskCount == 1 ? "task" : "tasks")}";
    public RelayCommand SelectCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public RelayCommand MoveToTopCommand { get; }
    public RelayCommand MoveToBottomCommand { get; }
}

public sealed record CompletedTaskGroupViewModel(string Heading, IReadOnlyList<TaskRowViewModel> Tasks)
{
    public IReadOnlyList<CompletedTaskRowViewModel> Rows { get; } = Tasks
        .Select((task, index) => new CompletedTaskRowViewModel(task, index == Tasks.Count - 1))
        .ToArray();
}

public sealed record CompletedTaskRowViewModel(TaskRowViewModel Task, bool IsLast);
public sealed record UpcomingTaskGroupViewModel(string Heading, IReadOnlyList<TaskRowViewModel> Tasks)
{
    public IReadOnlyList<UpcomingTaskRowViewModel> Rows { get; } = Tasks
        .Select((task, index) => new UpcomingTaskRowViewModel(task, index == Tasks.Count - 1))
        .ToArray();
}

public sealed record UpcomingTaskRowViewModel(TaskRowViewModel Task, bool IsLast);
public sealed class TodayTaskRowViewModel
{
    private readonly ProjectCaptureViewModel _owner;
    private readonly TodayLane _lane;
    private int _position;
    private int _count;

    public TodayTaskRowViewModel(ProjectCaptureViewModel owner, TaskRowViewModel task, TodayLane lane)
    {
        _owner = owner;
        Task = task;
        _lane = lane;
        MoveUpCommand = new(() => owner.MoveTodayTask(task.Id, _position - 1));
        MoveDownCommand = new(() => owner.MoveTodayTask(task.Id, _position + 1));
        MoveToTopCommand = new(() => owner.MoveTodayTask(task.Id, 0));
        MoveToBottomCommand = new(() => owner.MoveTodayTask(task.Id, _count - 1));
        MoveToOtherLaneCommand = new(() => owner.MoveToOtherTodayLane(task.Id));
    }

    public TaskRowViewModel Task { get; private set; }
    public bool IsLast => _position == _count - 1;
    public string PositionText => $"{_position + 1} of {_count}";
    public string ReorderAutomationId => $"today-reorder-{Task.Id}";
    public string ReorderAccessibleName => $"Reorder {Task.Title} in {LaneName}";
    public string MoveUpAccessibleName => $"Move {Task.Title} up in {LaneName}";
    public string MoveDownAccessibleName => $"Move {Task.Title} down in {LaneName}";
    public string MoveToTopAccessibleName => $"Move {Task.Title} to top of {LaneName}";
    public string MoveToBottomAccessibleName => $"Move {Task.Title} to bottom of {LaneName}";
    public string MoveToOtherLaneLabel => _lane == TodayLane.Planned ? "Start" : "Move to planned";
    public string MoveToOtherLaneAccessibleName => _lane == TodayLane.Planned
        ? $"Move {Task.Title} to In progress"
        : $"Move {Task.Title} to Planned";
    public bool IsPlanned => _lane == TodayLane.Planned;
    public bool IsInProgress => _lane == TodayLane.InProgress;
    public string ReorderHelpText => _lane == TodayLane.Planned
        ? "Drag within Planned, or activate for keyboard reorder actions."
        : "Drag within In progress, or activate for keyboard reorder actions.";
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public RelayCommand MoveToTopCommand { get; }
    public RelayCommand MoveToBottomCommand { get; }
    public RelayCommand MoveToOtherLaneCommand { get; }
    private string LaneName => _lane == TodayLane.Planned ? "Planned" : "In progress";

    public void Refresh(TaskRowViewModel task, int position, int count)
    {
        Task = task;
        _position = position;
        _count = count;
    }
}

public sealed class TaskRowViewModel : INotifyPropertyChanged
{
    private readonly ProjectCaptureViewModel _owner;
    private string _title = string.Empty;
    private string _categoryName = string.Empty;
    private string _categoryDisplay = string.Empty;
    private int _position;
    private int _count;
    private bool _isComplete;
    private string? _projectId;
    private string _dateText = string.Empty;
    private string _dateAccessibleText = string.Empty;
    private string _completionDateText = string.Empty;
    private int _projectPosition;
    private int _projectCount;
    private TodayLane? _todayLane;

    public TaskRowViewModel(ProjectCaptureViewModel owner, string id)
    {
        _owner = owner;
        Id = id;
        SelectCommand = new(() => owner.SelectTask(id));
        MoveUpCommand = new(() => owner.MoveUp(id));
        MoveDownCommand = new(() => owner.MoveDown(id));
        MoveToTopCommand = new(() => owner.MoveToTop(id));
        MoveToBottomCommand = new(() => owner.MoveToBottom(id));
        ToggleCompletionCommand = new(() => owner.ToggleCompletion(id));
        ToggleTodayCommand = new(() => owner.ToggleToday(id));
        MoveProjectTaskUpCommand = new(() => MoveInProject(_projectPosition - 1));
        MoveProjectTaskDownCommand = new(() => MoveInProject(_projectPosition + 1));
        MoveProjectTaskToTopCommand = new(() => MoveInProject(0));
        MoveProjectTaskToBottomCommand = new(() => MoveInProject(_projectCount - 1));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id { get; }
    public string? ProjectId => _projectId;
    public string Title => _title;
    public string CategoryName => _categoryName;
    public string CategoryDisplay => _categoryDisplay;
    public string PositionText => $"{_position} of {_count}";
    public bool IsLast => _position == _count;
    public string ReorderAccessibleName => $"Reorder {Title} in Backlog";
    public string ReorderAutomationId => $"backlog-reorder-{Id}";
    public string CompletionAutomationId => $"task-completion-{Id}";
    public string CompletionAccessibleName => $"{(IsComplete ? "Reopen" : "Complete")} {Title}";
    public bool IsComplete => _isComplete;
    public bool IsToday => _todayLane is not null;
    public string TodayAutomationId => $"task-today-{Id}";
    public string TodayAccessibleName => $"{(IsToday ? "Remove" : "Add")} {Title} {(IsToday ? "from" : "to")} Today";
    public string DateText => _dateText;
    public string DateAccessibleText => _dateAccessibleText;
    public string CompletionDateText => _completionDateText;
    public bool HasCompletionDate => !string.IsNullOrEmpty(CompletionDateText);
    public string ProjectReorderAccessibleName => $"Reorder {Title} in project";
    public string ProjectReorderAutomationId => $"project-task-reorder-{Id}";
    public bool IsProjectLast => _projectCount > 0 && _projectPosition == _projectCount - 1;
    public string MoveProjectTaskUpAccessibleName => $"Move {Title} up in project";
    public string MoveProjectTaskDownAccessibleName => $"Move {Title} down in project";
    public string MoveProjectTaskToTopAccessibleName => $"Move {Title} to top of project";
    public string MoveProjectTaskToBottomAccessibleName => $"Move {Title} to bottom of project";
    public string MoveUpAccessibleName => $"Move {Title} up in Backlog";
    public string MoveDownAccessibleName => $"Move {Title} down in Backlog";
    public string MoveToTopAccessibleName => $"Move {Title} to top of Backlog";
    public string MoveToBottomAccessibleName => $"Move {Title} to bottom of Backlog";
    public RelayCommand SelectCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public RelayCommand MoveToTopCommand { get; }
    public RelayCommand MoveToBottomCommand { get; }
    public RelayCommand ToggleCompletionCommand { get; }
    public RelayCommand ToggleTodayCommand { get; }
    public RelayCommand MoveProjectTaskUpCommand { get; }
    public RelayCommand MoveProjectTaskDownCommand { get; }
    public RelayCommand MoveProjectTaskToTopCommand { get; }
    public RelayCommand MoveProjectTaskToBottomCommand { get; }

    public void Refresh(TaskRecord task, string categoryName, string categoryBehaviour, DateOnly today)
    {
        _title = task.Title;
        _categoryName = categoryName;
        _categoryDisplay = $"{categoryName} · {categoryBehaviour}";
        _projectId = task.ProjectId;
        _isComplete = task.IsComplete;
        _todayLane = task.TodayLane;
        _dateText = WorkDatePresentation.Relative(task.DueDate, today);
        _dateAccessibleText = WorkDatePresentation.Accessible(task.DueDate, "Due");
        _completionDateText = task.CompletionDate is { } completionDate ? $"Completed {completionDate:d MMM yyyy}" : string.Empty;
        Notify(nameof(Title));
        Notify(nameof(CategoryName));
        Notify(nameof(CategoryDisplay));
        Notify(nameof(ReorderAccessibleName));
        Notify(nameof(MoveUpAccessibleName));
        Notify(nameof(MoveDownAccessibleName));
        Notify(nameof(MoveToTopAccessibleName));
        Notify(nameof(MoveToBottomAccessibleName));
        Notify(nameof(CompletionAccessibleName));
        Notify(nameof(IsComplete));
        Notify(nameof(IsToday));
        Notify(nameof(TodayAccessibleName));
        Notify(nameof(DateText));
        Notify(nameof(DateAccessibleText));
        Notify(nameof(CompletionDateText));
        Notify(nameof(HasCompletionDate));
        Notify(nameof(ProjectReorderAccessibleName));
        Notify(nameof(MoveProjectTaskUpAccessibleName));
        Notify(nameof(MoveProjectTaskDownAccessibleName));
        Notify(nameof(MoveProjectTaskToTopAccessibleName));
        Notify(nameof(MoveProjectTaskToBottomAccessibleName));
    }

    public void SetProjectPosition(int position, int count)
    {
        _projectPosition = position - 1;
        _projectCount = count;
        Notify(nameof(IsProjectLast));
    }

    private void MoveInProject(int targetPosition)
    {
        if (_projectId is not null) _owner.MoveProjectTask(_projectId, Id, targetPosition);
    }

    public void SetPosition(int position, int count)
    {
        _position = position;
        _count = count;
        Notify(nameof(PositionText));
        Notify(nameof(IsLast));
    }

    private void Notify(string propertyName) => PropertyChanged?.Invoke(this, new(propertyName));
}

public sealed class ProjectRowViewModel : INotifyPropertyChanged
{
    private readonly ProjectCaptureViewModel _owner;
    private bool _isExpanded = true;
    private string _quickTitle = string.Empty;
    private int _position;
    private int _count;
    public ProjectRowViewModel(ProjectCaptureViewModel owner, string id)
    {
        _owner = owner;
        Id = id;
        SelectCommand = new(() => owner.SelectProject(id));
        MoveUpCommand = new(() => owner.MoveProject(id, _position - 2));
        MoveDownCommand = new(() => owner.MoveProject(id, _position));
        MoveToTopCommand = new(() => owner.MoveProject(id, 0));
        MoveToBottomCommand = new(() => owner.MoveProject(id, _count - 1));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id { get; }
    public string Title { get; private set; } = string.Empty;
    public string Summary { get; private set; } = string.Empty;
    public string Status { get; private set; } = string.Empty;
    public string ProgressText { get; private set; } = string.Empty;
    public string AccessibleStatus => $"{Status}, {ProgressText}";
    public string CategoryName { get; private set; } = string.Empty;
    public string TargetText { get; private set; } = string.Empty;
    public string TargetAccessibleText { get; private set; } = string.Empty;
    public bool IsOverdue { get; private set; }
    public bool IsNotStarted => Status == "Not started";
    public bool IsInProgress => Status == "In progress";
    public bool IsComplete => Status == "Complete";
    public string OverdueText => IsOverdue ? "Overdue" : string.Empty;
    public string CompletionDateText { get; private set; } = string.Empty;
    public bool HasCompletionDate => !string.IsNullOrEmpty(CompletionDateText);
    public string ReorderAccessibleName => $"Reorder {Title} in Projects";
    public string ReorderAutomationId => $"project-reorder-{Id}";
    public string MoveUpAccessibleName => $"Move {Title} up in Projects";
    public string MoveDownAccessibleName => $"Move {Title} down in Projects";
    public string MoveToTopAccessibleName => $"Move {Title} to top of Projects";
    public string MoveToBottomAccessibleName => $"Move {Title} to bottom of Projects";
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            Notify(nameof(IsExpanded));
            Notify(nameof(ExpansionAccessibleName));
        }
    }
    public string ExpansionAccessibleName => $"{(IsExpanded ? "Collapse" : "Expand")} {Title}";
    public string QuickTitle { get => _quickTitle; set { _quickTitle = value; PropertyChanged?.Invoke(this, new(nameof(QuickTitle))); } }
    public RelayCommand SelectCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public RelayCommand MoveToTopCommand { get; }
    public RelayCommand MoveToBottomCommand { get; }
    public ObservableCollection<TaskRowViewModel> Tasks { get; } = [];
    public bool Submit() { if (!_owner.QuickAdd(Id, QuickTitle)) return false; QuickTitle = string.Empty; return true; }
    public void Refresh(ProjectRecord project, ProjectWorkSummary summary, string category, IEnumerable<TaskRowViewModel> tasks)
    {
        Title = project.Title;
        Status = summary.Status;
        ProgressText = $"{summary.CompletedCount}/{summary.TaskCount} tasks";
        CategoryName = category;
        TargetText = WorkDatePresentation.Relative(project.TargetDate, _owner.Today);
        TargetAccessibleText = WorkDatePresentation.Accessible(project.TargetDate, "Target");
        IsOverdue = WorkDatePresentation.IsOverdue(project, summary, _owner.Today);
        CompletionDateText = summary.CompletionDate is { } completionDate ? $"Completed {completionDate:d MMM yyyy}" : string.Empty;
        Tasks.Clear();
        foreach (var task in tasks) Tasks.Add(task);
        for (var index = 0; index < Tasks.Count; index++) Tasks[index].SetProjectPosition(index + 1, Tasks.Count);
        Summary = $"{summary.Status} · {summary.CompletedCount} of {summary.TaskCount} Tasks · {category}"
            + (string.IsNullOrEmpty(TargetText) ? string.Empty : $" · {TargetText}");
        PropertyChanged?.Invoke(this, new(nameof(Title)));
        PropertyChanged?.Invoke(this, new(nameof(Summary)));
        PropertyChanged?.Invoke(this, new(nameof(Status)));
        PropertyChanged?.Invoke(this, new(nameof(IsNotStarted)));
        PropertyChanged?.Invoke(this, new(nameof(IsInProgress)));
        PropertyChanged?.Invoke(this, new(nameof(IsComplete)));
        PropertyChanged?.Invoke(this, new(nameof(ProgressText)));
        PropertyChanged?.Invoke(this, new(nameof(AccessibleStatus)));
        PropertyChanged?.Invoke(this, new(nameof(CategoryName)));
        PropertyChanged?.Invoke(this, new(nameof(TargetText)));
        PropertyChanged?.Invoke(this, new(nameof(TargetAccessibleText)));
        PropertyChanged?.Invoke(this, new(nameof(IsOverdue)));
        PropertyChanged?.Invoke(this, new(nameof(OverdueText)));
        PropertyChanged?.Invoke(this, new(nameof(CompletionDateText)));
        PropertyChanged?.Invoke(this, new(nameof(HasCompletionDate)));
        PropertyChanged?.Invoke(this, new(nameof(ReorderAccessibleName)));
        PropertyChanged?.Invoke(this, new(nameof(MoveUpAccessibleName)));
        PropertyChanged?.Invoke(this, new(nameof(MoveDownAccessibleName)));
        PropertyChanged?.Invoke(this, new(nameof(MoveToTopAccessibleName)));
        PropertyChanged?.Invoke(this, new(nameof(MoveToBottomAccessibleName)));
        Notify(nameof(ExpansionAccessibleName));
    }

    public void SetPosition(int position, int count)
    {
        _position = position;
        _count = count;
    }

    private void Notify(string name) => PropertyChanged?.Invoke(this, new(name));
}
