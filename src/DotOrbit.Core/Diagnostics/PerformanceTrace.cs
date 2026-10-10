using System.Runtime.CompilerServices;

namespace DotOrbit.Core.Diagnostics;

public enum PerformanceStage
{
    Command, DispatchDelay, Refresh, ProjectionApply, StorageRead, StorageWrite,
    GateWait, ConnectionOpen, ConnectionConfigure, TransactionBegin, Query,
    Mutation, DerivedStorage, Commit, Recovery,
}

public enum PerformanceOperation
{
    Other,
    OpenConnection,
    ConfigureConnection,
    Reload,
    RefreshFromStore,
    Read,
    ReadTaskBin,
    ReadProjectBin,
    PreviewEmptyBin,
    SearchArchive,
    CreateCategory,
    RenameCategory,
    UpdateCategory,
    DeleteCategory,
    CreateParticipant,
    RenameParticipant,
    DeleteParticipant,
    MoveCategory,
    CreateProject,
    CreateTask,
    CreateTaskDraft,
    CreateStandaloneTask,
    UpdateProject,
    UpdateTask,
    CompleteTask,
    ReopenTask,
    ArchiveTask,
    RestoreTask,
    MoveTaskToBin,
    RestoreTaskFromBin,
    MoveProjectToBin,
    RestoreProjectFromBin,
    EmptyBin,
    ArchiveProject,
    RestoreProject,
    PreviewBulkTaskArchive,
    BulkArchiveTasks,
    SetTaskTodayLane,
    ToggleTaskToday,
    ToggleTaskTodayLane,
    ToggleTaskCompletion,
    ClearToday,
    MoveTaskInTodayLane,
    MoveTaskInSharedOrder,
    MoveProject,
    MoveTaskInProject,
    DetachTask,
    AttachTask,
}

public sealed record PerformanceSample(
    long Id,
    long? ParentId,
    PerformanceStage Stage,
    PerformanceOperation Operation,
    double StartMilliseconds,
    double DurationMilliseconds,
    int ThreadId);

/// <summary>Opt-in, bounded measurements containing only fixed operation identifiers and timing.</summary>
public static class PerformanceTrace
{
    private static readonly AsyncLocal<PerformanceScopeState?> Current = new();
    private static PerformanceRecording? recording;

    public static PerformanceRecording Start(TimeProvider? timeProvider = null, int maximumSamples = 30000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSamples);
        var next = new PerformanceRecording(timeProvider ?? TimeProvider.System, maximumSamples);
        if (Interlocked.CompareExchange(ref recording, next, null) is not null)
            throw new InvalidOperationException("A performance recording is already active.");
        return next;
    }

    public static PerformanceScope Measure(PerformanceStage stage, [CallerMemberName] string operation = "")
    {
        var active = Volatile.Read(ref recording);
        if (active is null) return default;
        var known = OperationNames.Values.GetValueOrDefault(operation, PerformanceOperation.Other);
        return Measure(active, stage, known);
    }

    public static PerformanceScope Measure(PerformanceStage stage, PerformanceOperation operation)
    {
        var active = Volatile.Read(ref recording);
        return active is null ? default : Measure(active, stage,
            Enum.IsDefined(operation) ? operation : PerformanceOperation.Other);
    }

    private static PerformanceScope Measure(PerformanceRecording active, PerformanceStage stage,
        PerformanceOperation operation)
    {
        var previous = Current.Value;
        var state = active.Begin(stage, operation, previous);
        if (state is null) return default;
        Current.Value = state;
        return new PerformanceScope(state);
    }

    internal static void Complete(PerformanceScopeState state, bool capture = true)
    {
        if (ReferenceEquals(Current.Value, state)) Current.Value = state.Previous;
        state.Recording.Complete(state, capture);
    }

    internal static void Stop(PerformanceRecording active) =>
        Interlocked.CompareExchange(ref recording, null, active);

    private static class OperationNames
    {
        internal static readonly Dictionary<string, PerformanceOperation> Values =
            Enum.GetValues<PerformanceOperation>().ToDictionary(value => value.ToString(), StringComparer.Ordinal);
    }
}

public readonly struct PerformanceScope : IDisposable
{
    private readonly PerformanceScopeState? state;
    internal PerformanceScope(PerformanceScopeState state) => this.state = state;
    public void Dispose()
    {
        if (state is not null) PerformanceTrace.Complete(state);
    }

    /// <summary>Restores this execution context without recording an unfinished measurement.</summary>
    public void Cancel()
    {
        if (state is not null) PerformanceTrace.Complete(state, capture: false);
    }
}

public sealed class PerformanceRecording : IDisposable
{
    private readonly object gate = new();
    private readonly TimeProvider timeProvider;
    private readonly long origin;
    private readonly int maximumSamples;
    private readonly List<PerformanceSample> samples = [];
    private long nextId;
    private long droppedSamples;
    private bool disposed;

    internal PerformanceRecording(TimeProvider timeProvider, int maximumSamples)
    {
        this.timeProvider = timeProvider;
        this.maximumSamples = maximumSamples;
        origin = timeProvider.GetTimestamp();
    }

    public long DroppedSamples
    {
        get { lock (gate) return droppedSamples; }
    }

    public IReadOnlyList<PerformanceSample> Snapshot()
    {
        lock (gate) return samples.ToArray();
    }

    internal PerformanceScopeState? Begin(PerformanceStage stage, PerformanceOperation operation,
        PerformanceScopeState? previous)
    {
        lock (gate)
        {
            if (disposed) return null;
            var parentId = previous is { Completed: false } && ReferenceEquals(previous.Recording, this)
                ? previous.Id : (long?)null;
            return new PerformanceScopeState(this, ++nextId, parentId, stage, operation,
                timeProvider.GetTimestamp(), Environment.CurrentManagedThreadId, previous);
        }
    }

    internal void Complete(PerformanceScopeState state, bool capture)
    {
        lock (gate)
        {
            if (state.Completed) return;
            state.Completed = true;
            if (disposed || !capture) return;
            if (samples.Count == maximumSamples)
            {
                droppedSamples++;
                return;
            }
            var finished = timeProvider.GetTimestamp();
            samples.Add(new PerformanceSample(state.Id, state.ParentId, state.Stage, state.Operation,
                timeProvider.GetElapsedTime(origin, state.Start).TotalMilliseconds,
                timeProvider.GetElapsedTime(state.Start, finished).TotalMilliseconds, state.ThreadId));
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            PerformanceTrace.Stop(this);
        }
    }
}

internal sealed class PerformanceScopeState(
    PerformanceRecording recording, long id, long? parentId, PerformanceStage stage,
    PerformanceOperation operation, long start, int threadId, PerformanceScopeState? previous)
{
    internal PerformanceRecording Recording { get; } = recording;
    internal long Id { get; } = id;
    internal long? ParentId { get; } = parentId;
    internal PerformanceStage Stage { get; } = stage;
    internal PerformanceOperation Operation { get; } = operation;
    internal long Start { get; } = start;
    internal int ThreadId { get; } = threadId;
    internal PerformanceScopeState? Previous { get; } = previous;
    internal bool Completed { get; set; }
}
