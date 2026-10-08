using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using DotOrbit.Core.Workspaces;
using DotOrbit.Markdown;

namespace DotOrbit.Desktop.ViewModels;

public sealed class ProjectCaptureViewModel : INotifyPropertyChanged
{
    private readonly IWorkspaceWork _work;
    private readonly TimeProvider _timeProvider;
    private readonly IInspectorSaveWriter _inspectorSaveWriter;
    private readonly Dictionary<string, TaskRowViewModel> _taskRows = new(StringComparer.Ordinal);
    private WorkspaceWorkSnapshot _snapshot = new([], [], []);
    private Action? _pendingNavigation;
    private TaskCompletionSource<bool>? _pendingDeferredActionCompletion;
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
    private string _categoryColourKey = IdentityColourPalette.DefaultKey;
    private string _projectColourKey = IdentityColourPalette.KeyForProjectPosition(0);
    private (string Title, string Description, string Date, string? CategoryId, string CategoryColourKey, string ProjectColourKey, string Participants) _original;
    private string _message = string.Empty;
    private bool _hasInspector;
    private string _backlogQuickTitle = string.Empty;
    private CategoryChoice? _backlogQuickCategory;
    private string _reorderAnnouncement = string.Empty;
    private bool _backlogActive;
    private bool _upcomingActive;
    private bool _completedActive;
    private bool _archiveActive;
    private bool _binActive;
    private string _archiveSearchText = string.Empty;
    private string _archiveSearchStatus = string.Empty;
    private bool _archiveSearchFailed;
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
    private long _inspectorGeneration;
    private Task<bool>? _backgroundSave;
    private Task _backgroundSaveObservation = Task.CompletedTask;
    private Task<bool>? _activeFlush;
    private bool _saveRequestedWhileInProgress;
    private bool _immediateSaveRequestedWhileInProgress;
    private long? _expiredTextSaveRevisionWhileInProgress;
    private bool _creationNeedsReload;
    private WorkspaceReloadData? _pendingCreationReload;
    private long _lastSavedRevision;
    private InspectorDraftFingerprint _persistedFingerprint =
        new(string.Empty, string.Empty, string.Empty, null, string.Empty, string.Empty, string.Empty);
    private string _autosaveStatus = "Changes save automatically.";
    private bool _hasAutosaveError;
    private string _todayAnnouncement = string.Empty;
    private string _todayFocusAutomationId = string.Empty;
    private string _archiveFocusAutomationId = string.Empty;
    private string _binFocusAutomationId = string.Empty;
    private string? _pendingArchiveTaskId;
    private BulkTaskArchivePreview? _pendingBulkTaskArchivePreview;
    private EmptyBinPreview? _pendingEmptyBinPreview;
    private int _bulkArchiveCompletedAgeDays = 30;
    private int _bulkArchiveAffectedCount;

    public static TimeSpan AutosaveDelay { get; } = TimeSpan.FromMilliseconds(600);

    public ProjectCaptureViewModel(IWorkspaceWork work, TimeProvider? timeProvider = null)
        : this(work, timeProvider, new InlineInspectorSaveWriter(new WorkspaceInspectorSaveOperation(work)))
    {
    }

    internal ProjectCaptureViewModel(
        IWorkspaceWork work,
        TimeProvider? timeProvider,
        IInspectorSaveWriter inspectorSaveWriter)
    {
        _work = work;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _inspectorSaveWriter = inspectorSaveWriter;
        NewProjectCommand = new(() => Navigate(BeginProject));
        NewTaskCommand = new(() => Navigate(BeginStandaloneTask));
        NewTodayTaskCommand = new(() => Navigate(BeginTodayTask));
        NewCategoryCommand = new(() => Navigate(BeginCategory));
        ClearTodayCommand = new(ClearToday);
        ClearArchiveSearchCommand = new(() =>
        {
            ArchiveSearchText = string.Empty;
            ArchiveFocusAutomationId = "archive-search";
        });
        MoveTaskToBinCommand = new(MoveCurrentTaskToBin);
        MoveProjectToBinCommand = new(MoveCurrentProjectToBin);
        RequestEmptyBinCommand = new(RequestEmptyBin);
        ConfirmEmptyBinCommand = new(ConfirmEmptyBin);
        CancelEmptyBinCommand = new(CancelEmptyBin);
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
        ConfirmArchiveTaskCommand = new(ConfirmArchiveTask);
        CancelArchiveTaskCommand = new(CancelArchiveTask);
        RequestBulkTaskArchiveCommand = new(RequestBulkTaskArchive);
        ConfirmBulkTaskArchiveCommand = new(ConfirmBulkTaskArchive);
        CancelBulkTaskArchiveCommand = new(CancelBulkTaskArchive);
        ChangeTaskContextCommand = new(RequestTaskContextChange);
        PreserveTaskCategoryCommand = new(() => CompletePendingAttachment(TaskAttachmentCategoryChoice.PreserveEffectiveCategory));
        AdoptProjectCategoryCommand = new(() => CompletePendingAttachment(TaskAttachmentCategoryChoice.AdoptProjectCategory));
        CancelAttachmentCommand = new(CancelPendingAttachment);
        EditMarkdownCommand = new(() => IsEditingMarkdown = true);
        SaveCommand = new(() => Save());
        CancelCommand = new(Cancel);
        SaveAndLeaveCommand = new(() => _ = SaveAndLeaveAsync());
        DiscardAndLeaveCommand = new(() => { Cancel(); Leave(); });
        StayCommand = new(() =>
        {
            _pendingNavigation = null;
            _pendingDeferredActionCompletion?.TrySetResult(false);
            _pendingDeferredActionCompletion = null;
            _pendingNavigationClosesInspector = true;
            Notify(nameof(NeedsDecision));
            Notify(nameof(HasBlockingDialog));
        });
        CapturePresentationClock();
        Reload();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<ProjectRowViewModel> Projects { get; } = [];
    public ObservableCollection<ArchivedProjectRowViewModel> ArchivedProjects { get; } = [];
    public ObservableCollection<TaskRowViewModel> Backlog { get; } = [];
    public ObservableCollection<UpcomingTaskGroupViewModel> UpcomingGroups { get; } = [];
    public ObservableCollection<TaskRowViewModel> Completed { get; } = [];
    public ObservableCollection<ArchivedTaskRowViewModel> Archived { get; } = [];
    public ObservableCollection<ArchivedWorkGroupViewModel> ArchiveGroups { get; } = [];
    public ObservableCollection<BinRowViewModel> Bin { get; } = [];
    public ObservableCollection<ArchiveSearchResultViewModel> ArchiveSearchResults { get; } = [];
    public ObservableCollection<ArchiveSearchResultGroupViewModel> ArchiveSearchGroups { get; } = [];
    public ObservableCollection<TodayTaskRowViewModel> TodayPlanned { get; } = [];
    public ObservableCollection<TodayTaskRowViewModel> TodayInProgress { get; } = [];
    public ObservableCollection<CompletedTaskRowViewModel> CompletedToday { get; } = [];
    public ObservableCollection<CompletedTaskGroupViewModel> CompletedGroups { get; } = [];
    public ObservableCollection<CategoryChoice> Categories { get; } = [];
    public ObservableCollection<CategoryChoice> BacklogCategories { get; } = [];
    public ObservableCollection<CategoryGroupViewModel> CategoryGroups { get; } = [];
    public ObservableCollection<CategoryChoice> CategoryReplacementChoices { get; } = [];
    public IReadOnlyList<CategoryColourChoice> CategoryColourChoices { get; } =
        IdentityColourPalette.Keys
            .Select(key => new CategoryColourChoice(key, CultureInfo.InvariantCulture.TextInfo.ToTitleCase(key)))
            .ToArray();
    public ObservableCollection<TaskContextChoice> TaskContextChoices { get; } = [];
    public ObservableCollection<ParticipantChoice> AvailableParticipants { get; } = [];
    public ObservableCollection<ParticipantDraftViewModel> SelectedParticipants { get; } = [];
    public RelayCommand NewProjectCommand { get; }
    public RelayCommand NewTaskCommand { get; }
    public RelayCommand NewTodayTaskCommand { get; }
    public RelayCommand NewCategoryCommand { get; }
    public RelayCommand ClearTodayCommand { get; }
    public RelayCommand ClearArchiveSearchCommand { get; }
    public RelayCommand MoveTaskToBinCommand { get; }
    public RelayCommand MoveProjectToBinCommand { get; }
    public RelayCommand RequestEmptyBinCommand { get; }
    public RelayCommand ConfirmEmptyBinCommand { get; }
    public RelayCommand CancelEmptyBinCommand { get; }
    public RelayCommand AddParticipantCommand { get; }
    public RelayCommand AddNewParticipantCommand { get; }
    public RelayCommand CancelNewParticipantCommand { get; }
    public RelayCommand RetryAutosaveCommand { get; }
    public RelayCommand DeleteCategoryCommand { get; }
    public RelayCommand ConfirmDeleteCategoryCommand { get; }
    public RelayCommand CancelDeleteCategoryCommand { get; }
    public RelayCommand ConfirmArchiveTaskCommand { get; }
    public RelayCommand CancelArchiveTaskCommand { get; }
    public RelayCommand RequestBulkTaskArchiveCommand { get; }
    public RelayCommand ConfirmBulkTaskArchiveCommand { get; }
    public RelayCommand CancelBulkTaskArchiveCommand { get; }
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
            Notify(nameof(ShowMoveTaskToBin));
            Notify(nameof(ShowMoveProjectToBin));
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
    public bool HasArchivedTasks => Archived.Count > 0;
    public bool HasArchivedProjects => ArchivedProjects.Count > 0;
    public bool HasArchived => Archived.Count > 0 || ArchivedProjects.Count > 0;
    public bool HasNoArchived => !HasArchived;
    public bool HasBinnedWork => Bin.Count > 0;
    public bool HasNoBinnedWork => !HasBinnedWork;
    public string ArchiveSearchText
    {
        get => _archiveSearchText;
        set
        {
            if (string.Equals(_archiveSearchText, value, StringComparison.Ordinal)) return;
            _archiveSearchText = value;
            Notify();
            RefreshArchiveSearch();
        }
    }
    public bool HasArchiveSearchQuery => !string.IsNullOrWhiteSpace(ArchiveSearchText);
    public bool HasArchiveSearchResults => ArchiveSearchResults.Count > 0;
    public bool ShowArchiveTimeline => !HasArchiveSearchQuery && HasArchived;
    public bool ShowArchiveEmpty => !HasArchiveSearchQuery && HasNoArchived;
    public bool ShowArchiveSearchEmpty => HasArchiveSearchQuery && !HasArchiveSearchResults && !_archiveSearchFailed;
    public bool ShowArchiveSearchError => HasArchiveSearchQuery && _archiveSearchFailed;
    public string ArchiveSearchStatus
    {
        get => _archiveSearchStatus;
        private set { _archiveSearchStatus = value; Notify(); }
    }
    public bool HasTodayTasks => TodayPlanned.Count + TodayInProgress.Count > 0;
    public bool HasNoTodayTasks => !HasTodayTasks;
    public bool HasCompletedToday => CompletedToday.Count > 0;
    public bool NeedsDecision => _pendingNavigation is not null;
    public bool NeedsCategoryReplacement => _pendingCategoryDeleteId is not null;
    public bool NeedsAttachmentChoice => _pendingAttachmentTaskId is not null;
    public bool NeedsArchiveConfirmation => _pendingArchiveTaskId is not null;
    public bool NeedsBulkTaskArchiveConfirmation => _pendingBulkTaskArchivePreview is not null;
    public bool NeedsEmptyBinConfirmation => _pendingEmptyBinPreview is not null;
    public bool HasBlockingDialog => NeedsDecision || NeedsCategoryReplacement || NeedsAttachmentChoice
        || NeedsArchiveConfirmation || NeedsBulkTaskArchiveConfirmation || NeedsEmptyBinConfirmation;
    public bool IsDirty => HasInspector && _original != Fingerprint();
    private bool RequiresPersistence => HasInspector && !_editingCategory
        && _persistedFingerprint != CurrentFingerprint();
    private bool HasPendingPersistence => _backgroundSave is { IsCompleted: false } || RequiresPersistence;
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
    public string TitleAutomationName => _editingCategory
        ? "Category name"
        : _editingTask ? "Task title" : "Project title";
    public bool ShowTaskIdentityIcon => HasInspector && _editingTask && !_editingCategory;
    public bool ShowProjectIdentityIcon => HasInspector && !_editingTask && !_editingCategory;
    public bool ShowCategoryIdentityIcon => HasInspector && _editingCategory;
    public bool ShowProjectIdentityEditor => HasInspector && !_editingTask && !_editingCategory;
    public string InspectorIdentityAccessibleText => !HasInspector
        ? string.Empty
        : _creating
            ? $"New {(_editingCategory ? "Category" : _editingTask ? "Task" : "Project")}"
            : _editingCategory
                ? CategoryGroups.Single(category => category.Id == _editingId).SelectionAccessibleName
                : _editingTask
                    ? _taskRows[_editingId!].AccessibleName
                    : InspectorProjectRow.AccessibleName;
    public bool ShowProjectSummary => HasInspector && !_editingCategory && !_creating && !_editingTask;
    public string ProjectSummary => ShowProjectSummary
        ? InspectorProjectRow.Summary
            + (string.IsNullOrEmpty(InspectorProjectRow.CompletionDateText)
                ? " · No completion date"
                : $" · {InspectorProjectRow.CompletionDateText}")
        : string.Empty;
    public bool ShowTaskCompletionDate => HasInspector && !_editingCategory && !_creating && _editingTask
        && _snapshot.Tasks.Single(task => task.Id == _editingId).CompletionDate is not null;
    public string TaskCompletionDateText => ShowTaskCompletionDate
        ? $"Completed {WorkDatePresentation.Relative(_snapshot.Tasks.Single(task => task.Id == _editingId).CompletionDate, Today)}"
        : string.Empty;
    public string TaskCompletionDateAccessibleText => ShowTaskCompletionDate
        ? WorkDatePresentation.Accessible(_snapshot.Tasks.Single(task => task.Id == _editingId).CompletionDate, "Completed")
        : string.Empty;
    public bool ShowMoveTaskToBin => HasInspector && !_editingCategory && !_creating && _editingTask;
    public bool ShowMoveProjectToBin => HasInspector && !_editingCategory && !_creating && !_editingTask;
    public string EmptyBinConfirmationHeading => NeedsEmptyBinConfirmation
        ? "Permanently empty Bin?"
        : string.Empty;
    public string EmptyBinConfirmationBody => _pendingEmptyBinPreview is null
        ? string.Empty
        : $"This permanently deletes {_pendingEmptyBinPreview.ProjectCount} {Plural(_pendingEmptyBinPreview.ProjectCount, "Project", "Projects")} and {_pendingEmptyBinPreview.TaskCount} {Plural(_pendingEmptyBinPreview.TaskCount, "Task", "Tasks")} from this workspace. A validated encrypted recovery point is created first and retained separately. Existing automatic backups may contain earlier copies until normal pruning removes them, and filesystem snapshots may also retain copies; this is not forensic erasure.";
    public string InspectorMetaLabel => _editingTask ? "CATEGORY BEHAVIOUR" : "STATUS";
    public string InspectorMetaValue => _editingTask
        ? CategoryHint
        : ShowProjectSummary
            ? $"{InspectorProjectRow.Status} · {InspectorProjectRow.ProgressText}"
            : "Not started · 0/0 tasks";
    private ProjectRowViewModel InspectorProjectRow =>
        Projects.Concat(ArchivedProjects.Select(row => row.Project))
            .Single(project => project.Id == _editingId);
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
            if (_editingCategory)
            {
                CategoryNameValidationMessage = string.Empty;
                Notify(nameof(CategoryPreviewName));
            }
            else
            {
                _workTitleValidationMessage = string.Empty;
                Notify(nameof(TitleValidationMessage));
                Notify(nameof(HasTitleValidationError));
                if (!_editingTask) Notify(nameof(ProjectPreviewName));
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
    public string CategoryColourKey
    {
        get => _categoryColourKey;
        set
        {
            if (!IdentityColourPalette.IsSupported(value)
                || string.Equals(_categoryColourKey, value, StringComparison.Ordinal)) return;
            _categoryColourKey = value;
            Notify();
            Notify(nameof(SelectedCategoryColour));
            DraftChanged(immediate: true);
        }
    }
    public CategoryColourChoice? SelectedCategoryColour
    {
        get => CategoryColourChoices.Single(choice => choice.Key == CategoryColourKey);
        set
        {
            if (value is not null) CategoryColourKey = value.Key;
        }
    }
    public string CategoryPreviewName => string.IsNullOrWhiteSpace(Title) ? "Category name" : Title.Trim();
    public string ProjectColourKey
    {
        get => _projectColourKey;
        set
        {
            if (!IdentityColourPalette.IsSupported(value)
                || string.Equals(_projectColourKey, value, StringComparison.Ordinal)) return;
            _projectColourKey = value;
            Notify();
            Notify(nameof(SelectedProjectColour));
            DraftChanged(immediate: true);
        }
    }
    public CategoryColourChoice? SelectedProjectColour
    {
        get => CategoryColourChoices.Single(choice => choice.Key == ProjectColourKey);
        set
        {
            if (value is not null) ProjectColourKey = value.Key;
        }
    }
    public string ProjectPreviewName => string.IsNullOrWhiteSpace(Title) ? "Project name" : Title.Trim();
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
    internal void ClearMessage() => Message = string.Empty;
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
    public string ArchiveConfirmationHeading => _pendingArchiveTaskId is null
        ? string.Empty
        : $"Archive “{_snapshot.Tasks.Single(task => task.Id == _pendingArchiveTaskId).Title}”?";
    public string ArchiveConfirmationBody => _pendingArchiveTaskId is null
        ? string.Empty
        : _snapshot.Tasks.Single(task => task.Id == _pendingArchiveTaskId).ProjectId is null
            ? "This Task will leave Completed and remain available to restore from Archive."
            : "This Task will leave Completed, remain under its Project, and stay available to restore from Archive.";
    public IReadOnlyList<int> BulkArchiveThresholdChoices { get; } =
        Enumerable.Range(BulkTaskArchiveThreshold.MinimumDays,
            BulkTaskArchiveThreshold.MaximumDays - BulkTaskArchiveThreshold.MinimumDays + 1).ToArray();
    public int BulkArchiveCompletedAgeDays
    {
        get => _bulkArchiveCompletedAgeDays;
        set
        {
            if (_bulkArchiveCompletedAgeDays == value) return;
            BulkTaskArchiveThreshold.Validate(value);
            _bulkArchiveCompletedAgeDays = value;
            RefreshBulkArchivePreview();
            Notify();
            Notify(nameof(BulkArchiveThresholdSummary));
            Notify(nameof(BulkArchiveConfirmationHeading));
            Notify(nameof(BulkArchiveConfirmationBody));
        }
    }
    public int BulkArchiveAffectedCount
    {
        get => _bulkArchiveAffectedCount;
        private set
        {
            if (_bulkArchiveAffectedCount == value) return;
            _bulkArchiveAffectedCount = value;
            Notify();
            Notify(nameof(CanBulkArchive));
            Notify(nameof(BulkArchiveThresholdSummary));
            Notify(nameof(BulkArchiveConfirmationHeading));
            Notify(nameof(BulkArchiveConfirmationBody));
        }
    }
    public bool CanBulkArchive => BulkArchiveAffectedCount > 0;
    public string BulkArchiveThresholdSummary => BulkArchiveAffectedCount == 1
        ? $"1 completed Task is older than {BulkArchiveCompletedAgeDays} calendar {(BulkArchiveCompletedAgeDays == 1 ? "day" : "days")}."
        : $"{BulkArchiveAffectedCount} completed Tasks are older than {BulkArchiveCompletedAgeDays} calendar {(BulkArchiveCompletedAgeDays == 1 ? "day" : "days")}.";
    public string BulkArchiveConfirmationHeading => $"Archive {BulkArchiveAffectedCount} completed {(BulkArchiveAffectedCount == 1 ? "Task" : "Tasks")}?";
    public string BulkArchiveConfirmationBody =>
        $"This will archive only completed Tasks older than {BulkArchiveCompletedAgeDays} calendar {(BulkArchiveCompletedAgeDays == 1 ? "day" : "days")}. Projects will not be archived.";
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
    public string ArchiveFocusAutomationId { get => _archiveFocusAutomationId; private set { _archiveFocusAutomationId = value; Notify(); } }
    public string BinFocusAutomationId { get => _binFocusAutomationId; private set { _binFocusAutomationId = value; Notify(); } }
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
        if (!immediate && _backgroundSave is { IsCompleted: false })
        {
            _saveRequestedWhileInProgress = false;
            _immediateSaveRequestedWhileInProgress = false;
            _expiredTextSaveRevisionWhileInProgress = null;
        }
        HasAutosaveError = false;
        AutosaveStatus = HasPendingPersistence
            ? immediate || _backgroundSave is { IsCompleted: false } ? "Saving…" : "Saving soon…"
            : "Saved";
        if (immediate)
        {
            _immediateSaveRequestedWhileInProgress = _backgroundSave is { IsCompleted: false };
            RunScheduledAutosave();
        }
        else AutosaveRequested?.Invoke(this, new(_autosaveRevision));
    }

    public bool RunScheduledAutosave(long? scheduledRevision = null, bool force = false)
    {
        if (!HasInspector || _editingCategory) return true;
        if (!force && scheduledRevision is not null && scheduledRevision != _autosaveRevision) return true;
        if (_backgroundSave is { IsCompleted: false })
        {
            _saveRequestedWhileInProgress = true;
            if (!force && scheduledRevision is not null)
                _expiredTextSaveRevisionWhileInProgress = scheduledRevision;
            return true;
        }
        if (!RequiresPersistence && !(force && _creating))
        {
            AutosaveStatus = _creating ? "Start typing to create." : "Saved";
            return true;
        }
        _saveRequestedWhileInProgress = false;
        _immediateSaveRequestedWhileInProgress = false;
        _expiredTextSaveRevisionWhileInProgress = null;
        var save = SaveOneRevisionAsync();
        _backgroundSave = save;
        if (save.IsCompleted)
        {
            _backgroundSave = null;
            return save.GetAwaiter().GetResult();
        }
        _backgroundSaveObservation = ObserveBackgroundSaveAsync(save);
        return true;
    }

    public bool FlushPendingAutosave()
    {
        var flush = FlushPendingAutosaveAsync();
        return flush.IsCompletedSuccessfully && flush.Result;
    }

    public Task<bool> FlushPendingAutosaveAsync()
    {
        if (_activeFlush is { IsCompleted: false } active) return active;
        var flush = FlushPendingAutosaveCoreAsync();
        _activeFlush = flush;
        if (flush.IsCompleted) _activeFlush = null;
        else _ = ClearCompletedFlushAsync(flush);
        return flush;
    }

    private async Task ClearCompletedFlushAsync(Task<bool> flush)
    {
        await flush;
        if (ReferenceEquals(_activeFlush, flush)) _activeFlush = null;
    }

    private async Task<bool> FlushPendingAutosaveCoreAsync()
    {
        if (_editingCategory) return !IsDirty;
        while (HasPendingPersistence)
        {
            if (_backgroundSave is { } background)
            {
                var backgroundSaved = await background;
                if (ReferenceEquals(_backgroundSave, background)) _backgroundSave = null;
                if (!backgroundSaved && background.IsCompleted && HasAutosaveError
                    && !_saveRequestedWhileInProgress) return false;
                if (!RequiresPersistence) return true;
            }

            _saveRequestedWhileInProgress = false;
            _immediateSaveRequestedWhileInProgress = false;
            if (!await SaveOneRevisionAsync()) return false;
        }
        return true;
    }

    internal long AutosaveRevision => _autosaveRevision;

    internal Task WaitForBackgroundSaveObservationAsync() => _backgroundSaveObservation;

    internal async Task<bool> WaitForAutosaveRevisionAsync(long revision)
    {
        if (_lastSavedRevision >= revision) return true;
        return await FlushPendingAutosaveAsync() && _lastSavedRevision >= revision;
    }

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

    public void Navigate(Action destination) => _ = NavigateAsync(destination);

    internal async Task NavigateAsync(Action destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (NeedsDecision) return;
        if ((_editingCategory ? IsDirty : HasPendingPersistence)
            && (_editingCategory || !await FlushPendingAutosaveAsync()))
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
        DraftChanged(immediate: true);
    }

    public void SetBacklogActive(bool active) => _backlogActive = active;
    public void SetUpcomingActive(bool active) => _upcomingActive = active;
    public void SetCompletedActive(bool active) => _completedActive = active;

    public void SetArchiveActive(bool active) => _archiveActive = active;
    public void SetBinActive(bool active) => _binActive = active;
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
        var submission = QuickAddAsync(projectId, title);
        if (submission.IsCompleted) return submission.GetAwaiter().GetResult();
        _ = submission;
        return true;
    }

