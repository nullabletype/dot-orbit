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
    IReadOnlyList<ProjectBinRecord> ProjectBin);

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

internal interface IInspectorSaveOperation
{
    InspectorSaveResult Execute(InspectorSaveRequest request);
}

internal sealed class WorkspaceInspectorSaveOperation(IWorkspaceWork work) : IInspectorSaveOperation
{
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
            ? work.CreateProject(
                request.Title,
                request.Description,
                request.CategoryId!,
                request.Date,
                request.ProjectColourKey)
            : work.UpdateProject(
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
                ? work.CreateTaskDraft(
                    request.ProjectId,
                    request.Title,
                    request.Description,
                    request.CategoryId,
                    request.Date,
                    participantChange,
                    request.TodayLane)
                : work.CreateStandaloneTask(
                    request.Title,
                    request.Description,
                    request.CategoryId!,
                    request.Date,
                    participantChange)
            : work.UpdateTask(
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
        var snapshot = work.Read();
        return new(
            request,
            InspectorSaveOutcome.Saved,
            Task: snapshot.Tasks.Single(item => item.Id == task.Id),
            Snapshot: snapshot);
    }

    private WorkspaceReloadData ReadReloadData() => new(
        work.Read(),
        work.ReadTaskBin(),
        work.ReadProjectBin());
}

internal sealed class InlineInspectorSaveWriter(IInspectorSaveOperation operation) : IInspectorSaveWriter
{
    public Task<InspectorSaveResult> SubmitAsync(InspectorSaveRequest request) =>
        Task.FromResult(operation.Execute(request));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class SerializedInspectorSaveWriter : IInspectorSaveWriter
{
    private readonly object _gate = new();
    private readonly IInspectorSaveOperation _operation;
    private PendingSave? _pending;
    private Task _pump = Task.CompletedTask;
    private bool _running;
    private bool _accepting = true;
    private long _latestGeneration = long.MinValue;
    private long _latestRevision = long.MinValue;

    public SerializedInspectorSaveWriter(IWorkspaceWork work)
        : this(new WorkspaceInspectorSaveOperation(work))
    {
    }

    internal SerializedInspectorSaveWriter(IInspectorSaveOperation operation)
    {
        _operation = operation;
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
            if (_pending is { } pending)
                pending.Completion.TrySetResult(InspectorSaveResult.Superseded(pending.Request));
            var completion = new TaskCompletionSource<InspectorSaveResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = new(request, completion);
            if (!_running)
            {
                _running = true;
                _pump = Task.Run(Pump);
            }
            return completion.Task;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task pump;
        lock (_gate)
        {
            _accepting = false;
            if (_pending is { } pending)
            {
                _pending = null;
                pending.Completion.TrySetResult(InspectorSaveResult.Superseded(pending.Request));
            }
            pump = _pump;
        }
        await pump.ConfigureAwait(false);
    }

    private void Pump()
    {
        while (true)
        {
            PendingSave? pending;
            lock (_gate)
            {
                pending = _pending;
                _pending = null;
                if (pending is null)
                {
                    _running = false;
                    return;
                }
            }

            try
            {
                pending.Completion.TrySetResult(_operation.Execute(pending.Request));
            }
            catch (Exception exception)
            {
                pending.Completion.TrySetException(exception);
            }
        }
    }

    private sealed record PendingSave(
        InspectorSaveRequest Request,
        TaskCompletionSource<InspectorSaveResult> Completion);
}
