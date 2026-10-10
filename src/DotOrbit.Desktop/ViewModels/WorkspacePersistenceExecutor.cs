using DotOrbit.Core.Workspaces;

namespace DotOrbit.Desktop.ViewModels;

internal enum InspectorSaveKind
{
    Project,
    Task,
}

internal enum InspectorSaveOutcome
{
    Saved,
    Superseded,
    Invalid,
    Failed,
}

internal sealed record InspectorDraftFingerprint(
    string Title,
    string Description,
    string Date,
    string? CategoryId,
    string CategoryColourKey,
    string ProjectColourKey,
    string Participants);

internal sealed record InspectorSaveRequest(
    long InspectorGeneration,
    long Revision,
    InspectorSaveKind Kind,
    bool IsCreating,
    bool RequiresReload,
    string? Id,
    string Title,
    string Description,
    DateOnly? Date,
    string? CategoryId,
    string ProjectColourKey,
    string? ProjectId,
    TodayLane? TodayLane,
    IReadOnlyList<string> ParticipantIds,
    IReadOnlyList<string> NewParticipantLabels,
    InspectorDraftFingerprint Fingerprint);

internal sealed record WorkspaceReloadData(
    WorkspaceWorkSnapshot Snapshot,
    IReadOnlyList<TaskBinRecord> TaskBin,
    IReadOnlyList<ProjectBinRecord> ProjectBin,
    int? BulkArchiveAffectedCount = null,
    string? ArchiveSearchQuery = null,
    IReadOnlyList<ArchiveSearchResult>? ArchiveSearchResults = null);

internal sealed record InspectorSaveResult(
    InspectorSaveRequest Request,
    InspectorSaveOutcome Outcome,
    ProjectRecord? Project = null,
    TaskRecord? Task = null,
    WorkspaceWorkSnapshot? Snapshot = null,
    WorkspaceReloadData? Reload = null)
{
    public static InspectorSaveResult Saved(InspectorSaveRequest request) =>
        new(request, InspectorSaveOutcome.Saved);

    public static InspectorSaveResult Superseded(InspectorSaveRequest request) =>
        new(request, InspectorSaveOutcome.Superseded);

    public static InspectorSaveResult Invalid(InspectorSaveRequest request) =>
        new(request, InspectorSaveOutcome.Invalid);

    public static InspectorSaveResult Failed(InspectorSaveRequest request) =>
        new(request, InspectorSaveOutcome.Failed);
}

internal interface IInspectorSaveWriter : IAsyncDisposable
{
    Task<InspectorSaveResult> SubmitAsync(InspectorSaveRequest request);
}

internal enum WorkspaceActionKind
{
    ToggleToday,
    ToggleTodayLane,
    ToggleCompletion,
    MoveBacklog,
    MoveToday,
    Refresh,
}

internal enum WorkspaceMoveKind
{
    Up,
    Down,
    Top,
    Bottom,
    TargetTaskPosition,
}

internal sealed record WorkspaceActionRequest(
    WorkspaceActionKind Kind,
    string? TaskId = null,
    WorkspaceMoveKind? Move = null,
    string? TargetTaskId = null,
    int BulkArchiveCompletedAgeDays = 30,
    string ArchiveSearchText = "",
    long? AdmittedAtTimestamp = null);

internal enum WorkspaceActionOutcome
{
    Committed,
    MutationFailed,
    CommittedRefreshFailed,
    Refreshed,
    RefreshFailed,
}

internal sealed record WorkspaceActionEffect(
    string? TaskId = null,
    TodayLane? TodayLane = null,
    bool? IsComplete = null,
    int? Position = null,
    int? Count = null);

internal sealed record WorkspaceActionTiming(
    TimeSpan QueueWait,
    TimeSpan Persistence);

internal sealed record WorkspaceActionResult(
    WorkspaceActionRequest Request,
    WorkspaceActionOutcome Outcome,
    WorkspaceReloadData? Reload = null,
    WorkspaceActionEffect? Effect = null,
    WorkspaceActionTiming? Timing = null)
{
    public WorkspaceActionResult WithQueueWait(TimeSpan queueWait) =>
        this with { Timing = new(queueWait, Timing?.Persistence ?? TimeSpan.Zero) };
}

internal interface IWorkspaceActionExecutor
{
    Task<WorkspaceActionResult> SubmitActionAsync(WorkspaceActionRequest request);
}

internal interface IWorkspacePersistenceOperation : IInspectorSaveOperation
{
    WorkspaceActionResult ExecuteAction(WorkspaceActionRequest request);
}

internal interface IInspectorSaveOperation
{
    InspectorSaveResult Execute(InspectorSaveRequest request);
}

internal sealed class WorkspacePersistenceOperation : IWorkspacePersistenceOperation
{
    private readonly IWorkspaceWork _work;
    private readonly TimeProvider _timeProvider;

