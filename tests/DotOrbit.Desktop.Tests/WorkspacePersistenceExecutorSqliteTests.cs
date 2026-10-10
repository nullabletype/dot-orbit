using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.ViewModels;
using DotOrbit.Storage.Sqlite;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class WorkspacePersistenceExecutorSqliteTests
{
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
}
