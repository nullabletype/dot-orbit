using System.Collections.Concurrent;
using DotOrbit.Desktop.ViewModels;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class InspectorSaveWriterTests
{
    [Fact]
    public async Task SubmitReturnsBeforePersistenceAndRunsOneLatestRevisionAtATime()
    {
        var operation = new ControlledInspectorSaveOperation();
        await using var writer = new SerializedInspectorSaveWriter(operation);
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
    public async Task DisposeDrainsCommittedWriteWithoutStartingQueuedWork()
    {
        var operation = new ControlledInspectorSaveOperation();
        var writer = new SerializedInspectorSaveWriter(operation);
        var committed = writer.SubmitAsync(ProjectUpdate(1, "Committed"));
        Assert.True(operation.WaitUntilStarted(1));
        var stale = writer.SubmitAsync(ProjectUpdate(2, "Stale"));

        var dispose = writer.DisposeAsync().AsTask();

        Assert.False(dispose.IsCompleted);
        Assert.Equal(InspectorSaveOutcome.Superseded, (await stale).Outcome);
        operation.Complete(1);
        Assert.Equal(InspectorSaveOutcome.Saved, (await committed).Outcome);
        await dispose;
        Assert.False(operation.WasStarted(2));
        Assert.Throws<ObjectDisposedException>(() => { _ = writer.SubmitAsync(ProjectUpdate(3, "Late")); });
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

    private sealed class ControlledInspectorSaveOperation : IInspectorSaveOperation
    {
        private readonly ConcurrentDictionary<long, ManualResetEventSlim> _started = new();
        private readonly ConcurrentDictionary<long, ManualResetEventSlim> _completed = new();
        private readonly ConcurrentDictionary<long, int> _threads = new();
        private readonly List<long> _startedRevisions = [];
        private int _concurrency;

        public IReadOnlyList<long> StartedRevisions
        {
            get { lock (_startedRevisions) return _startedRevisions.ToArray(); }
        }

        public int MaximumConcurrency { get; private set; }

        public InspectorSaveResult Execute(InspectorSaveRequest request)
        {
            var concurrency = Interlocked.Increment(ref _concurrency);
            MaximumConcurrency = Math.Max(MaximumConcurrency, concurrency);
            lock (_startedRevisions) _startedRevisions.Add(request.Revision);
            _threads[request.Revision] = Environment.CurrentManagedThreadId;
            _started.GetOrAdd(request.Revision, _ => new()).Set();
            _completed.GetOrAdd(request.Revision, _ => new()).Wait();
            Interlocked.Decrement(ref _concurrency);
            return InspectorSaveResult.Saved(request);
        }

        public bool WaitUntilStarted(long revision) =>
            _started.GetOrAdd(revision, _ => new()).Wait(TimeSpan.FromSeconds(5));

        public bool WasStarted(long revision) => _started.TryGetValue(revision, out var started) && started.IsSet;

        public int ThreadFor(long revision) => _threads[revision];

        public void Complete(long revision) => _completed.GetOrAdd(revision, _ => new()).Set();
    }
}
