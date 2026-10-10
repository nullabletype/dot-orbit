using System.Collections.Concurrent;
using DotOrbit.Desktop.ViewModels;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class WorkspacePersistenceExecutorTests
{
    [Fact]
    public async Task SubmitReturnsBeforePersistenceAndRunsOneLatestRevisionAtATime()
    {
        var operation = new ControlledInspectorSaveOperation();
        await using var writer = new SerializedWorkspacePersistenceExecutor(operation);
        var callerThread = Environment.CurrentManagedThreadId;

        var first = writer.SubmitAsync(ProjectUpdate(1, "First"));
        Assert.True(operation.WaitUntilStarted(1));
        Assert.False(first.IsCompleted);
        Assert.NotEqual(callerThread, operation.ThreadFor(1));

        var superseded = writer.SubmitAsync(ProjectUpdate(2, "Second"));
        var latest = writer.SubmitAsync(ProjectUpdate(3, "Latest"));

        Assert.Equal(InspectorSaveOutcome.Superseded, (await superseded).Outcome);
        Assert.False(operation.WasStarted(2));
        Assert.False(operation.WasStarted(3));

        operation.Complete(1);
        Assert.Equal(InspectorSaveOutcome.Saved, (await first).Outcome);
        Assert.True(operation.WaitUntilStarted(3));
        Assert.Equal(1, operation.MaximumConcurrency);

        operation.Complete(3);
        Assert.Equal(InspectorSaveOutcome.Saved, (await latest).Outcome);
        Assert.Equal([1L, 3L], operation.StartedRevisions);
    }

    [Fact]
    public async Task DisposeDrainsLatestAcceptedWriteBeforeStoppingAdmission()
    {
        var operation = new ControlledInspectorSaveOperation();
        var writer = new SerializedWorkspacePersistenceExecutor(operation);
        var committed = writer.SubmitAsync(ProjectUpdate(1, "Committed"));
        Assert.True(operation.WaitUntilStarted(1));
        var queued = writer.SubmitAsync(ProjectUpdate(2, "Queued"));

        var dispose = writer.DisposeAsync().AsTask();

        Assert.False(dispose.IsCompleted);
        operation.Complete(1);
        Assert.Equal(InspectorSaveOutcome.Saved, (await committed).Outcome);
        Assert.True(operation.WaitUntilStarted(2));
        operation.Complete(2);
        Assert.Equal(InspectorSaveOutcome.Saved, (await queued).Outcome);
        await dispose;
        Assert.True(operation.WasStarted(2));
        Assert.Throws<ObjectDisposedException>(() => { _ = writer.SubmitAsync(ProjectUpdate(3, "Late")); });
    }

    [Fact]
    public async Task DiscreteActionsRemainLosslessFifoBarriersBetweenCoalescedInspectorRevisions()
    {
        var operation = new ControlledInspectorSaveOperation();
        await using var executor = new SerializedWorkspacePersistenceExecutor(operation);

        var first = executor.SubmitAsync(ProjectUpdate(1, "First"));
        Assert.True(operation.WaitUntilStarted(1));
        var supersededBefore = executor.SubmitAsync(ProjectUpdate(2, "Before action"));
        var latestBefore = executor.SubmitAsync(ProjectUpdate(3, "Before action latest"));
        var firstAction = executor.SubmitActionAsync(new(WorkspaceActionKind.ToggleToday, "task-1"));
        var secondAction = executor.SubmitActionAsync(new(WorkspaceActionKind.ToggleCompletion, "task-2"));
        var supersededAfter = executor.SubmitAsync(ProjectUpdate(4, "After action"));
        var latestAfter = executor.SubmitAsync(ProjectUpdate(5, "After action latest"));

        Assert.Equal(InspectorSaveOutcome.Superseded, (await supersededBefore).Outcome);
        Assert.Equal(InspectorSaveOutcome.Superseded, (await supersededAfter).Outcome);
        operation.Complete(1);
        Assert.Equal(InspectorSaveOutcome.Saved, (await first).Outcome);
        Assert.True(operation.WaitUntilStarted(3));
        operation.Complete(3);
        Assert.Equal(InspectorSaveOutcome.Saved, (await latestBefore).Outcome);
        Assert.Equal(WorkspaceActionOutcome.Committed, (await firstAction).Outcome);
        Assert.Equal(WorkspaceActionOutcome.Committed, (await secondAction).Outcome);
        Assert.True(operation.WaitUntilStarted(5));
        operation.Complete(5);
        Assert.Equal(InspectorSaveOutcome.Saved, (await latestAfter).Outcome);

        Assert.Equal(
            ["save:1", "save:3", "action:ToggleToday:task-1", "action:ToggleCompletion:task-2", "save:5"],
            operation.StartedWork);
        Assert.Equal(1, operation.MaximumConcurrency);
    }

    [Fact]
    public async Task ThirtyTwoActionBurstReturnsImmediatelyAndExecutesEveryActionOnceInAdmissionOrder()
    {
        var operation = new BlockingActionOperation();
        await using var executor = new SerializedWorkspacePersistenceExecutor(operation);
        var callerThread = Environment.CurrentManagedThreadId;

        var actions = Enumerable.Range(0, 32)
            .Select(index => executor.SubmitActionAsync(new(
                WorkspaceActionKind.ToggleToday,
                $"task-{index}")))
            .ToArray();

        Assert.True(operation.Started.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.All(actions, action => Assert.False(action.IsCompleted));
        Assert.NotEqual(callerThread, operation.WorkerThread);
        operation.Release.Set();
        await Task.WhenAll(actions);

        Assert.Equal(Enumerable.Range(0, 32).Select(index => $"task-{index}"), operation.ExecutedTaskIds);
        Assert.All(actions, action => Assert.Equal(WorkspaceActionOutcome.Committed, action.Result.Outcome));
    }

    private static InspectorSaveRequest ProjectUpdate(long revision, string title) => new(
        InspectorGeneration: 1,
        Revision: revision,
        Kind: InspectorSaveKind.Project,
        IsCreating: false,
        RequiresReload: false,
        Id: "project",
        Title: title,
        Description: string.Empty,
        Date: null,
        CategoryId: "home",
        ProjectColourKey: "orchid",
        ProjectId: null,
        TodayLane: null,
        ParticipantIds: [],
        NewParticipantLabels: [],
        Fingerprint: new(title, string.Empty, string.Empty, "home", "orchid", "orchid", string.Empty));

    private sealed class ControlledInspectorSaveOperation : IWorkspacePersistenceOperation
    {
        private readonly ConcurrentDictionary<long, ManualResetEventSlim> _started = new();
        private readonly ConcurrentDictionary<long, ManualResetEventSlim> _completed = new();
        private readonly ConcurrentDictionary<long, int> _threads = new();
        private readonly List<long> _startedRevisions = [];
        private readonly List<string> _startedWork = [];
        private int _concurrency;

        public IReadOnlyList<long> StartedRevisions
        {
            get { lock (_startedRevisions) return _startedRevisions.ToArray(); }
        }

        public IReadOnlyList<string> StartedWork
        {
            get { lock (_startedWork) return _startedWork.ToArray(); }
        }

        public int MaximumConcurrency { get; private set; }

        public InspectorSaveResult Execute(InspectorSaveRequest request)
        {
            var concurrency = Interlocked.Increment(ref _concurrency);
            MaximumConcurrency = Math.Max(MaximumConcurrency, concurrency);
            lock (_startedRevisions) _startedRevisions.Add(request.Revision);
            lock (_startedWork) _startedWork.Add($"save:{request.Revision}");
            _threads[request.Revision] = Environment.CurrentManagedThreadId;
            _started.GetOrAdd(request.Revision, _ => new()).Set();
            _completed.GetOrAdd(request.Revision, _ => new()).Wait();
            Interlocked.Decrement(ref _concurrency);
            return InspectorSaveResult.Saved(request);
        }

        public WorkspaceActionResult ExecuteAction(WorkspaceActionRequest request)
        {
            var concurrency = Interlocked.Increment(ref _concurrency);
            MaximumConcurrency = Math.Max(MaximumConcurrency, concurrency);
            lock (_startedWork) _startedWork.Add($"action:{request.Kind}:{request.TaskId}");
            Interlocked.Decrement(ref _concurrency);
            return new(request, WorkspaceActionOutcome.Committed, new(new([], [], []), [], []));
        }

        public bool WaitUntilStarted(long revision) =>
            _started.GetOrAdd(revision, _ => new()).Wait(TimeSpan.FromSeconds(5));

        public bool WasStarted(long revision) => _started.TryGetValue(revision, out var started) && started.IsSet;

        public int ThreadFor(long revision) => _threads[revision];

        public void Complete(long revision) => _completed.GetOrAdd(revision, _ => new()).Set();
    }


    private sealed class BlockingActionOperation : IWorkspacePersistenceOperation
    {
        private readonly List<string> _executedTaskIds = [];

        public ManualResetEventSlim Started { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public int WorkerThread { get; private set; }
        public IReadOnlyList<string> ExecutedTaskIds
        {
            get { lock (_executedTaskIds) return _executedTaskIds.ToArray(); }
        }

        public InspectorSaveResult Execute(InspectorSaveRequest request) => InspectorSaveResult.Saved(request);

        public WorkspaceActionResult ExecuteAction(WorkspaceActionRequest request)
        {
            WorkerThread = Environment.CurrentManagedThreadId;
            Started.Set();
            Release.Wait();
            lock (_executedTaskIds) _executedTaskIds.Add(request.TaskId!);
            return new(request, WorkspaceActionOutcome.Committed, new(new([], [], []), [], []));
        }
    }
}
