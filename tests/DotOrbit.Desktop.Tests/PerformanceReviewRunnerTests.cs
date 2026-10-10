using DotOrbit.Desktop;
using DotOrbit.Core.Diagnostics;
using DotOrbit.Core.Workspaces;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class PerformanceReviewRunnerTests
{
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
}
