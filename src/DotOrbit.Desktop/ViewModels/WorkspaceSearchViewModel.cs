using System.Collections.ObjectModel;
using System.ComponentModel;
using DotOrbit.Core.Workspaces;

namespace DotOrbit.Desktop.ViewModels;

public sealed class WorkspaceSearchViewModel : INotifyPropertyChanged
{
    private readonly ProjectCaptureViewModel _owner;
    private readonly IWorkspaceActionExecutor _executor;
    private readonly IUiDispatcher _dispatcher;
    private string _text = string.Empty;
    private string _status = "Enter a word or prefix to find Projects and Tasks.";
    private long _generation;
    private bool _reading;
    private bool _isLoading;

    internal WorkspaceSearchViewModel(ProjectCaptureViewModel owner, IWorkspaceActionExecutor executor, IUiDispatcher dispatcher)
    {
        _owner = owner;
        _executor = executor;
        _dispatcher = dispatcher;
        ClearCommand = new(() => Text = string.Empty, () => Text.Length > 0);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public RelayCommand ClearCommand { get; }
    public ObservableCollection<ProjectRowViewModel> Projects { get; } = [];
    public ObservableCollection<TaskRowViewModel> Backlog { get; } = [];
    public ObservableCollection<CompletedTaskRowViewModel> Completed { get; } = [];
    public ObservableCollection<ArchivedWorkRowViewModel> Archive { get; } = [];
    public ObservableCollection<BinRowViewModel> Bin { get; } = [];
    internal bool IsActive { get; private set; }

    public string Text
    {
        get => _text;
        set
        {
            if (value == _text) return;
            _text = value;
            PropertyChanged?.Invoke(this, new(nameof(Text)));
            ClearCommand.RaiseCanExecuteChanged();
            ClearRows();
            Refresh();
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (_isLoading == value) return;
            _isLoading = value;
            PropertyChanged?.Invoke(this, new(nameof(IsLoading)));
        }
    }

    public string Status
    {
        get => _status;
        private set
        {
            if (value == _status) return;
            _status = value;
            PropertyChanged?.Invoke(this, new(nameof(Status)));
        }
    }

    internal void SetActive(bool active)
    {
        if (IsActive == active) return;
        IsActive = active;
        if (!active)
        {
            _generation++;
            ClearRows();
            IsLoading = false;
            return;
        }
        Refresh();
    }

    internal void Refresh()
    {
        if (!IsActive) return;
        _generation++;
        if (string.IsNullOrWhiteSpace(Text))
        {
            ClearRows();
            Status = "Enter a word or prefix to find Projects and Tasks.";
            IsLoading = false;
            return;
        }

        IsLoading = true;
        Status = "Searching the workspace…";
        if (_reading) return;
        _reading = true;
        _ = ReadAsync(_generation, Text);
    }

    private async Task ReadAsync(long generation, string query)
    {
        WorkspaceActionResult? response = null;
        try
        {
            response = await _executor.SubmitActionAsync(new(WorkspaceActionKind.SearchWorkspace, SearchQuery: query))
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is WorkspaceWorkException or InvalidOperationException)
        {
            // Only the fixed presentation below crosses the workspace boundary.
        }
        await _dispatcher.InvokeAsync(() =>
        {
            _reading = false;
            if (generation != _generation)
            {
                Refresh();
                return;
            }
            if (response?.SearchResults is { } hits) Apply(hits);
            else
            {
                ClearRows();
                Status = "Workspace search is unavailable. Try again.";
            }
            IsLoading = false;
        }).ConfigureAwait(false);
    }

    private void Apply(IReadOnlyList<WorkspaceSearchResult> hits)
    {
        var matched = hits.Select(hit => (hit.Group, hit.RecordType, hit.Id)).ToHashSet();
        bool Has(WorkspaceSearchGroup group, ArchiveSearchRecordType type, string id) => matched.Contains((group, type, id));

        Synchronise(Projects, _owner.Projects.Where(row => Has(WorkspaceSearchGroup.Projects, ArchiveSearchRecordType.Project, row.Id)));
        Synchronise(Backlog, _owner.Backlog.Where(row => Has(WorkspaceSearchGroup.Backlog, ArchiveSearchRecordType.Task, row.Id)));
        Synchronise(Completed, _owner.CompletedGroups.SelectMany(group => group.Rows)
            .Where(row => Has(WorkspaceSearchGroup.Completed, ArchiveSearchRecordType.Task, row.Task.Id)));

        var archived = _owner.ArchiveGroups.SelectMany(group => group.Rows).Where(row => row.IsProject
            ? Has(WorkspaceSearchGroup.Archive, ArchiveSearchRecordType.Project, row.Project!.Id)
            : Has(WorkspaceSearchGroup.Archive, ArchiveSearchRecordType.Task, row.Task!.Id)).ToList();
        var presentTasks = archived.Where(row => row.IsTask).Select(row => row.Task!.Id).ToHashSet();
        foreach (var hit in hits.Where(hit => hit.Group == WorkspaceSearchGroup.Archive && hit.RecordType == ArchiveSearchRecordType.Task))
            if (presentTasks.Add(hit.Id) && _owner.HistoricalChild(hit.Id) is { } child) archived.Add(child);
        Synchronise(Archive, archived.OrderByDescending(row => row.ArchiveDate).ThenByDescending(row => row.ArchivedAt));

        var binned = _owner.Bin.Where(row => row.Project is { } project
                ? Has(WorkspaceSearchGroup.Bin, ArchiveSearchRecordType.Project, project.Id)
                : Has(WorkspaceSearchGroup.Bin, ArchiveSearchRecordType.Task, row.Task!.Id))
            .Concat(hits.Where(hit => hit.AggregateBinTask is not null)
                .Select(hit => _owner.AggregateBinRow(hit.AggregateBinTask!)).OfType<BinRowViewModel>());
        Synchronise(Bin, binned.OrderByDescending(row => row.RemovedAt).ThenBy(row => row.Title, StringComparer.Ordinal));

        var count = Projects.Count + Backlog.Count + Completed.Count + Archive.Count + Bin.Count;
        Status = count switch
        {
            0 => "No matching Projects or Tasks.",
            1 => "1 result.",
            _ => $"{count} results.",
        };
    }

    private void ClearRows()
    {
        Projects.Clear();
        Backlog.Clear();
        Completed.Clear();
        Archive.Clear();
        Bin.Clear();
    }

    private static void Synchronise<T>(ObservableCollection<T> rows, IEnumerable<T> desired)
        where T : class
    {
        var position = 0;
        foreach (var row in desired)
        {
            if (position == rows.Count) rows.Add(row);
            else if (!EqualityComparer<T>.Default.Equals(rows[position], row))
            {
                var from = rows.IndexOf(row);
                if (from < 0) rows.Insert(position, row);
                else rows.Move(from, position);
            }
            position++;
        }
        while (rows.Count > position) rows.RemoveAt(rows.Count - 1);
    }
}
