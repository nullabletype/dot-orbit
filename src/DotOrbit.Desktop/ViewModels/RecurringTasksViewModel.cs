using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DotOrbit.Core.Workspaces;
using DotOrbit.Markdown;

namespace DotOrbit.Desktop.ViewModels;

/// <summary>Explicit recurring-definition drafts; generated Tasks remain in the ordinary work projections.</summary>
public sealed class RecurringTasksViewModel : INotifyPropertyChanged
{
    private readonly bool _available;
    private readonly TimeProvider _time;
    private readonly Func<WorkspaceActionRequest, bool> _submit;
    private readonly Action<Action> _navigate;
    private readonly Action<string> _focus;
    private readonly RecurringDateReconciler _reconciler;
    private WorkspaceWorkSnapshot _snapshot = new([], [], []);
    private IReadOnlyList<RecurringTaskDefinition> _definitions = [];
    private IReadOnlyList<ProjectRecord> _allProjects = [];
    private string? _editingId;
    private DateOnly? _originalFirst;
    private RecurringDraftFingerprint? _original;
    private string _title = string.Empty;
    private string _description = string.Empty;
    private string _firstDate = string.Empty;
    private string _offset = "0";
    private ParticipantChoice? _participantToAdd;
    private string _newParticipantLabel = string.Empty;
    private string _participantValidation = string.Empty;
    private int _participantDraftSequence;
    private bool _hasDueDate;
    private bool _enabled = true;
    private bool _hasEditor;
    private bool _busy;
    private bool _loading;
    private bool _editingMarkdown;
    private RecurrenceCadence _cadence;
    private TaskContextChoice? _project;
    private CategoryChoice? _category;
    private string _validation = string.Empty;
    private string _validationField = string.Empty;
    private string _announcement = string.Empty;
    private RecurringTaskDefinition? _deleting;
    private Action? _pendingNavigation;
    private Action? _pendingDefinitionAction;
    private Action? _afterWorkspaceAction;
    private bool _saveThenLeave;
    private bool _waitingForRefresh;

