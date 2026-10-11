using DotOrbit.Desktop;
using DotOrbit.Core.Diagnostics;
using DotOrbit.Core.Workspaces;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class PerformanceReviewRunnerTests
{
    [Fact]
    public void StorageUpdateTraceSummaryAggregatesEveryDirectWritePhase()
    {
        var summary = StorageUpdateTraceSummary.Create(
            "storage-update",
            PerformanceOperation.UpdateTask,
            CreateCompleteStorageTrace(PerformanceOperation.UpdateTask));

        Assert.Equal("storage-update", summary.Operation);
        Assert.Equal(2, summary.Iterations);
        Assert.Equal(2, summary.FullSnapshotReads);
        Assert.Equal(2, summary.ArchiveIndexRebuilds);
        AssertPhase(summary.GateWait, 1, 1.5, 10);
        AssertPhase(summary.TransactionBegin, 2, 2.5, 20);
        AssertPhase(summary.Mutation, 4, 5, 600);
        AssertPhase(summary.DerivedStorage, 3, 4, 300);
        AssertPhase(summary.Commit, 5, 7, 500);
        AssertPhase(summary.Recovery, 6, 8, 100);
    }

    [Fact]
    public void StorageUpdateTraceSummaryRejectsMissingOrDuplicateDirectWritePhases()
    {
        var missing = CreateCompleteStorageTrace(PerformanceOperation.UpdateTask);
        missing.RemoveAll(sample => sample.ParentId == 11 && sample.Stage == PerformanceStage.Recovery);
        var duplicate = CreateCompleteStorageTrace(PerformanceOperation.UpdateTask);
        duplicate.Add(Sample(21, 1, PerformanceStage.Commit, PerformanceOperation.UpdateTask, 9, 900));

        Assert.Throws<InvalidOperationException>(() => StorageUpdateTraceSummary.Create(
            "storage-update", PerformanceOperation.UpdateTask, missing));
        Assert.Throws<InvalidOperationException>(() => StorageUpdateTraceSummary.Create(
            "storage-update", PerformanceOperation.UpdateTask, duplicate));
    }

    [Fact]
    public void StorageUpdateTraceSummaryFormatsProjectPhaseCountsLatencyAndAllocation()
    {
        var summary = StorageUpdateTraceSummary.Create(
            "storage-project-update",
            PerformanceOperation.UpdateProject,
            CreateCompleteStorageTrace(PerformanceOperation.UpdateProject));

        var output = PerformanceReviewRunner.FormatStorageUpdateTraceSummary(summary);

        Assert.Equal(
            "performance-review: operation=storage-project-update-internal-phases iterations=2 "
            + "gate_wait_count=2 gate_wait_p50_ms=1.000 gate_wait_p95_ms=1.500 gate_wait_max_ms=1.500 gate_wait_alloc_p50_bytes=10 "
            + "transaction_begin_count=2 transaction_begin_p50_ms=2.000 transaction_begin_p95_ms=2.500 transaction_begin_max_ms=2.500 transaction_begin_alloc_p50_bytes=20 "
            + "mutation_count=2 mutation_p50_ms=4.000 mutation_p95_ms=5.000 mutation_max_ms=5.000 mutation_alloc_p50_bytes=600 "
            + "derived_count=2 derived_p50_ms=3.000 derived_p95_ms=4.000 derived_max_ms=4.000 derived_alloc_p50_bytes=300 "
            + "commit_count=2 commit_p50_ms=5.000 commit_p95_ms=7.000 commit_max_ms=7.000 commit_alloc_p50_bytes=500 "
            + "recovery_count=2 recovery_p50_ms=6.000 recovery_p95_ms=8.000 recovery_max_ms=8.000 recovery_alloc_p50_bytes=100 "
            + "full_snapshot_reads=2 archive_index_rebuilds=2 result=observed",
            output);
    }

    [Theory]
    [InlineData(50, 999_999, true)]
    [InlineData(50.001, 999_999, false)]
    [InlineData(50, 1_000_000, false)]
    public void StorageUpdateConstraintUsesDedicatedLatencyAndAllocationBudgets(
        double p95Milliseconds,
        long allocatedBytes,
        bool expected)
    {
        var result = PerformanceOperationResult.Create(
            "storage-update",
            [1, p95Milliseconds],
            [allocatedBytes, allocatedBytes],
            100);

        Assert.Equal(expected, PerformanceReviewRunner.StorageUpdateConstraintsPassed(result));
    }

    [Fact]
    public void ScopedActionReadCountsSeparateWritesFromIncidentalSurfaceReads()
    {
        var counts = ScopedActionReadCounts.Create(
            "today-membership",
            [
                Sample(PerformanceStage.StorageWrite, PerformanceOperation.ToggleTaskToday),
                Sample(PerformanceStage.StorageWrite, PerformanceOperation.ToggleTaskToday),
                Sample(PerformanceStage.StorageRead, PerformanceOperation.Read),
                Sample(PerformanceStage.StorageRead, PerformanceOperation.ReadTaskBin),
                Sample(PerformanceStage.StorageRead, PerformanceOperation.ReadProjectBin),
                Sample(PerformanceStage.StorageRead, PerformanceOperation.PreviewBulkTaskArchive),
                Sample(PerformanceStage.StorageRead, PerformanceOperation.SearchArchive),
            ]);

        Assert.Equal(2, counts.StorageWrites);
        Assert.Equal(1, counts.Workspace);
        Assert.Equal(1, counts.TaskBin);
        Assert.Equal(1, counts.ProjectBin);
        Assert.Equal(1, counts.BulkPreview);
        Assert.Equal(1, counts.ArchiveSearch);
        Assert.Equal(5, counts.IncidentalReadCount);

        static PerformanceSample Sample(PerformanceStage stage, PerformanceOperation operation) =>
            new(1, null, stage, operation, 0, 1, 1);
    }

    [Theory]
    [InlineData(0, 0, 0, true)]
    [InlineData(1, 0, 0, false)]
    [InlineData(0, 1, 0, false)]
    [InlineData(0, 0, 1, false)]
    public void ConnectionReuseConstraintRequiresCompleteTraceWithoutWarmOpens(
        int connectionOpenCount,
        int connectionConfigureCount,
        long droppedSamples,
        bool expected)
    {
        Assert.Equal(
            expected,
            PerformanceReviewRunner.ConnectionReuseConstraintsPassed(
                connectionOpenCount,
                connectionConfigureCount,
                droppedSamples));
    }

    [Fact]
    public void UnrelatedArgumentsDoNotEnableTheReview()
    {
        var scenario = PerformanceReviewScenario.FromArguments(["--native-smoke"]);

        Assert.False(scenario.IsEnabled);
        Assert.True(scenario.IsValid);
    }

    [Fact]
    public void ReviewArgumentsDefaultToABoundedRepresentativeScenario()
    {
        var scenario = PerformanceReviewScenario.FromArguments(["--performance-review"]);

        Assert.True(scenario.IsEnabled);
        Assert.True(scenario.IsValid);
        Assert.Equal(250, scenario.TaskCount);
        Assert.Equal(20, scenario.Iterations);
        Assert.Equal(100, scenario.BudgetMilliseconds);
        Assert.Equal(0, scenario.RecoveryPointCount);
    }

    [Fact]
    public void ReviewArgumentsAcceptExplicitBoundedValues()
    {
        var scenario = PerformanceReviewScenario.FromArguments(
            [
                "--performance-review",
                "--performance-tasks=1000",
                "--performance-iterations=30",
                "--performance-budget-ms=75",
                "--performance-recovery-points=16",
            ]);

        Assert.True(scenario.IsEnabled);
        Assert.True(scenario.IsValid);
        Assert.Equal(1000, scenario.TaskCount);
        Assert.Equal(30, scenario.Iterations);
        Assert.Equal(75, scenario.BudgetMilliseconds);
        Assert.Equal(16, scenario.RecoveryPointCount);
    }

    [Theory]
    [InlineData("--performance-recovery-points=-1")]
    [InlineData("--performance-recovery-points=17")]
    [InlineData("--performance-recovery-points=invalid")]
    [InlineData("--performance-tasks=1")]
    [InlineData("--performance-tasks=5001")]
    [InlineData("--performance-iterations=4")]
    [InlineData("--performance-budget-ms=0")]
    [InlineData("--performance-tasks=not-a-number")]
    public void ReviewArgumentsRejectInvalidOrUnboundedValues(string argument)
    {
        var scenario = PerformanceReviewScenario.FromArguments(["--performance-review", argument]);

        Assert.True(scenario.IsEnabled);
        Assert.False(scenario.IsValid);
    }

    [Fact]
    public void ReviewArgumentsRejectDuplicateRecoveryPointCounts()
    {
        var scenario = PerformanceReviewScenario.FromArguments(
            ["--performance-review", "--performance-recovery-points=1", "--performance-recovery-points=16"]);

        Assert.False(scenario.IsValid);
    }

    [Fact]
    public void ReviewArgumentsRejectConcurrentDiagnosticTraceMode()
    {
        var scenario = PerformanceReviewScenario.FromArguments(
            ["--performance-review", "--performance-trace"]);

        Assert.True(scenario.IsEnabled);
        Assert.False(scenario.IsValid);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    public void WorkspaceSeedsRealAutomaticPointsAndReopensWithTheirConfiguration(int pointCount)
    {
        using var workspace = PerformanceReviewWorkspace.Create(2, pointCount);
        var directory = workspace.Session.Recovery.AutomaticRecoveryDirectoryPath;
        Assert.NotNull(directory);
        Assert.Equal(pointCount, Directory.GetFiles(directory, "*.dotorbit-recovery").Length);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero), workspace.TimeProvider.GetUtcNow());
        var task = workspace.Session.Work.Read().Tasks[0];

        workspace.Session.Work.SetTaskTodayLane(task.Id, TodayLane.Planned);

        // Setup ended at the last successful point, so the warm mutation is pre-deadline.
        Assert.Equal(pointCount, Directory.GetFiles(directory, "*.dotorbit-recovery").Length);
    }

    [Fact]
    public void MeasurementSummaryUsesNearestRankPercentiles()
    {
        var result = PerformanceOperationResult.Create(
            "task-title-save",
            Enumerable.Range(1, 20).Select(value => (double)value).Reverse().ToArray(),
            Enumerable.Range(1, 20).Select(value => (long)value * 100).Reverse().ToArray(),
            100);

        Assert.Equal(20, result.Iterations);
        Assert.Equal(10, result.P50Milliseconds);
        Assert.Equal(19, result.P95Milliseconds);
        Assert.Equal(20, result.MaximumMilliseconds);
        Assert.Equal(1000, result.P50AllocatedBytes);
    }

    [Fact]
    public void TitleSaveAllocationGateUsesWholeStableBoundary()
    {
        var action = PerformanceOperationResult.Create(
            "task-title-save-action",
            [1, 1, 1],
            [1_000, 1_000, 1_000],
            50);
        var withinBudget = PerformanceOperationResult.Create(
            "task-title-save-stable",
            [25, 30, 35],
            [3_000_000, 3_500_000, 3_900_000],
            100);
        var writerAllocationExceeded = PerformanceOperationResult.Create(
            "task-title-save-stable",
            [25, 30, 35],
            [4_100_000, 4_500_000, 5_000_000],
            100);

        Assert.True(PerformanceReviewRunner.TitleSaveStableConstraintsPassed(withinBudget, 100));
        Assert.False(PerformanceReviewRunner.TitleSaveStableConstraintsPassed(writerAllocationExceeded, 100));
        Assert.False(PerformanceReviewRunner.ReviewConstraintsPassed([action, writerAllocationExceeded], 100));
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(201, false)]
    public void TitleSaveStableMaximumUsesTwiceTheBudgetAsAnInclusiveBoundary(
        double maximumMilliseconds,
        bool expected)
    {
        var stable = PerformanceOperationResult.Create(
            "task-title-save-stable",
            [25, 30, maximumMilliseconds],
            [1_000, 1_000, 1_000],
            100);

        Assert.Equal(expected, PerformanceReviewRunner.TitleSaveStableConstraintsPassed(stable, 100));
    }

    [Theory]
    [InlineData(3_999_999, true)]
    [InlineData(4_000_000, false)]
    public void TitleSaveStableAllocationBudgetIsStrict(long allocatedBytes, bool expected)
    {
        var stable = PerformanceOperationResult.Create(
            "task-title-save-stable",
            [25, 30, 35],
            [allocatedBytes, allocatedBytes, allocatedBytes],
            100);

        Assert.Equal(expected, PerformanceReviewRunner.TitleSaveStableConstraintsPassed(stable, 100));
    }

    [Fact]
    public void WorkspaceUsesARepeatableSyntheticShapeAndDeletesItsFiles()
    {
        string directory;
        using (var workspace = PerformanceReviewWorkspace.Create(25))
        {
            directory = workspace.DirectoryPath;
            var snapshot = workspace.Session.Work.Read();

            Assert.True(Directory.Exists(directory));
            Assert.Equal(8, snapshot.Categories.Count);
            Assert.Equal(8, snapshot.Participants.Count);
            Assert.Single(snapshot.Projects);
            Assert.Equal(25, snapshot.Tasks.Count);
            Assert.Single(snapshot.Tasks, task => task.IsArchived);
            Assert.Equal(
                new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero),
                workspace.TimeProvider.GetUtcNow());
            Assert.Equal(TimeZoneInfo.Utc, workspace.TimeProvider.LocalTimeZone);
        }

        Assert.False(Directory.Exists(directory));
    }

    [Theory]
    [InlineData(
        "1.0.0+A120FADEAE0B63048A9D370B4AC8298875789D2D",
        "a120fadeae0b63048a9d370b4ac8298875789d2d")]
    [InlineData("1.0.0", "unrecorded")]
    [InlineData("1.0.0+not-a-commit", "unrecorded")]
    [InlineData(null, "unrecorded")]
    public void CommitIsDerivedFromTheAssemblyInformationalVersion(
        string? informationalVersion,
        string expected)
    {
        Assert.Equal(expected, PerformanceReviewBuild.Commit(informationalVersion));
    }

    private static List<PerformanceSample> CreateCompleteStorageTrace(PerformanceOperation operation) =>
    [
        Sample(1, null, PerformanceStage.StorageWrite, operation, 10, 1_000),
        Sample(2, 1, PerformanceStage.GateWait, operation, 1, 10),
        Sample(3, 1, PerformanceStage.TransactionBegin, PerformanceOperation.Other, 2, 20),
        Sample(4, 1, PerformanceStage.Mutation, operation, 4, 600),
        Sample(5, 4, PerformanceStage.SnapshotRead, PerformanceOperation.Other, 1, 200),
        Sample(6, 1, PerformanceStage.DerivedStorage, operation, 3, 300),
        Sample(7, 6, PerformanceStage.ArchiveIndexRebuild, PerformanceOperation.Other, 2, 280),
        Sample(8, 7, PerformanceStage.SnapshotRead, PerformanceOperation.Other, 1, 250),
        Sample(9, 1, PerformanceStage.Commit, operation, 5, 500),
        Sample(10, 1, PerformanceStage.Recovery, operation, 6, 100),
        Sample(11, null, PerformanceStage.StorageWrite, operation, 12, 1_200),
        Sample(12, 11, PerformanceStage.GateWait, operation, 1.5, 15),
        Sample(13, 11, PerformanceStage.TransactionBegin, PerformanceOperation.Other, 2.5, 25),
        Sample(14, 11, PerformanceStage.Mutation, operation, 5, 700),
        Sample(15, 11, PerformanceStage.DerivedStorage, operation, 4, 400),
        Sample(16, 15, PerformanceStage.ArchiveIndexRebuild, PerformanceOperation.Other, 3, 380),
        Sample(17, 11, PerformanceStage.Commit, operation, 7, 550),
        Sample(18, 11, PerformanceStage.Recovery, operation, 8, 150),
        Sample(19, 99, PerformanceStage.SnapshotRead, PerformanceOperation.Other, 50, 50_000),
        Sample(20, null, PerformanceStage.ArchiveIndexRebuild, PerformanceOperation.Other, 50, 50_000),
    ];

    private static void AssertPhase(
        PerformancePhaseObservation phase,
        double p50Milliseconds,
        double maximumMilliseconds,
        long p50AllocatedBytes)
    {
        Assert.Equal(2, phase.Count);
        Assert.Equal(p50Milliseconds, phase.P50Milliseconds);
        Assert.Equal(maximumMilliseconds, phase.P95Milliseconds);
        Assert.Equal(maximumMilliseconds, phase.MaximumMilliseconds);
        Assert.Equal(p50AllocatedBytes, phase.P50AllocatedBytes);
    }

    private static PerformanceSample Sample(
        long id,
        long? parentId,
        PerformanceStage stage,
        PerformanceOperation operation,
        double duration,
        long allocation) =>
        new(id, parentId, stage, operation, 0, duration, 1, allocation);
}
