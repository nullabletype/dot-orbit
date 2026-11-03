using DotOrbit.Core.Workspaces;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DotOrbit.Storage.Sqlite.Tests;

public sealed class WorkspaceSearchTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"orbit-search-{Guid.NewGuid():N}");
    private readonly WorkspacePassphrase _passphrase = WorkspacePassphrase.Create("search fixture", "search fixture").Passphrase!;
    private readonly EncryptedWorkspaceStore _store = new();
    private string WorkspacePath => Path.Combine(_directory, "workspace.orb");

    [Fact]
    public void MatchesEveryLifecycleOnceInCanonicalOrderWithoutChangingWorkspaceRevision()
    {
        using var session = Create();
        var work = session.Work;
        var category = Assert.Single(work.Read().Categories);
        var project = work.CreateProject("Shared heading", "", category.Id, null);
        var older = work.CreateTask(project.Id, "Shared older");
        var latest = work.CreateTaskDraft(project.Id, "Shared latest", "", null, null, todayLane: TodayLane.Planned);
        var completed = work.CreateStandaloneTask("Shared completed", "", category.Id, null);
        var archived = work.CreateStandaloneTask("Shared archived", "", category.Id, null);
        var binned = work.CreateStandaloneTask("Shared binned", "", category.Id, null);
        work.CompleteTask(completed.Id);
        work.CompleteTask(archived.Id);
        work.ArchiveTask(archived.Id);
        work.MoveTaskToBin(binned.Id);
        var revision = session.Planning.Read().WorkspaceRevision;

        var hits = work.SearchWorkspace("sHAr");
        Assert.Equal(new[] { project.Id, latest.Id, older.Id, completed.Id, archived.Id, binned.Id },
            hits.Select(hit => hit.Id));
        Assert.Equal(new[] { WorkspaceSearchGroup.Projects, WorkspaceSearchGroup.Backlog, WorkspaceSearchGroup.Backlog,
            WorkspaceSearchGroup.Completed, WorkspaceSearchGroup.Archive, WorkspaceSearchGroup.Bin }, hits.Select(hit => hit.Group));
        Assert.Equal(ArchiveSearchRecordType.Project, hits[0].RecordType);
        Assert.All(hits.Skip(1), hit => Assert.Equal(ArchiveSearchRecordType.Task, hit.RecordType));
        Assert.Equal(archived.Id, Assert.Single(work.SearchArchive("shared")).Id);
        Assert.Equal(revision, session.Planning.Read().WorkspaceRevision);
        Assert.Equal(TodayLane.Planned, work.Read().Tasks.Single(task => task.Id == latest.Id).TodayLane);
    }

    [Fact]
    public void CombinesMatchingFieldsAndTracksRenamesOverridesAndReassignment()
    {
        using var session = Create();
        var work = session.Work;
        var initial = Assert.Single(work.Read().Categories);
        var overriding = work.CreateCategory("Seasonal");
        var participant = work.CreateParticipant("Müller");
        var project = work.CreateProject("Greenhouse", "**Ventilation**", initial.Id, null);
        var task = work.CreateTaskDraft(project.Id, "Tulips", "[Weather](https://private.invalid)",
            null, null, new([participant.Id], []));
        Assert.Equal(task.Id, Assert.Single(work.SearchWorkspace("TUL weather home mull")).Id);
        Assert.Equal(project.Id, Assert.Single(work.SearchWorkspace("green vent home")).Id);
        foreach (var query in new[] { "", "  ", "*!", "priva", "ulip", "tulipx", "TUL OR unrelated", "green tul" })
            Assert.Empty(work.SearchWorkspace(query));

        work.UpdateTask(task.Id, "Revised", "Flower", overriding.Id, null);
        Assert.Empty(work.SearchWorkspace("TUL"));
        Assert.Empty(work.SearchWorkspace("revised home"));
        Assert.Equal(task.Id, Assert.Single(work.SearchWorkspace("flower season")).Id);
        work.RenameParticipant(participant.Id, "Designer");
        work.RenameCategory(overriding.Id, "Botany");
        Assert.Empty(work.SearchWorkspace("mull"));
        Assert.Empty(work.SearchWorkspace("season"));
        Assert.Equal(task.Id, Assert.Single(work.SearchWorkspace("bot desig")).Id);
        work.DetachTask(task.Id);
        work.AttachTask(task.Id, project.Id, TaskAttachmentCategoryChoice.AdoptProjectCategory);
        Assert.Equal(task.Id, Assert.Single(work.SearchWorkspace("home revis desig")).Id);
        work.UpdateProject(project.Id, "Glasshouse", "Warmth", overriding.Id, null);
        Assert.Equal(task.Id, Assert.Single(work.SearchWorkspace("bot revis")).Id);
        Assert.Equal(project.Id, Assert.Single(work.SearchWorkspace("glass warmth bot")).Id);
        Assert.Empty(work.SearchWorkspace("green vent"));
    }

    [Fact]
    public void FollowsLifecycleIncludingArchivedProjectChildrenAndAggregateBinRestore()
    {
        using var session = Create();
        var work = session.Work;
        var category = Assert.Single(work.Read().Categories);
        var project = work.CreateProject("Parent", "", category.Id, null);
        var task = work.CreateTask(project.Id, "Child");
        WorkspaceSearchResult Hit() => Assert.Single(work.SearchWorkspace("child"));
        work.CompleteTask(task.Id);
        Assert.Equal(WorkspaceSearchGroup.Completed, Hit().Group);
        work.ArchiveTask(task.Id);
        Assert.Equal(WorkspaceSearchGroup.Archive, Hit().Group);
        work.RestoreTask(task.Id);
        Assert.Equal(WorkspaceSearchGroup.Completed, Hit().Group);
        work.ReopenTask(task.Id);
        Assert.Equal(WorkspaceSearchGroup.Backlog, Hit().Group);
        work.ArchiveProject(project.Id);
        Assert.Equal(WorkspaceSearchGroup.Archive, Hit().Group);
        Assert.Empty(work.SearchArchive("child"));
        work.RestoreProject(project.Id);
        Assert.Equal(WorkspaceSearchGroup.Backlog, Hit().Group);
        work.MoveProjectToBin(project.Id);
        var bin = Hit();
        Assert.Equal(WorkspaceSearchGroup.Bin, bin.Group);
        Assert.Equal(WorkspaceSearchGroup.Bin, Assert.Single(work.SearchWorkspace("parent")).Group);
        Assert.Equal(task.Id, bin.AggregateBinTask?.Task.Id);
        Assert.False(bin.AggregateBinTask!.CanRestore);
        Assert.NotNull(bin.AggregateBinTask.RestoreBlockedReason);
        Assert.Equal(project.Title, bin.AggregateBinTask.ProjectTitle);
        Assert.Equal(category.Name, bin.AggregateBinTask.CategoryName);
        Assert.Empty(work.ReadTaskBin());
        work.RestoreProjectFromBin(project.Id);
        Assert.Equal(WorkspaceSearchGroup.Backlog, Hit().Group);
        work.MoveTaskToBin(task.Id);
        Assert.Equal(WorkspaceSearchGroup.Bin, Hit().Group);
        Assert.Null(Hit().AggregateBinTask);
        work.RestoreTaskFromBin(task.Id);
        Assert.Equal(WorkspaceSearchGroup.Backlog, Hit().Group);
        work.MoveProjectToBin(project.Id);
        Assert.Equal(EmptyBinStatus.Emptied, work.EmptyBin(work.PreviewEmptyBin()).Status);
        Assert.Empty(work.SearchWorkspace("child"));
        Assert.Empty(work.SearchWorkspace("parent"));
    }

    [Fact]
    public void DuplicationAndRecurringMaterialisationIndexOnlyProducedWork()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var store = new EncryptedWorkspaceStore(new SystemIdentifierGenerator(), new WorkspaceFileOperations(), clock);
        using var session = store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;
        var work = session.Work;
        var category = Assert.Single(work.Read().Categories);
        var parent = work.CreateProject("Parent", "", category.Id, null);
        var original = work.CreateTask(parent.Id, "Source");
        var duplicate = work.DuplicateTask(original.Id);
        var copied = work.DuplicateProject(work.PreviewProjectDuplication(parent.Id), "Copied parent");
        var expected = new[] { original.Id, duplicate.Id }.Concat(copied.Tasks.Select(task => task.Id));
        Assert.Equal(expected.Order(StringComparer.Ordinal),
            work.SearchWorkspace("source").Select(hit => hit.Id).Order(StringComparer.Ordinal));
        Assert.Equal(copied.Project.Id, Assert.Single(work.SearchWorkspace("copied parent")).Id);

        var recurring = Assert.IsAssignableFrom<IRecurringTasks>(work);
        recurring.CreateRecurringTask(new("Scheduled", "Occurrence", null, category.Id, [],
            RecurrenceCadence.Daily, new DateOnly(2026, 1, 1), null, true));
        Assert.Empty(work.SearchWorkspace("scheduled"));
        var occurrence = Assert.Single(recurring.ProcessDueRecurringTasks());
        Assert.Equal(occurrence.Id, Assert.Single(work.SearchWorkspace("sched occurr")).Id);
    }

    [Fact]
    public void RecoveryOnReusedConnectionAcceptsNewIndexSegmentsAndRejectsCorruption()
    {
        using var session = Create();
        var work = session.Work;
        var category = Assert.Single(work.Read().Categories);
        var directory = Path.Combine(_directory, "recovery");
        Assert.Equal(RecoveryPointCreationStatus.Created, session.Recovery.CreateRecoveryPoint(directory).Status);
        work.CreateStandaloneTask("Indexed", "", category.Id, null);
        Assert.Equal(RecoveryPointCreationStatus.Created, session.Recovery.CreateRecoveryPoint(directory).Status);

        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite))
        {
            using var corrupt = connection.CreateCommand();
            corrupt.CommandText = "UPDATE workspace_search_content SET c2='Inconsistent';";
            corrupt.ExecuteNonQuery();
        }
        Assert.Equal(RecoveryPointCreationStatus.Failed, session.Recovery.CreateRecoveryPoint(directory).Status);
    }

    [Fact]
    public void MigrationIndexesExistingRecordsAndRecoveryRestoresTheMatchingRevision()
    {
        string taskId;
        using (var session = Create())
        {
            var work = session.Work;
            var category = Assert.Single(work.Read().Categories);
            taskId = work.CreateStandaloneTask("Original", "**Visible**", category.Id, null).Id;
            work.MoveTaskToBin(taskId);
        }
        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite))
        {
            using var downgrade = connection.CreateCommand();
            downgrade.CommandText = "DROP TABLE workspace_search; PRAGMA user_version=19;";
            downgrade.ExecuteNonQuery();
        }
        using (var migrated = _store.Open(WorkspacePath, _passphrase).Session!)
        {
            var match = Assert.Single(migrated.Work.SearchWorkspace("orig visible"));
            Assert.Equal((WorkspaceSearchGroup.Bin, taskId), (match.Group, match.Id));
        }
        using var reopened = _store.Open(WorkspacePath, _passphrase).Session!;
        var recoveryDirectory = Path.Combine(_directory, "recovery");
        var selected = reopened.Recovery.CreateRecoveryPoint(recoveryDirectory);
        Assert.Equal(RecoveryPointCreationStatus.Created, selected.Status);
        reopened.Work.RestoreTaskFromBin(taskId);
        var categoryId = Assert.Single(reopened.Work.Read().Categories).Id;
        reopened.Work.UpdateTask(taskId, "Replacement", "", categoryId, null);
        Assert.Empty(reopened.Work.SearchWorkspace("original"));
        Assert.Equal(taskId, Assert.Single(reopened.Work.SearchWorkspace("replacement")).Id);
        var restored = reopened.Recovery.Restore(selected.RecoveryPointPath!, recoveryDirectory);
        using var restoredSession = restored.Session;
        Assert.Equal(WorkspaceRestoreStatus.Restored, restored.Status);
        Assert.Empty(restoredSession!.Work.SearchWorkspace("replacement"));
        var restoredHit = Assert.Single(restoredSession.Work.SearchWorkspace("orig visible"));
        Assert.Equal((WorkspaceSearchGroup.Bin, taskId), (restoredHit.Group, restoredHit.Id));
    }

    [Fact]
    public void FailedTransactionRollsBackTheAlreadyUpdatedWorkspaceIndexAndTask()
    {
        using var session = Create();
        var work = session.Work;
        var category = Assert.Single(work.Read().Categories);
        var task = work.CreateStandaloneTask("Original", "", category.Id, null);
        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE archive_search;";
            command.ExecuteNonQuery();
        }
        var revision = session.Planning.Read().WorkspaceRevision;
        Assert.Throws<WorkspaceWorkException>(() => work.UpdateTask(task.Id, "Replacement", "", category.Id, null));
        Assert.Equal(task.Id, Assert.Single(work.SearchWorkspace("original")).Id);
        Assert.Empty(work.SearchWorkspace("replacement"));
        Assert.Equal(task.Title, Assert.Single(work.Read().Tasks).Title);
        Assert.Equal(revision, session.Planning.Read().WorkspaceRevision);
    }

    private IWorkspaceSession Create() =>
        _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