    internal RecurringTasksViewModel(IWorkspaceWork work, TimeProvider time,
        Func<WorkspaceActionRequest, bool> submit, Action<Action> navigate, Action<string> focus)
    {
        _available = work is IRecurringTasks;
        _time = time;
        _submit = submit;
        _navigate = navigate;
        _focus = focus;
        _reconciler = new(time, () => _submit(new(WorkspaceActionKind.ProcessRecurring)));
        NewCommand = new(() => _navigate(() => Begin(null)));
        SaveCommand = new(() => Save());
        CancelCommand = new(Cancel);
        AddParticipantCommand = new(AddParticipant);
        AddNewParticipantCommand = new(AddNewParticipant);
        CancelNewParticipantCommand = new(() => { NewParticipantLabel = string.Empty; ParticipantToAdd = null; _focus("recurring-participant-picker"); });
        ConfirmDeleteCommand = new(ConfirmDelete);
        CancelDeleteCommand = new(CancelDelete);
        StayCommand = new(() => { _pendingNavigation = null; Notify(nameof(NeedsDecision)); _focus("recurring-title"); });
        DiscardAndLeaveCommand = new(() => { var next = _pendingNavigation; _pendingNavigation = null; Notify(nameof(NeedsDecision)); Cancel(); next?.Invoke(); });
        SaveAndLeaveCommand = new(() => { _saveThenLeave = true; if (!Save()) _saveThenLeave = false; });
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<RecurringTaskRowViewModel> Definitions { get; } = [];
    public ObservableCollection<TaskContextChoice> Projects { get; } = [];
    public ObservableCollection<CategoryChoice> Categories { get; } = [];
    public ObservableCollection<RecurringParticipantChoice> Participants { get; } = [];
    public IEnumerable<RecurringParticipantChoice> SelectedParticipants => Participants.Where(item => item.IsSelected);
    public bool HasSelectedParticipants => SelectedParticipants.Any();
    public ObservableCollection<ParticipantChoice> AvailableParticipants { get; } = [];
    public RelayCommand AddParticipantCommand { get; }
    public RelayCommand AddNewParticipantCommand { get; }
    public RelayCommand CancelNewParticipantCommand { get; }
    public ParticipantChoice? ParticipantToAdd
    {
        get => _participantToAdd;
        set
        {
            var wasNew = _participantToAdd?.IsNew == true;
            Set(ref _participantToAdd, value);
            ParticipantValidation = string.Empty;
            Notify(nameof(ShowNewParticipantEntry)); Notify(nameof(ShowExistingParticipantAction));
            if (value?.IsNew == true && !wasNew) _focus("recurring-new-participant-label");
        }
    }
    public bool ShowNewParticipantEntry => ParticipantToAdd?.IsNew == true || NewParticipantLabel.Length > 0;
    public bool ShowExistingParticipantAction => ParticipantToAdd is { IsNew: false };
    public string NewParticipantLabel { get => _newParticipantLabel; set { Set(ref _newParticipantLabel, value); ParticipantValidation = string.Empty; Notify(nameof(ShowNewParticipantEntry)); } }
    public string ParticipantValidation { get => _participantValidation; private set { _participantValidation = value; Notify(); Notify(nameof(HasParticipantError)); } }
    public bool HasParticipantError => ParticipantValidation.Length > 0;
    public IReadOnlyList<RecurrenceCadence> Cadences { get; } = Enum.GetValues<RecurrenceCadence>();
    public RelayCommand NewCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ConfirmDeleteCommand { get; }
    public RelayCommand CancelDeleteCommand { get; }
    public RelayCommand StayCommand { get; }
    public RelayCommand DiscardAndLeaveCommand { get; }
    public RelayCommand SaveAndLeaveCommand { get; }
    public bool HasEditor { get => _hasEditor; private set { _hasEditor = value; Notify(); UpdateSelectedRows(); } }
    public bool HasNoEditor => !HasEditor;
    public bool CanEdit => !_busy;
    public bool CanSave => CanEdit && (!HasDueDate || IsDueOffsetValid);
    public bool HasDefinitions => Definitions.Count > 0;
    public bool HasNoDefinitions => !HasDefinitions;
    public bool NeedsDeleteConfirmation => _deleting is not null;
    public bool NeedsDecision => _pendingNavigation is not null;
    public bool IsDirty => HasEditor && Fingerprint() != _original;
    public string EditorHeading => _editingId is null ? "NEW RECURRING TASK" : "RECURRING TASK";
    public string DeleteHeading => $"Delete {_deleting?.Title}?";
    public string Validation
    {
        get => _validation;
        private set
        {
            _validation = value;
            if (value.Length == 0) _validationField = string.Empty;
            Notify();
            Notify(nameof(HasValidation));
            Notify(nameof(HasTitleError)); Notify(nameof(HasFirstDateError)); Notify(nameof(HasOffsetError));
            Notify(nameof(HasCategoryError)); Notify(nameof(HasProjectError));
        }
    }
    public bool HasValidation => Validation.Length > 0;
    public bool HasTitleError => _validationField == "recurring-title";
    public bool HasFirstDateError => _validationField == "recurring-first-date";
    public bool HasOffsetError => _validationField == "recurring-due-offset" || (HasDueDate && !IsDueOffsetValid);
    public bool HasCategoryError => _validationField == "recurring-category";
    public bool HasProjectError => _validationField == "recurring-project";
    private bool Invalid(string message, string field)
    {
        _validationField = field;
        Validation = message;
        _focus(field);
        return false;
    }
    public string Announcement { get => _announcement; private set { _announcement = value; Notify(); } }
    public string Title { get => _title; set => Set(ref _title, value); }
    public string Description
    {
        get => _description;
        set { if (Set(ref _description, value)) Notify(nameof(RenderedDescription)); }
    }
    public SanitisedMarkdownDocument RenderedDescription => SanitisedMarkdownRenderer.Render(Description);
    public bool IsEditingMarkdown
    {
        get => _editingMarkdown;
        set { if (Set(ref _editingMarkdown, value)) Notify(nameof(ShowMarkdownPreview)); }
    }
    public bool ShowMarkdownPreview => !IsEditingMarkdown;
    public string FirstDate { get => _firstDate; set { if (Set(ref _firstDate, value)) NotifyOffsetValidation(); } }
    public decimal MaximumDueOffset => DateOnly.MaxValue.DayNumber - Math.Max(Today.DayNumber,
        DateOnly.TryParseExact(FirstDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first) ? first.DayNumber : Today.DayNumber);
    private bool IsDueOffsetValid => int.TryParse(DueOffset, NumberStyles.None, CultureInfo.InvariantCulture, out var days) && days >= 0 && days <= MaximumDueOffset;
    public string DueOffset { get => _offset; set { if (Set(ref _offset, value)) NotifyOffsetValidation(); } }
    public bool HasDueDate { get => _hasDueDate; set { if (Set(ref _hasDueDate, value)) NotifyOffsetValidation(); } }
    public string DueOffsetValidation => HasDueDate && !IsDueOffsetValid ? "Enter a non-negative whole number of days within the supported date range." : string.Empty;
    private void NotifyOffsetValidation()
    {
        Notify(nameof(MaximumDueOffset)); Notify(nameof(DueOffsetValidation)); Notify(nameof(HasOffsetError)); Notify(nameof(CanSave));
    }
    public bool IsEnabled { get => _enabled; set => Set(ref _enabled, value); }
    public RecurrenceCadence Cadence { get => _cadence; set => Set(ref _cadence, value); }
    public CategoryChoice? Category { get => _category; set => Set(ref _category, value); }
    public TaskContextChoice? Project
    {
        get => _project;
        set
        {
            if (!Set(ref _project, value) || _loading) return;
            RefreshCategories(null);
        }
    }

    internal void Refresh(WorkspaceWorkSnapshot snapshot, IReadOnlyList<RecurringTaskDefinition>? definitions,
        IReadOnlyList<ProjectBinRecord>? projectBin = null)
    {
        _snapshot = snapshot;
        _allProjects = snapshot.Projects.Concat((projectBin ?? []).Select(item => item.Project)).ToArray();
        if (definitions is not null) _definitions = definitions;
        Definitions.Clear();
        for (var index = 0; index < _definitions.Count; index++)
            Definitions.Add(new(this, _definitions[index], snapshot with { Projects = _allProjects }, index == _definitions.Count - 1));
        UpdateSelectedRows();
        Notify(nameof(HasDefinitions));
        Notify(nameof(HasNoDefinitions));
        // Keep an explicit draft stable while background reconciliation refreshes the list.
        if (!HasEditor) return;
        var selected = Participants.Where(item => item.IsSelected).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var participant in Participants)
            participant.UpdateLabel(snapshot.Participants.FirstOrDefault(item => item.Id == participant.Id)?.Label ?? participant.Label);
        foreach (var participant in snapshot.Participants.Where(item => Participants.All(choice => choice.Id != item.Id)))
            AddParticipantChoice(participant.Id, participant.Label, selected.Contains(participant.Id));
        RefreshAvailableParticipants();
    }