    internal Task<bool> QuickAddAsync(string projectId, string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return Task.FromResult(false);
        return RunAfterDraftFlushAsync(() =>
            Attempt(() => { _work.CreateTask(projectId, title); Reload(); Message = "Task created."; }));
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
        var submission = SubmitBacklogQuickAddAsync();
        if (submission.IsCompleted) return submission.GetAwaiter().GetResult();
        _ = submission;
        return true;
    }

    internal Task<bool> SubmitBacklogQuickAddAsync()
    {
        if (string.IsNullOrWhiteSpace(BacklogQuickTitle)) return Task.FromResult(false);
        if (BacklogQuickCategory?.Id is null)
        {
            Message = "Choose a category before creating a standalone Task.";
            return Task.FromResult(false);
        }

        var title = BacklogQuickTitle;
        var categoryId = BacklogQuickCategory.Id;
        return RunAfterDraftFlushAsync(() =>
        {
            if (!Attempt(() => { _work.CreateStandaloneTask(title, string.Empty, categoryId, null); Reload(); Message = "Task created."; }))
                return false;
            if (string.Equals(BacklogQuickTitle, title, StringComparison.Ordinal))
                BacklogQuickTitle = string.Empty;
            return true;
        });
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
            CategoryReplacementChoices.Add(new(category.Id, category.Name, category.ColourKey));
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

    internal void RequestArchiveTask(string id)
    {
        if (HasBlockingDialog) return;
        ResolveDraftBeforeAction(() =>
        {
            var task = _snapshot.Tasks.Single(item => item.Id == id);
            if (!task.IsComplete || task.IsArchived) return;
            DialogReturnFocusAutomationId = $"task-archive-{id}";
            _pendingArchiveTaskId = id;
            Notify(nameof(NeedsArchiveConfirmation));
            Notify(nameof(ArchiveConfirmationHeading));
            Notify(nameof(ArchiveConfirmationBody));
            Notify(nameof(HasBlockingDialog));
        });
    }

    private void ConfirmArchiveTask()
    {
        if (_pendingArchiveTaskId is null) return;
        var id = _pendingArchiveTaskId;
        var completedIndex = Completed.IndexOf(Completed.FirstOrDefault(row => row.Id == id)!);
        if (!Attempt(() =>
            {
                _work.ArchiveTask(id);
                if (_editingTask && _editingId == id) CloseInspector();
                ClearArchiveConfirmation();
                Reload();
                ArchiveFocusAutomationId = Completed.Count == 0
                    ? "navigation-completed"
                    : Completed[Math.Min(Math.Max(completedIndex, 0), Completed.Count - 1)].ArchiveAutomationId;
                Message = "Task archived.";
            }, "Could not archive the Task. No changes were made.")) return;
    }

    private void CancelArchiveTask() => ClearArchiveConfirmation();

    private void ClearArchiveConfirmation()
    {
        _pendingArchiveTaskId = null;
        Notify(nameof(NeedsArchiveConfirmation));
        Notify(nameof(ArchiveConfirmationHeading));
        Notify(nameof(ArchiveConfirmationBody));
        Notify(nameof(HasBlockingDialog));
    }

    private void RequestBulkTaskArchive()
    {
        if (HasBlockingDialog || !CanBulkArchive) return;
        ResolveDraftBeforeAction(() =>
        {
            BulkTaskArchivePreview? preview = null;
            if (!Attempt(() => preview = _work.PreviewBulkTaskArchive(BulkArchiveCompletedAgeDays),
                    "Could not refresh the affected Task count. No changes were made."))
                return;
            BulkArchiveAffectedCount = preview!.AffectedCount;
            if (!CanBulkArchive) return;
            DialogReturnFocusAutomationId = "bulk-archive-request";
            _pendingBulkTaskArchivePreview = preview;
            Notify(nameof(NeedsBulkTaskArchiveConfirmation));
            Notify(nameof(HasBlockingDialog));
        });
    }

    private void ConfirmBulkTaskArchive()
    {
        if (!NeedsBulkTaskArchiveConfirmation) return;
        BulkTaskArchivePreview? refreshedPreview = null;
        if (!Attempt(() => refreshedPreview = _work.PreviewBulkTaskArchive(BulkArchiveCompletedAgeDays),
                "Could not refresh the affected Task count. No changes were made."))
            return;
        var currentPreview = refreshedPreview!;
        if (!_pendingBulkTaskArchivePreview!.Matches(currentPreview))
        {
            var countChanged = currentPreview.AffectedCount != _pendingBulkTaskArchivePreview!.AffectedCount;
            BulkArchiveAffectedCount = currentPreview.AffectedCount;
            _pendingBulkTaskArchivePreview = currentPreview;
            Message = countChanged
                ? "The affected count changed. Review it and confirm again."
                : "The affected Tasks changed. Review them and confirm again.";
            return;
        }
        if (!Attempt(() =>
            {
                var result = _work.BulkArchiveTasks(currentPreview);
                if (!result.Applied)
                {
                    _pendingBulkTaskArchivePreview = result.Preview;
                    BulkArchiveAffectedCount = result.Preview.AffectedCount;
                    Message = "The affected Tasks changed. Review them and confirm again.";
                    return;
                }
                CancelBulkTaskArchive();
                if (_editingTask && _snapshot.Tasks.Single(task => task.Id == _editingId).IsComplete)
                    CloseInspector();
                Reload();
                ArchiveFocusAutomationId = Completed.Count == 0
                    ? "navigation-completed"
                    : Completed[0].ArchiveAutomationId;
                Message = result.ArchivedCount == 1
                    ? "1 completed Task archived."
                    : $"{result.ArchivedCount} completed Tasks archived.";
            }, "Could not archive the completed Tasks. No changes were made.")) return;
    }

    private void CancelBulkTaskArchive()
    {
        _pendingBulkTaskArchivePreview = null;
        Notify(nameof(NeedsBulkTaskArchiveConfirmation));
        Notify(nameof(HasBlockingDialog));
    }

    internal void ArchiveProject(string id)
    {
        if (HasBlockingDialog) return;
        ResolveDraftBeforeAction(() =>
        {
            var activeIndex = Projects.IndexOf(Projects.Single(project => project.Id == id));
            if (!Attempt(() =>
                {
                    _work.ArchiveProject(id);
                    if ((!_editingTask && _editingId == id)
                        || (_editingTask && _editingId is { } taskId
                            && _snapshot.Tasks.Single(task => task.Id == taskId).ProjectId == id))
                        CloseInspector();
                    Reload();
                    ArchiveFocusAutomationId = Projects.Count == 0
                        ? "navigation-projects"
                        : Projects[Math.Min(activeIndex, Projects.Count - 1)].ArchiveAutomationId;
                    Message = "Project archived.";
                }, "Could not archive the Project. No changes were made.")) return;
        });
    }

    internal void RestoreProject(string id)
    {
        if (HasBlockingDialog) return;
        ResolveDraftBeforeAction(() =>
        {
            var archivedIndex = ArchivedProjects.IndexOf(ArchivedProjects.Single(project => project.Project.Id == id));
            var searchIndex = ArchiveSearchResults.IndexOf(ArchiveSearchResults.FirstOrDefault(result => result.Result.Id == id)!);
            if (!Attempt(() =>
                {
                    _work.RestoreProject(id);
                    Reload();
                    ArchiveFocusAutomationId = _archiveActive
                        ? HasArchiveSearchQuery
                            ? ArchiveSearchResults.Count == 0
                                ? "archive-search"
                                : ArchiveSearchResults[Math.Min(Math.Max(searchIndex, 0), ArchiveSearchResults.Count - 1)].RestoreAutomationId
                            : ArchivedProjects.Count == 0
                                ? Archived.Count == 0 ? "navigation-archive" : Archived[0].Task.RestoreAutomationId
                                : ArchivedProjects[Math.Min(archivedIndex, ArchivedProjects.Count - 1)].Project.RestoreAutomationId
                        : $"project-archive-{id}";
                    Message = "Project restored.";
                }, "Could not restore the Project. No changes were made.")) return;
        });
    }

    internal void RestoreTask(string id)
    {
        if (HasBlockingDialog) return;
        ResolveDraftBeforeAction(() =>
        {
            var task = _snapshot.Tasks.Single(item => item.Id == id);
            if (!task.IsArchived) return;
            var archivedIndex = Archived.IndexOf(Archived.FirstOrDefault(row => row.Task.Id == id)!);
            var searchIndex = ArchiveSearchResults.IndexOf(ArchiveSearchResults.FirstOrDefault(result => result.Result.Id == id)!);
            if (!Attempt(() =>
                {
                    _work.RestoreTask(id);
                    Reload();
                    ArchiveFocusAutomationId = _archiveActive
                        ? HasArchiveSearchQuery
                            ? ArchiveSearchResults.Count == 0
                                ? "archive-search"
                                : ArchiveSearchResults[Math.Min(Math.Max(searchIndex, 0), ArchiveSearchResults.Count - 1)].RestoreAutomationId
                            : Archived.Count == 0
                                ? ArchivedProjects.Count == 0
                                    ? "navigation-archive"
                                    : ArchivedProjects[0].Project.RestoreAutomationId
                                : Archived[Math.Min(Math.Max(archivedIndex, 0), Archived.Count - 1)].Task.RestoreAutomationId
                        : $"task-completion-{id}";
                    Message = "Task restored to Completed.";
                }, "Could not restore the Task. No changes were made.")) return;
        });
    }

    private void MoveCurrentTaskToBin()
    {
        if (!ShowMoveTaskToBin || _editingId is null || HasBlockingDialog) return;
        var taskId = _editingId;
        ResolveDraftBeforeAction(() =>
        {
            if (!Attempt(() =>
                {
                    _work.MoveTaskToBin(taskId);
                    CloseInspector();
                    Reload();
                    BinFocusAutomationId = "navigation-bin";
                    Message = "Task moved to Bin. You can restore it from Bin.";
                }, "Could not move the Task to Bin. No changes were made.")) return;
        });
    }

    private void MoveCurrentProjectToBin()
    {
        if (!ShowMoveProjectToBin || _editingId is null || HasBlockingDialog) return;
        var projectId = _editingId;
        ResolveDraftBeforeAction(() =>
        {
            if (!Attempt(() =>
                {
                    _work.MoveProjectToBin(projectId);
                    CloseInspector();
                    Reload();
                    BinFocusAutomationId = "navigation-bin";
                    Message = "Project and its Tasks moved to Bin. You can restore the aggregate from Bin.";
                }, "Could not move the Project to Bin. No changes were made.")) return;
        });
    }

    internal void RestoreTaskFromBin(string id)
    {
        if (HasBlockingDialog) return;
        var binIndex = Bin.IndexOf(Bin.FirstOrDefault(row => row.Task?.Id == id)!);
        if (!Attempt(() =>
            {
                _work.RestoreTaskFromBin(id);
                Reload();
                BinFocusAutomationId = _binActive
                    ? Bin.Count == 0
                        ? "navigation-bin"
                        : Bin[Math.Min(Math.Max(binIndex, 0), Bin.Count - 1)].RestoreAutomationId
                    : $"task-completion-{id}";
                Message = "Task restored from Bin.";
            }, "Could not restore the Task from Bin. No changes were made.")) return;
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

    internal void RestoreProjectFromBin(string id)
    {
        if (HasBlockingDialog) return;
        var binIndex = Bin.IndexOf(Bin.FirstOrDefault(row => row.Project?.Id == id)!);
        if (!Attempt(() =>
            {
                _work.RestoreProjectFromBin(id);
                Reload();
                BinFocusAutomationId = _binActive
                    ? Bin.Count == 0
                        ? "navigation-bin"
                        : Bin[Math.Min(Math.Max(binIndex, 0), Bin.Count - 1)].RestoreAutomationId
                    : $"project-select-{id}";
                Message = "Project and its Tasks restored from Bin.";
            }, "Could not restore the Project from Bin. No changes were made.")) return;
    }

    private void RequestEmptyBin()
    {
        if (HasBlockingDialog) return;
        EmptyBinPreview? preview = null;
        if (!Attempt(() => preview = _work.PreviewEmptyBin(),
                "Could not inspect Bin. No changes were made.")) return;
        if (preview is null || preview.IsEmpty) return;
        _pendingEmptyBinPreview = preview;
        Notify(nameof(NeedsEmptyBinConfirmation));
        Notify(nameof(EmptyBinConfirmationHeading));
        Notify(nameof(EmptyBinConfirmationBody));
        Notify(nameof(HasBlockingDialog));
    }

    private void ConfirmEmptyBin()
    {
        if (_pendingEmptyBinPreview is null) return;
        EmptyBinResult? result = null;
        if (!Attempt(() => result = _work.EmptyBin(_pendingEmptyBinPreview),
                "Could not empty Bin. Nothing was deleted."))
        {
            CloseEmptyBinConfirmation();
            Reload();
            BinFocusAutomationId = "empty-bin";
            return;
        }
        var completed = result!;
        if (completed.Status == EmptyBinStatus.PreviewChanged)
        {
            _pendingEmptyBinPreview = completed.Preview;
            Reload();
            Notify(nameof(EmptyBinConfirmationBody));
            Message = "Bin changed. Review the updated counts before confirming again.";
            return;
        }

        CloseEmptyBinConfirmation();
        Reload();
        BinFocusAutomationId = completed.Status == EmptyBinStatus.Emptied ? "navigation-bin" : "empty-bin";
        Message = completed.Status switch
        {
            EmptyBinStatus.Emptied => "Bin emptied after creating a validated encrypted recovery point.",
            EmptyBinStatus.RecoveryPointCreationFailed => "Could not empty Bin because a validated recovery point could not be created. Nothing was deleted.",
            _ => "Could not empty Bin. Nothing was deleted.",
        };
    }

    private void CancelEmptyBin()
    {
        if (_pendingEmptyBinPreview is null) return;
        CloseEmptyBinConfirmation();
        BinFocusAutomationId = "empty-bin";
    }

    private void CloseEmptyBinConfirmation()
    {
        _pendingEmptyBinPreview = null;
        Notify(nameof(NeedsEmptyBinConfirmation));
        Notify(nameof(EmptyBinConfirmationHeading));
        Notify(nameof(EmptyBinConfirmationBody));
        Notify(nameof(HasBlockingDialog));
    }

    private void ResolveDraftBeforeAction(Action action, bool closesInspector = false) =>
        _ = ResolveDraftBeforeActionAsync(action, closesInspector);

    private bool RunAfterDraftFlush(Func<bool> action)
    {
        var execution = RunAfterDraftFlushAsync(action);
        if (execution.IsCompleted) return execution.GetAwaiter().GetResult();
        _ = execution;
        return true;
    }

    private async Task<bool> RunAfterDraftFlushAsync(Func<bool> action)
    {
        if (NeedsDecision) return false;
        if ((_editingCategory ? IsDirty : HasPendingPersistence)
            && (_editingCategory || !await FlushPendingAutosaveAsync()))
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingDeferredActionCompletion = completion;
            _pendingNavigation = () => completion.TrySetResult(action());
            _pendingNavigationClosesInspector = false;
            Notify(nameof(NeedsDecision));
            Notify(nameof(HasBlockingDialog));
            return await completion.Task;
        }
        return action();
    }

    private async Task ResolveDraftBeforeActionAsync(Action action, bool closesInspector)
    {
        if (NeedsDecision) return;
        if ((_editingCategory ? IsDirty : HasPendingPersistence)
            && (_editingCategory || !await FlushPendingAutosaveAsync()))
        {
            _pendingNavigation = action;
            _pendingNavigationClosesInspector = closesInspector;
            Notify(nameof(NeedsDecision));
            Notify(nameof(HasBlockingDialog));
            return;
        }

        if (closesInspector) CloseInspector();
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
        SetDraft(string.Empty, string.Empty, null, _snapshot.Categories.Count == 0 ? null : _snapshot.Categories[0].Id,
            projectColourKey: IdentityColourPalette.KeyForProjectPosition(_snapshot.Projects.Count));
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
        SetDraft(project.Title, project.Description, project.TargetDate, project.CategoryId,
            projectColourKey: project.ColourKey);
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
        SetCategoryDraft(string.Empty, IdentityColourPalette.KeyForPosition(_snapshot.Categories.Count));
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
        SetCategoryDraft(category.Name, category.ColourKey);
    }

    private void SetCategoryDraft(string name, string colourKey)
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
            _categoryColourKey = colourKey;
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
            Notify(nameof(CategoryColourKey));
            Notify(nameof(SelectedCategoryColour));
            Notify(nameof(CategoryPreviewName));
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
            _categoryColourKey = colourKey;
            _loadingDraft = false;
        }
    }

    private void RefreshTaskContextChoices(TaskRecord? task)
    {
        TaskContextChoices.Clear();
        TaskContextChoices.Add(new(null, "Standalone"));
        foreach (var project in _snapshot.Projects.Where(project => !project.IsArchived))
            TaskContextChoices.Add(new(project.Id, project.Title));
        TaskContextTarget = TaskContextChoices.SingleOrDefault(choice => choice.ProjectId == task?.ProjectId)
            ?? TaskContextChoices[0];
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
                var inheritedCategory = CategoryById(project.CategoryId);
                Categories.Add(new(null, $"Inherit — {inheritedCategory.Name}", inheritedCategory.ColourKey));
            }
            foreach (var category in _snapshot.Categories)
                Categories.Add(new(category.Id, category.Name, category.ColourKey));
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
        IReadOnlyList<string>? participantIds = null, string? projectColourKey = null)
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
                var inheritedCategory = CategoryById(project.CategoryId);
                Categories.Add(new(null, $"Inherit — {inheritedCategory.Name}", inheritedCategory.ColourKey));
            }
            foreach (var category in _snapshot.Categories)
                Categories.Add(new(category.Id, category.Name, category.ColourKey));
            _title = title;
            _description = description;
            _renderedDescription = SanitisedMarkdownRenderer.Render(description);
            _editingMarkdown = false;
            _date = dateText;
            _categoryColourKey = IdentityColourPalette.DefaultKey;
            _projectColourKey = projectColourKey ?? IdentityColourPalette.KeyForProjectPosition(0);
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
            _persistedFingerprint = CurrentFingerprint();
            Notify(nameof(Title));
            Notify(nameof(Description));
            Notify(nameof(RenderedDescription));
            Notify(nameof(HasRenderedDescription));
            Notify(nameof(MarkdownPreviewAutomationName));
            Notify(nameof(Date));
            Notify(nameof(Category));
            Notify(nameof(ProjectColourKey));
            Notify(nameof(SelectedProjectColour));
            Notify(nameof(ProjectPreviewName));
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
        Notify(nameof(ShowTaskIdentityIcon)); Notify(nameof(ShowProjectIdentityIcon)); Notify(nameof(ShowCategoryIdentityIcon));
        Notify(nameof(ShowProjectIdentityEditor));
        Notify(nameof(InspectorIdentityAccessibleText));
        Notify(nameof(ShowWorkInspector)); Notify(nameof(ShowCategoryInspector)); Notify(nameof(ShowCategoryDelete));
        Notify(nameof(ShowParticipants)); Notify(nameof(ShowTaskContext)); Notify(nameof(ShowTaskContextAction));
        Notify(nameof(ShowProjectSummary)); Notify(nameof(ProjectSummary));
        Notify(nameof(ShowTaskCompletionDate)); Notify(nameof(TaskCompletionDateText)); Notify(nameof(TaskCompletionDateAccessibleText));
        Notify(nameof(ShowMoveTaskToBin));
        Notify(nameof(ShowMoveProjectToBin));
        Notify(nameof(InspectorMetaLabel)); Notify(nameof(InspectorMetaValue));
        Notify(nameof(ShowMarkdownPreview)); Notify(nameof(ShowMarkdownEditor));
        Notify(nameof(ShowExplicitInspectorActions)); Notify(nameof(ShowAutosaveStatus));
        Notify(nameof(DecisionHeading)); Notify(nameof(DecisionBody)); Notify(nameof(DecisionSaveLabel));
        Notify(nameof(TitleValidationMessage)); Notify(nameof(HasTitleValidationError));
    }

    private void ResetAutosavePresentation()
    {
        _inspectorGeneration++;
        _autosaveRevision++;
        _saveRequestedWhileInProgress = false;
        _immediateSaveRequestedWhileInProgress = false;
        _expiredTextSaveRevisionWhileInProgress = null;
        _creationNeedsReload = false;
        _pendingCreationReload = null;
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
        return RunScheduledAutosave(force: true);
    }

    private async Task SaveAndLeaveAsync()
    {
        if (_editingCategory)
        {
            if (SaveCategory()) Leave();
            return;
        }
        if (await FlushPendingAutosaveAsync()) Leave();
    }

    private async Task ObserveBackgroundSaveAsync(Task<bool> save)
    {
        await save;
        if (ReferenceEquals(_backgroundSave, save)) _backgroundSave = null;
        if (_activeFlush is { IsCompleted: false }) return;
        if (!RequiresPersistence)
        {
            HasAutosaveError = false;
            AutosaveStatus = "Saved";
            return;
        }
        if (_immediateSaveRequestedWhileInProgress)
        {
            _immediateSaveRequestedWhileInProgress = false;
            RunScheduledAutosave();
        }
        else if (_saveRequestedWhileInProgress)
        {
            _saveRequestedWhileInProgress = false;
            var expiredRevision = _expiredTextSaveRevisionWhileInProgress;
            _expiredTextSaveRevisionWhileInProgress = null;
            if (expiredRevision == _autosaveRevision) RunScheduledAutosave();
            else AutosaveRequested?.Invoke(this, new(_autosaveRevision));
        }
    }

    private async Task<bool> SaveOneRevisionAsync()
    {
        if (!TryBuildSaveRequest(out var request))
        {
            HasAutosaveError = true;
            AutosaveStatus = string.IsNullOrEmpty(Message) ? "Could not save. Retry." : Message;
            return false;
        }
        AutosaveStatus = "Saving…";
        InspectorSaveResult result;
        try
        {
            result = await _inspectorSaveWriter.SubmitAsync(request);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        var sameInspector = request.InspectorGeneration == _inspectorGeneration;
        if (result.Outcome == InspectorSaveOutcome.Superseded || !sameInspector) return true;
        if (result.Outcome != InspectorSaveOutcome.Saved)
        {
            if (request.Revision != _autosaveRevision) return true;
            Message = result.Outcome == InspectorSaveOutcome.Invalid && request.Kind == InspectorSaveKind.Task
                ? "Check the Participant labels and selections. No changes were made."
                : "Could not save workspace changes. Your draft is retained. Try again.";
            HasAutosaveError = true;
            AutosaveStatus = Message;
            return false;
        }

        if (result.Snapshot is { } committedSnapshot && request.NewParticipantLabels.Count > 0)
            ReconcileCommittedParticipantIdentities(request, committedSnapshot);

        _persistedFingerprint = CommittedFingerprint(result);
        _lastSavedRevision = Math.Max(_lastSavedRevision, request.Revision);

        if (request.IsCreating)
        {
            _editingId = result.Project?.Id ?? result.Task?.Id;
            _creating = false;
            _creationNeedsReload = request.Revision != _autosaveRevision;
            _pendingCreationReload = _creationNeedsReload ? result.Reload : null;
        }

        if (request.Revision != _autosaveRevision && RequiresPersistence)
        {
            AutosaveStatus = "Saving…";
            return true;
        }

        ApplyCurrentSaveResult(result);
        _lastSavedRevision = Math.Max(_lastSavedRevision, _autosaveRevision);
        HasAutosaveError = false;
        AutosaveStatus = "Saved";
        return true;
    }

    private static InspectorDraftFingerprint CommittedFingerprint(InspectorSaveResult result)
    {
        if (result.Task is not { } task || result.Request.NewParticipantLabels.Count == 0)
            return result.Request.Fingerprint;
        return result.Request.Fingerprint with
        {
            Participants = string.Join('\u001f', task.Participants.Select(id => $"id:{id}")),
        };
    }

    private void ReconcileCommittedParticipantIdentities(
        InspectorSaveRequest request,
        WorkspaceWorkSnapshot committedSnapshot)
    {
        _snapshot = _snapshot with { ParticipantRecords = committedSnapshot.Participants };
        foreach (var label in request.NewParticipantLabels)
        {
            var selected = SelectedParticipants.FirstOrDefault(item => item.Id is null
                && string.Equals(
                    ParticipantLabel.ComparisonKey(item.Label),
                    ParticipantLabel.ComparisonKey(label),
                    StringComparison.Ordinal));
            if (selected is null) continue;
            var participant = committedSnapshot.Participants.Single(item => string.Equals(
                ParticipantLabel.ComparisonKey(item.Label),
                ParticipantLabel.ComparisonKey(label),
                StringComparison.Ordinal));
            var index = SelectedParticipants.IndexOf(selected);
            SelectedParticipants[index] = new(this, participant.Id, participant.Label, participant.Id);
        }
        SynchroniseParticipantChoices();
        Notify(nameof(IsDirty));
    }

    private bool TryBuildSaveRequest(out InspectorSaveRequest request)
    {
        request = null!;
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
        var participantIds = SelectedParticipants.Where(item => item.Id is not null).Select(item => item.Id!).ToArray();
        var newLabels = SelectedParticipants.Where(item => item.Id is null).Select(item => item.Label).ToArray();
        request = new(
            _inspectorGeneration,
            _autosaveRevision,
            _editingTask ? InspectorSaveKind.Task : InspectorSaveKind.Project,
            _creating,
            _creationNeedsReload,
            _editingId,
            Title,
            Description,
            date,
            Category.Id,
            ProjectColourKey,
            _editingTask ? TaskContextTarget?.ProjectId : null,
            _creatingTodayTask ? TodayLane.Planned : null,
            participantIds,
            newLabels,
            CurrentFingerprint());
        return true;
    }

    private void ApplyCurrentSaveResult(InspectorSaveResult result)
    {
        if (result.Reload is { } reload)
        {
            ReloadAndKeepInspector(reload);
        }
        else if (result.Project is { } project)
        {
            var previous = _snapshot.Projects.Single(item => item.Id == project.Id);
            ApplyCommittedProjectEdit(previous, project);
        }
        else if (result.Task is { } task)
        {
            var previous = _snapshot.Tasks.Single(item => item.Id == task.Id);
            ApplyCommittedTaskEdit(previous, task, result.Snapshot);
        }
        _creationNeedsReload = false;
        _pendingCreationReload = null;
        _creatingStandaloneTask = false;
        _creatingTodayTask = false;
        CompleteRecordLocalSave();
    }

    internal void ApplyCommittedTaskEdit(TaskRecord updated)
    {
        var previous = _snapshot.Tasks.Single(task => task.Id == updated.Id);
        ApplyCommittedTaskEdit(previous, updated, committedSnapshot: null);
    }

    private void ApplyCommittedProjectEdit(ProjectRecord previous, ProjectRecord updated)
    {
        _snapshot = _snapshot with
        {
            Projects = _snapshot.Projects
                .Select(project => project.Id == updated.Id ? updated : project)
                .ToArray(),
        };
        var row = Projects.FirstOrDefault(item => item.Id == updated.Id)
            ?? ArchivedProjects.Single(item => item.Project.Id == updated.Id).Project;
        var relationshipPresentationChanged =
            !string.Equals(previous.Title, updated.Title, StringComparison.Ordinal)
            || !string.Equals(previous.CategoryId, updated.CategoryId, StringComparison.Ordinal)
            || !string.Equals(previous.ColourKey, updated.ColourKey, StringComparison.Ordinal);
        var projectTasks = _snapshot.Tasks
            .Where(task => task.ProjectId == updated.Id)
            .OrderBy(task => task.ProjectPosition)
            .Select(task => relationshipPresentationChanged ? ToTaskRow(task) : TaskRowForProjection(task))
            .ToArray();
        row.Refresh(updated, ProjectWorkSummary.From(_snapshot, updated.Id), CategoryById(updated.CategoryId), projectTasks);
        if (!string.Equals(previous.CategoryId, updated.CategoryId, StringComparison.Ordinal))
        {
            RefreshCategoryGroup(previous.CategoryId);
            RefreshCategoryGroup(updated.CategoryId);
        }
        if (updated.IsArchived && HasArchiveSearchQuery) RefreshArchiveSearch();
        NotifyRecordLocalProjectionState();
    }

    private void ApplyCommittedTaskEdit(TaskRecord previous, TaskRecord updated, WorkspaceWorkSnapshot? committedSnapshot)
    {
        if (committedSnapshot is not null)
        {
            _snapshot = committedSnapshot;
            updated = _snapshot.Tasks.Single(task => task.Id == updated.Id);
        }
        else
        {
            _snapshot = _snapshot with
            {
                Tasks = _snapshot.Tasks
                    .Select(task => task.Id == updated.Id ? updated : task)
                    .ToArray(),
            };
        }
        ToTaskRow(updated);
        if (previous.ProjectId is null && updated.ProjectId is null
            && !string.Equals(previous.ExplicitCategoryId, updated.ExplicitCategoryId, StringComparison.Ordinal))
        {
            RefreshCategoryGroup(previous.ExplicitCategoryId!);
            RefreshCategoryGroup(updated.ExplicitCategoryId!);
        }
        if (previous.DueDate != updated.DueDate) RefreshUpcomingGroups();
        if (_editingTask && string.Equals(_editingId, updated.Id, StringComparison.Ordinal))
            SynchroniseSelectedParticipants(updated, committedSnapshot is not null);
        if (updated.IsArchived && HasArchiveSearchQuery) RefreshArchiveSearch();
        NotifyRecordLocalProjectionState();
    }

    private void RefreshCategoryGroup(string categoryId)
    {
        var category = CategoryById(categoryId);
        var categoryIndex = _snapshot.Categories
            .Select((item, index) => (item, index))
            .Single(pair => pair.item.Id == categoryId).index;
        var rows = CategoryRows(category.Id);
        CategoryGroups.Single(group => group.Id == categoryId).Refresh(
            category,
            rows.Projects,
            rows.StandaloneTasks,
            categoryIndex + 1,
            _snapshot.Categories.Count);
    }

    private (ProjectRowViewModel[] Projects, TaskRowViewModel[] StandaloneTasks) CategoryRows(string categoryId) =>
        (Projects
            .Where(project => _snapshot.Projects.Single(item => item.Id == project.Id).CategoryId == categoryId)
            .ToArray(),
        _snapshot.Tasks
            .Where(task => task.ProjectId is null && task.ExplicitCategoryId == categoryId && !task.IsArchived)
            .OrderBy(task => task.SharedPosition)
            .Select(TaskRowForProjection)
            .ToArray());

    private void SynchroniseSelectedParticipants(TaskRecord task, bool participantRecordsChanged)
    {
        var desired = task.Participants
            .Select(participantId => _snapshot.Participants.Single(participant => participant.Id == participantId))
            .ToArray();
        for (var index = SelectedParticipants.Count - 1; index >= 0; index--)
        {
            var selected = SelectedParticipants[index];
            if (selected.Id is not null && desired.Any(participant => participant.Id == selected.Id)) continue;
            if (selected.Id is null && desired.Any(participant =>
                    string.Equals(ParticipantLabel.ComparisonKey(participant.Label),
                        ParticipantLabel.ComparisonKey(selected.Label), StringComparison.Ordinal))) continue;
            SelectedParticipants.RemoveAt(index);
        }
        for (var index = 0; index < desired.Length; index++)
        {
            var participant = desired[index];
            var current = SelectedParticipants.FirstOrDefault(item => item.Id == participant.Id)
                ?? SelectedParticipants.FirstOrDefault(item => item.Id is null
                    && string.Equals(ParticipantLabel.ComparisonKey(item.Label),
                        ParticipantLabel.ComparisonKey(participant.Label), StringComparison.Ordinal));
            if (current?.Id is null)
            {
                var replacement = new ParticipantDraftViewModel(this, participant.Id, participant.Label, participant.Id);
                if (current is null) SelectedParticipants.Insert(index, replacement);
                else SelectedParticipants[SelectedParticipants.IndexOf(current)] = replacement;
                current = replacement;
            }
            else
            {
                current.RefreshLabel(participant.Label);
            }
            var currentIndex = SelectedParticipants.IndexOf(current);
            if (currentIndex != index) SelectedParticipants.Move(currentIndex, index);
        }
        if (participantRecordsChanged) SynchroniseParticipantChoices();
    }

    private void SynchroniseParticipantChoices()
    {
        var selectedId = ParticipantToAdd?.Id;
        var selectedNew = ParticipantToAdd?.IsNew == true;
        var desired = _snapshot.Participants
            .Select(participant => new ParticipantChoice(participant.Id, participant.Label))
            .Append(ParticipantChoice.New)
            .ToArray();
        for (var index = AvailableParticipants.Count - 1; index >= 0; index--)
        {
            var current = AvailableParticipants[index];
            if (desired.Any(choice => choice.Id == current.Id && choice.IsNew == current.IsNew)) continue;
            AvailableParticipants.RemoveAt(index);
        }
        for (var index = 0; index < desired.Length; index++)
        {
            var choice = desired[index];
            var current = AvailableParticipants.FirstOrDefault(item =>
                item.Id == choice.Id && item.IsNew == choice.IsNew);
            if (current is null)
            {
                AvailableParticipants.Insert(index, choice);
            }
            else
            {
                var currentIndex = AvailableParticipants.IndexOf(current);
                if (!string.Equals(current.Label, choice.Label, StringComparison.Ordinal))
                {
                    AvailableParticipants[currentIndex] = choice;
                    current = choice;
                }
                currentIndex = AvailableParticipants.IndexOf(current);
                if (currentIndex != index) AvailableParticipants.Move(currentIndex, index);
            }
        }
        ParticipantToAdd = selectedNew
            ? AvailableParticipants.Single(item => item.IsNew)
            : AvailableParticipants.FirstOrDefault(item => item.Id == selectedId && !item.IsNew);
    }

    private void CompleteRecordLocalSave()
    {
        _original = Fingerprint();
        _persistedFingerprint = CurrentFingerprint();
        Message = string.Empty;
        Notify(nameof(IsDirty));
        NotifyInspectorPresentation();
    }

    private void NotifyRecordLocalProjectionState()
    {
        Notify(nameof(ProjectSummary));
        Notify(nameof(InspectorMetaValue));
        Notify(nameof(UpcomingCount));
        Notify(nameof(HasUpcoming));
        Notify(nameof(HasNoUpcoming));
    }

    private void ReloadAndKeepInspector(WorkspaceReloadData reload)
    {
        Reload(reload);
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
        _persistedFingerprint = CurrentFingerprint();
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
                ? _work.CreateCategory(Title, CategoryColourKey).Id
                : _work.UpdateCategory(_editingId!, Title, CategoryColourKey).Id;
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
        if (_pendingCreationReload is { } creationReload)
        {
            Reload(creationReload);
            _pendingCreationReload = null;
            _creationNeedsReload = false;
        }
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
        _pendingDeferredActionCompletion = null;
        var closesInspector = _pendingNavigationClosesInspector;
        _pendingNavigationClosesInspector = true;
        Notify(nameof(NeedsDecision));
        Notify(nameof(HasBlockingDialog));
        if (closesInspector) CloseInspector();
        destination?.Invoke();
    }
    private (string Title, string Description, string Date, string? CategoryId, string CategoryColourKey, string ProjectColourKey, string Participants) Fingerprint() =>
        (Title, Description, Date, Category?.Id, CategoryColourKey, ProjectColourKey,
            string.Join('\u001f', SelectedParticipants.Select(item => item.Id is null ? $"new:{item.Label}" : $"id:{item.Id}")));
    private InspectorDraftFingerprint CurrentFingerprint()
    {
        var fingerprint = Fingerprint();
        return new(fingerprint.Title, fingerprint.Description, fingerprint.Date, fingerprint.CategoryId,
            fingerprint.CategoryColourKey, fingerprint.ProjectColourKey, fingerprint.Participants);
    }
    private WorkspaceCategory CategoryById(string id) => _snapshot.Categories.Single(category => category.Id == id);
    private string CategoryName(string id) => CategoryById(id).Name;
    private string EffectiveCategoryId(TaskRecord task) => task.ExplicitCategoryId
        ?? _snapshot.Projects.Single(project => project.Id == task.ProjectId).CategoryId;
    private void Reload() => Reload(new(_work.Read(), _work.ReadTaskBin(), _work.ReadProjectBin()));

    private void Reload(WorkspaceReloadData reload)
    {
        _snapshot = reload.Snapshot;
        var taskBin = reload.TaskBin;
        var projectBin = reload.ProjectBin;
        RefreshParticipantChoices();
        RefreshBacklogCategories();
        foreach (var removedId in _taskRows.Keys.Except(_snapshot.Tasks.Select(task => task.Id), StringComparer.Ordinal).ToArray())
            _taskRows.Remove(removedId);
        var existing = Projects
            .Concat(ArchivedProjects.Select(row => row.Project))
            .ToDictionary(p => p.Id, StringComparer.Ordinal);
        Projects.Clear();
        var activeProjects = _snapshot.Projects.Where(project => !project.IsArchived).ToArray();
        for (var projectIndex = 0; projectIndex < activeProjects.Length; projectIndex++)
        {
            var project = activeProjects[projectIndex];
            var row = existing.GetValueOrDefault(project.Id) ?? new ProjectRowViewModel(this, project.Id);
            row.Refresh(project, ProjectWorkSummary.From(_snapshot, project.Id), CategoryById(project.CategoryId), _snapshot.Tasks.Where(t => t.ProjectId == project.Id).OrderBy(t => t.ProjectPosition).Select(ToTaskRow));
            row.SetPosition(projectIndex + 1, activeProjects.Length);
            Projects.Add(row);
        }
        ArchivedProjects.Clear();
        var archivedProjects = _snapshot.Projects.Where(project => project.IsArchived)
            .OrderByDescending(project => project.ArchiveDate).ThenByDescending(project => project.ArchivedAt)
            .ThenBy(project => project.Position)
            .ToArray();
        for (var projectIndex = 0; projectIndex < archivedProjects.Length; projectIndex++)
        {
            var project = archivedProjects[projectIndex];
            var row = existing.GetValueOrDefault(project.Id) ?? new ProjectRowViewModel(this, project.Id);
            row.Refresh(project, ProjectWorkSummary.From(_snapshot, project.Id), CategoryById(project.CategoryId),
                _snapshot.Tasks.Where(task => task.ProjectId == project.Id).OrderBy(task => task.ProjectPosition).Select(ToTaskRow));
            ArchivedProjects.Add(new(row, projectIndex == archivedProjects.Length - 1));
        }
        var categoryExpansion = CategoryGroups.ToDictionary(
            category => category.Id,
            category => category.IsExpanded,
            StringComparer.Ordinal);
        CategoryGroups.Clear();
        for (var categoryIndex = 0; categoryIndex < _snapshot.Categories.Count; categoryIndex++)
        {
            var category = _snapshot.Categories[categoryIndex];
            var rows = CategoryRows(category.Id);
            CategoryGroups.Add(new(
                this,
                category,
                rows.Projects,
                rows.StandaloneTasks,
                categoryIndex + 1,
                _snapshot.Categories.Count,
                categoryExpansion.GetValueOrDefault(category.Id, true)));
        }
        var desiredBacklog = _snapshot.Tasks.Where(task => IsTaskInActiveWork(task) && !task.IsComplete && !task.IsArchived)
            .OrderBy(t => t.SharedPosition).Select(ToTaskRow).ToArray();
        SynchroniseBacklog(desiredBacklog);
        for (var index = 0; index < Backlog.Count; index++) Backlog[index].SetPosition(index + 1, Backlog.Count);
        RefreshUpcomingGroups();
        var desiredCompleted = _snapshot.Tasks.Where(task => IsTaskInActiveWork(task) && task.IsComplete && !task.IsArchived)
            .OrderByDescending(task => task.CompletedAt).ThenBy(task => task.SharedPosition).Select(ToTaskRow).ToArray();
        Synchronise(Completed, desiredCompleted);
        var desiredArchived = _snapshot.Tasks.Where(task => task.IsArchived && IsTaskInActiveWork(task))
            .OrderByDescending(task => task.ArchiveDate).ThenByDescending(task => task.ArchivedAt)
            .ThenByDescending(task => task.CompletedAt).ThenBy(task => task.SharedPosition)
            .Select(ToTaskRow).ToArray();
        Archived.Clear();
        for (var index = 0; index < desiredArchived.Length; index++)
            Archived.Add(new(desiredArchived[index], index == desiredArchived.Length - 1));
        RefreshArchiveGroups();
        RefreshArchiveSearch();
        Bin.Clear();
        var projectsIncludingBin = _snapshot.Projects
            .Concat(projectBin.Select(item => item.Project))
            .ToDictionary(project => project.Id, StringComparer.Ordinal);
        var binRows = projectBin
            .Select(item => BinRowViewModel.ForProject(
                this,
                item,
                CategoryById(item.Project.CategoryId).ColourKey,
                TimeZoneInfo.ConvertTime(item.RemovedAt, _timeProvider.LocalTimeZone)))
            .Concat(taskBin.Select(item =>
            {
                projectsIncludingBin.TryGetValue(item.Task.ProjectId ?? string.Empty, out var project);
                var category = item.Task.ExplicitCategoryId is { } categoryId
                    ? CategoryById(categoryId)
                    : project is not null
                        ? CategoryById(project.CategoryId)
                        : _snapshot.Categories.Single(candidate => candidate.Name == item.CategoryName);
                return BinRowViewModel.ForTask(
                    this,
                    item,
                    category.ColourKey,
                    project?.ColourKey ?? IdentityColourPalette.KeyForProjectPosition(0),
                    TimeZoneInfo.ConvertTime(item.RemovedAt, _timeProvider.LocalTimeZone));
            }))
            .OrderByDescending(item => item.RemovedAt)
            .ThenBy(item => item.Title, StringComparer.Ordinal)
            .ToArray();
        for (var index = 0; index < binRows.Length; index++)
        {
            binRows[index].IsLast = index == binRows.Length - 1;
            Bin.Add(binRows[index]);
        }
        SynchroniseToday(TodayPlanned, _snapshot.Tasks
            .Where(task => IsTaskInActiveWork(task) && !task.IsComplete && !task.IsArchived && task.TodayLane == TodayLane.Planned)
            .OrderBy(task => task.SharedPosition).Select(ToTaskRow).ToArray(), TodayLane.Planned);
        SynchroniseToday(TodayInProgress, _snapshot.Tasks
            .Where(task => IsTaskInActiveWork(task) && !task.IsComplete && !task.IsArchived && task.TodayLane == TodayLane.InProgress)
            .OrderBy(task => task.SharedPosition).Select(ToTaskRow).ToArray(), TodayLane.InProgress);
        var completedToday = _snapshot.Tasks
            .Where(task => IsTaskInActiveWork(task) && task.IsComplete && !task.IsArchived && task.CompletionDate == Today)
            .OrderByDescending(task => task.CompletedAt).ThenBy(task => task.SharedPosition).Select(ToTaskRow).ToArray();
        CompletedToday.Clear();
        for (var index = 0; index < completedToday.Length; index++)
            CompletedToday.Add(new(completedToday[index], index == completedToday.Length - 1));
        RefreshCompletedGroups();
        RefreshBulkArchivePreview();
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
        Notify(nameof(HasArchived));
        Notify(nameof(HasArchivedTasks));
        Notify(nameof(HasArchivedProjects));
        Notify(nameof(HasNoArchived));
        Notify(nameof(HasBinnedWork));
        Notify(nameof(HasNoBinnedWork));
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
        var project = task.ProjectId is null
            ? null
            : _snapshot.Projects.Single(project => project.Id == task.ProjectId);
        var projectTitle = project?.Title;
        var categoryId = task.ExplicitCategoryId
            ?? _snapshot.Projects.Single(project => project.Id == task.ProjectId).CategoryId;
        if (!_taskRows.TryGetValue(task.Id, out var row))
        {
            row = new(this, task.Id);
            _taskRows.Add(task.Id, row);
        }
        var category = CategoryById(categoryId);
        row.Refresh(task, category.Name, category.ColourKey, projectTitle,
            project?.ColourKey ?? IdentityColourPalette.KeyForProjectPosition(0), inherited, Today);
        return row;
    }

    private void RefreshBacklogCategories()
    {
        var selectedId = BacklogQuickCategory?.Id;
        BacklogCategories.Clear();
        foreach (var category in _snapshot.Categories)
            BacklogCategories.Add(new(category.Id, category.Name, category.ColourKey));
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
        foreach (var row in collection.Where(row => desired.All(task => task.Id != row.Task.Id))) row.Detach();
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

    private void RefreshArchiveGroups()
    {
        ArchiveGroups.Clear();
        var projects = ArchivedProjects.Select(row =>
        {
            var project = _snapshot.Projects.Single(item => item.Id == row.Project.Id);
            return new ArchivedWorkRowViewModel(project.ArchiveDate!.Value, project.ArchivedAt!.Value, row.Project, null, false);
        });
        var tasks = Archived.Select(row =>
        {
            var task = _snapshot.Tasks.Single(item => item.Id == row.Task.Id);
            return new ArchivedWorkRowViewModel(task.ArchiveDate!.Value, task.ArchivedAt!.Value, null, row.Task, false);
        });
        foreach (var group in projects.Concat(tasks)
                     .OrderByDescending(row => row.ArchiveDate)
                     .ThenByDescending(row => row.ArchivedAt)
                     .GroupBy(row => DateGroupFor(row.ArchiveDate))
                     .OrderByDescending(group => group.Key.Start))
        {
            var rows = group.Select((row, index) => row with { IsLast = index == group.Count() - 1 }).ToArray();
            ArchiveGroups.Add(new(group.Key.Heading, rows));
        }
    }

    private void RefreshArchiveSearch()
    {
        _archiveSearchFailed = false;
        if (!HasArchiveSearchQuery)
        {
            ArchiveSearchResults.Clear();
            ArchiveSearchGroups.Clear();
            ArchiveSearchStatus = string.Empty;
            NotifyArchiveSearchPresentation();
            return;
        }

        try
        {
            var results = _work.SearchArchive(ArchiveSearchText);
            var matches = results.Select(result =>
            {
                if (result.RecordType == ArchiveSearchRecordType.Project)
                {
                    var project = _snapshot.Projects.Single(item => item.Id == result.Id);
                    return (Result: result, ArchiveDate: project.ArchiveDate!.Value, ArchivedAt: project.ArchivedAt!.Value);
                }

                var task = _snapshot.Tasks.Single(item => item.Id == result.Id);
                return (Result: result, ArchiveDate: task.ArchiveDate!.Value, ArchivedAt: task.ArchivedAt!.Value);
            }).ToArray();
            var existingRows = ArchiveSearchResults.ToDictionary(row => row.Key, StringComparer.Ordinal);
            var desiredGroups = new List<(string Heading, ArchiveSearchResultViewModel[] Rows)>();
            foreach (var group in matches.GroupBy(match => DateGroupFor(match.ArchiveDate))
                         .OrderByDescending(group => group.Key.Start))
            {
                var groupMatches = group.ToArray();
                var rows = groupMatches.Select((match, index) =>
                {
                    existingRows.TryGetValue($"{match.Result.RecordType}:{match.Result.Id}", out var existing);
                    return RefreshArchiveSearchRow(existing, match.Result, index == groupMatches.Length - 1);
                }).ToArray();
                desiredGroups.Add((group.Key.Heading, rows));
            }
            SynchroniseArchiveSearchResults(desiredGroups.SelectMany(group => group.Rows).ToArray());
            SynchroniseArchiveSearchGroups(desiredGroups);
            ArchiveSearchStatus = results.Count switch
            {
                0 => $"No archived work matches “{ArchiveSearchText.Trim()}”.",
                1 => "1 archived result.",
                _ => $"{results.Count.ToString(CultureInfo.InvariantCulture)} archived results.",
            };
        }
        catch (WorkspaceWorkException)
        {
            ArchiveSearchResults.Clear();
            ArchiveSearchGroups.Clear();
            _archiveSearchFailed = true;
            ArchiveSearchStatus = "Archive search is unavailable. Try again.";
        }
        NotifyArchiveSearchPresentation();
    }

    private ArchiveSearchResultViewModel RefreshArchiveSearchRow(
        ArchiveSearchResultViewModel? row,
        ArchiveSearchResult result,
        bool isLast)
    {
        if (result.RecordType == ArchiveSearchRecordType.Project)
        {
            var project = ArchivedProjects.Single(item => item.Project.Id == result.Id).Project;
            if (row is null)
                return new(this, result, project.RelationshipText, project.CategoryName, project.CategoryColourKey,
                    project.Title, project.ColourKey, false, string.Empty, isLast);
            row.Refresh(result, project.RelationshipText, project.CategoryName, project.CategoryColourKey,
                project.Title, project.ColourKey, false, string.Empty, isLast);
            return row;
        }

        var task = _taskRows[result.Id];
        if (row is null)
            return new(this, result, task.BroadRelationshipText, task.CategoryName, task.CategoryColourKey,
                task.ProjectTitle, task.ProjectColourKey, task.HasCategoryOverride, task.CategoryOverrideText, isLast);
        row.Refresh(result, task.BroadRelationshipText, task.CategoryName, task.CategoryColourKey,
            task.ProjectTitle, task.ProjectColourKey, task.HasCategoryOverride, task.CategoryOverrideText, isLast);
        return row;
    }

    private void SynchroniseArchiveSearchResults(ArchiveSearchResultViewModel[] desired)
    {
        for (var index = ArchiveSearchResults.Count - 1; index >= 0; index--)
            if (desired.All(row => row.Key != ArchiveSearchResults[index].Key)) ArchiveSearchResults.RemoveAt(index);
        for (var index = 0; index < desired.Length; index++)
        {
            var current = ArchiveSearchResults.FirstOrDefault(row => row.Key == desired[index].Key);
            if (current is null) ArchiveSearchResults.Insert(index, desired[index]);
            else
            {
                var currentIndex = ArchiveSearchResults.IndexOf(current);
                if (currentIndex != index) ArchiveSearchResults.Move(currentIndex, index);
            }
        }
    }

    private void SynchroniseArchiveSearchGroups(
        List<(string Heading, ArchiveSearchResultViewModel[] Rows)> desired)
    {
        for (var index = ArchiveSearchGroups.Count - 1; index >= 0; index--)
            if (desired.All(group => group.Heading != ArchiveSearchGroups[index].Heading))
                ArchiveSearchGroups.RemoveAt(index);
        for (var index = 0; index < desired.Count; index++)
        {
            var group = ArchiveSearchGroups.FirstOrDefault(item => item.Heading == desired[index].Heading);
            if (group is null)
            {
                ArchiveSearchGroups.Insert(index, new(desired[index].Heading, desired[index].Rows));
            }
            else
            {
                group.Refresh(desired[index].Rows);
                var current = ArchiveSearchGroups.IndexOf(group);
                if (current != index) ArchiveSearchGroups.Move(current, index);
            }
        }
    }

    private void NotifyArchiveSearchPresentation()
    {
        Notify(nameof(HasArchiveSearchQuery));
        Notify(nameof(HasArchiveSearchResults));
        Notify(nameof(ShowArchiveTimeline));
        Notify(nameof(ShowArchiveEmpty));
        Notify(nameof(ShowArchiveSearchEmpty));
        Notify(nameof(ShowArchiveSearchError));
    }

    private void RefreshBulkArchivePreview()
    {
        BulkArchiveAffectedCount = _work.PreviewBulkTaskArchive(BulkArchiveCompletedAgeDays).AffectedCount;
    }

    private bool IsTaskInActiveWork(TaskRecord task) => task.ProjectId is null
        || !_snapshot.Projects.Single(project => project.Id == task.ProjectId).IsArchived;

    private void RefreshUpcomingGroups()
    {
        var tasks = UpcomingTaskProjection.Create(_snapshot.Tasks.Where(IsTaskInActiveWork), Today);
        var desired = new List<(string Heading, TaskRowViewModel[] Tasks)>();
        var overdue = tasks.Where(task => task.DueDate < Today).Select(TaskRowForProjection).ToArray();
        if (overdue.Length > 0) desired.Add(("Overdue", overdue));
        foreach (var group in tasks.Where(task => task.DueDate >= Today).GroupBy(task => task.DueDate!.Value))
        {
            var heading = group.Key == Today
                ? "Today"
                : group.Key.DayNumber == Today.DayNumber + 1
                    ? "Tomorrow"
                    : group.Key.ToString("dddd, d MMMM yyyy", CultureInfo.InvariantCulture);
            desired.Add((heading, group.Select(TaskRowForProjection).ToArray()));
        }
        for (var index = UpcomingGroups.Count - 1; index >= 0; index--)
            if (desired.All(group => !string.Equals(group.Heading, UpcomingGroups[index].Heading, StringComparison.Ordinal)))
                UpcomingGroups.RemoveAt(index);
        for (var index = 0; index < desired.Count; index++)
        {
            var group = UpcomingGroups.FirstOrDefault(item =>
                string.Equals(item.Heading, desired[index].Heading, StringComparison.Ordinal));
            if (group is null)
            {
                group = new(desired[index].Heading, desired[index].Tasks);
                UpcomingGroups.Insert(index, group);
            }
            else
            {
                group.Refresh(desired[index].Tasks);
                var current = UpcomingGroups.IndexOf(group);
                if (current != index) UpcomingGroups.Move(current, index);
            }
        }
    }

    private TaskRowViewModel TaskRowForProjection(TaskRecord task) =>
        _taskRows.TryGetValue(task.Id, out var row) ? row : ToTaskRow(task);

    private (DateOnly Start, string Heading) CompletedGroupFor(DateOnly completionDate) => DateGroupFor(completionDate);

    private (DateOnly Start, string Heading) DateGroupFor(DateOnly date)
    {
        var age = Today.DayNumber - date.DayNumber;
        if (age < 0)
            return (date, date.ToString("dddd, d MMMM yyyy", CultureInfo.InvariantCulture));
        if (age is >= 0 and <= 2)
        {
            var heading = age switch
            {
                0 => "Today",
                1 => "Yesterday",
                _ => date.ToString("dddd, d MMMM yyyy", CultureInfo.InvariantCulture),
            };
            return (date, heading);
        }

        var daysSinceMonday = ((int)date.DayOfWeek + 6) % 7;
        var weekStart = date.AddDays(-daysSinceMonday);
        return (weekStart, $"Week of {weekStart.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}");
    }

    private bool MoveTask(string taskId, int targetPosition)
    {
        if (HasPendingPersistence || (_editingCategory && IsDirty))
            return RunAfterDraftFlush(() => MoveTask(taskId, targetPosition));
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
        ResolveDraftBeforeAction(action);
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
        return RunAfterDraftFlush(() => Attempt(() =>
        {
            var change = _work.MoveTaskInTodayLane(taskId, targetPosition);
            Reload();
            var title = _snapshot.Tasks.Single(item => item.Id == taskId).Title;
            var laneName = change.Lane == TodayLane.Planned ? "Planned" : "In progress";
            TodayAnnouncement = $"Moved {title} to position {change.Position} of {change.Count} in {laneName}.";
            TodayFocusAutomationId = $"today-reorder-{taskId}";
            Message = TodayAnnouncement;
        }, "Could not reorder the Today lane."));
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
        catch (InvalidOperationException) { Message = failureMessage; return false; }
    }

    private static string Plural(int count, string singular, string plural) =>
        count == 1 ? singular : plural;

    internal void MoveUp(string taskId) => MoveTask(taskId, Backlog.IndexOf(Backlog.Single(task => task.Id == taskId)) - 1);
    internal void MoveDown(string taskId) => MoveTask(taskId, Backlog.IndexOf(Backlog.Single(task => task.Id == taskId)) + 1);
    internal void MoveToTop(string taskId) => MoveTask(taskId, 0);
    internal void MoveToBottom(string taskId) => MoveTask(taskId, Backlog.Count - 1);
    internal void ToggleCompletion(string taskId)
    {
        var task = _snapshot.Tasks.Single(item => item.Id == taskId);
        if (task.IsArchived) return;
        Action action = () => ApplyCompletion(taskId, !task.IsComplete);
        var removesDraftFromCurrentView = (!task.IsComplete && _backlogActive)
            || (!task.IsComplete && _upcomingActive)
            || (task.IsComplete && _completedActive)
            || (task.IsComplete && _todayActive && task.CompletionDate == Today);
        ResolveDraftBeforeAction(
            action,
            removesDraftFromCurrentView && _editingTask && _editingId == taskId);
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
        if (HasPendingPersistence || (_editingCategory && IsDirty))
            return RunAfterDraftFlush(() => MoveProject(projectId, targetPosition));
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
        if (HasPendingPersistence || (_editingCategory && IsDirty))
            return RunAfterDraftFlush(() => MoveCategory(categoryId, targetPosition));
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
        if (HasPendingPersistence || (_editingCategory && IsDirty))
            return RunAfterDraftFlush(() => MoveProjectTask(projectId, taskId, targetPosition));
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

public sealed record CategoryChoice(string? Id, string Name, string ColourKey = IdentityColourPalette.DefaultKey);
public sealed record CategoryColourChoice(string Key, string Name);
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

public sealed class CategoryProjectRowViewModel : INotifyPropertyChanged
{
    public CategoryProjectRowViewModel(ProjectRowViewModel project, bool isLast) => Refresh(project, isLast);
    public event PropertyChangedEventHandler? PropertyChanged;
    public ProjectRowViewModel Project { get; private set; } = null!;
    public bool IsLast { get; private set; }
    public void Refresh(ProjectRowViewModel project, bool isLast)
    {
        Project = project;
        IsLast = isLast;
        PropertyChanged?.Invoke(this, new(nameof(Project)));
        PropertyChanged?.Invoke(this, new(nameof(IsLast)));
    }
}

public sealed class CategoryTaskRowViewModel : INotifyPropertyChanged
{
    public CategoryTaskRowViewModel(TaskRowViewModel task, bool isLast) => Refresh(task, isLast);
    public event PropertyChangedEventHandler? PropertyChanged;
    public TaskRowViewModel Task { get; private set; } = null!;
    public bool IsLast { get; private set; }
    public void Refresh(TaskRowViewModel task, bool isLast)
    {
        Task = task;
        IsLast = isLast;
        PropertyChanged?.Invoke(this, new(nameof(Task)));
        PropertyChanged?.Invoke(this, new(nameof(IsLast)));
    }
}

public sealed class CategoryGroupViewModel : INotifyPropertyChanged
{
    private readonly ProjectCaptureViewModel _owner;
    private int _position;
    private int _count;
    private bool _isExpanded;

    public CategoryGroupViewModel(
        ProjectCaptureViewModel owner,
        WorkspaceCategory category,
        IReadOnlyList<ProjectRowViewModel> projects,
        IReadOnlyList<TaskRowViewModel> standaloneTasks,
        int position,
        int count,
        bool isExpanded)
    {
        _owner = owner;
        Id = category.Id;
        _isExpanded = isExpanded;
        SelectCommand = new(() => owner.SelectCategory(Id));
        MoveUpCommand = new(() => owner.MoveCategory(Id, _position - 2));
        MoveDownCommand = new(() => owner.MoveCategory(Id, _position));
        MoveToTopCommand = new(() => owner.MoveCategory(Id, 0));
        MoveToBottomCommand = new(() => owner.MoveCategory(Id, _count - 1));
        Refresh(category, projects, standaloneTasks, position, count);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id { get; }
    public string Name { get; private set; } = string.Empty;
    public string ColourKey { get; private set; } = IdentityColourPalette.DefaultKey;
    public int ProjectCount => Projects.Count;
    public int StandaloneTaskCount => StandaloneTasks.Count;
    public bool HasProjects => ProjectCount > 0;
    public bool HasStandaloneTasks => StandaloneTaskCount > 0;
    public bool HasNoWork => !HasProjects && !HasStandaloneTasks;
    public ObservableCollection<CategoryProjectRowViewModel> Projects { get; } = [];
    public ObservableCollection<CategoryTaskRowViewModel> StandaloneTasks { get; } = [];
    public string PositionText => $"{_position} of {_count}";
    public string ReorderAutomationId => $"category-reorder-{Id}";
    public string SelectionAutomationId => $"category-selection-{Id}";
    public string ReorderAccessibleName => $"Reorder {Name} in Categories";
    public string MoveUpAccessibleName => $"Move {Name} up in Categories";
    public string MoveDownAccessibleName => $"Move {Name} down in Categories";
    public string MoveToTopAccessibleName => $"Move {Name} to top of Categories";
    public string MoveToBottomAccessibleName => $"Move {Name} to bottom of Categories";
    public string SelectionAccessibleName => $"Category {Name}. {Summary}";
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
    public string Summary => $"{ProjectCount} {(ProjectCount == 1 ? "Project" : "Projects")} · {StandaloneTaskCount} standalone {(StandaloneTaskCount == 1 ? "Task" : "Tasks")}";
    public RelayCommand SelectCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public RelayCommand MoveToTopCommand { get; }
    public RelayCommand MoveToBottomCommand { get; }

    public void Refresh(
        WorkspaceCategory category,
        IReadOnlyList<ProjectRowViewModel> projects,
        IReadOnlyList<TaskRowViewModel> standaloneTasks,
        int position,
        int count)
    {
        Name = category.Name;
        ColourKey = category.ColourKey;
        SynchroniseProjects(projects);
        SynchroniseTasks(standaloneTasks);
        _position = position;
        _count = count;
        PropertyChanged?.Invoke(this, new(nameof(Name)));
        PropertyChanged?.Invoke(this, new(nameof(ColourKey)));
        PropertyChanged?.Invoke(this, new(nameof(Projects)));
        PropertyChanged?.Invoke(this, new(nameof(StandaloneTasks)));
        PropertyChanged?.Invoke(this, new(nameof(ProjectCount)));
        PropertyChanged?.Invoke(this, new(nameof(StandaloneTaskCount)));
        PropertyChanged?.Invoke(this, new(nameof(HasProjects)));
        PropertyChanged?.Invoke(this, new(nameof(HasStandaloneTasks)));
        PropertyChanged?.Invoke(this, new(nameof(HasNoWork)));
        PropertyChanged?.Invoke(this, new(nameof(PositionText)));
        PropertyChanged?.Invoke(this, new(nameof(Summary)));
        PropertyChanged?.Invoke(this, new(nameof(SelectionAccessibleName)));
        PropertyChanged?.Invoke(this, new(nameof(ExpansionAccessibleName)));
        PropertyChanged?.Invoke(this, new(nameof(ReorderAccessibleName)));
        PropertyChanged?.Invoke(this, new(nameof(MoveUpAccessibleName)));
        PropertyChanged?.Invoke(this, new(nameof(MoveDownAccessibleName)));
        PropertyChanged?.Invoke(this, new(nameof(MoveToTopAccessibleName)));
        PropertyChanged?.Invoke(this, new(nameof(MoveToBottomAccessibleName)));
    }

    private void SynchroniseProjects(IReadOnlyList<ProjectRowViewModel> projects)
    {
        for (var index = Projects.Count - 1; index >= 0; index--)
            if (projects.All(project => project.Id != Projects[index].Project.Id)) Projects.RemoveAt(index);
        for (var index = 0; index < projects.Count; index++)
        {
            var row = Projects.FirstOrDefault(item => item.Project.Id == projects[index].Id);
            if (row is null)
            {
                row = new(projects[index], index == projects.Count - 1);
                Projects.Insert(index, row);
            }
            else
            {
                row.Refresh(projects[index], index == projects.Count - 1);
                var current = Projects.IndexOf(row);
                if (current != index) Projects.Move(current, index);
            }
        }
    }

    private void SynchroniseTasks(IReadOnlyList<TaskRowViewModel> tasks)
    {
        for (var index = StandaloneTasks.Count - 1; index >= 0; index--)
            if (tasks.All(task => task.Id != StandaloneTasks[index].Task.Id)) StandaloneTasks.RemoveAt(index);
        for (var index = 0; index < tasks.Count; index++)
        {
            var row = StandaloneTasks.FirstOrDefault(item => item.Task.Id == tasks[index].Id);
            if (row is null)
            {
                row = new(tasks[index], index == tasks.Count - 1);
                StandaloneTasks.Insert(index, row);
            }
            else
            {
                row.Refresh(tasks[index], index == tasks.Count - 1);
                var current = StandaloneTasks.IndexOf(row);
                if (current != index) StandaloneTasks.Move(current, index);
            }
        }
    }
}

public sealed record CompletedTaskGroupViewModel(string Heading, IReadOnlyList<TaskRowViewModel> Tasks)
{
    public IReadOnlyList<CompletedTaskRowViewModel> Rows { get; } = Tasks
        .Select((task, index) => new CompletedTaskRowViewModel(task, index == Tasks.Count - 1))
        .ToArray();
}

public sealed record CompletedTaskRowViewModel(TaskRowViewModel Task, bool IsLast);
public sealed record ArchivedTaskRowViewModel(TaskRowViewModel Task, bool IsLast);
public sealed record ArchivedProjectRowViewModel(ProjectRowViewModel Project, bool IsLast);

public sealed class BinRowViewModel
{
    private BinRowViewModel(
        ProjectCaptureViewModel owner,
        ProjectBinRecord? projectItem,
        TaskBinRecord? taskItem,
        string categoryColourKey,
        string projectColourKey,
        DateTimeOffset localRemovedAt)
    {
        Project = projectItem?.Project;
        Task = taskItem?.Task;
        CategoryName = projectItem?.CategoryName ?? taskItem!.CategoryName;
        CategoryColourKey = categoryColourKey;
        ProjectColourKey = projectItem?.Project.ColourKey ?? projectColourKey;
        ProjectTitle = projectItem is not null
            ? projectItem.Project.Title
            : taskItem!.ProjectTitle;
        TaskCount = projectItem?.TaskCount ?? 0;
        RemovedAt = localRemovedAt;
        RemovedText = $"Removed {localRemovedAt.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture)}";
        CanRestore = projectItem is not null || taskItem!.CanRestore;
        RestoreBlockedText = taskItem?.RestoreBlockedReason ?? string.Empty;
        RestoreCommand = new(() =>
        {
            if (!CanRestore) return;
            if (Project is not null) owner.RestoreProjectFromBin(Project.Id);
            else owner.RestoreTaskFromBin(Task!.Id);
        });
    }

    public static BinRowViewModel ForProject(
        ProjectCaptureViewModel owner,
        ProjectBinRecord item,
        string categoryColourKey,
        DateTimeOffset localRemovedAt) => new(
            owner, item, null, categoryColourKey, item.Project.ColourKey, localRemovedAt);

    public static BinRowViewModel ForTask(
        ProjectCaptureViewModel owner,
        TaskBinRecord item,
        string categoryColourKey,
        string projectColourKey,
        DateTimeOffset localRemovedAt) => new(
            owner, null, item, categoryColourKey, projectColourKey, localRemovedAt);

    public ProjectRecord? Project { get; }
    public TaskRecord? Task { get; }
    public bool IsProject => Project is not null;
    public bool IsTask => Task is not null;
    public string Title => Project?.Title ?? Task!.Title;
    public string? ProjectTitle { get; }
    public string? PillProjectTitle => IsProject ? null : ProjectTitle;
    public string CategoryName { get; }
    public string CategoryColourKey { get; }
    public string ProjectColourKey { get; }
    public int TaskCount { get; }
    public bool IsStandaloneTask => IsTask && ProjectTitle is null;
    public bool HasCategoryOverride => IsTask && ProjectTitle is not null && Task!.ExplicitCategoryId is not null;
    public string RelationshipText => IsProject
        ? $"{CategoryName} · {TaskCount} {(TaskCount == 1 ? "Task" : "Tasks")}"
        : ProjectTitle ?? $"Standalone · {CategoryName}";
    public string CategoryOverrideText => HasCategoryOverride ? CategoryName : string.Empty;
    public string ContextText => RelationshipText;
    public string AccessibleName => string.Join(". ", new[]
    {
        $"{(IsProject ? "Project" : "Task")} {Title}",
        IsProject
            ? $"Category {CategoryName}. {TaskCount} {(TaskCount == 1 ? "Task" : "Tasks")}"
            : ProjectTitle is null
                ? $"Standalone. Category {CategoryName}"
                : HasCategoryOverride
                    ? $"Project {ProjectTitle}. Category override {CategoryName}"
                    : $"Project {ProjectTitle}. Inherited Category {CategoryName}",
        RemovedText,
        IsRestoreBlocked ? RestoreBlockedText : string.Empty,
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public DateTimeOffset RemovedAt { get; }
    public string RemovedText { get; }
    public bool IsLast { get; internal set; }
    public bool CanRestore { get; }
    public bool IsRestoreBlocked => !CanRestore;
    public string RestoreBlockedText { get; }
    public string RestoreAutomationId => IsProject
        ? $"bin-restore-project-{Project!.Id}"
        : $"bin-restore-task-{Task!.Id}";
    public string RestoreAccessibleName => IsProject
        ? $"Restore {Title} Project and its Tasks from Bin"
        : $"Restore {Title} from Bin";
    public string RestoreHelpText => CanRestore
        ? IsProject
            ? $"Restores {Title} and its Tasks to their nearest surviving former positions."
            : $"Restores {Title} to its nearest surviving former position."
        : RestoreBlockedText;
    public RelayCommand RestoreCommand { get; }
}

public sealed record ArchivedWorkRowViewModel(
    DateOnly ArchiveDate,
    DateTimeOffset ArchivedAt,
    ProjectRowViewModel? Project,
    TaskRowViewModel? Task,
    bool IsLast)
{
    public bool IsProject => Project is not null;
    public bool IsTask => Task is not null;
}
public sealed record ArchivedWorkGroupViewModel(string Heading, IReadOnlyList<ArchivedWorkRowViewModel> Rows);
public sealed class ArchiveSearchResultGroupViewModel
{
    public ArchiveSearchResultGroupViewModel(string heading, IReadOnlyList<ArchiveSearchResultViewModel> rows)
    {
        Heading = heading;
        Refresh(rows);
    }

    public string Heading { get; }
    public ObservableCollection<ArchiveSearchResultViewModel> Rows { get; } = [];

    public void Refresh(IReadOnlyList<ArchiveSearchResultViewModel> rows)
    {
        for (var index = Rows.Count - 1; index >= 0; index--)
            if (rows.All(row => row.Key != Rows[index].Key)) Rows.RemoveAt(index);
        for (var index = 0; index < rows.Count; index++)
        {
            var current = Rows.FirstOrDefault(row => row.Key == rows[index].Key);
            if (current is null) Rows.Insert(index, rows[index]);
            else
            {
                var currentIndex = Rows.IndexOf(current);
                if (currentIndex != index) Rows.Move(currentIndex, index);
            }
        }
    }
}

public sealed class ArchiveSearchResultViewModel : INotifyPropertyChanged
{
    public ArchiveSearchResultViewModel(
        ProjectCaptureViewModel owner,
        ArchiveSearchResult result,
        string relationshipText,
        string categoryName,
        string categoryColourKey,
        string? projectTitle,
        string projectColourKey,
        bool hasCategoryOverride,
        string categoryOverrideText,
        bool isLast)
    {
        Refresh(result, relationshipText, categoryName, categoryColourKey, projectTitle,
            projectColourKey, hasCategoryOverride, categoryOverrideText, isLast);
        OpenCommand = new(() =>
        {
            if (Result.RecordType == ArchiveSearchRecordType.Project) owner.SelectProject(Result.Id);
            else owner.SelectTask(Result.Id);
        });
        RestoreCommand = new(() =>
        {
            if (Result.RecordType == ArchiveSearchRecordType.Project) owner.RestoreProject(Result.Id);
            else owner.RestoreTask(Result.Id);
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ArchiveSearchResult Result { get; private set; } = null!;
    public string Key => $"{Result.RecordType}:{Result.Id}";
    public bool IsLast { get; private set; }
    public bool IsProject => Result.RecordType == ArchiveSearchRecordType.Project;
    public bool IsTask => Result.RecordType == ArchiveSearchRecordType.Task;
    public string Title => Result.Title;
    public string TypeLabel => Result.RecordType == ArchiveSearchRecordType.Project ? "Project" : "Task";
    public string StateLabel => $"Archived {TypeLabel}";
    public bool HasParentProject => Result.ParentProjectTitle is not null;
    public string ParentProjectText => Result.ParentProjectTitle is null
        ? string.Empty
        : $"Project · {Result.ParentProjectTitle}";
    public string DateText => $"{(Result.DateKind == ArchiveSearchDateKind.Completed ? "Completed" : "Archived")} {Result.Date.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}";
    public string Excerpt => Result.Excerpt;
    public string RelationshipText { get; private set; } = string.Empty;
    public string CategoryName { get; private set; } = string.Empty;
    public string CategoryColourKey { get; private set; } = IdentityColourPalette.DefaultKey;
    public string? ProjectTitle { get; private set; }
    public string? PillProjectTitle => IsProject ? null : ProjectTitle;
    public string ProjectColourKey { get; private set; } = IdentityColourPalette.KeyForProjectPosition(0);
    public bool HasCategoryOverride { get; private set; }
    public string CategoryOverrideText { get; private set; } = string.Empty;
    public string ContextText => string.Join(" · ", new[] { RelationshipText, Excerpt }
        .Where(value => !string.IsNullOrWhiteSpace(value)));
    public string AutomationId => $"archive-search-{Result.RecordType.ToString().ToLowerInvariant()}-{Result.Id}";
    public string RestoreAutomationId => $"archive-search-restore-{Result.RecordType.ToString().ToLowerInvariant()}-{Result.Id}";
    public string RestoreAccessibleName => Result.RecordType == ArchiveSearchRecordType.Project
        ? $"Restore Project {Title} to Projects"
        : $"Restore {Title} to Completed";
    public string AccessibleName => string.Join(". ", new[]
        {
            $"{TypeLabel} {Title}",
            RelationshipText,
            HasCategoryOverride ? $"Category override {CategoryOverrideText}" : string.Empty,
            StateLabel,
            DateText,
            Excerpt,
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public RelayCommand OpenCommand { get; }
    public RelayCommand RestoreCommand { get; }

    public void Refresh(
        ArchiveSearchResult result,
        string relationshipText,
        string categoryName,
        string categoryColourKey,
        string? projectTitle,
        string projectColourKey,
        bool hasCategoryOverride,
        string categoryOverrideText,
        bool isLast)
    {
        Result = result;
        RelationshipText = relationshipText;
        CategoryName = categoryName;
        CategoryColourKey = categoryColourKey;
        ProjectTitle = projectTitle;
        ProjectColourKey = projectColourKey;
        HasCategoryOverride = hasCategoryOverride;
        CategoryOverrideText = categoryOverrideText;
        IsLast = isLast;
        foreach (var property in new[]
                 {
                     nameof(Result), nameof(Key), nameof(IsLast), nameof(IsProject), nameof(IsTask), nameof(Title),
                     nameof(TypeLabel), nameof(StateLabel), nameof(HasParentProject), nameof(ParentProjectText),
                     nameof(DateText), nameof(Excerpt), nameof(RelationshipText), nameof(CategoryName),
                     nameof(CategoryColourKey), nameof(ProjectTitle), nameof(PillProjectTitle), nameof(ProjectColourKey),
                     nameof(HasCategoryOverride), nameof(CategoryOverrideText), nameof(ContextText), nameof(AutomationId),
                     nameof(RestoreAutomationId), nameof(RestoreAccessibleName), nameof(AccessibleName),
                 })
            PropertyChanged?.Invoke(this, new(property));
    }
}

public sealed class UpcomingTaskGroupViewModel
{
    public UpcomingTaskGroupViewModel(string heading, IReadOnlyList<TaskRowViewModel> tasks)
    {
        Heading = heading;
        Refresh(tasks);
    }

    public string Heading { get; }
    public ObservableCollection<UpcomingTaskRowViewModel> Rows { get; } = [];
    public IReadOnlyList<TaskRowViewModel> Tasks => Rows.Select(row => row.Task).ToArray();

    public void Refresh(IReadOnlyList<TaskRowViewModel> tasks)
    {
        for (var index = Rows.Count - 1; index >= 0; index--)
            if (tasks.All(task => task.Id != Rows[index].Task.Id)) Rows.RemoveAt(index);
        for (var index = 0; index < tasks.Count; index++)
        {
            var row = Rows.FirstOrDefault(item => item.Task.Id == tasks[index].Id);
            if (row is null)
            {
                row = new(tasks[index], index == tasks.Count - 1);
                Rows.Insert(index, row);
            }
            else
            {
                row.Refresh(tasks[index], index == tasks.Count - 1);
                var current = Rows.IndexOf(row);
                if (current != index) Rows.Move(current, index);
            }
        }
    }
}

public sealed class UpcomingTaskRowViewModel : INotifyPropertyChanged
{
    public UpcomingTaskRowViewModel(TaskRowViewModel task, bool isLast)
    {
        Task = task;
        IsLast = isLast;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public TaskRowViewModel Task { get; private set; }
    public bool IsLast { get; private set; }

    public void Refresh(TaskRowViewModel task, bool isLast)
    {
        Task = task;
        IsLast = isLast;
        PropertyChanged?.Invoke(this, new(nameof(Task)));
        PropertyChanged?.Invoke(this, new(nameof(IsLast)));
    }
}

public sealed class TodayTaskRowViewModel : INotifyPropertyChanged
{
    private readonly ProjectCaptureViewModel _owner;
    private readonly TodayLane _lane;
    private int _position;
    private int _count;

    public TodayTaskRowViewModel(ProjectCaptureViewModel owner, TaskRowViewModel task, TodayLane lane)
    {
        _owner = owner;
        Task = task;
        Task.PropertyChanged += OnTaskPropertyChanged;
        _lane = lane;
        MoveUpCommand = new(() => owner.MoveTodayTask(task.Id, _position - 1));
        MoveDownCommand = new(() => owner.MoveTodayTask(task.Id, _position + 1));
        MoveToTopCommand = new(() => owner.MoveTodayTask(task.Id, 0));
        MoveToBottomCommand = new(() => owner.MoveTodayTask(task.Id, _count - 1));
        MoveToOtherLaneCommand = new(() => owner.MoveToOtherTodayLane(task.Id));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
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
        if (!ReferenceEquals(Task, task))
        {
            Task.PropertyChanged -= OnTaskPropertyChanged;
            task.PropertyChanged += OnTaskPropertyChanged;
        }
        Task = task;
        _position = position;
        _count = count;
        NotifyTaskPresentation();
    }

    internal void Detach() => Task.PropertyChanged -= OnTaskPropertyChanged;

    private void OnTaskPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        NotifyTaskPresentation();
    }

    private void NotifyTaskPresentation()
    {
        PropertyChanged?.Invoke(this, new(nameof(Task)));
        PropertyChanged?.Invoke(this, new(nameof(IsLast)));
        PropertyChanged?.Invoke(this, new(nameof(PositionText)));
        PropertyChanged?.Invoke(this, new(nameof(ReorderAccessibleName)));
        PropertyChanged?.Invoke(this, new(nameof(MoveUpAccessibleName)));
        PropertyChanged?.Invoke(this, new(nameof(MoveDownAccessibleName)));
        PropertyChanged?.Invoke(this, new(nameof(MoveToTopAccessibleName)));
        PropertyChanged?.Invoke(this, new(nameof(MoveToBottomAccessibleName)));
        PropertyChanged?.Invoke(this, new(nameof(MoveToOtherLaneLabel)));
        PropertyChanged?.Invoke(this, new(nameof(MoveToOtherLaneAccessibleName)));
    }
}

public sealed class TaskRowViewModel : INotifyPropertyChanged
{
    private readonly ProjectCaptureViewModel _owner;
    private string _title = string.Empty;
    private string _categoryName = string.Empty;
    private string _categoryColourKey = IdentityColourPalette.DefaultKey;
    private string _categoryDisplay = string.Empty;
    private string? _projectTitle;
    private string _projectColourKey = IdentityColourPalette.KeyForProjectPosition(0);
    private bool _usesInheritedCategory;
    private int _position;
    private int _count;
    private bool _isComplete;
    private string? _projectId;
    private string _dateText = string.Empty;
    private string _dateAccessibleText = string.Empty;
    private string _completionDateText = string.Empty;
    private string _projectStateText = string.Empty;
    private int _projectPosition;
    private int _projectCount;
    private TodayLane? _todayLane;
    private bool _isArchived;
    private bool _isOverdue;

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
        ArchiveCommand = new(() => owner.RequestArchiveTask(id));
        RestoreCommand = new(() => owner.RestoreTask(id));
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
    public string CategoryColourKey => _categoryColourKey;
    public string CategoryDisplay => _categoryDisplay;
    public string? ProjectTitle => _projectTitle;
    public string ProjectColourKey => _projectColourKey;
    public string RelationshipText => _projectTitle ?? "Standalone";
    public string BroadRelationshipText => _projectTitle is null
        ? $"Standalone · {CategoryName}"
        : RelationshipText;
    public string CategoryGroupRelationshipText => ProjectId is null ? "Standalone" : RelationshipText;
    public bool HasCategoryOverride => _projectTitle is not null && !_usesInheritedCategory;
    public string CategoryOverrideText => HasCategoryOverride ? CategoryName : string.Empty;
    public string AccessibleName => string.Join(". ", new[]
    {
        $"Task {Title}",
        _projectTitle is null
            ? $"Standalone. Category {CategoryName}"
            : HasCategoryOverride
                ? $"Project {_projectTitle}. Category override {CategoryName}"
                : $"Project {_projectTitle}. Inherited Category {CategoryName}",
        HasProjectState ? ProjectStateAccessibleText : IsComplete ? "Complete" : IsArchived ? "Archived" : "Incomplete",
        DateAccessibleText,
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public string PositionText => $"{_position} of {_count}";
    public bool IsLast => _position == _count;
    public string ReorderAccessibleName => $"Reorder {Title} in Backlog";
    public string ReorderAutomationId => $"backlog-reorder-{Id}";
    public string CompletionAutomationId => $"task-completion-{Id}";
    public string CompletionAccessibleName => IsArchived
        ? $"{Title} is archived. Restore it before reopening."
        : $"{(IsComplete ? "Reopen" : "Complete")} {Title}";
    public bool IsComplete => _isComplete;
    public bool IsArchived => _isArchived;
    public bool IsOverdue => _isOverdue;
    public bool CanArchive => IsComplete && !IsArchived;
    public string ArchiveAutomationId => $"task-archive-{Id}";
    public string RestoreAutomationId => $"task-restore-{Id}";
    public string ArchiveAccessibleName => $"Archive {Title}";
    public string RestoreAccessibleName => $"Restore {Title} to Completed";
    public bool IsToday => _todayLane is not null;
    public string TodayAutomationId => $"task-today-{Id}";
    public string TodayAccessibleName => $"{(IsToday ? "Remove" : "Add")} {Title} {(IsToday ? "from" : "to")} Today";
    public string DateText => _dateText;
    public string DateAccessibleText => _dateAccessibleText;
    public string CompletionDateText => _completionDateText;
    public bool HasCompletionDate => !string.IsNullOrEmpty(CompletionDateText);
    public string ProjectStateText => _projectStateText;
    public string ProjectStateAccessibleText => IsOverdue ? "Overdue" : ProjectStateText;
    public bool HasProjectState => !string.IsNullOrEmpty(ProjectStateText);
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
    public RelayCommand ArchiveCommand { get; }
    public RelayCommand RestoreCommand { get; }
    public RelayCommand MoveProjectTaskUpCommand { get; }
    public RelayCommand MoveProjectTaskDownCommand { get; }
    public RelayCommand MoveProjectTaskToTopCommand { get; }
    public RelayCommand MoveProjectTaskToBottomCommand { get; }

    public void Refresh(
        TaskRecord task,
        string categoryName,
        string categoryColourKey,
        string? projectTitle,
        string projectColourKey,
        bool usesInheritedCategory,
        DateOnly today)
    {
        _title = task.Title;
        _categoryName = categoryName;
        _categoryColourKey = categoryColourKey;
        _projectTitle = projectTitle;
        _projectColourKey = projectColourKey;
        _usesInheritedCategory = usesInheritedCategory;
        _categoryDisplay = BroadRelationshipText;
        _projectId = task.ProjectId;
        _isComplete = task.IsComplete;
        _isArchived = task.IsArchived;
        _isOverdue = !task.IsComplete && !task.IsArchived && task.DueDate is { } dueDate && dueDate < today;
        _todayLane = task.TodayLane;
        _dateText = WorkDatePresentation.Relative(task.DueDate, today);
        _dateAccessibleText = WorkDatePresentation.Accessible(task.DueDate, "Due");
        _completionDateText = task.CompletionDate is { } completionDate ? $"Completed {completionDate:d MMM yyyy}" : string.Empty;
        _projectStateText = task.IsArchived && task.ArchiveDate is { } archiveDate
            ? $"Archived {archiveDate:d MMM yyyy}"
            : task.IsComplete
                ? _completionDateText
                : _isOverdue ? "⚠ Overdue" : string.Empty;
        Notify(nameof(Title));
        Notify(nameof(CategoryName));
        Notify(nameof(CategoryColourKey));
        Notify(nameof(CategoryDisplay));
        Notify(nameof(ProjectTitle));
        Notify(nameof(ProjectColourKey));
        Notify(nameof(RelationshipText));
        Notify(nameof(BroadRelationshipText));
        Notify(nameof(CategoryGroupRelationshipText));
        Notify(nameof(HasCategoryOverride));
        Notify(nameof(CategoryOverrideText));
        Notify(nameof(AccessibleName));
        Notify(nameof(ReorderAccessibleName));
        Notify(nameof(MoveUpAccessibleName));
        Notify(nameof(MoveDownAccessibleName));
        Notify(nameof(MoveToTopAccessibleName));
        Notify(nameof(MoveToBottomAccessibleName));
        Notify(nameof(CompletionAccessibleName));
        Notify(nameof(IsComplete));
        Notify(nameof(IsArchived));
        Notify(nameof(IsOverdue));
        Notify(nameof(CanArchive));
        Notify(nameof(ArchiveAccessibleName));
        Notify(nameof(RestoreAccessibleName));
        Notify(nameof(IsToday));
        Notify(nameof(TodayAccessibleName));
        Notify(nameof(DateText));
        Notify(nameof(DateAccessibleText));
        Notify(nameof(CompletionDateText));
        Notify(nameof(HasCompletionDate));
        Notify(nameof(ProjectStateText));
        Notify(nameof(ProjectStateAccessibleText));
        Notify(nameof(HasProjectState));
        Notify(nameof(ProjectReorderAccessibleName));
        Notify(nameof(MoveProjectTaskUpAccessibleName));
        Notify(nameof(MoveProjectTaskDownAccessibleName));
        Notify(nameof(MoveProjectTaskToTopAccessibleName));
        Notify(nameof(MoveProjectTaskToBottomAccessibleName));
        Notify(nameof(AccessibleName));
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
        ArchiveCommand = new(() => owner.ArchiveProject(id));
        RestoreCommand = new(() => owner.RestoreProject(id));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id { get; }
    public string Title { get; private set; } = string.Empty;
    public string Summary { get; private set; } = string.Empty;
    public string Status { get; private set; } = string.Empty;
    public string ProgressText { get; private set; } = string.Empty;
    public string AccessibleStatus => $"{Status}, {ProgressText}";
    public string CategoryName { get; private set; } = string.Empty;
    public string CategoryColourKey { get; private set; } = IdentityColourPalette.DefaultKey;
    public string ColourKey { get; private set; } = IdentityColourPalette.KeyForProjectPosition(0);
    public int TaskCount { get; private set; }
    public string RelationshipText =>
        $"{CategoryName} · {TaskCount} {(TaskCount == 1 ? "Task" : "Tasks")}";
    public string StatusText => $"{Status} · {ProgressText}";
    public string MetadataText => StatusText
        + (string.IsNullOrEmpty(TargetText) ? string.Empty : $" · {TargetText}");
    public string AccessibleName => string.Join(". ", new[]
    {
        $"Project {Title}",
        $"Category {CategoryName}",
        $"{TaskCount} {(TaskCount == 1 ? "Task" : "Tasks")}",
        Status,
        ProgressText,
        TargetAccessibleText,
        IsOverdue ? "Overdue" : string.Empty,
        IsArchived ? "Archived" : string.Empty,
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public string TargetText { get; private set; } = string.Empty;
    public string TargetAccessibleText { get; private set; } = string.Empty;
    public bool IsOverdue { get; private set; }
    public bool IsNotStarted => Status == "Not started";
    public bool IsInProgress => Status == "In progress";
    public bool IsComplete => Status == "Complete";
    public bool IsArchived { get; private set; }
    public string OverdueText => IsOverdue ? "Overdue" : string.Empty;
    public string CompletionDateText { get; private set; } = string.Empty;
    public bool HasCompletionDate => !string.IsNullOrEmpty(CompletionDateText);
    public string ReorderAccessibleName => $"Reorder {Title} in Projects";
    public string ReorderAutomationId => $"project-reorder-{Id}";
    public string MoveUpAccessibleName => $"Move {Title} up in Projects";
    public string MoveDownAccessibleName => $"Move {Title} down in Projects";
    public string MoveToTopAccessibleName => $"Move {Title} to top of Projects";
    public string MoveToBottomAccessibleName => $"Move {Title} to bottom of Projects";
    public string ArchiveAutomationId => $"project-archive-{Id}";
    public string RestoreAutomationId => $"project-restore-{Id}";
    public string ArchiveAccessibleName => $"Archive Project {Title}";
    public string RestoreAccessibleName => $"Restore Project {Title} to Projects";
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
    public RelayCommand ArchiveCommand { get; }
    public RelayCommand RestoreCommand { get; }
    public ObservableCollection<TaskRowViewModel> Tasks { get; } = [];
    public bool Submit()
    {
        var submittedTitle = QuickTitle;
        var submission = _owner.QuickAddAsync(Id, submittedTitle);
        if (submission.IsCompleted)
        {
            if (!submission.GetAwaiter().GetResult()) return false;
            if (string.Equals(QuickTitle, submittedTitle, StringComparison.Ordinal)) QuickTitle = string.Empty;
            return true;
        }
        _ = CompleteSubmissionAsync(submission, submittedTitle);
        return true;
    }

    private async Task CompleteSubmissionAsync(Task<bool> submission, string submittedTitle)
    {
        if (await submission && string.Equals(QuickTitle, submittedTitle, StringComparison.Ordinal))
            QuickTitle = string.Empty;
    }
    public void Refresh(ProjectRecord project, ProjectWorkSummary summary, WorkspaceCategory category, IEnumerable<TaskRowViewModel> tasks)
    {
        Title = project.Title;
        Status = summary.Status;
        IsArchived = project.IsArchived;
        ProgressText = $"{summary.CompletedCount}/{summary.TaskCount} tasks";
        CategoryName = category.Name;
        CategoryColourKey = category.ColourKey;
        ColourKey = project.ColourKey;
        TaskCount = summary.TaskCount;
        TargetText = WorkDatePresentation.Relative(project.TargetDate, _owner.Today);
        TargetAccessibleText = WorkDatePresentation.Accessible(project.TargetDate, "Target");
        IsOverdue = WorkDatePresentation.IsOverdue(project, summary, _owner.Today);
        CompletionDateText = summary.CompletionDate is { } completionDate ? $"Completed {completionDate:d MMM yyyy}" : string.Empty;
        var desiredTasks = tasks.ToArray();
        for (var index = Tasks.Count - 1; index >= 0; index--)
            if (!desiredTasks.Contains(Tasks[index])) Tasks.RemoveAt(index);
        for (var index = 0; index < desiredTasks.Length; index++)
        {
            var current = Tasks.IndexOf(desiredTasks[index]);
            if (current < 0) Tasks.Insert(index, desiredTasks[index]);
            else if (current != index) Tasks.Move(current, index);
        }
        for (var index = 0; index < Tasks.Count; index++) Tasks[index].SetProjectPosition(index + 1, Tasks.Count);
        Summary = $"{summary.Status} · {summary.CompletedCount} of {summary.TaskCount} Tasks · {category.Name}"
            + (string.IsNullOrEmpty(TargetText) ? string.Empty : $" · {TargetText}");
        PropertyChanged?.Invoke(this, new(nameof(Title)));
        PropertyChanged?.Invoke(this, new(nameof(Summary)));
        PropertyChanged?.Invoke(this, new(nameof(Status)));
        PropertyChanged?.Invoke(this, new(nameof(IsNotStarted)));
        PropertyChanged?.Invoke(this, new(nameof(IsInProgress)));
        PropertyChanged?.Invoke(this, new(nameof(IsComplete)));
        PropertyChanged?.Invoke(this, new(nameof(IsArchived)));
        PropertyChanged?.Invoke(this, new(nameof(ProgressText)));
        PropertyChanged?.Invoke(this, new(nameof(AccessibleStatus)));
        PropertyChanged?.Invoke(this, new(nameof(CategoryName)));
        PropertyChanged?.Invoke(this, new(nameof(CategoryColourKey)));
        PropertyChanged?.Invoke(this, new(nameof(ColourKey)));
        PropertyChanged?.Invoke(this, new(nameof(TaskCount)));
        PropertyChanged?.Invoke(this, new(nameof(RelationshipText)));
        PropertyChanged?.Invoke(this, new(nameof(StatusText)));
        PropertyChanged?.Invoke(this, new(nameof(MetadataText)));
        PropertyChanged?.Invoke(this, new(nameof(AccessibleName)));
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
        PropertyChanged?.Invoke(this, new(nameof(ArchiveAccessibleName)));
        PropertyChanged?.Invoke(this, new(nameof(RestoreAccessibleName)));
        Notify(nameof(ExpansionAccessibleName));
    }

    public void SetPosition(int position, int count)
    {
        _position = position;
        _count = count;
    }

    private void Notify(string name) => PropertyChanged?.Invoke(this, new(name));
}
