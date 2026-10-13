using DotOrbit.Core.Interchange;
using DotOrbit.Core.Workspaces;
using DotOrbit.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DotOrbit.Storage.Sqlite.Tests;

public sealed class WorkspaceRevisionTests : IDisposable
{
    private const string PassphraseText = "private synthetic password";
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "orbit-revision-" + Guid.NewGuid().ToString("N"));
    private readonly EncryptedWorkspaceStore _store = new();
    private readonly WorkspacePassphrase _passphrase = WorkspacePassphrase.Create(
        PassphraseText,
        PassphraseText).Passphrase!;

    private string WorkspacePath => Path.Combine(_directory, "workspace.db");

    [Fact]
    public void NewWorkspaceStartsAtRevisionOneAndPersistsIt()
    {
        using (var session = CreateWorkspace())
            Assert.Equal(1, session.Planning.Read().WorkspaceRevision.Value);

        using var reopened = Assert.IsAssignableFrom<IWorkspaceSession>(
            _store.Open(WorkspacePath, _passphrase).Session);
        Assert.Equal(1, reopened.Planning.Read().WorkspaceRevision.Value);
    }

    [Fact]
    public void SchemaFourteenMigrationStartsAtRevisionOne()
    {
        CreateWorkspace().Dispose();
        using (var connection = OpenConnection(SqliteOpenMode.ReadWrite))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE workspace_revision; PRAGMA user_version=14;";
            command.ExecuteNonQuery();
        }

        var opened = _store.Open(WorkspacePath, _passphrase);
        using var migrated = Assert.IsAssignableFrom<IWorkspaceSession>(opened.Session);

        Assert.Equal(WorkspaceOpenStatus.Opened, opened.Status);
        Assert.Equal(EncryptedWorkspaceStore.CurrentSchemaVersion, migrated.SchemaVersion);
        Assert.Equal(1, Revision(migrated));
    }

    [Fact]
    public void NonIntegerRevisionIsRejectedOnOpen()
    {
        CreateWorkspace().Dispose();
        using (var connection = OpenConnection(SqliteOpenMode.ReadWrite))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA ignore_check_constraints=ON; UPDATE workspace_revision SET revision=1.5;";
            command.ExecuteNonQuery();
        }

        var opened = _store.Open(WorkspacePath, _passphrase);

        Assert.Equal(WorkspaceOpenStatus.InvalidPassphraseOrStore, opened.Status);
        Assert.Null(opened.Session);
    }

    [Fact]
    public void EffectiveTransactionsAdvanceRevisionExactlyOnce()
    {
        using var session = CreateWorkspace();
        var home = Assert.Single(session.Work.Read().Categories);

        var work = session.Work.CreateCategory("Work");
        Assert.Equal(2, Revision(session));
        var project = session.Work.CreateProject("Project", "", work.Id, null);
        Assert.Equal(3, Revision(session));
        session.Work.CreateTaskDraft(
            project.Id,
            "Task",
            "",
            null,
            null,
            new([], ["Alex"]),
            TodayLane.Planned);

        Assert.Equal(4, Revision(session));
        Assert.Equal([home.Id, work.Id], session.Planning.Read().Orders.CategoryIds);
    }

    [Fact]
    public void HiddenStateMutationFamiliesAdvanceRevisionExactlyOnce()
    {
        using var session = CreateWorkspace();
        var expected = Revision(session);
        var category = AssertAdvances(session, ref expected,
            () => session.Work.CreateCategory("Work", "rose"));
        var participant = AssertAdvances(session, ref expected,
            () => session.Work.CreateParticipant("Alex"));
        var project = AssertAdvances(session, ref expected,
            () => session.Work.CreateProject("Project", "", category.Id, null, "ocean"));
        var first = AssertAdvances(session, ref expected,
            () => session.Work.CreateTaskDraft(
                project.Id,
                "First",
                "",
                null,
                null,
                new([participant.Id], []),
                TodayLane.Planned));
        var second = AssertAdvances(session, ref expected,
            () => session.Work.CreateTask(project.Id, "Second"));
        _ = AssertAdvances(session, ref expected, () => session.Work.CompleteTask(first.Id));
        _ = AssertAdvances(session, ref expected, () => session.Work.CompleteTask(second.Id));
        _ = AssertAdvances(session, ref expected, () => session.Work.ArchiveTask(first.Id));
        _ = AssertAdvances(session, ref expected, () => session.Work.RestoreTask(first.Id));
        _ = AssertAdvances(session, ref expected, () => session.Work.MoveTaskToBin(first.Id));
        _ = AssertAdvances(session, ref expected, () => session.Work.RestoreTaskFromBin(first.Id));
        _ = AssertAdvances(session, ref expected, () => session.Work.ArchiveProject(project.Id));
        _ = AssertAdvances(session, ref expected, () => session.Work.RestoreProject(project.Id));
        _ = AssertAdvances(session, ref expected, () => session.Work.MoveProjectToBin(project.Id));
        _ = AssertAdvances(session, ref expected, () => session.Work.RestoreProjectFromBin(project.Id));
        _ = AssertAdvances(session, ref expected,
            () => session.Work.UpdateCategory(category.Id, "Work renamed", "emerald"));
        _ = AssertAdvances(session, ref expected,
            () => session.Work.UpdateProject(project.Id, "Project renamed", "", category.Id, null, "gold"));
        var persistedFirst = session.Work.Read().Tasks.Single(task => task.Id == first.Id);
        _ = AssertAdvances(session, ref expected,
            () => session.Work.UpdateTask(
                persistedFirst.Id,
                persistedFirst.Title,
                persistedFirst.Description,
                persistedFirst.ExplicitCategoryId,
                persistedFirst.DueDate,
                new([], [])));
        AssertAdvances(session, ref expected, () => session.Work.DeleteParticipant(participant.Id));
    }

    [Fact]
    public void EmptyBinAdvancesOnceAndAnEmptyBinDoesNotAdvance()
    {
        using var session = CreateWorkspace();
        var category = Assert.Single(session.Work.Read().Categories);
        var task = session.Work.CreateStandaloneTask("Discard", "", category.Id, null);
        session.Work.MoveTaskToBin(task.Id);
        Assert.Equal(RecoveryDirectoryConfigurationStatus.Configured,
            session.Recovery.ConfigureAutomaticRecoveryDirectory(Path.Combine(_directory, "empty-bin-recovery")).Status);
        var before = Revision(session);

        var result = session.Work.EmptyBin(session.Work.PreviewEmptyBin());

        Assert.Equal(EmptyBinStatus.Emptied, result.Status);
        Assert.Equal(before + 1, Revision(session));
        var after = Revision(session);
        var noOp = session.Work.EmptyBin(session.Work.PreviewEmptyBin());
        Assert.Equal(EmptyBinStatus.Emptied, noOp.Status);
        Assert.Equal(after, Revision(session));
    }

    [Fact]
    public void RemainingUserMutationFamiliesAdvanceRevisionExactlyOnce()
    {
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        var store = new EncryptedWorkspaceStore(
            new SystemIdentifierGenerator(),
            new WorkspaceFileOperations(),
            time);
        using var session = Assert.IsAssignableFrom<IWorkspaceSession>(store.Create(
            WorkspacePath,
            _passphrase,
            CategoryName.Create("Home").CategoryName!).Session);
        var expected = Revision(session);
        var work = AssertAdvances(session, ref expected,
            () => session.Work.CreateCategory("Work"));
        var disposable = AssertAdvances(session, ref expected,
            () => session.Work.CreateCategory("Disposable"));
        AssertAdvances(session, ref expected,
            () => session.Work.DeleteCategory(disposable.Id));
        _ = AssertAdvances(session, ref expected,
            () => session.Work.MoveCategory(work.Id, 0));
        var participant = AssertAdvances(session, ref expected,
            () => session.Work.CreateParticipant("Alex"));
        _ = AssertAdvances(session, ref expected,
            () => session.Work.RenameParticipant(participant.Id, "Alexandra"));
        var firstProject = AssertAdvances(session, ref expected,
            () => session.Work.CreateProject("First", "", work.Id, null));
        var secondProject = AssertAdvances(session, ref expected,
            () => session.Work.CreateProject("Second", "", work.Id, null));
        _ = AssertAdvances(session, ref expected,
            () => session.Work.MoveProject(secondProject.Id, 0));
        var firstTask = AssertAdvances(session, ref expected,
            () => session.Work.CreateTask(firstProject.Id, "First task"));
        var secondTask = AssertAdvances(session, ref expected,
            () => session.Work.CreateTask(firstProject.Id, "Second task"));
        _ = AssertAdvances(session, ref expected,
            () => session.Work.MoveTaskInSharedOrder(firstTask.Id, 0));
        _ = AssertAdvances(session, ref expected,
            () => session.Work.MoveTaskInProject(firstProject.Id, secondTask.Id, 0));
        _ = AssertAdvances(session, ref expected,
            () => session.Work.SetTaskTodayLane(firstTask.Id, TodayLane.Planned));
        _ = AssertAdvances(session, ref expected,
            () => session.Work.SetTaskTodayLane(secondTask.Id, TodayLane.Planned));
        _ = AssertAdvances(session, ref expected,
            () => session.Work.MoveTaskInTodayLane(secondTask.Id, 0));
        _ = AssertAdvances(session, ref expected,
            () => session.Work.ToggleTaskTodayLane(firstTask.Id));
        _ = AssertAdvances(session, ref expected,
            () => session.Work.ToggleTaskToday(firstTask.Id));
        AssertAdvances(session, ref expected, () =>
            Assert.Equal(1, session.Work.ClearToday()));
        _ = AssertAdvances(session, ref expected,
            () => session.Work.DetachTask(firstTask.Id));
        _ = AssertAdvances(session, ref expected,
            () => session.Work.AttachTask(firstTask.Id, secondProject.Id));
        _ = AssertAdvances(session, ref expected,
            () => session.Work.DuplicateTask(firstTask.Id));
        var preview = session.Work.PreviewProjectDuplication(firstProject.Id);
        _ = AssertAdvances(session, ref expected,
            () => session.Work.DuplicateProject(preview, "First copy"));
        _ = AssertAdvances(session, ref expected,
            () => session.Work.ToggleTaskCompletion(secondTask.Id));
        _ = AssertAdvances(session, ref expected,
            () => session.Work.ToggleTaskCompletion(secondTask.Id));
        var oldTask = AssertAdvances(session, ref expected,
            () => session.Work.CreateStandaloneTask("Old", "", work.Id, null));
        _ = AssertAdvances(session, ref expected,
            () => session.Work.CompleteTask(oldTask.Id));
        time.Advance(TimeSpan.FromDays(3));
        var bulkPreview = session.Work.PreviewBulkTaskArchive(1);
        var bulkResult = AssertAdvances(session, ref expected,
            () => session.Work.BulkArchiveTasks(bulkPreview));
        Assert.True(bulkResult.Applied);
        Assert.Equal(1, bulkResult.ArchivedCount);
    }

    [Fact]
    public void ReadsNoOpsAndRecoveryMaintenanceDoNotAdvanceRevision()
    {
        using var session = CreateWorkspace();
        var category = Assert.Single(session.Work.Read().Categories);
        var recoveryDirectory = Path.Combine(_directory, "recovery");
        var participant = session.Work.CreateParticipant("Alex");
        var task = session.Work.CreateStandaloneTask(
            "Task",
            "",
            category.Id,
            null,
            new([participant.Id], []));
        var revision = Revision(session);

        _ = session.Work.Read();
        _ = session.Planning.Read();
        session.Work.RenameCategory(category.Id, category.Name);
        session.Work.MoveCategory(category.Id, 0);
        session.Work.UpdateTask(
            task.Id,
            task.Title,
            task.Description,
            task.ExplicitCategoryId,
            task.DueDate,
            new([participant.Id], []));
        session.Work.SetTaskTodayLane(task.Id, null);
        Assert.Equal(0, session.Work.ClearToday());
        var recovery = session.Recovery.CreateRecoveryPoint(recoveryDirectory);

        Assert.Equal(RecoveryPointCreationStatus.Created, recovery.Status);
        Assert.Equal(revision, Revision(session));
    }

    [Fact]
    public void FailedAndOverflowingMutationsRollBackDataAndRevision()
    {
        using var session = CreateWorkspace();
        using (var connection = OpenConnection(SqliteOpenMode.ReadWrite))
        {
            using var trigger = connection.CreateCommand();
            trigger.CommandText = "CREATE TRIGGER reject_category BEFORE INSERT ON categories BEGIN SELECT RAISE(ABORT, 'private'); END;";
            trigger.ExecuteNonQuery();
        }

        Assert.Throws<WorkspaceWorkException>(() => session.Work.CreateCategory("Rejected"));
        Assert.Equal(1, Revision(session));
        Assert.DoesNotContain(session.Work.Read().Categories, category => category.Name == "Rejected");

        using (var connection = OpenConnection(SqliteOpenMode.ReadWrite))
        {
            using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TRIGGER reject_category; UPDATE workspace_revision SET revision=$revision;";
            drop.Parameters.AddWithValue("$revision", long.MaxValue);
            drop.ExecuteNonQuery();
        }

        Assert.Throws<WorkspaceWorkException>(() => session.Work.CreateCategory("Overflow"));
        Assert.Equal(long.MaxValue, Revision(session));
        Assert.DoesNotContain(session.Work.Read().Categories, category => category.Name == "Overflow");
    }

    [Fact]
    public void PlanningReadUsesCanonicalOrdersAndExcludesHiddenWork()
    {
        using var session = CreateWorkspace();
        var home = Assert.Single(session.Work.Read().Categories);
        var work = session.Work.CreateCategory("Work");
        var participantB = session.Work.CreateParticipant("B");
        var participantA = session.Work.CreateParticipant("A");
        var activeProject = session.Work.CreateProject("Active", "", home.Id, null);
        var secondProject = session.Work.CreateProject("Second", "", work.Id, null);
        var activeTask = session.Work.CreateTaskDraft(
            activeProject.Id,
            "Active task",
            "",
            null,
            null,
            new([participantB.Id, participantA.Id], []),
            TodayLane.InProgress);
        var archivedTask = session.Work.CreateTask(activeProject.Id, "Archived task");
        session.Work.CompleteTask(archivedTask.Id);
        session.Work.ArchiveTask(archivedTask.Id);
        var archivedProject = session.Work.CreateProject("Archived", "", home.Id, null);
        var archivedProjectTask = session.Work.CreateTask(archivedProject.Id, "Hidden by parent");
        session.Work.ArchiveProject(archivedProject.Id);
        var binnedProject = session.Work.CreateProject("Binned", "", home.Id, null);
        var binnedProjectTask = session.Work.CreateTask(binnedProject.Id, "Hidden in Bin");
        session.Work.MoveProjectToBin(binnedProject.Id);
        var standalone = session.Work.CreateStandaloneTask("Standalone", "", work.Id, null);
        var binnedTask = session.Work.CreateStandaloneTask("Binned task", "", work.Id, null);
        session.Work.MoveTaskToBin(binnedTask.Id);

        var workSnapshot = session.Work.Read();
        var planning = session.Planning.Read();

        Assert.Equal([home.Id, work.Id], planning.Orders.CategoryIds);
        Assert.Equal([activeProject.Id, secondProject.Id], planning.Orders.ProjectIds);
        Assert.Equal(
            workSnapshot.Tasks
                .Where(task => !task.IsArchived
                    && (task.ProjectId is null
                        || task.ProjectId == activeProject.Id
                        || task.ProjectId == secondProject.Id))
                .OrderBy(task => task.SharedPosition)
                .Select(task => task.Id),
            planning.Orders.SharedTaskIds);
        Assert.Equal(
            new[] { participantA.Id, participantB.Id }.Order(StringComparer.Ordinal),
            planning.Participants.Select(participant => participant.Id));
        Assert.Equal(
            new[] { participantA.Id, participantB.Id }.Order(StringComparer.Ordinal),
            planning.Tasks.Single(task => task.Id == activeTask.Id).ParticipantIds);
        Assert.Equal(PlanningTaskCategoryMode.Inherited,
            planning.Tasks.Single(task => task.Id == activeTask.Id).Category.Mode);
        Assert.Equal(PlanningTaskCategoryMode.Explicit,
            planning.Tasks.Single(task => task.Id == standalone.Id).Category.Mode);
        var project = planning.Projects.Single(item => item.Id == activeProject.Id);
        Assert.Equal(new PlanningProjectProgress(1, 2), project.Progress);
        Assert.Equal(1, project.ExcludedArchivedTaskCount);
        Assert.Equal([activeTask.Id], project.TaskIds);
        Assert.DoesNotContain(planning.Tasks, task => task.Id == archivedTask.Id);
        Assert.DoesNotContain(planning.Tasks, task => task.Id == archivedProjectTask.Id);
        Assert.DoesNotContain(planning.Tasks, task => task.Id == binnedProjectTask.Id);
        Assert.DoesNotContain(planning.Tasks, task => task.Id == binnedTask.Id);
    }

    [Fact]
    public void RestoreRebasesRevisionAboveCurrentAndRecoveredHistory()
    {
        var session = CreateWorkspace();
        var recoveryDirectory = Path.Combine(_directory, "recovery");
        var point = session.Recovery.CreateRecoveryPoint(recoveryDirectory);
        var category = Assert.Single(session.Work.Read().Categories);
        session.Work.RenameCategory(category.Id, "Changed once");
        session.Work.RenameCategory(category.Id, "Changed twice");
        Assert.Equal(3, Revision(session));

        var result = session.Recovery.Restore(
            Assert.IsType<string>(point.RecoveryPointPath),
            recoveryDirectory);
        using var restored = Assert.IsAssignableFrom<IWorkspaceSession>(result.Session);

        Assert.Equal(WorkspaceRestoreStatus.Restored, result.Status);
        Assert.Equal(4, Revision(restored));
        Assert.Equal("Home", restored.FirstCategoryName);
    }

    [Fact]
    public void RestoreRebasesAboveRecoveredHistoryWhenItExceedsCurrentRevision()
    {
        var session = CreateWorkspace();
        var category = Assert.Single(session.Work.Read().Categories);
        for (var index = 0; index < 4; index++)
            session.Work.RenameCategory(category.Id, $"Recovered {index}");
        Assert.Equal(5, Revision(session));
        var recoveryDirectory = Path.Combine(_directory, "higher-recovery");
        var point = session.Recovery.CreateRecoveryPoint(recoveryDirectory);
        using (var connection = OpenConnection(SqliteOpenMode.ReadWrite))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE workspace_revision SET revision=2;";
            command.ExecuteNonQuery();
        }

        var result = session.Recovery.Restore(
            Assert.IsType<string>(point.RecoveryPointPath),
            recoveryDirectory);
        using var restored = Assert.IsAssignableFrom<IWorkspaceSession>(result.Session);

        Assert.Equal(WorkspaceRestoreStatus.Restored, result.Status);
        Assert.Equal(6, Revision(restored));
        Assert.Equal("Recovered 3", restored.FirstCategoryName);
    }

    [Fact]
    public async Task PlanningReadKeepsContentAndRevisionFromOneCommittedState()
    {
        using var session = CreateWorkspace();
        const int createdCategoryCount = 64;
        var cancellationToken = TestContext.Current.CancellationToken;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = Task.Run(async () =>
        {
            await start.Task.WaitAsync(cancellationToken);
            for (var index = 0; index < createdCategoryCount; index++)
                session.Work.CreateCategory($"Category {index:D2}");
        }, cancellationToken);
        var reader = Task.Run(async () =>
        {
            await start.Task.WaitAsync(cancellationToken);
            var observed = new List<PlanningSnapshotContent>();
            do
            {
                observed.Add(session.Planning.Read());
                await Task.Yield();
            }
            while (!writer.IsCompleted);
            observed.Add(session.Planning.Read());
            return observed;
        }, cancellationToken);

        start.SetResult();
        var snapshots = await reader;
        await writer;

        Assert.All(
            snapshots,
            snapshot => Assert.Equal(snapshot.Categories.Count, snapshot.WorkspaceRevision.Value));
        Assert.Equal(createdCategoryCount + 1, snapshots[^1].Categories.Count);
        Assert.Equal(createdCategoryCount + 1, snapshots[^1].WorkspaceRevision.Value);
    }

    [Fact]
    public void PassphraseRotationDoesNotAdvanceRevision()
    {
        var session = CreateWorkspace();
        session.Work.CreateCategory("Work");
        var before = Revision(session);
        var replacement = WorkspacePassphrase.Create(
            "different private password",
            "different private password").Passphrase!;

        var result = session.RotatePassphrase(_passphrase, replacement);
        using var rotated = Assert.IsAssignableFrom<IWorkspaceSession>(result.Session);

        Assert.Equal(PassphraseRotationStatus.Rotated, result.Status);
        Assert.Equal(before, Revision(rotated));
    }

    [Fact]
    public void RestoreFailsClosedBeforeRevisionOverflow()
    {
        using var session = CreateWorkspace();
        var recoveryDirectory = Path.Combine(_directory, "recovery");
        var point = session.Recovery.CreateRecoveryPoint(recoveryDirectory);
        using (var connection = OpenConnection(SqliteOpenMode.ReadWrite))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE workspace_revision SET revision=$revision;";
            command.Parameters.AddWithValue("$revision", long.MaxValue);
            command.ExecuteNonQuery();
        }

        var result = session.Recovery.Restore(
            Assert.IsType<string>(point.RecoveryPointPath),
            recoveryDirectory);

        Assert.Equal(WorkspaceRestoreStatus.Failed, result.Status);
        Assert.Null(result.Session);
        Assert.Equal(long.MaxValue, Revision(session));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private IWorkspaceSession CreateWorkspace() => Assert.IsAssignableFrom<IWorkspaceSession>(
        _store.Create(
            WorkspacePath,
            _passphrase,
            CategoryName.Create("Home").CategoryName!).Session);

    private static long Revision(IWorkspaceSession session) =>
        session.Planning.Read().WorkspaceRevision.Value;

    private static T AssertAdvances<T>(
        IWorkspaceSession session,
        ref long expected,
        Func<T> mutation)
    {
        var result = mutation();
        expected++;
        Assert.Equal(expected, Revision(session));
        return result;
    }

    private static void AssertAdvances(
        IWorkspaceSession session,
        ref long expected,
        Action mutation)
    {
        mutation();
        expected++;
        Assert.Equal(expected, Revision(session));
    }

    private SqliteConnection OpenConnection(SqliteOpenMode mode)
    {
        var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, mode);
        EncryptedWorkspaceStore.ConfigureConnection(connection);
        return connection;
    }
}