    internal bool ReferencesCategory(string id) => _definitions.Any(item => (item.ExplicitCategoryId
        ?? _allProjects.FirstOrDefault(project => project.Id == item.ProjectId)?.CategoryId) == id);

    internal bool GuardNavigation(Action destination)
    {
        // Leaving the current context cancels a suspended row action; a failed read
        // must never prevent ordinary navigation, recovery, or closing the workspace.
        _pendingDefinitionAction = null;
        if (_busy)
        {
            _pendingNavigation = destination;
            _saveThenLeave = true;
            return true;
        }
        if (NeedsDeleteConfirmation) { Announcement = "Finish the recurring Task change before leaving."; return true; }
        if (!IsDirty) { if (HasEditor) Cancel(); return false; }
        _pendingNavigation = destination;
        Notify(nameof(NeedsDecision));
        _focus("recurring-guard-save");
        return true;
    }

    private RecurringTaskDefinition? CurrentDefinition(string id)
    {
        var definition = _definitions.SingleOrDefault(item => item.Id == id);
        if (definition is null) Announcement = "This recurring definition is no longer available. Refresh the view.";
        return definition;
    }
    private void NavigateDefinitionAction(Action action) => _navigate(() =>
    {
        // Every final row callback passes this boundary, including after save,
        // discard, refresh, and any future navigation continuation.
        if (_waitingForRefresh)
        {
            _pendingDefinitionAction = () => NavigateDefinitionAction(action);
            Announcement = "Change saved, but the view could not refresh. Retry refresh before continuing this action.";
            return;
        }
        action();
    });
    internal void Edit(string id) => NavigateDefinitionAction(() =>
    {
        if (CurrentDefinition(id) is { } current) Begin(current);
    });
    private void Begin(RecurringTaskDefinition? definition)
    {
        _loading = true;
        _editingId = definition?.Id;
        _originalFirst = definition?.FirstOccurrenceDate;
        Title = definition?.Title ?? string.Empty;
        Description = definition?.Description ?? string.Empty;
        IsEditingMarkdown = false;
        FirstDate = (definition?.FirstOccurrenceDate ?? Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Cadence = definition?.Cadence ?? RecurrenceCadence.Daily;
        HasDueDate = definition?.DueDateOffsetDays is not null;
        DueOffset = (definition?.DueDateOffsetDays ?? 0).ToString(CultureInfo.InvariantCulture);
        IsEnabled = definition?.IsEnabled ?? true;
        Projects.Clear();
        Projects.Add(new(null, "Standalone"));
        foreach (var project in _snapshot.Projects.Where(item => !item.IsArchived)) Projects.Add(new(project.Id, project.Title));
        if (definition?.ProjectId is { } id && Projects.All(item => item.ProjectId != id))
            Projects.Add(new(id, "Unavailable Project — restore or reassign"));
        Project = Projects.Single(item => item.ProjectId == definition?.ProjectId);
        RefreshCategories(definition?.ExplicitCategoryId);
        Participants.Clear();
        foreach (var participant in _snapshot.Participants)
            AddParticipantChoice(participant.Id, participant.Label, definition?.ParticipantIds.Contains(participant.Id) ?? false);
        ParticipantToAdd = null;
        RefreshAvailableParticipants();
        NewParticipantLabel = string.Empty;
        Notify(nameof(SelectedParticipants));
        Notify(nameof(HasSelectedParticipants));
        Validation = string.Empty;
        HasEditor = true;
        Notify(nameof(HasNoEditor));
        Notify(nameof(EditorHeading));
        _original = Fingerprint();
        _loading = false;
        _focus("recurring-title");
    }

    private void RefreshCategories(string? categoryId)
    {
        Categories.Clear();
        if (Project?.ProjectId is not null) Categories.Add(new(null, "Inherit Project Category"));
        foreach (var category in _snapshot.Categories) Categories.Add(new(category.Id, category.Name, category.ColourKey));
        Category = Categories.FirstOrDefault(item => item.Id == categoryId);
    }

    private void RefreshAvailableParticipants()
    {
        var selected = ParticipantToAdd;
        AvailableParticipants.Clear();
        foreach (var participant in Participants.Where(item => !item.IsDraft || !item.IsSelected))
            AvailableParticipants.Add(new(participant.Id, participant.Label));
        AvailableParticipants.Add(ParticipantChoice.New);
        ParticipantToAdd = selected?.IsNew == true ? ParticipantChoice.New
            : AvailableParticipants.FirstOrDefault(item => item.Id == selected?.Id && !item.IsNew);
    }

    private void AddParticipantChoice(string id, string label, bool selected, bool draft = false)
    {
        var choice = new RecurringParticipantChoice(id, label, selected, item =>
        {
            item.IsSelected = false;
            Announcement = $"Removed {item.Label} from the recurring Task draft.";
            _focus("recurring-participant-picker");
        }, draft);
        choice.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(RecurringParticipantChoice.IsSelected)) return;
            Notify(nameof(SelectedParticipants));
            Notify(nameof(HasSelectedParticipants));
            RefreshAvailableParticipants();
        };
        Participants.Add(choice);
        Notify(nameof(SelectedParticipants));
        Notify(nameof(HasSelectedParticipants));
    }
    private void AddParticipant()
    {
        if (ParticipantToAdd is not { IsNew: false, Id: { } id } choice) return;
        var participant = Participants.Single(item => item.Id == id);
        if (participant.IsSelected) { ParticipantValidation = "This Participant is already selected."; return; }
        participant.IsSelected = true;
        Announcement = $"Added {choice.Label} to the recurring Task draft.";
        ParticipantToAdd = null;
        _focus("recurring-participant-picker");
    }
    private void AddNewParticipant()
    {
        string label;
        try { label = ParticipantLabel.Normalize(NewParticipantLabel); }
        catch (ArgumentException) { ParticipantValidation = "Enter a Participant label."; return; }
        var key = ParticipantLabel.ComparisonKey(label);
        var existing = Participants.FirstOrDefault(item => ParticipantLabel.ComparisonKey(item.Label) == key);
        if (existing?.IsSelected == true) { ParticipantValidation = "This Participant is already selected."; return; }
        if (existing is not null) existing.IsSelected = true;
        else AddParticipantChoice($"draft-{_participantDraftSequence++}", label, true, draft: true);
        Announcement = existing is null ? $"Created and added {label} to the recurring Task draft." : $"Added existing Participant {existing.Label} to the recurring Task draft.";
        NewParticipantLabel = string.Empty;
        ParticipantToAdd = null;
        _focus("recurring-participant-picker");
    }
    private void UpdateSelectedRows()
    {
        foreach (var row in Definitions) row.SetSelected(HasEditor && row.Id == _editingId);
    }

    internal DateOnly CurrentDate => Today;
    private DateOnly Today => DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
    private bool Save()
    {
        if (_busy || !HasEditor) return false;
        if (!string.IsNullOrWhiteSpace(NewParticipantLabel))
        {
            ParticipantValidation = "Add or cancel the new Participant before saving.";
            _focus("recurring-new-participant-label");
            return false;
        }
        string title;
        try { title = WorkTitle.Normalize(Title); }
        catch (ArgumentException) { return Invalid("Enter a valid nonblank title.", "recurring-title"); }
        if (!DateOnly.TryParseExact(FirstDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first)
            || (first < Today && first != _originalFirst))
        { return Invalid("Enter a first occurrence date of today or later (YYYY-MM-DD).", "recurring-first-date"); }
        int? offset = null;
        if (HasDueDate)
        {
            if (!int.TryParse(DueOffset, NumberStyles.None, CultureInfo.InvariantCulture, out var days) || days < 0
                || days > DateOnly.MaxValue.DayNumber - Math.Max(first.DayNumber, Today.DayNumber))
            { return Invalid("Enter a non-negative whole-day offset that produces a valid due date.", "recurring-due-offset"); }
            offset = days;
        }
        if (Project?.ProjectId is null && Category?.Id is null)
        { return Invalid("Choose a Category for this standalone definition.", "recurring-category"); }
        if (Project?.ProjectId is { } projectId && !_snapshot.Projects.Any(item => item.Id == projectId && !item.IsArchived))
        { return Invalid("Restore the Project first, or choose an active Project or Standalone.", "recurring-project"); }
        var draft = new RecurringTaskDraft(title, Description, Project?.ProjectId, Category?.Id,
            Participants.Where(item => item.IsSelected && !item.IsDraft).Select(item => item.Id).ToArray(), Cadence, first, offset, IsEnabled,
            Participants.Where(item => item.IsSelected && item.IsDraft).Select(item => item.Label).ToArray());
        Validation = string.Empty;
        return Submit(new(_editingId is null ? WorkspaceActionKind.CreateRecurring : WorkspaceActionKind.UpdateRecurring,
            RecurringId: _editingId, RecurringDraft: draft));
    }

    internal void SetEnabled(string id, bool enabled) => NavigateDefinitionAction(() =>
        Submit(new(WorkspaceActionKind.SetRecurringEnabled, RecurringId: id, RecurringEnabled: enabled)));
    internal void RequestDelete(string id) => NavigateDefinitionAction(() =>
    {
        if (CurrentDefinition(id) is not { } current) return;
        _deleting = current;
        Notify(nameof(NeedsDeleteConfirmation));
        Notify(nameof(DeleteHeading));
        _focus("recurring-confirm-delete");
    });
    private void ConfirmDelete()
    {
        if (_deleting is null || _busy) return;
        Submit(new(WorkspaceActionKind.DeleteRecurring, RecurringId: _deleting.Id));
    }
    private void CancelDelete()
    {
        var id = _deleting?.Id;
        _deleting = null;
        Notify(nameof(NeedsDeleteConfirmation));
        _focus(id is null ? "recurring-new" : $"recurring-delete-{id}");
    }
    private bool Submit(WorkspaceActionRequest request)
    {
        _busy = true;
        Notify(nameof(CanEdit));
        Notify(nameof(CanSave));
        if (_submit(request)) return true;
        _busy = false;
        Notify(nameof(CanEdit));
        Notify(nameof(CanSave));
        Announcement = "Could not start the recurring Task change. Try again.";
        return false;
    }
    private void Cancel()
    {
        HasEditor = false;
        Notify(nameof(HasNoEditor));
        Validation = string.Empty;
        _focus(_editingId is null ? "recurring-new" : $"recurring-edit-{_editingId}");
    }

    internal void ReconcileHint() { if (_available) _reconciler.Hint(); }
    internal void ApplyResult(WorkspaceActionResult result)
    {
        var processing = result.Request.Kind == WorkspaceActionKind.ProcessRecurring;
        var committed = result.Outcome is WorkspaceActionOutcome.Committed or WorkspaceActionOutcome.CommittedRefreshFailed;
        if (result.Outcome == WorkspaceActionOutcome.CommittedRefreshFailed)
        {
            _waitingForRefresh = true;
            Notify(nameof(NeedsDecision));
        }
        if (processing) _reconciler.Complete(committed);
        else { _busy = false; Notify(nameof(CanEdit)); Notify(nameof(CanSave)); }
        if (!committed)
        {
            Announcement = processing ? "Could not add scheduled Tasks. Will retry while this workspace is unlocked."
                : "Could not save the recurring Task change. Check its references and try again.";
            if (processing) return;
            Validation = Announcement;
            _saveThenLeave = false;
            if (_pendingNavigation is { } pending)
            {
                if (IsDirty)
                {
                    Notify(nameof(NeedsDecision));
                    _focus("recurring-guard-save");
                }
                else
                {
                    _pendingNavigation = null;
                    Notify(nameof(NeedsDecision));
                    pending();
                }
            }
            return;
        }
        Announcement = result.Outcome == WorkspaceActionOutcome.CommittedRefreshFailed
            ? "Change saved, but the view could not refresh. Retry refresh."
            : processing ? result.GeneratedTaskCount == 0 ? Announcement
                : $"Added {result.GeneratedTaskCount} scheduled {(result.GeneratedTaskCount == 1 ? "Task" : "Tasks")} to Backlog."
            : result.Request.Kind == WorkspaceActionKind.DeleteRecurring ? "Recurring definition deleted. Existing Tasks are unchanged."
                : "Recurring definition saved. Existing Tasks are unchanged.";
        if (!processing)
        {
            if (_deleting is not null) { _deleting = null; Notify(nameof(NeedsDeleteConfirmation)); }
            Cancel();
            _focus("recurring-new");
            if (_saveThenLeave)
            {
                _saveThenLeave = false;
                _afterWorkspaceAction = CompletePendingNavigation;
            }
            _reconciler.Invalidate();
            if (result.Outcome == WorkspaceActionOutcome.Committed) ReconcileHint();
        }
    }

    internal void CompleteRefresh()
    {
        if (!_waitingForRefresh) return;
        _waitingForRefresh = false;
        var action = _pendingDefinitionAction;
        _pendingDefinitionAction = null;
        if (action is not null) _afterWorkspaceAction = action;
    }
    internal void CompleteActionContinuations()
    {
        // Run only after the owning queue has accounted for the accepted result.
        // Navigation can then pass a settled queue without closing read admission.
        var action = _afterWorkspaceAction;
        _afterWorkspaceAction = null;
        action?.Invoke();
    }
    private void CompletePendingNavigation()
    {
        var next = _pendingNavigation;
        _pendingNavigation = null;
        Notify(nameof(NeedsDecision));
        next?.Invoke();
    }

    private RecurringDraftFingerprint Fingerprint() => new(Title, Description, FirstDate, Cadence, Project?.ProjectId,
        Category?.Id, HasDueDate, DueOffset, IsEnabled, string.Join(',', Participants.Where(item => item.IsSelected && !item.IsDraft).Select(item => item.Id)),
        JsonSerializer.Serialize(Participants.Where(item => item.IsSelected && item.IsDraft).Select(item => item.Label)), NewParticipantLabel);
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

