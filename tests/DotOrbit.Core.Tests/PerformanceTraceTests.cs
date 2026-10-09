using DotOrbit.Core.Diagnostics;
using DotOrbit.Core.Workspaces;
using Xunit;

namespace DotOrbit.Core.Tests;

public sealed class PerformanceTraceTests
{
    [Fact]
    public void DisabledMeasurementAllocatesNothing()
    {
        PerformanceTrace.Measure(PerformanceStage.Command).Dispose();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 100; index++)
            PerformanceTrace.Measure(PerformanceStage.Command).Dispose();
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void NestedSamplesUseMonotonicTimeAndParentIdentifiers()
    {
        var clock = new ManualClock();
        using var recording = PerformanceTrace.Start(clock);
        clock.Advance(5);
        using (PerformanceTrace.Measure(PerformanceStage.Command, PerformanceOperation.CompleteTask))
        {
            clock.Advance(3);
            using (PerformanceTrace.Measure(PerformanceStage.StorageWrite, "CompleteTask"))
                clock.Advance(7);
            clock.Advance(2);
        }
        var samples = recording.Snapshot();
        var child = samples[0];
        var parent = samples[1];
        Assert.Null(parent.ParentId);
        Assert.Equal(parent.Id, child.ParentId);
        Assert.Equal(5, parent.StartMilliseconds);
        Assert.Equal(12, parent.DurationMilliseconds);
        Assert.Equal(8, child.StartMilliseconds);
        Assert.Equal(7, child.DurationMilliseconds);
        Assert.Equal(Environment.CurrentManagedThreadId, parent.ThreadId);
        Assert.Equal(PerformanceOperation.CompleteTask, child.Operation);
    }

    [Fact]
    public void OperationAllowlistDoesNotRetainArbitraryStrings()
    {
        using var recording = PerformanceTrace.Start();
        foreach (var operation in new[] { "private task text", "1", "Read, CompleteTask", "read" })
            PerformanceTrace.Measure(PerformanceStage.Command, operation).Dispose();
        PerformanceTrace.Measure(PerformanceStage.Command, (PerformanceOperation)9999).Dispose();
        Assert.All(recording.Snapshot(), sample => Assert.Equal(PerformanceOperation.Other, sample.Operation));
        var allowed = Enum.GetNames<PerformanceOperation>();
        Assert.All(typeof(IWorkspaceWork).GetMethods(), method => Assert.Contains(method.Name, allowed));
    }

    [Fact]
    public void SamplesAreBoundedAndRepeatedScopeDisposalDoesNotDoubleCount()
    {
        using var recording = PerformanceTrace.Start(maximumSamples: 2);
        for (var index = 0; index < 5; index++)
        {
            var scope = PerformanceTrace.Measure(PerformanceStage.Command);
            scope.Dispose();
            scope.Dispose();
        }
        Assert.Equal(2, recording.Snapshot().Count);
        Assert.Equal(3, recording.DroppedSamples);
    }

    [Fact]
    public async Task ParallelScopesPreserveParentAndBoundedDropCounts()
    {
        using var recording = PerformanceTrace.Start(maximumSamples: 30);
        using var parent = PerformanceTrace.Measure(PerformanceStage.Command);
        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() =>
        {
            using var child = PerformanceTrace.Measure(PerformanceStage.StorageRead);
        }, TestContext.Current.CancellationToken)));
        var samples = recording.Snapshot();
        Assert.Equal(30, samples.Count);
        Assert.Equal(70, recording.DroppedSamples);
        Assert.Equal(30, samples.Select(sample => sample.Id).Distinct().Count());
        Assert.All(samples, sample => Assert.Equal(1L, sample.ParentId));
    }

    [Fact]
    public void StopFreezesSnapshotAndOldScopesCannotAffectNewRecording()
    {
        using var first = PerformanceTrace.Start();
        PerformanceTrace.Measure(PerformanceStage.Command).Dispose();
        var snapshot = first.Snapshot();
        var pending = PerformanceTrace.Measure(PerformanceStage.Command);
        first.Dispose();
        first.Dispose();
        using var second = PerformanceTrace.Start();
        using (PerformanceTrace.Measure(PerformanceStage.Command)) { }
        pending.Dispose();
        Assert.Single(snapshot);
        Assert.Single(first.Snapshot());
        Assert.Null(Assert.Single(second.Snapshot()).ParentId);
        Assert.Equal(0, first.DroppedSamples);
    }

    [Fact]
    public void ConcurrentRecordingIsRejectedAndInvalidBoundsDoNotStartCapture()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PerformanceTrace.Start(maximumSamples: 0));
        using var recording = PerformanceTrace.Start();
        Assert.Throws<InvalidOperationException>(() => PerformanceTrace.Start());
    }

    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => timestamp;
        internal void Advance(long milliseconds) => timestamp += milliseconds;
    }
}
