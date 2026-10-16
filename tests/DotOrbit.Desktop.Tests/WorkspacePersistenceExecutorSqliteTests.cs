using DotOrbit.Core.Diagnostics;
using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.ViewModels;
using DotOrbit.Storage.Sqlite;
using Xunit;

namespace DotOrbit.Desktop.Tests;

[CollectionDefinition("Desktop performance trace", DisableParallelization = true)]
public sealed class DesktopPerformanceTraceTestGroup;

[Collection("Desktop performance trace")]
public sealed class WorkspacePersistenceExecutorSqliteTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RelativeDropIntentsResolveAgainstCommittedOrderAndPreserveHiddenTodaySlots(bool today)
    {
        var directory = Path.Combine(Path.GetTempPath(), "orbit-relative-drop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var passphrase = WorkspacePassphrase.Create("correct horse battery", "correct horse battery").Passphrase!;
            using var session = new EncryptedWorkspaceStore().Create(Path.Combine(directory, "workspace.db"),
                passphrase, CategoryName.Create("Home").CategoryName!).Session!;
            var category = Assert.Single(session.Work.Read().Categories);
            var ids = Enumerable.Range(0, 7).Reverse()
                .Select(index => (index, task: session.Work.CreateStandaloneTask($"Task {index}", "", category.Id, null)))
                .OrderBy(item => item.index).Select(item => item.task.Id).ToArray();
            foreach (var index in new[] { 0, 2, 4, 6 }) session.Work.ToggleTaskToday(ids[index]);
            session.Work.ToggleTaskTodayLane(ids[6]);
            var kind = today ? WorkspaceActionKind.MoveToday : WorkspaceActionKind.MoveBacklog;
            await using var executor = new SerializedWorkspacePersistenceExecutor(session.Work);
            var before = await executor.SubmitActionAsync(new(kind, ids[4], WorkspaceMoveKind.BeforeTargetTask, ids[0]));
            Assert.Equal(WorkspaceActionOutcome.Committed, before.Outcome);
            var expected = today ? new[] { 4, 1, 0, 3, 2, 5, 6 } : [4, 0, 1, 2, 3, 5, 6];
            Assert.Equal(expected.Select(index => ids[index]), session.Work.Read().Tasks.Select(task => task.Id));
            // These intents enter the same serialized queue together. The second anchor
            // is resolved after the first move, rather than using a stale visible index.
            var moves = await Task.WhenAll(
                executor.SubmitActionAsync(new(kind, ids[4], WorkspaceMoveKind.AfterTargetTask, ids[2])),
                executor.SubmitActionAsync(new(kind, ids[0], WorkspaceMoveKind.AfterTargetTask, ids[4])));
            Assert.All(moves, result => Assert.Equal(WorkspaceActionOutcome.Committed, result.Outcome));
            expected = today ? [2, 1, 4, 3, 0, 5, 6] : [1, 2, 4, 0, 3, 5, 6];
            Assert.Equal(expected.Select(index => ids[index]), session.Work.Read().Tasks.Select(task => task.Id));
            var revision = session.Planning.Read().WorkspaceRevision;
            var noOp = await executor.SubmitActionAsync(new(kind, ids[0], WorkspaceMoveKind.AfterTargetTask, ids[4]));
            Assert.Equal(WorkspaceActionOutcome.Committed, noOp.Outcome);
            Assert.Empty(noOp.Effect!.PositionChanges);
            var self = await executor.SubmitActionAsync(new(kind, ids[0], WorkspaceMoveKind.BeforeTargetTask, ids[0]));
            Assert.Empty(self.Effect!.PositionChanges);
            Assert.Equal(revision, session.Planning.Read().WorkspaceRevision);
            if (today)
            {
                var invalid = await executor.SubmitActionAsync(new(kind, ids[0], WorkspaceMoveKind.BeforeTargetTask, ids[6]));
                Assert.NotEqual(WorkspaceActionOutcome.Committed, invalid.Outcome);
                Assert.Equal(revision, session.Planning.Read().WorkspaceRevision);
                Assert.Equal(expected.Select(index => ids[index]), session.Work.Read().Tasks.Select(task => task.Id));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task OrderedActionsPersistExactlyOnceAgainstTheRealEncryptedWorkspace()
    {
        var directory = Path.Combine(Path.GetTempPath(), "orbit-action-executor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "workspace.db");
            var passphrase = WorkspacePassphrase.Create(
                "correct horse battery", "correct horse battery").Passphrase!;
            var created = new EncryptedWorkspaceStore().Create(
                path, passphrase, CategoryName.Create("Home").CategoryName!);
            using var session = created.Session!;
            var category = Assert.Single(session.Work.Read().Categories);
            var third = session.Work.CreateStandaloneTask("Third", "", category.Id, null);
            var second = session.Work.CreateStandaloneTask("Second", "", category.Id, null);
            var first = session.Work.CreateStandaloneTask("First", "", category.Id, null);
            await using var executor = new SerializedWorkspacePersistenceExecutor(session.Work);

            var results = await Task.WhenAll(
                executor.SubmitActionAsync(new(WorkspaceActionKind.ToggleToday, first.Id)),
                executor.SubmitActionAsync(new(WorkspaceActionKind.ToggleToday, first.Id)),
                executor.SubmitActionAsync(new(WorkspaceActionKind.ToggleCompletion, second.Id)),
                executor.SubmitActionAsync(new(WorkspaceActionKind.ToggleCompletion, second.Id)),
                executor.SubmitActionAsync(new(
                    WorkspaceActionKind.MoveBacklog, first.Id, WorkspaceMoveKind.Down)),
                executor.SubmitActionAsync(new(
                    WorkspaceActionKind.MoveBacklog, first.Id, WorkspaceMoveKind.Down)));

            Assert.All(results, result => Assert.Equal(WorkspaceActionOutcome.Committed, result.Outcome));
            var snapshot = session.Work.Read();
            Assert.Null(snapshot.Tasks.Single(task => task.Id == first.Id).TodayLane);
            Assert.False(snapshot.Tasks.Single(task => task.Id == second.Id).IsComplete);
            Assert.Equal([second.Id, third.Id, first.Id],
                snapshot.Tasks.OrderBy(task => task.SharedPosition).Select(task => task.Id));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ScopedActionsReturnAuthoritativeDeltasWithoutIncidentalWorkspaceReads()
    {
        var directory = Path.Combine(Path.GetTempPath(), "orbit-action-deltas-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "workspace.db");
            var passphrase = WorkspacePassphrase.Create(
                "correct horse battery", "correct horse battery").Passphrase!;
            var created = new EncryptedWorkspaceStore().Create(
                path, passphrase, CategoryName.Create("Home").CategoryName!);
            using var session = created.Session!;
            var category = Assert.Single(session.Work.Read().Categories);
            var third = session.Work.CreateStandaloneTask("Third", "", category.Id, null);
            var second = session.Work.CreateStandaloneTask("Second", "", category.Id, null);
            var first = session.Work.CreateStandaloneTask("First", "", category.Id, null);
            var operation = new WorkspacePersistenceOperation(session.Work);
            using var recording = PerformanceTrace.Start();

            var results = new[]
            {
                operation.ExecuteAction(new(WorkspaceActionKind.ToggleToday, first.Id)),
                operation.ExecuteAction(new(WorkspaceActionKind.ToggleToday, second.Id)),
                operation.ExecuteAction(new(WorkspaceActionKind.ToggleTodayLane, first.Id)),
                operation.ExecuteAction(new(WorkspaceActionKind.ToggleTodayLane, first.Id)),
                operation.ExecuteAction(new(WorkspaceActionKind.ToggleCompletion, first.Id)),
                operation.ExecuteAction(new(WorkspaceActionKind.ToggleCompletion, first.Id)),
                operation.ExecuteAction(new(WorkspaceActionKind.ToggleToday, first.Id)),
                operation.ExecuteAction(new(WorkspaceActionKind.MoveBacklog, first.Id, WorkspaceMoveKind.Bottom)),
                operation.ExecuteAction(new(WorkspaceActionKind.MoveToday, first.Id, WorkspaceMoveKind.Top)),
            };
            recording.Dispose();

            Assert.All(results, result =>
            {
                Assert.Equal(WorkspaceActionOutcome.Committed, result.Outcome);
                Assert.Null(result.Reload);
                Assert.NotNull(result.Effect);
            });
            Assert.All(results[..7], result => Assert.NotNull(result.Effect!.CommittedTask));
            Assert.Equal([second.Id, third.Id, first.Id],
                results[7].Effect!.PositionChanges.OrderBy(change => change.SharedPosition)
                    .Select(change => change.TaskId));
            Assert.Equal(1, results[8].Effect!.Position);
            Assert.Equal(2, results[8].Effect!.Count);

            var samples = recording.Snapshot();
            Assert.DoesNotContain(samples, sample => sample.Operation is
                PerformanceOperation.Read or
                PerformanceOperation.ReadTaskBin or
                PerformanceOperation.ReadProjectBin or
                PerformanceOperation.PreviewBulkTaskArchive or
                PerformanceOperation.SearchArchive);
            Assert.Equal(3, samples.Count(sample => sample.Operation == PerformanceOperation.ToggleTaskToday
                && sample.Stage == PerformanceStage.StorageWrite));
            Assert.Equal(2, samples.Count(sample => sample.Operation == PerformanceOperation.ToggleTaskTodayLane
                && sample.Stage == PerformanceStage.StorageWrite));
            Assert.Equal(2, samples.Count(sample => sample.Operation == PerformanceOperation.ToggleTaskCompletion
                && sample.Stage == PerformanceStage.StorageWrite));
            Assert.Single(samples, sample => sample.Operation == PerformanceOperation.MoveTaskInSharedOrder
                && sample.Stage == PerformanceStage.StorageWrite);
            Assert.Single(samples, sample => sample.Operation == PerformanceOperation.MoveTaskInTodayLane
                && sample.Stage == PerformanceStage.StorageWrite);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