/// <summary>A timer or activation is only a hint. Persisted next dates decide what is due.</summary>
internal sealed class RecurringDateReconciler(TimeProvider time, Func<bool> submit)
{
    private DateOnly? _date;
    private string? _zone;
    private bool _pending;
    private bool _retry = true;
    public void Invalidate() => _retry = true;
    public void Hint()
    {
        var date = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
        var zone = time.LocalTimeZone.Id;
        if (_pending || (!_retry && _date == date && _zone == zone)) return;
        _pending = true;
        _date = date;
        _zone = zone;
        _retry = false;
        if (!submit()) { _pending = false; _retry = true; }
    }
    public void Complete(bool committed) { _pending = false; _retry = !committed; }
}

public sealed class RecurringParticipantChoice(string id, string label, bool selected,
    Action<RecurringParticipantChoice>? remove = null, bool isDraft = false) : INotifyPropertyChanged
{
    private bool _selected = selected;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id { get; } = id;
    public string Label { get; private set; } = label;
    public bool IsDraft { get; } = isDraft;
    public RelayCommand RemoveCommand => new(() => remove?.Invoke(this));
    public string RemoveAccessibleName => $"Remove {Label} from recurring Task draft";
    public string AccessibleName => $"Include Participant {Label}";
    public bool IsSelected { get => _selected; set { _selected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); } }
    internal void UpdateLabel(string label) { Label = label; PropertyChanged?.Invoke(this, new(nameof(Label))); PropertyChanged?.Invoke(this, new(nameof(AccessibleName))); }
}

