using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using DotOrbit.Core.Workspaces;

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
    private bool _creating;
    private bool _creatingStandaloneTask;
    private string _title = string.Empty;
    private string _description = string.Empty;
    private string _date = string.Empty;
    private DateTime? _selectedDate;
    private CategoryChoice? _category;
    private (string Title, string Description, string Date, string? CategoryId) _original;
    private string _message = string.Empty;
    private bool _hasInspector;
    private string _backlogQuickTitle = string.Empty;
    private CategoryChoice? _backlogQuickCategory;
    private string _reorderAnnouncement = string.Empty;
    private bool _backlogActive;
    private bool _completedActive;
    private string _completionFocusAutomationId = string.Empty;
    private string _reorderFocusAutomationId = string.Empty;
    private DateOnly _presentationDate;
    private string _presentationTimeZoneId = string.Empty;

    public ProjectCaptureViewModel(IWorkspaceWork work, TimeProvider? timeProvider = null)
    {
        _work = work;
        _timeProvider = timeProvider ?? TimeProvider.System;
        NewProjectCommand = new(() => Navigate(BeginProject));
        NewTaskCommand = new(() => Navigate(BeginStandaloneTask));
        SaveCommand = new(() => Save());
        CancelCommand = new(Cancel);
        SaveAndLeaveCommand = new(() => { if (Save()) Leave(); });
        DiscardAndLeaveCommand = new(() => { Cancel(); Leave(); });
        StayCommand = new(() => { _pendingNavigation = null; Notify(nameof(NeedsDecision)); });
        CapturePresentationClock();
        Reload();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<ProjectRowViewModel> Projects { get; } = [];
    public ObservableCollection<TaskRowViewModel> Backlog { get; } = [];
    public ObservableCollection<TaskRowViewModel> Completed { get; } = [];
    public ObservableCollection<CompletedTaskGroupViewModel> CompletedGroups { get; } = [];
    public ObservableCollection<CategoryChoice> Categories { get; } = [];
    public ObservableCollection<CategoryChoice> BacklogCategories { get; } = [];
    public RelayCommand NewProjectCommand { get; }
    public RelayCommand NewTaskCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand SaveAndLeaveCommand { get; }
    public RelayCommand DiscardAndLeaveCommand { get; }
    public RelayCommand StayCommand { get; }
    public bool HasInspector { get => _hasInspector; private set { _hasInspector = value; Notify(); Notify(nameof(HasNoInspector)); } }
    public bool HasNoInspector => !HasInspector;
    public bool HasProjects => Projects.Count > 0;
    public bool HasNoProjects => !HasProjects;
    public bool HasCompleted => Completed.Count > 0;
    public bool HasNoCompleted => !HasCompleted;
    public bool NeedsDecision => _pendingNavigation is not null;
    public bool IsDirty => HasInspector && (_creating || _original != Fingerprint());
    public string InspectorHeading => _creating ? (_editingTask ? "New task" : "New project") : _editingTask ? "Task details" : "Project details";
    public bool ShowProjectSummary => HasInspector && !_creating && !_editingTask;
    public string ProjectSummary => ShowProjectSummary
        ? Projects.Single(p => p.Id == _editingId).Summary
            + (string.IsNullOrEmpty(Projects.Single(p => p.Id == _editingId).CompletionDateText)
                ? " · No completion date"
                : $" · {Projects.Single(p => p.Id == _editingId).CompletionDateText}")
        : string.Empty;
    public bool ShowTaskCompletionDate => HasInspector && !_creating && _editingTask
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
    public string DateLabel => _editingTask ? "Due date (YYYY-MM-DD, optional)" : "Target date (YYYY-MM-DD, optional)";
    public string CategoryHint => _editingTask
        ? IsStandaloneTaskDraft
            ? "Standalone Task category"
            : (_category?.Id is null ? "Inherited from project" : "Explicit category override")
        : "Project category";
    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public string Message { get => _message; private set { _message = value; Notify(); Notify(nameof(HasMessage)); } }
    public string Title { get => _title; set { _title = value; Notify(); } }
    public string Description { get => _description; set { _description = value; Notify(); } }
    public string Date
    {
        get => _date;
        set
        {
            if (string.Equals(_date, value, StringComparison.Ordinal)) return;
            _date = value;
            Notify();
            if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                var selected = parsed.ToDateTime(TimeOnly.MinValue);
                if (_selectedDate != selected)
                {
                    _selectedDate = selected;
                    Notify(nameof(SelectedDate));
                }
            }
            else if (string.IsNullOrEmpty(value) && _selectedDate is not null)
            {
                _selectedDate = null;
                Notify(nameof(SelectedDate));
            }
        }
    }
    public DateTime? SelectedDate
    {
        get => _selectedDate;
        set
        {
            if (_selectedDate == value) return;
            _selectedDate = value;
            Notify();
            var text = value is null
                ? string.Empty
                : DateOnly.FromDateTime(value.Value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!string.Equals(_date, text, StringComparison.Ordinal))
            {
                _date = text;
                Notify(nameof(Date));
            }
        }
    }
    public string BacklogQuickTitle { get => _backlogQuickTitle; set { _backlogQuickTitle = value; Notify(); } }
    public CategoryChoice? BacklogQuickCategory { get => _backlogQuickCategory; set { _backlogQuickCategory = value; Notify(); } }
    public string ReorderAnnouncement { get => _reorderAnnouncement; private set { _reorderAnnouncement = value; Notify(); } }
    public string CompletionFocusAutomationId { get => _completionFocusAutomationId; private set { _completionFocusAutomationId = value; Notify(); } }
    public string ReorderFocusAutomationId { get => _reorderFocusAutomationId; private set { _reorderFocusAutomationId = value; Notify(); } }
    internal DateOnly Today => DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime);
    private bool IsStandaloneTaskDraft => _editingTask && (_creatingStandaloneTask
        || (_editingId is not null && _snapshot.Tasks.Single(task => task.Id == _editingId).ProjectId is null));
    public CategoryChoice? Category
    {
        get => _category;
        set
        {
            _category = value;
            Notify();
            Notify(nameof(CategoryHint));
            Notify(nameof(InspectorMetaValue));
        }
    }

    public void Navigate(Action destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (NeedsDecision) return;
        if (IsDirty)
        {
            _pendingNavigation = destination;
            Notify(nameof(NeedsDecision));
            return;
        }
        CloseInspector();
        destination();
    }

    public void SelectProject(string id) => Navigate(() => LoadProject(id));
    public void SelectTask(string id) => Navigate(() => LoadTask(id));
    public void SetBacklogActive(bool active) => _backlogActive = active;
    public void SetCompletedActive(bool active) => _completedActive = active;

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

    public bool DragTask(string taskId, string targetTaskId)
    {
        var targetPosition = Backlog.IndexOf(Backlog.Single(task => task.Id == targetTaskId));
        return MoveTask(taskId, targetPosition);
    }

    public bool DragProject(string projectId, string targetProjectId) =>
        MoveProject(projectId, Projects.IndexOf(Projects.Single(project => project.Id == targetProjectId)));

    public bool DragProjectTask(string projectId, string taskId, string targetTaskId)
    {
        var project = Projects.Single(item => item.Id == projectId);
        var target = project.Tasks.SingleOrDefault(task => task.Id == targetTaskId);
        return target is not null && MoveProjectTask(projectId, taskId, project.Tasks.IndexOf(target));
    }

    private void BeginProject()
    {
        _creating = true;
        _editingTask = false;
        _creatingStandaloneTask = false;
        _editingId = null;
        SetDraft(string.Empty, string.Empty, null, _snapshot.Categories.Count == 0 ? null : _snapshot.Categories[0].Id);
    }

    private void BeginStandaloneTask()
    {
        _creating = true;
        _editingTask = true;
        _creatingStandaloneTask = true;
        _editingId = null;
        SetDraft(string.Empty, string.Empty, null, null);
    }

    private void LoadProject(string id)
    {
        var project = _snapshot.Projects.Single(p => p.Id == id);
        _editingId = id;
        _editingTask = false;
        _creating = false;
        _creatingStandaloneTask = false;
        SetDraft(project.Title, project.Description, project.TargetDate, project.CategoryId);
    }

    private void LoadTask(string id)
    {
        var task = _snapshot.Tasks.Single(t => t.Id == id);
        _editingId = id;
        _editingTask = true;
        _creating = false;
        _creatingStandaloneTask = false;
        SetDraft(task.Title, task.Description, task.DueDate, task.ExplicitCategoryId);
    }

    private void SetDraft(string title, string description, DateOnly? date, string? categoryId)
    {
        Categories.Clear();
        if (_editingTask && !IsStandaloneTaskDraft)
        {
            var task = _snapshot.Tasks.Single(t => t.Id == _editingId);
            var project = _snapshot.Projects.Single(p => p.Id == task.ProjectId);
            Categories.Add(new(null, $"Inherit — {CategoryName(project.CategoryId)}"));
        }
        foreach (var category in _snapshot.Categories) Categories.Add(new(category.Id, category.Name));
        Title = title;
        Description = description;
        Date = date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
        Category = Categories.FirstOrDefault(c => c.Id == categoryId);
        _original = Fingerprint();
        HasInspector = true;
        Message = string.Empty;
        Notify(nameof(InspectorHeading)); Notify(nameof(SaveLabel)); Notify(nameof(DateLabel));
        Notify(nameof(ShowProjectSummary)); Notify(nameof(ProjectSummary));
        Notify(nameof(ShowTaskCompletionDate)); Notify(nameof(TaskCompletionDateText)); Notify(nameof(TaskCompletionDateAccessibleText));
        Notify(nameof(InspectorMetaLabel)); Notify(nameof(InspectorMetaValue));
    }

    public bool Save()
    {
        if (!HasInspector) return true;
        if (string.IsNullOrWhiteSpace(Title)) { Message = "Enter a title."; return false; }
        DateOnly? date = null;
        if (!string.IsNullOrWhiteSpace(Date))
        {
            if (!DateOnly.TryParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            { Message = "Enter a valid date as YYYY-MM-DD, or leave it empty."; return false; }
            date = parsed;
        }
        if (Category is null || ((!_editingTask || IsStandaloneTaskDraft) && Category.Id is null)) { Message = "Choose a category."; return false; }
        return Attempt(() =>
        {
            if (_creating && _editingTask) _editingId = _work.CreateStandaloneTask(Title, Description, Category.Id!, date).Id;
            else if (_creating) _editingId = _work.CreateProject(Title, Description, Category.Id!, date).Id;
            else if (_editingTask) _work.UpdateTask(_editingId!, Title, Description, Category.Id, date);
            else _work.UpdateProject(_editingId!, Title, Description, Category.Id!, date);
            _creating = false;
            _creatingStandaloneTask = false;
            Reload();
            if (_editingTask) LoadTask(_editingId!); else LoadProject(_editingId!);
            Message = "Changes saved.";
        });
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
        else if (_editingTask) LoadTask(_editingId);
        else LoadProject(_editingId);
    }
    private void CloseInspector() { HasInspector = false; _creating = false; _creatingStandaloneTask = false; Message = string.Empty; }
    private void Leave()
    {
        var destination = _pendingNavigation;
        _pendingNavigation = null;
        Notify(nameof(NeedsDecision));
        CloseInspector();
        destination?.Invoke();
    }
    private (string Title, string Description, string Date, string? CategoryId) Fingerprint() => (Title, Description, Date, Category?.Id);
    private string CategoryName(string id) => _snapshot.Categories.Single(c => c.Id == id).Name;
    private void Reload()
    {
        _snapshot = _work.Read();
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
        var desiredBacklog = _snapshot.Tasks.Where(task => !task.IsComplete).OrderBy(t => t.SharedPosition).Select(ToTaskRow).ToArray();
        SynchroniseBacklog(desiredBacklog);
        for (var index = 0; index < Backlog.Count; index++) Backlog[index].SetPosition(index + 1, Backlog.Count);
        var desiredCompleted = _snapshot.Tasks.Where(task => task.IsComplete)
            .OrderByDescending(task => task.CompletedAt).ThenBy(task => task.SharedPosition).Select(ToTaskRow).ToArray();
        Synchronise(Completed, desiredCompleted);
        RefreshCompletedGroups();
        Notify(nameof(ProjectSummary));
        Notify(nameof(InspectorMetaValue));
        Notify(nameof(ShowTaskCompletionDate));
        Notify(nameof(TaskCompletionDateText));
        Notify(nameof(TaskCompletionDateAccessibleText));
        Notify(nameof(HasProjects));
        Notify(nameof(HasNoProjects));
        Notify(nameof(HasCompleted));
        Notify(nameof(HasNoCompleted));
    }
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

    private void RefreshCompletedGroups()
    {
        CompletedGroups.Clear();
        foreach (var group in Completed.GroupBy(row => CompletedGroupFor(
                     _snapshot.Tasks.Single(task => task.Id == row.Id).CompletionDate!.Value))
                 .OrderByDescending(group => group.Key.Start))
            CompletedGroups.Add(new(group.Key.Heading, group.ToArray()));
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
        var removesDraftFromCurrentView = (!task.IsComplete && _backlogActive) || (task.IsComplete && _completedActive);
        if (removesDraftFromCurrentView && IsDirty && _editingTask && _editingId == taskId)
        {
            _pendingNavigation = action;
            Notify(nameof(NeedsDecision));
            return;
        }
        action();
    }

    private void ApplyCompletion(string taskId, bool complete)
    {
        var backlogIndex = Backlog.IndexOf(Backlog.FirstOrDefault(row => row.Id == taskId)!);
        var completedIndex = Completed.IndexOf(Completed.FirstOrDefault(row => row.Id == taskId)!);
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
                else if (!complete && _completedActive)
                {
                    CompletionFocusAutomationId = Completed.Count == 0
                        ? "navigation-completed"
                        : Completed[Math.Min(Math.Max(completedIndex, 0), Completed.Count - 1)].CompletionAutomationId;
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

    internal bool MoveProjectTask(string projectId, string taskId, int targetPosition)
    {
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
public sealed record CompletedTaskGroupViewModel(string Heading, IReadOnlyList<TaskRowViewModel> Tasks)
{
    public IReadOnlyList<CompletedTaskRowViewModel> Rows { get; } = Tasks
        .Select((task, index) => new CompletedTaskRowViewModel(task, index == Tasks.Count - 1))
        .ToArray();
}

public sealed record CompletedTaskRowViewModel(TaskRowViewModel Task, bool IsLast);
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
    public string DateText => _dateText;
    public string DateAccessibleText => _dateAccessibleText;
    public string CompletionDateText => _completionDateText;
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
        Notify(nameof(DateText));
        Notify(nameof(DateAccessibleText));
        Notify(nameof(CompletionDateText));
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