    public WorkspacePersistenceOperation(IWorkspaceWork work, TimeProvider? timeProvider = null)
    {
        _work = work;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public InspectorSaveResult Execute(InspectorSaveRequest request)
    {
        try
        {
            return request.Kind == InspectorSaveKind.Project
                ? SaveProject(request)
                : SaveTask(request);
        }
        catch (ArgumentException)
        {
            return InspectorSaveResult.Invalid(request);
        }
        catch (WorkspaceWorkException)
        {
            return InspectorSaveResult.Failed(request);
        }
    }

    private InspectorSaveResult SaveProject(InspectorSaveRequest request)
    {
        var project = request.IsCreating
            ? _work.CreateProject(
                request.Title,
                request.Description,
                request.CategoryId!,
                request.Date,
                request.ProjectColourKey)
            : _work.UpdateProject(
                request.Id!,
                request.Title,
                request.Description,
                request.CategoryId!,
                request.Date,
                request.ProjectColourKey);
        return request.IsCreating || request.RequiresReload
            ? new(request, InspectorSaveOutcome.Saved, Project: project, Reload: ReadReloadData())
            : new(request, InspectorSaveOutcome.Saved, Project: project);
    }

    private InspectorSaveResult SaveTask(InspectorSaveRequest request)
    {
        var participantChange = new ParticipantDraftChange(
            request.ParticipantIds,
            request.NewParticipantLabels);
        var task = request.IsCreating
            ? request.TodayLane is not null
                ? _work.CreateTaskDraft(
                    request.ProjectId,
                    request.Title,
                    request.Description,
                    request.CategoryId,
                    request.Date,
                    participantChange,
                    request.TodayLane)
                : _work.CreateStandaloneTask(
                    request.Title,
                    request.Description,
                    request.CategoryId!,
                    request.Date,
                    participantChange)
            : _work.UpdateTask(
                request.Id!,
                request.Title,
                request.Description,
                request.CategoryId,
                request.Date,
                participantChange);
        if (request.IsCreating || request.RequiresReload)
            return new(request, InspectorSaveOutcome.Saved, Task: task, Reload: ReadReloadData());
        if (request.NewParticipantLabels.Count == 0)
            return new(request, InspectorSaveOutcome.Saved, Task: task);
        var snapshot = _work.Read();
        return new(
            request,
            InspectorSaveOutcome.Saved,
            Task: snapshot.Tasks.Single(item => item.Id == task.Id),
            Snapshot: snapshot);
    }

    public WorkspaceActionResult ExecuteAction(WorkspaceActionRequest request)
    {
        var started = _timeProvider.GetTimestamp();
        if (request.Kind == WorkspaceActionKind.Refresh)
        {
            try
            {
                return new(request, WorkspaceActionOutcome.Refreshed,
                    ReadReloadData(request.BulkArchiveCompletedAgeDays, request.ArchiveSearchText),
                    Timing: new(TimeSpan.Zero, _timeProvider.GetElapsedTime(started)));
            }
            catch (Exception exception) when (IsWorkspaceFailure(exception))
            {
                return new(request, WorkspaceActionOutcome.RefreshFailed,
                    Timing: new(TimeSpan.Zero, _timeProvider.GetElapsedTime(started)));
            }
        }

        WorkspaceActionEffect effect;
        try
        {
            effect = ExecuteMutation(request);
        }
        catch (Exception exception) when (IsWorkspaceFailure(exception))
        {
            return new(request, WorkspaceActionOutcome.MutationFailed,
                Timing: new(TimeSpan.Zero, _timeProvider.GetElapsedTime(started)));
        }

        try
        {
            return new(request, WorkspaceActionOutcome.Committed,
                ReadReloadData(request.BulkArchiveCompletedAgeDays, request.ArchiveSearchText), effect,
                new(TimeSpan.Zero, _timeProvider.GetElapsedTime(started)));
        }
        catch (Exception exception) when (IsWorkspaceFailure(exception))
        {
            return new(request, WorkspaceActionOutcome.CommittedRefreshFailed, Effect: effect,
                Timing: new(TimeSpan.Zero, _timeProvider.GetElapsedTime(started)));
        }
    }

    private WorkspaceActionEffect ExecuteMutation(WorkspaceActionRequest request)
    {
        var taskId = request.TaskId ?? throw new ArgumentException("A Task is required.", nameof(request));
        var snapshot = _work.Read();
        var task = snapshot.Tasks.Single(item => item.Id == taskId);
        return request.Kind switch
        {
            WorkspaceActionKind.ToggleToday => ToggleToday(task),
            WorkspaceActionKind.ToggleTodayLane => ToggleTodayLane(task),
            WorkspaceActionKind.ToggleCompletion => ToggleCompletion(task),
            WorkspaceActionKind.MoveBacklog => MoveBacklog(snapshot, task, request),
            WorkspaceActionKind.MoveToday => MoveToday(snapshot, task, request),
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
    }

    private WorkspaceActionEffect ToggleToday(TaskRecord task)
    {
        TodayLane? lane = task.TodayLane is null ? DotOrbit.Core.Workspaces.TodayLane.Planned : null;
        _work.SetTaskTodayLane(task.Id, lane);
        return new(task.Id, TodayLane: lane);
    }

    private WorkspaceActionEffect ToggleTodayLane(TaskRecord task)
    {
        if (task.TodayLane is null)
            throw new ArgumentException("The Task does not belong to Today.", nameof(task));
        var lane = task.TodayLane == DotOrbit.Core.Workspaces.TodayLane.Planned
            ? DotOrbit.Core.Workspaces.TodayLane.InProgress
            : DotOrbit.Core.Workspaces.TodayLane.Planned;
        _work.SetTaskTodayLane(task.Id, lane);
        return new(task.Id, TodayLane: lane);
    }

    private WorkspaceActionEffect ToggleCompletion(TaskRecord task)
    {
        if (task.IsArchived) throw new ArgumentException("An archived Task cannot be changed.", nameof(task));
        var complete = !task.IsComplete;
        if (complete) _work.CompleteTask(task.Id); else _work.ReopenTask(task.Id);
        return new(task.Id, IsComplete: complete);
    }

    private WorkspaceActionEffect MoveBacklog(
        WorkspaceWorkSnapshot snapshot,
        TaskRecord task,
        WorkspaceActionRequest request)
    {
        var archivedProjectIds = snapshot.Projects.Where(project => project.IsArchived)
            .Select(project => project.Id).ToHashSet(StringComparer.Ordinal);
        var visible = snapshot.Tasks.Where(item => !item.IsComplete && !item.IsArchived
                && (item.ProjectId is null || !archivedProjectIds.Contains(item.ProjectId)))
            .OrderBy(item => item.SharedPosition).ToArray();
        var target = ResolveTarget(visible, task.Id, request);
        var change = _work.MoveTaskInSharedOrder(task.Id, target);
        return new(task.Id, Position: change.Position, Count: change.Count);
    }

    private WorkspaceActionEffect MoveToday(
        WorkspaceWorkSnapshot snapshot,
        TaskRecord task,
        WorkspaceActionRequest request)
    {
        if (task.IsComplete || task.TodayLane is null)
            throw new ArgumentException("The Task does not belong to an incomplete Today lane.", nameof(task));
        var visible = snapshot.Tasks.Where(item => !item.IsComplete && item.TodayLane == task.TodayLane)
            .OrderBy(item => item.SharedPosition).ToArray();
        var target = ResolveTarget(visible, task.Id, request);
        var change = _work.MoveTaskInTodayLane(task.Id, target);
        return new(task.Id, TodayLane: change.Lane, Position: change.Position, Count: change.Count);
    }

    private static int ResolveTarget(
        TaskRecord[] visible,
        string taskId,
        WorkspaceActionRequest request)
    {
        var current = visible.Select((item, index) => (item.Id, index))
            .Single(item => item.Id == taskId).index;
        return request.Move switch
        {
            WorkspaceMoveKind.Up => Math.Max(0, current - 1),
            WorkspaceMoveKind.Down => Math.Min(visible.Length - 1, current + 1),
            WorkspaceMoveKind.Top => 0,
            WorkspaceMoveKind.Bottom => visible.Length - 1,
            WorkspaceMoveKind.TargetTaskPosition => visible.Select((item, index) => (item.Id, index))
                .Single(item => item.Id == request.TargetTaskId).index,
            _ => throw new ArgumentException("A move intent is required.", nameof(request)),
        };
    }

    private static bool IsWorkspaceFailure(Exception exception) => exception is
        ArgumentException or InvalidOperationException or WorkspaceWorkException;

    internal WorkspaceReloadData ReadReloadData(
        int? bulkArchiveCompletedAgeDays = null,
        string? archiveSearchText = null) => new(
        _work.Read(),
        _work.ReadTaskBin(),
        _work.ReadProjectBin(),
        bulkArchiveCompletedAgeDays is { } ageDays
            ? _work.PreviewBulkTaskArchive(ageDays).AffectedCount
            : null,
        archiveSearchText,
        archiveSearchText switch
        {
            null => null,
            { } query when string.IsNullOrWhiteSpace(query) => [],
            { } query => _work.SearchArchive(query),
        });
}

internal sealed class InlineWorkspacePersistenceExecutor : IInspectorSaveWriter, IWorkspaceActionExecutor
{
    private readonly IWorkspacePersistenceOperation _operation;
    private readonly TimeProvider _timeProvider;

    public InlineWorkspacePersistenceExecutor(
        IWorkspacePersistenceOperation operation,
        TimeProvider? timeProvider = null)
    {
        _operation = operation;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<InspectorSaveResult> SubmitAsync(InspectorSaveRequest request) =>
        Task.FromResult(_operation.Execute(request));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public Task<WorkspaceActionResult> SubmitActionAsync(WorkspaceActionRequest request)
    {
        var admittedAt = request.AdmittedAtTimestamp ?? _timeProvider.GetTimestamp();
        var queueWait = _timeProvider.GetElapsedTime(admittedAt);
        return Task.FromResult(_operation.ExecuteAction(request).WithQueueWait(queueWait));
    }

}

internal sealed class SerializedWorkspacePersistenceExecutor : IInspectorSaveWriter, IWorkspaceActionExecutor
{
    private readonly object _gate = new();
    private readonly IWorkspacePersistenceOperation _operation;
    private readonly TimeProvider _timeProvider;
    private readonly LinkedList<PendingWork> _pending = [];
    private LinkedListNode<PendingWork>? _coalescibleInspector;
    private Task _pump = Task.CompletedTask;
    private bool _running;
    private bool _accepting = true;
    private long _latestGeneration = long.MinValue;
    private long _latestRevision = long.MinValue;

    public SerializedWorkspacePersistenceExecutor(IWorkspaceWork work, TimeProvider? timeProvider = null)
        : this(new WorkspacePersistenceOperation(work, timeProvider), timeProvider)
    {
    }

    internal SerializedWorkspacePersistenceExecutor(
        IWorkspacePersistenceOperation operation,
        TimeProvider? timeProvider = null)
    {
        _operation = operation;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<InspectorSaveResult> SubmitAsync(InspectorSaveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(!_accepting, this);
            if (request.InspectorGeneration < _latestGeneration
                || (request.InspectorGeneration == _latestGeneration && request.Revision < _latestRevision))
                return Task.FromResult(InspectorSaveResult.Superseded(request));
            _latestGeneration = request.InspectorGeneration;
            _latestRevision = request.Revision;
            var completion = new TaskCompletionSource<InspectorSaveResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (_coalescibleInspector is { List: not null } coalescible
                && coalescible.Value is PendingInspectorSave prior)
            {
                prior.Completion.TrySetResult(InspectorSaveResult.Superseded(prior.Request));
                coalescible.Value = new PendingInspectorSave(request, completion);
            }
            else
            {
                _coalescibleInspector = _pending.AddLast(new PendingInspectorSave(request, completion));
            }
            StartPump();
            return completion.Task;
        }
    }

    public Task<WorkspaceActionResult> SubmitActionAsync(WorkspaceActionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(!_accepting, this);
            _coalescibleInspector = null;
            var completion = new TaskCompletionSource<WorkspaceActionResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var admittedAt = request.AdmittedAtTimestamp ?? _timeProvider.GetTimestamp();
            _pending.AddLast(new PendingAction(request, admittedAt, completion));
            StartPump();
            return completion.Task;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task pump;
        lock (_gate)
        {
            _accepting = false;
            _coalescibleInspector = null;
            pump = _pump;
        }
        await pump.ConfigureAwait(false);
    }

    private void StartPump()
    {
        if (_running) return;
        _running = true;
        _pump = Task.Run(Pump);
    }

    private void Pump()
    {
        while (true)
        {
            PendingWork? pending;
            lock (_gate)
            {
                if (_pending.First is null)
                {
                    _running = false;
                    return;
                }
                var node = _pending.First;
                pending = node.Value;
                _pending.RemoveFirst();
                if (ReferenceEquals(_coalescibleInspector, node)) _coalescibleInspector = null;
            }

            switch (pending)
            {
                case PendingInspectorSave save:
                    try { save.Completion.TrySetResult(_operation.Execute(save.Request)); }
                    catch (Exception exception) { save.Completion.TrySetException(exception); }
                    break;
                case PendingAction action:
                    try
                    {
                        var queueWait = _timeProvider.GetElapsedTime(action.AdmittedAt);
                        action.Completion.TrySetResult(_operation.ExecuteAction(action.Request).WithQueueWait(queueWait));
                    }
                    catch (Exception exception) { action.Completion.TrySetException(exception); }
                    break;
            }
        }
    }

    private abstract record PendingWork;

    private sealed record PendingInspectorSave(
        InspectorSaveRequest Request,
        TaskCompletionSource<InspectorSaveResult> Completion) : PendingWork;

    private sealed record PendingAction(
        WorkspaceActionRequest Request,
        long AdmittedAt,
        TaskCompletionSource<WorkspaceActionResult> Completion) : PendingWork;

}