public sealed class RecurringTaskRowViewModel : INotifyPropertyChanged
{
    private bool _selected;
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsSelected => _selected;
    public string SelectionStatus => IsSelected ? $"{State}, editing" : State;
    internal void SetSelected(bool selected)
    {
        if (_selected == selected) return;
        _selected = selected;
        PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
        PropertyChanged?.Invoke(this, new(nameof(SelectionStatus)));
    }
    internal RecurringTaskRowViewModel(RecurringTasksViewModel owner, RecurringTaskDefinition definition, WorkspaceWorkSnapshot snapshot, bool isLast = false)
    {
        Id = definition.Id;
        IsLast = isLast;
        Title = definition.Title;
        Cadence = definition.Cadence.ToString();
        State = definition.IsEnabled ? "Enabled" : "Paused";
        Guidance = definition.PauseReason ?? string.Empty;
        NextDate = definition.NextOccurrenceDate is { } next ? $"Adds {next:dd MMM yyyy}" : "Schedule complete";
        DueDate = definition.DueDateOffsetDays is not { } offset ? "No due date"
            : definition.NextOccurrenceDate is not { } occurrence ? "No future additions"
            : offset > DateOnly.MaxValue.DayNumber - occurrence.DayNumber ? "Due date outside supported range"
            : $"Due {occurrence.AddDays(offset):dd MMM yyyy}";
        var project = snapshot.Projects.FirstOrDefault(item => item.Id == definition.ProjectId);
        var category = snapshot.Categories.FirstOrDefault(item => item.Id == (definition.ExplicitCategoryId ?? project?.CategoryId));
        Context = $"{category?.Name ?? "Unavailable Category"} · {project?.Title ?? (definition.ProjectId is null ? "Standalone" : "Unavailable Project")}";
        CategoryName = category?.Name ?? "Unavailable Category";
        CategoryColourKey = category?.ColourKey ?? IdentityColourPalette.DefaultKey;
        ProjectTitle = project?.Title;
        ProjectColourKey = project?.ColourKey ?? IdentityColourPalette.DefaultKey;
        HasOverride = project is not null && definition.ExplicitCategoryId is not null;
        ToggleLabel = definition.IsEnabled ? "Pause" : "Resume";
        EditCommand = new(() => owner.Edit(definition.Id));
        ToggleCommand = new(() => owner.SetEnabled(definition.Id, !definition.IsEnabled));
        DeleteCommand = new(() => owner.RequestDelete(definition.Id));
    }
    public string Id { get; }
    public string Title { get; }
    public bool IsLast { get; }
    public bool HasGuidance => Guidance.Length > 0;
    public string ScheduleSummary => $"{Cadence} · {State}";
    public string Cadence { get; }
    public string State { get; }
    public string Guidance { get; }
    public string NextDate { get; }
    public string DueDate { get; }
    public string Context { get; }
    public string CategoryName { get; }
    public string CategoryColourKey { get; }
    public string? ProjectTitle { get; }
    public string ProjectColourKey { get; }
    public bool HasOverride { get; }
    public string ToggleLabel { get; }
    public string AccessibleName => $"Edit recurring Task {Title}, {Cadence}, {State}, {Context}, {NextDate}, {DueDate}";
    public string ToggleAccessibleName => $"{ToggleLabel} recurring Task {Title}";
    public string DeleteAccessibleName => $"Delete recurring Task {Title}";
    public string EditAutomationId => $"recurring-edit-{Id}";
    public string DeleteAutomationId => $"recurring-delete-{Id}";
    public RelayCommand EditCommand { get; }
    public RelayCommand ToggleCommand { get; }
    public RelayCommand DeleteCommand { get; }
}

internal sealed record RecurringDraftFingerprint(string Title, string Description, string FirstDate,
    RecurrenceCadence Cadence, string? ProjectId, string? CategoryId, bool HasDueDate,
    string DueOffset, bool IsEnabled, string ParticipantIds, string NewParticipantLabels, string PendingParticipantLabel);
