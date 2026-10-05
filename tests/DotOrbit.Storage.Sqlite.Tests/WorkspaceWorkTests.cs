using System.Text;
using DotOrbit.Core.Workspaces;
using DotOrbit.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DotOrbit.Storage.Sqlite.Tests;

public sealed class WorkspaceWorkTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "orbit-work-" + Guid.NewGuid().ToString("N"));
    private readonly EncryptedWorkspaceStore _store = new();
    private readonly WorkspacePassphrase _passphrase = WorkspacePassphrase.Create("correct horse battery", "correct horse battery").Passphrase!;
    private string WorkspacePath => Path.Combine(_directory, "workspace.db");

    [Fact]
    public void CreateTaskDraftPersistsOptionalProjectContentParticipantsAndTodayLaneAtomically()
    {
        var created = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!);
        using var session = created.Session!;
        var category = Assert.Single(session.Work.Read().Categories);
        var project = session.Work.CreateProject("Garden", "", category.Id, null);
        var participant = session.Work.CreateParticipant("SD");

        var task = session.Work.CreateTaskDraft(project.Id, " Plant bulbs ", "Near the fence", null,
            new DateOnly(2026, 10, 8), new([participant.Id], []), TodayLane.Planned);

        var persisted = session.Work.Read().Tasks.Single(item => item.Id == task.Id);
        Assert.Equal("Plant bulbs", persisted.Title);
        Assert.Equal(project.Id, persisted.ProjectId);
        Assert.Null(persisted.ExplicitCategoryId);
        Assert.Equal("Near the fence", persisted.Description);
        Assert.Equal(new DateOnly(2026, 10, 8), persisted.DueDate);
        Assert.Equal([participant.Id], persisted.Participants);
        Assert.Equal(TodayLane.Planned, persisted.TodayLane);
    }

    [Fact]
    public void CreateTaskDraftRollsBackEveryWriteWhenTodayPlacementFails()
    {
        var created = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!);
        using var session = created.Session!;
        var category = Assert.Single(session.Work.Read().Categories);
        var project = session.Work.CreateProject("Garden", "", category.Id, null);
        session.Work.CreateTask(project.Id, "Existing");
        var before = session.Work.Read();
        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_today BEFORE INSERT ON today_tasks BEGIN SELECT RAISE(ABORT, 'private'); END;";
            command.ExecuteNonQuery();
        }

        Assert.Throws<WorkspaceWorkException>(() => session.Work.CreateTaskDraft(project.Id, "Private title",
            "Private description", null, null, new([], ["SD"]), TodayLane.Planned));

        var unchanged = session.Work.Read();
        Assert.Equal(before.Tasks.Select(task => (task.Id, task.SharedPosition, task.ProjectPosition, task.TodayLane)),
            unchanged.Tasks.Select(task => (task.Id, task.SharedPosition, task.ProjectPosition, task.TodayLane)));
        Assert.Equal(before.Participants, unchanged.Participants);
        Assert.All(unchanged.Tasks, task => Assert.Empty(task.Participants));
    }

    [Fact]
    public void CreateAndEditRoundTripsMarkdownDatesInheritanceAndIndependentOrders()
    {
        var created = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!);
        using (var session = created.Session!)
        {
            Assert.Empty(session.Work.Read().Projects);
            Assert.Empty(session.Work.Read().Tasks);
            var category = Assert.Single(session.Work.Read().Categories);
            var first = session.Work.CreateProject("  First  ", "# Project\ntext", category.Id, new DateOnly(2028, 2, 29));
            var second = session.Work.CreateProject("Second", "", category.Id, null);
            var a = session.Work.CreateTask(first.Id, " A ");
            var b = session.Work.CreateTask(second.Id, "B");
            var c = session.Work.CreateTask(first.Id, "C");
            Assert.Null(a.ExplicitCategoryId);
            Assert.Null(a.DueDate);
            Assert.Equal(string.Empty, a.Description);
            Assert.Equal(new[] { first.Id, second.Id }, session.Work.Read().Projects.Select(p => p.Id));
            Assert.Equal(new[] { c.Id, b.Id, a.Id }, session.Work.Read().Tasks.Select(t => t.Id));
            Assert.Equal(new[] { a.Id, c.Id }, session.Work.Read().Tasks.Where(t => t.ProjectId == first.Id).OrderBy(t => t.ProjectPosition).Select(t => t.Id));
            var updated = session.Work.UpdateProject(first.Id, " Updated ", "**saved**", category.Id, new DateOnly(2030, 1, 2));
            Assert.Equal(first.Position, updated.Position);
            var beforeTaskUpdate = session.Work.Read().Tasks.Single(existing => existing.Id == a.Id);
            var task = session.Work.UpdateTask(a.Id, " Edited ", "- markdown\n- list", category.Id, new DateOnly(2029, 12, 31));
            Assert.Equal(beforeTaskUpdate.SharedPosition, task.SharedPosition);
            Assert.Equal(beforeTaskUpdate.ProjectPosition, task.ProjectPosition);
        }

        using var reopened = _store.Open(WorkspacePath, _passphrase).Session!;
        var snapshot = reopened.Work.Read();
        Assert.Collection(snapshot.Projects,
            project => Assert.Equal("Updated", project.Title),
            project => Assert.Equal("Second", project.Title));
        Assert.Collection(snapshot.Tasks,
            task => Assert.Equal("C", task.Title),
            task => Assert.Equal("B", task.Title),
            task => Assert.Equal("Edited", task.Title));
        Assert.Collection(snapshot.Tasks
            .Where(t => t.ProjectId == snapshot.Projects[0].Id)
            .OrderBy(t => t.ProjectPosition)
            .Select(t => t.Title),
            title => Assert.Equal("Edited", title),
            title => Assert.Equal("C", title));
        Assert.Equal("Updated", snapshot.Projects[0].Title);
        Assert.Equal("**saved**", snapshot.Projects[0].Description);
        Assert.Equal(new DateOnly(2030, 1, 2), snapshot.Projects[0].TargetDate);
        var edited = snapshot.Tasks.Single(t => t.Title == "Edited");
        Assert.Equal("- markdown\n- list", edited.Description);
        Assert.Equal(new DateOnly(2029, 12, 31), edited.DueDate);
        Assert.Equal(snapshot.Categories[0].Id, edited.ExplicitCategoryId);
        reopened.Work.UpdateTask(edited.Id, edited.Title, edited.Description, null, null);
        Assert.Null(reopened.Work.Read().Tasks.Single(t => t.Id == edited.Id).ExplicitCategoryId);
        Assert.DoesNotContain("markdown", Encoding.UTF8.GetString(File.ReadAllBytes(WorkspacePath)), StringComparison.Ordinal);
    }

    [Fact]
    public void StandaloneCreationAndAtomicSharedReorderPersistAcrossRestart()
    {
        using (var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!)
        {
            var category = Assert.Single(session.Work.Read().Categories);
            var project = session.Work.CreateProject("Garden", "", category.Id, null);
            var first = session.Work.CreateTask(project.Id, "Attached first");
            var standalone = session.Work.CreateStandaloneTask("Standalone", "**note**", category.Id, new DateOnly(2027, 6, 1));
            var newest = session.Work.CreateTask(project.Id, "Attached newest");
            Assert.Equal([newest.Id, standalone.Id, first.Id], session.Work.Read().Tasks.Select(task => task.Id));
            Assert.Null(standalone.ProjectId);
            Assert.Null(standalone.ProjectPosition);
            Assert.Equal(category.Id, standalone.ExplicitCategoryId);

            var moved = session.Work.MoveTaskInSharedOrder(first.Id, 0);
            Assert.Equal(new SharedTaskOrderChange(first.Id, 1, 3), moved);
            Assert.Equal([first.Id, newest.Id, standalone.Id], session.Work.Read().Tasks.Select(task => task.Id));

            using var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite);
            using var trigger = connection.CreateCommand();
            trigger.CommandText = "CREATE TRIGGER reject_reorder BEFORE UPDATE OF shared_position ON tasks BEGIN SELECT RAISE(ABORT, 'private order'); END;";
            trigger.ExecuteNonQuery();
            Assert.Throws<WorkspaceWorkException>(() => session.Work.MoveTaskInSharedOrder(standalone.Id, 0));
            Assert.Equal([first.Id, newest.Id, standalone.Id], session.Work.Read().Tasks.Select(task => task.Id));
        }

        using var reopened = _store.Open(WorkspacePath, _passphrase).Session!;
        var tasks = reopened.Work.Read().Tasks;
        Assert.Equal(["Attached first", "Attached newest", "Standalone"], tasks.Select(task => task.Title));
        var restoredStandalone = tasks.Single(task => task.Title == "Standalone");
        Assert.Null(restoredStandalone.ProjectId);
        Assert.Equal("**note**", restoredStandalone.Description);
        Assert.Equal(new DateOnly(2027, 6, 1), restoredStandalone.DueDate);
    }

    [Fact]
    public void SharedReorderMovesOnlyIncompleteTasksAndPreservesCompletedSlots()
    {
        using var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;
        var category = Assert.Single(session.Work.Read().Categories);
        var project = session.Work.CreateProject("Garden", "", category.Id, null);
        var a = session.Work.CreateTask(project.Id, "A");
        var b = session.Work.CreateTask(project.Id, "B");
        var c = session.Work.CreateTask(project.Id, "C");
        var d = session.Work.CreateTask(project.Id, "D");
        var e = session.Work.CreateTask(project.Id, "E");
        var f = session.Work.CreateTask(project.Id, "F");
        session.Work.CompleteTask(e.Id);
        session.Work.CompleteTask(c.Id);

        Assert.Equal(new SharedTaskOrderChange(f.Id, 2, 4), session.Work.MoveTaskInSharedOrder(f.Id, 1));
        Assert.Equal([d.Id, e.Id, f.Id, c.Id, b.Id, a.Id], session.Work.Read().Tasks.Select(task => task.Id));
        Assert.Equal(new SharedTaskOrderChange(d.Id, 4, 4), session.Work.MoveTaskInSharedOrder(d.Id, 3));
        Assert.Equal([f.Id, e.Id, b.Id, c.Id, a.Id, d.Id], session.Work.Read().Tasks.Select(task => task.Id));
        Assert.Equal(new SharedTaskOrderChange(d.Id, 1, 4), session.Work.MoveTaskInSharedOrder(d.Id, 0));
        Assert.Equal([d.Id, e.Id, f.Id, c.Id, b.Id, a.Id], session.Work.Read().Tasks.Select(task => task.Id));
        Assert.Equal([e.Id, c.Id], session.Work.Read().Tasks.Where(task => task.IsComplete).Select(task => task.Id));
        Assert.Throws<ArgumentException>(() => session.Work.MoveTaskInSharedOrder(e.Id, 0));
    }

    [Fact]
    public void CompleteReopenAndRecompleteAtomicallyReplaceCapturedValuesAcrossTimeZones()
    {
        var east = TimeZoneInfo.CreateCustomTimeZone("UTC+14", TimeSpan.FromHours(14), "UTC+14", "UTC+14");
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 11, 30, 0, TimeSpan.Zero), east);
        var store = new EncryptedWorkspaceStore(new SystemIdentifierGenerator(), new WorkspaceFileOperations(), time);
        using (var session = store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!)
        {
            var project = session.Work.CreateProject("Garden", "", session.Work.Read().Categories[0].Id, null);
            var task = session.Work.CreateTask(project.Id, "Dig");
            var completed = session.Work.CompleteTask(task.Id);
            Assert.Equal(time.GetUtcNow(), completed.CompletedAt);
            Assert.Equal(new DateOnly(2026, 9, 30), completed.CompletionDate);

            var reopened = session.Work.ReopenTask(task.Id);
            Assert.Null(reopened.CompletedAt);
            Assert.Null(reopened.CompletionDate);

            time.SetUtcNow(new DateTimeOffset(2026, 10, 1, 1, 15, 0, TimeSpan.Zero));
            var recompleted = session.Work.CompleteTask(task.Id);
            Assert.Equal(time.GetUtcNow(), recompleted.CompletedAt);
            Assert.Equal(new DateOnly(2026, 10, 1), recompleted.CompletionDate);
        }

        var west = TimeZoneInfo.CreateCustomTimeZone("UTC-12", TimeSpan.FromHours(-12), "UTC-12", "UTC-12");
        var reopenedStore = new EncryptedWorkspaceStore(new SystemIdentifierGenerator(), new WorkspaceFileOperations(), new ManualTimeProvider(time.GetUtcNow(), west));
        using var reopenedSession = reopenedStore.Open(WorkspacePath, _passphrase).Session!;
        var persisted = Assert.Single(reopenedSession.Work.Read().Tasks);
        Assert.Equal(new DateOnly(2026, 10, 1), persisted.CompletionDate);
        Assert.Equal(time.GetUtcNow(), persisted.CompletedAt);
    }

    [Fact]
    public void ArchiveRequiresACompleteCurrentTaskAndRestoreRequiresAnArchivedTask()
    {
        using var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;
        var task = session.Work.CreateStandaloneTask("File receipt", "", session.Work.Read().Categories[0].Id, null);

        Assert.Throws<InvalidOperationException>(() => session.Work.ArchiveTask(task.Id));
        Assert.Throws<InvalidOperationException>(() => session.Work.RestoreTask(task.Id));
        Assert.Throws<ArgumentException>(() => session.Work.ArchiveTask("missing"));
        Assert.Throws<ArgumentException>(() => session.Work.RestoreTask("missing"));

        session.Work.CompleteTask(task.Id);
        var archived = session.Work.ArchiveTask(task.Id);
        Assert.True(archived.IsArchived);
        Assert.NotNull(archived.ArchivedAt);
        Assert.NotNull(archived.ArchiveDate);
        Assert.Throws<InvalidOperationException>(() => session.Work.ArchiveTask(task.Id));
        Assert.Throws<InvalidOperationException>(() => session.Work.CompleteTask(task.Id));
        Assert.Throws<InvalidOperationException>(() => session.Work.ReopenTask(task.Id));

        var restored = session.Work.RestoreTask(task.Id);
        Assert.False(restored.IsArchived);
        Assert.Null(restored.ArchivedAt);
        Assert.Null(restored.ArchiveDate);
        Assert.True(restored.IsComplete);
        Assert.Null(restored.TodayLane);
        Assert.Throws<InvalidOperationException>(() => session.Work.RestoreTask(task.Id));
    }

    [Fact]
    public void ArchiveAndRestorePreserveTaskAndProjectStateAcrossRestarts()
    {
        var archiveZone = TimeZoneInfo.CreateCustomTimeZone("UTC+14", TimeSpan.FromHours(14), "UTC+14", "UTC+14");
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero), archiveZone);
        var store = new EncryptedWorkspaceStore(new SystemIdentifierGenerator(), new WorkspaceFileOperations(), time);
        string taskId;
        TaskRecord beforeArchive;
        using (var session = store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!)
        {
            var category = Assert.Single(session.Work.Read().Categories);
            var project = session.Work.CreateProject("Garden", "", category.Id, null);
            var participant = session.Work.CreateParticipant("SD");
            var task = session.Work.CreateTask(project.Id, "File receipt");
            session.Work.UpdateTask(task.Id, task.Title, "**kept**", category.Id, new DateOnly(2026, 10, 7),
                new([participant.Id], []));
            session.Work.CreateTask(project.Id, "Other work");
            session.Work.CompleteTask(task.Id);
            time.SetUtcNow(new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero));
            session.Work.CompleteTask(session.Work.Read().Tasks.Single(item => item.Title == "Other work").Id);
            beforeArchive = session.Work.Read().Tasks.Single(item => item.Id == task.Id);
            taskId = task.Id;

            time.SetUtcNow(new DateTimeOffset(2026, 10, 3, 10, 30, 0, TimeSpan.Zero));
            var archived = session.Work.ArchiveTask(task.Id);

            Assert.Equal(time.GetUtcNow(), archived.ArchivedAt);
            Assert.Equal(new DateOnly(2026, 10, 4), archived.ArchiveDate);
            Assert.Null(archived.TodayLane);
            var summary = ProjectWorkSummary.From(session.Work.Read(), project.Id);
            Assert.True(summary.IsComplete);
            Assert.Equal(2, summary.CompletedCount);
            Assert.Equal(new DateOnly(2026, 10, 2), summary.CompletionDate);
        }

        var laterZone = TimeZoneInfo.CreateCustomTimeZone("UTC-12-archive", TimeSpan.FromHours(-12), "UTC-12", "UTC-12");
        var laterStore = new EncryptedWorkspaceStore(new SystemIdentifierGenerator(), new WorkspaceFileOperations(),
            new ManualTimeProvider(time.GetUtcNow(), laterZone));
        using (var reopened = laterStore.Open(WorkspacePath, _passphrase).Session!)
        {
            var archived = reopened.Work.Read().Tasks.Single(item => item.Id == taskId);
            Assert.True(archived.IsArchived);
            Assert.Equal(new DateTimeOffset(2026, 10, 3, 10, 30, 0, TimeSpan.Zero), archived.ArchivedAt);
            Assert.Equal(new DateOnly(2026, 10, 4), archived.ArchiveDate);
            Assert.Equal(beforeArchive.ProjectId, archived.ProjectId);
            Assert.Equal(beforeArchive.ExplicitCategoryId, archived.ExplicitCategoryId);
            Assert.Equal(beforeArchive.Description, archived.Description);
            Assert.Equal(beforeArchive.DueDate, archived.DueDate);
            Assert.Equal(beforeArchive.SharedPosition, archived.SharedPosition);
            Assert.Equal(beforeArchive.ProjectPosition, archived.ProjectPosition);
            Assert.Equal(beforeArchive.CompletedAt, archived.CompletedAt);
            Assert.Equal(beforeArchive.CompletionDate, archived.CompletionDate);
            Assert.Equal(beforeArchive.Participants, archived.Participants);
            Assert.Null(archived.TodayLane);

            var restored = reopened.Work.RestoreTask(taskId);
            Assert.False(restored.IsArchived);
            Assert.Null(restored.ArchivedAt);
            Assert.Null(restored.ArchiveDate);
            Assert.Equal(beforeArchive.CompletedAt, restored.CompletedAt);
            Assert.Equal(beforeArchive.CompletionDate, restored.CompletionDate);
            Assert.Null(restored.TodayLane);
        }

        using var restoredRestart = store.Open(WorkspacePath, _passphrase).Session!;
        var persisted = restoredRestart.Work.Read().Tasks.Single(item => item.Id == taskId);
        Assert.False(persisted.IsArchived);
        Assert.Null(persisted.ArchivedAt);
        Assert.Null(persisted.ArchiveDate);
        Assert.True(persisted.IsComplete);
        Assert.Null(persisted.TodayLane);
    }

    [Fact]
    public void ProjectArchivePreservesTaskStatesClearsTodayAndRestoresAcrossRestart()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 4, 10, 0, 0, TimeSpan.Zero));
        var store = new EncryptedWorkspaceStore(new SystemIdentifierGenerator(), new WorkspaceFileOperations(), time);
        string projectId;
        string incompleteId;
        string completedId;
        string individuallyArchivedId;
        using (var session = store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!)
        {
            var category = Assert.Single(session.Work.Read().Categories);
            var project = session.Work.CreateProject("Garden", "kept", category.Id, new DateOnly(2026, 10, 20));
            var incomplete = session.Work.CreateTask(project.Id, "Incomplete");
            var completed = session.Work.CreateTask(project.Id, "Completed");
            var individuallyArchived = session.Work.CreateTask(project.Id, "Archived first");
            session.Work.SetTaskTodayLane(incomplete.Id, TodayLane.InProgress);
            session.Work.CompleteTask(completed.Id);
            session.Work.CompleteTask(individuallyArchived.Id);
            session.Work.ArchiveTask(individuallyArchived.Id);
            projectId = project.Id;
            incompleteId = incomplete.Id;
            completedId = completed.Id;
            individuallyArchivedId = individuallyArchived.Id;

            time.SetUtcNow(new DateTimeOffset(2026, 10, 4, 23, 30, 0, TimeSpan.Zero));
            var archived = session.Work.ArchiveProject(project.Id);

            Assert.True(archived.IsArchived);
            Assert.Equal(time.GetUtcNow(), archived.ArchivedAt);
            Assert.Equal(new DateOnly(2026, 10, 4), archived.ArchiveDate);
            Assert.Equal("kept", archived.Description);
            var snapshot = session.Work.Read();
            Assert.Null(snapshot.Tasks.Single(task => task.Id == incomplete.Id).TodayLane);
            Assert.False(snapshot.Tasks.Single(task => task.Id == incomplete.Id).IsComplete);
            Assert.True(snapshot.Tasks.Single(task => task.Id == completed.Id).IsComplete);
            Assert.False(snapshot.Tasks.Single(task => task.Id == completed.Id).IsArchived);
            Assert.True(snapshot.Tasks.Single(task => task.Id == individuallyArchived.Id).IsArchived);
            Assert.Throws<InvalidOperationException>(() => session.Work.ArchiveProject(project.Id));
        }

        using (var reopened = store.Open(WorkspacePath, _passphrase).Session!)
        {
            Assert.True(reopened.Work.Read().Projects.Single(project => project.Id == projectId).IsArchived);
            var restored = reopened.Work.RestoreProject(projectId);
            Assert.False(restored.IsArchived);
            var snapshot = reopened.Work.Read();
            Assert.Null(snapshot.Tasks.Single(task => task.Id == incompleteId).TodayLane);
            Assert.False(snapshot.Tasks.Single(task => task.Id == incompleteId).IsArchived);
            Assert.True(snapshot.Tasks.Single(task => task.Id == completedId).IsComplete);
            Assert.True(snapshot.Tasks.Single(task => task.Id == individuallyArchivedId).IsArchived);
            Assert.Throws<InvalidOperationException>(() => reopened.Work.RestoreProject(projectId));
        }

        using var restoredRestart = store.Open(WorkspacePath, _passphrase).Session!;
        Assert.False(restoredRestart.Work.Read().Projects.Single(project => project.Id == projectId).IsArchived);
        Assert.True(restoredRestart.Work.Read().Tasks.Single(task => task.Id == individuallyArchivedId).IsArchived);
    }

    [Fact]
    public void EmptyPartialAndCompleteProjectsCanBeArchivedWithoutChangingDerivedState()
    {
        using var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;
        var category = Assert.Single(session.Work.Read().Categories);
        var empty = session.Work.CreateProject("Empty", "", category.Id, null);
        var partial = session.Work.CreateProject("Partial", "", category.Id, null);
        var complete = session.Work.CreateProject("Complete", "", category.Id, null);
        session.Work.CreateTask(partial.Id, "Open");
        session.Work.CompleteTask(session.Work.CreateTask(partial.Id, "Done").Id);
        session.Work.CompleteTask(session.Work.CreateTask(complete.Id, "Done").Id);
        var before = new[] { empty, partial, complete }
            .ToDictionary(project => project.Id, project => ProjectWorkSummary.From(session.Work.Read(), project.Id));

        foreach (var project in new[] { empty, partial, complete }) session.Work.ArchiveProject(project.Id);

        var snapshot = session.Work.Read();
        Assert.All(snapshot.Projects, project => Assert.True(project.IsArchived));
        foreach (var project in snapshot.Projects)
            Assert.Equal(before[project.Id], ProjectWorkSummary.From(snapshot, project.Id));
    }

    [Fact]
    public void ProjectArchiveAndRestoreFailuresRollBackAggregateVisibilityAndTodayState()
    {
        using var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;
        var category = Assert.Single(session.Work.Read().Categories);
        var project = session.Work.CreateProject("Garden", "", category.Id, null);
        var task = session.Work.CreateTask(project.Id, "Plant bulbs");
        session.Work.SetTaskTodayLane(task.Id, TodayLane.Planned);
        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER reject_project_archive BEFORE INSERT ON project_archives BEGIN SELECT RAISE(ABORT, 'private'); END;";
            command.ExecuteNonQuery();
        }

        Assert.Throws<WorkspaceWorkException>(() => session.Work.ArchiveProject(project.Id));
        Assert.False(session.Work.Read().Projects.Single().IsArchived);
        Assert.Equal(TodayLane.Planned, session.Work.Read().Tasks.Single().TodayLane);

        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TRIGGER reject_project_archive;";
            command.ExecuteNonQuery();
        }
        session.Work.ArchiveProject(project.Id);
        Assert.Throws<InvalidOperationException>(() => session.Work.SetTaskTodayLane(task.Id, TodayLane.InProgress));
        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER reject_project_restore BEFORE DELETE ON project_archives BEGIN SELECT RAISE(ABORT, 'private'); END;";
            command.ExecuteNonQuery();
        }

        Assert.Throws<WorkspaceWorkException>(() => session.Work.RestoreProject(project.Id));
        Assert.True(session.Work.Read().Projects.Single().IsArchived);
        Assert.Null(session.Work.Read().Tasks.Single().TodayLane);
    }

    [Fact]
    public void ArchivedProjectsRejectNewAndAttachedTasksWithoutChangingExistingWork()
    {
        using var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;
        var category = Assert.Single(session.Work.Read().Categories);
        var project = session.Work.CreateProject("Archived", "", category.Id, null);
        var standalone = session.Work.CreateStandaloneTask("Standalone", "", category.Id, null);
        session.Work.SetTaskTodayLane(standalone.Id, TodayLane.Planned);
        session.Work.ArchiveProject(project.Id);

        Assert.Throws<InvalidOperationException>(() => session.Work.CreateTask(project.Id, "New Task"));
        Assert.Throws<InvalidOperationException>(() => session.Work.CreateTaskDraft(
            project.Id, "New Today Task", "", null, null, todayLane: TodayLane.Planned));
        Assert.Throws<InvalidOperationException>(() => session.Work.AttachTask(standalone.Id, project.Id));

        var snapshot = session.Work.Read();
        Assert.Single(snapshot.Tasks);
        var unchanged = Assert.Single(snapshot.Tasks);
        Assert.Null(unchanged.ProjectId);
        Assert.Equal(TodayLane.Planned, unchanged.TodayLane);
        Assert.True(Assert.Single(snapshot.Projects).IsArchived);
    }

    [Fact]
    public void BulkArchiveUsesStrictCompletionDateBoundaryAndSkipsArchivedProjectsAndTasks()
    {
        var archiveZone = TimeZoneInfo.CreateCustomTimeZone("UTC+14-bulk", TimeSpan.FromHours(14), "UTC+14", "UTC+14");
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), archiveZone);
        var store = new EncryptedWorkspaceStore(new SystemIdentifierGenerator(), new WorkspaceFileOperations(), time);
        string eligibleProjectTaskId;
        string eligibleStandaloneId;
        string atCutoffId;
        string hiddenProjectTaskId;
        string alreadyArchivedId;
        using (var session = store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!)
        {
            var category = Assert.Single(session.Work.Read().Categories);
            var activeProject = session.Work.CreateProject("Active", "", category.Id, null);
            var archivedProject = session.Work.CreateProject("Archived", "", category.Id, null);
            var eligibleProjectTask = session.Work.CreateTask(activeProject.Id, "Old project task");
            var eligibleStandalone = session.Work.CreateStandaloneTask("Old standalone", "", category.Id, null);
            var atCutoff = session.Work.CreateTask(activeProject.Id, "At cutoff");
            var hiddenProjectTask = session.Work.CreateTask(archivedProject.Id, "Hidden old task");
            var alreadyArchived = session.Work.CreateTask(activeProject.Id, "Already archived");
            session.Work.CreateTask(activeProject.Id, "Incomplete");

            time.SetUtcNow(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
            session.Work.CompleteTask(eligibleProjectTask.Id);
            session.Work.CompleteTask(eligibleStandalone.Id);
            session.Work.CompleteTask(hiddenProjectTask.Id);
            session.Work.CompleteTask(alreadyArchived.Id);
            time.SetUtcNow(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
            session.Work.CompleteTask(atCutoff.Id);
            session.Work.ArchiveTask(alreadyArchived.Id);
            session.Work.ArchiveProject(archivedProject.Id);
            eligibleProjectTaskId = eligibleProjectTask.Id;
            eligibleStandaloneId = eligibleStandalone.Id;
            atCutoffId = atCutoff.Id;
            hiddenProjectTaskId = hiddenProjectTask.Id;
            alreadyArchivedId = alreadyArchived.Id;

            time.SetUtcNow(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
            var preview = session.Work.PreviewBulkTaskArchive(3);
            Assert.Equal(3, preview.CompletedAgeDays);
            Assert.Equal(new DateOnly(2026, 10, 10), preview.EvaluatedOn);
            Assert.Equal(
                new[] { eligibleProjectTaskId, eligibleStandaloneId }.Order(StringComparer.Ordinal),
                preview.EligibleTaskIds.Order(StringComparer.Ordinal));
            var result = session.Work.BulkArchiveTasks(preview);
            Assert.True(result.Applied);
            Assert.Equal(2, result.ArchivedCount);
            Assert.Same(preview, result.Preview);

            var snapshot = session.Work.Read();
            Assert.True(snapshot.Tasks.Single(task => task.Id == eligibleProjectTaskId).IsArchived);
            Assert.True(snapshot.Tasks.Single(task => task.Id == eligibleStandaloneId).IsArchived);
            Assert.False(snapshot.Tasks.Single(task => task.Id == atCutoffId).IsArchived);
            Assert.False(snapshot.Tasks.Single(task => task.Id == hiddenProjectTaskId).IsArchived);
            Assert.True(snapshot.Tasks.Single(task => task.Id == alreadyArchivedId).IsArchived);
            Assert.False(snapshot.Projects.Single(project => project.Id == activeProject.Id).IsArchived);
            Assert.True(snapshot.Projects.Single(project => project.Id == archivedProject.Id).IsArchived);
            Assert.Empty(session.Work.PreviewBulkTaskArchive(3).EligibleTaskIds);
        }

        using var reopened = store.Open(WorkspacePath, _passphrase).Session!;
        Assert.True(reopened.Work.Read().Tasks.Single(task => task.Id == eligibleProjectTaskId).IsArchived);
        Assert.True(reopened.Work.Read().Tasks.Single(task => task.Id == eligibleStandaloneId).IsArchived);
    }

    [Fact]
    public void BulkArchiveRollsBackEveryTaskWhenOneArchiveInsertFails()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        var store = new EncryptedWorkspaceStore(new SystemIdentifierGenerator(), new WorkspaceFileOperations(), time);
        using var session = store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;
        var category = Assert.Single(session.Work.Read().Categories);
        var first = session.Work.CreateStandaloneTask("First", "", category.Id, null);
        var second = session.Work.CreateStandaloneTask("Second", "", category.Id, null);
        session.Work.CompleteTask(first.Id);
        session.Work.CompleteTask(second.Id);
        time.SetUtcNow(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"CREATE TRIGGER reject_second BEFORE INSERT ON task_archives WHEN NEW.task_id='{second.Id}' BEGIN SELECT RAISE(ABORT, 'private'); END;";
            command.ExecuteNonQuery();
        }

        var preview = session.Work.PreviewBulkTaskArchive(1);
        Assert.Throws<WorkspaceWorkException>(() => session.Work.BulkArchiveTasks(preview));

        Assert.All(session.Work.Read().Tasks, task => Assert.False(task.IsArchived));
    }

    [Fact]
    public void BulkArchiveRejectsAConfirmedPreviewThatBecomesStaleBeforeItsTransaction()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
        var store = new EncryptedWorkspaceStore(new SystemIdentifierGenerator(), new WorkspaceFileOperations(), time);
        using var session = store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;
        var category = Assert.Single(session.Work.Read().Categories);
        var task = session.Work.CreateStandaloneTask("Boundary", "", category.Id, null);
        time.SetUtcNow(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        session.Work.CompleteTask(task.Id);
        time.SetUtcNow(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
        var preview = session.Work.PreviewBulkTaskArchive(3);
        Assert.Empty(preview.EligibleTaskIds);

        time.SetUtcNow(new DateTimeOffset(2026, 10, 11, 0, 1, 0, TimeSpan.Zero));
        var result = session.Work.BulkArchiveTasks(preview);

        Assert.False(result.Applied);
        Assert.Equal(new DateOnly(2026, 10, 11), result.Preview.EvaluatedOn);
        Assert.Equal([task.Id], result.Preview.EligibleTaskIds);
        Assert.Equal(0, result.ArchivedCount);
        Assert.False(session.Work.Read().Tasks.Single().IsArchived);
    }

    [Fact]
    public void BulkArchiveRejectsSameCountEligibilitySubstitutionWithoutArchivingEitherTask()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        var store = new EncryptedWorkspaceStore(new SystemIdentifierGenerator(), new WorkspaceFileOperations(), time);
        using var session = store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;
        var category = Assert.Single(session.Work.Read().Categories);
        var first = session.Work.CreateStandaloneTask("First", "", category.Id, null);
        var replacement = session.Work.CreateStandaloneTask("Replacement", "", category.Id, null);
        time.SetUtcNow(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
        session.Work.CompleteTask(first.Id);
        time.SetUtcNow(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        session.Work.CompleteTask(replacement.Id);
        time.SetUtcNow(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
        var preview = session.Work.PreviewBulkTaskArchive(3);
        Assert.Equal([first.Id], preview.EligibleTaskIds);

        session.Work.ReopenTask(first.Id);
        time.SetUtcNow(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
        var result = session.Work.BulkArchiveTasks(preview);

        Assert.False(result.Applied);
        Assert.Equal([replacement.Id], result.Preview.EligibleTaskIds);
        Assert.Equal(preview.AffectedCount, result.Preview.AffectedCount);
        Assert.All(session.Work.Read().Tasks, task => Assert.False(task.IsArchived));
    }

    [Fact]
    public void ArchiveAndRestoreFailuresRollBackWithoutExposingPrivateDatabaseDetails()
    {
        using var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;
        var task = session.Work.CreateStandaloneTask("Private task", "secret", session.Work.Read().Categories[0].Id, null);
        var completed = session.Work.CompleteTask(task.Id);
        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER reject_archive BEFORE INSERT ON task_archives BEGIN SELECT RAISE(ABORT, 'private archive'); END;";
            command.ExecuteNonQuery();
        }

        var archiveError = Assert.Throws<WorkspaceWorkException>(() => session.Work.ArchiveTask(task.Id));
        Assert.Equal("The workspace operation could not be completed.", archiveError.Message);
        Assert.Null(archiveError.InnerException);
        var unchanged = session.Work.Read().Tasks.Single(item => item.Id == task.Id);
        Assert.False(unchanged.IsArchived);
        Assert.Null(unchanged.ArchivedAt);
        Assert.Null(unchanged.ArchiveDate);
        Assert.Equal(completed.CompletedAt, unchanged.CompletedAt);
        Assert.Equal(completed.CompletionDate, unchanged.CompletionDate);

        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TRIGGER reject_archive;";
            command.ExecuteNonQuery();
        }
        var archived = session.Work.ArchiveTask(task.Id);
        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER reject_restore BEFORE DELETE ON task_archives BEGIN SELECT RAISE(ABORT, 'private restore'); END;";
            command.ExecuteNonQuery();
        }

        var restoreError = Assert.Throws<WorkspaceWorkException>(() => session.Work.RestoreTask(task.Id));
        Assert.Equal("The workspace operation could not be completed.", restoreError.Message);
        Assert.Null(restoreError.InnerException);
        var stillArchived = session.Work.Read().Tasks.Single(item => item.Id == task.Id);
        Assert.Equal(archived.ArchivedAt, stillArchived.ArchivedAt);
        Assert.Equal(archived.ArchiveDate, stillArchived.ArchiveDate);
        Assert.Equal(completed.CompletedAt, stillArchived.CompletedAt);
        Assert.Equal(completed.CompletionDate, stillArchived.CompletionDate);
    }

    [Fact]
    public void ArchiveSearchIndexesPlainMarkdownCategoryParticipantsAndDeterministicResultContext()
    {
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);
        var store = new EncryptedWorkspaceStore(new SystemIdentifierGenerator(), new WorkspaceFileOperations(), time);
        using var session = store.Create(WorkspacePath, _passphrase, CategoryName.Create("Résumé Café").CategoryName!).Session!;
        var category = Assert.Single(session.Work.Read().Categories);
        var participant = session.Work.CreateParticipant("Zoë");
        var project = session.Work.CreateProject(
            "Garden archive",
            "# Tulip **layout** [reference](https://private.example)\n\n"
                + "<div>Hidden raw HTML</div>\n\n"
                + "![Seed chart](https://images.example/secret.png)\n\n"
                + "[Unsafe label](javascript:alert(1))",
            category.Id,
            null);
        var task = session.Work.CreateTaskDraft(
            project.Id,
            "Order bulbs",
            "Blue, tulips for the border",
            null,
            null,
            new([participant.Id], []));
        session.Work.CreateStandaloneTask("Active tulip notes", "Blue tulips", category.Id, null);
        session.Work.CompleteTask(task.Id);
        session.Work.ArchiveTask(task.Id);
        time.SetUtcNow(new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.Zero));
        session.Work.ArchiveProject(project.Id);

        var tulipResults = session.Work.SearchArchive("TUL");

        Assert.Collection(tulipResults,
            result =>
            {
                Assert.Equal(ArchiveSearchRecordType.Project, result.RecordType);
                Assert.Equal(project.Id, result.Id);
                Assert.Equal("Garden archive", result.Title);
                Assert.Null(result.ParentProjectTitle);
                Assert.Equal(ArchiveSearchDateKind.Archived, result.DateKind);
                Assert.Equal(new DateOnly(2026, 10, 2), result.Date);
                Assert.Contains("Tulip", result.Excerpt, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("**", result.Excerpt, StringComparison.Ordinal);
            },
            result =>
            {
                Assert.Equal(ArchiveSearchRecordType.Task, result.RecordType);
                Assert.Equal(task.Id, result.Id);
                Assert.Equal("Garden archive", result.ParentProjectTitle);
                Assert.Equal(ArchiveSearchDateKind.Completed, result.DateKind);
                Assert.Equal(new DateOnly(2026, 10, 1), result.Date);
                Assert.Contains("tulips", result.Excerpt, StringComparison.OrdinalIgnoreCase);
            });
        Assert.Equal([project.Id, task.Id], session.Work.SearchArchive("résu").Select(result => result.Id));
        Assert.Equal(task.Id, Assert.Single(session.Work.SearchArchive("zo")).Id);
        Assert.Empty(session.Work.SearchArchive("private"));
        Assert.Empty(session.Work.SearchArchive("hidden"));
        Assert.Empty(session.Work.SearchArchive("images"));
        Assert.Empty(session.Work.SearchArchive("javascript"));
        Assert.Equal(project.Id, Assert.Single(session.Work.SearchArchive("seed")).Id);
        Assert.Equal(project.Id, Assert.Single(session.Work.SearchArchive("unsafe")).Id);
        Assert.DoesNotContain(session.Work.SearchArchive("tulip"), result => result.Title == "Active tulip notes");

        session.Dispose();
        using var reopened = store.Open(WorkspacePath, _passphrase).Session!;
        Assert.Equal([project.Id, task.Id], reopened.Work.SearchArchive("tul").Select(result => result.Id));
    }

    [Fact]
    public void ArchiveSearchUsesTokenPrefixesWithoutSubstringOrAdvancedQuerySemantics()
    {
        using var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Café").CategoryName!).Session!;
        var category = Assert.Single(session.Work.Read().Categories);
        var task = session.Work.CreateStandaloneTask("Planting guide", "Blue, tulips", category.Id, null);
        session.Work.CompleteTask(task.Id);
        session.Work.ArchiveTask(task.Id);

        Assert.Equal(task.Id, Assert.Single(session.Work.SearchArchive("plant")).Id);
        Assert.Equal(task.Id, Assert.Single(session.Work.SearchArchive("BLUE—TUL")).Id);
        Assert.Equal(task.Id, Assert.Single(session.Work.SearchArchive("cafe")).Id);
        Assert.Empty(session.Work.SearchArchive("ant"));
        Assert.Empty(session.Work.SearchArchive("blue rose"));
        Assert.Empty(session.Work.SearchArchive("blue OR rose"));
        Assert.Empty(session.Work.SearchArchive("!!! OR *"));

        var compatibility = session.Work.CreateStandaloneTask(
            "Ｆｏｏ ﬂower", "Compatibility characters", category.Id, null);
        session.Work.CompleteTask(compatibility.Id);
        session.Work.ArchiveTask(compatibility.Id);
        Assert.Equal(compatibility.Id, Assert.Single(session.Work.SearchArchive("Ｆｏ")).Id);
        Assert.Equal(compatibility.Id, Assert.Single(session.Work.SearchArchive("ﬂo")).Id);
    }

    [Fact]
    public void ArchiveSearchStaysAlignedThroughEditsRenamesReassignmentArchiveAndRestore()
    {
        using var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;
        var home = Assert.Single(session.Work.Read().Categories);
        var reference = session.Work.CreateCategory("Work");
        var participant = session.Work.CreateParticipant("SD");
        var firstProject = session.Work.CreateProject("First parent", "", home.Id, null);
        var secondProject = session.Work.CreateProject("Second parent", "", reference.Id, null);
        var task = session.Work.CreateTaskDraft(firstProject.Id, "Old title", "Old description", null, null,
            new([participant.Id], []));
        session.Work.CompleteTask(task.Id);
        session.Work.ArchiveTask(task.Id);
        Assert.Equal(task.Id, Assert.Single(session.Work.SearchArchive("old")).Id);

        session.Work.UpdateTask(task.Id, "Updated title", "Fresh phrase", reference.Id, null,
            new([participant.Id], []));
        session.Work.RenameParticipant(participant.Id, "ZX");
        session.Work.RenameCategory(reference.Id, "Reference");

        Assert.Empty(session.Work.SearchArchive("old"));
        Assert.Equal(task.Id, Assert.Single(session.Work.SearchArchive("fresh reference zx")).Id);
        Assert.Equal("First parent", Assert.Single(session.Work.SearchArchive("updated")).ParentProjectTitle);

        session.Work.DetachTask(task.Id);
        Assert.Null(Assert.Single(session.Work.SearchArchive("updated")).ParentProjectTitle);
        session.Work.AttachTask(task.Id, secondProject.Id);
        Assert.Equal("Second parent", Assert.Single(session.Work.SearchArchive("updated")).ParentProjectTitle);
        session.Work.UpdateProject(secondProject.Id, "Revised parent", "", home.Id, null);
        var moved = Assert.Single(session.Work.SearchArchive("updated"));
        Assert.Equal("Revised parent", moved.ParentProjectTitle);
        Assert.Equal(task.Id, Assert.Single(session.Work.SearchArchive("home")).Id);
        Assert.Empty(session.Work.SearchArchive("reference"));

        session.Work.RestoreTask(task.Id);
        Assert.Empty(session.Work.SearchArchive("updated"));
        session.Work.ArchiveTask(task.Id);
        Assert.Equal(task.Id, Assert.Single(session.Work.SearchArchive("updated")).Id);

        session.Work.ArchiveProject(firstProject.Id);
        session.Work.UpdateProject(firstProject.Id, "Revised project", "Historical notes", reference.Id, null);
        Assert.Equal(firstProject.Id, Assert.Single(session.Work.SearchArchive("historical reference")).Id);
        session.Work.RestoreProject(firstProject.Id);
        Assert.Empty(session.Work.SearchArchive("historical"));
    }

    [Fact]
    public void SearchIndexFailureRollsBackTheSourceEdit()
    {
        using var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;
        var category = Assert.Single(session.Work.Read().Categories);
        var task = session.Work.CreateStandaloneTask("Original", "Original description", category.Id, null);
        session.Work.CompleteTask(task.Id);
        session.Work.ArchiveTask(task.Id);
        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TABLE archive_search;";
            command.ExecuteNonQuery();
        }

        Assert.Throws<WorkspaceWorkException>(() => session.Work.UpdateTask(
            task.Id, "Changed", "Changed description", category.Id, null));

        var unchanged = Assert.Single(session.Work.Read().Tasks);
        Assert.Equal("Original", unchanged.Title);
        Assert.Equal("Original description", unchanged.Description);
    }

    [Fact]
    public void TodayMembershipLaneAndOrderPersistWithoutFollowingDatesOrCalendarRollover()
    {
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 29, 23, 55, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);
        var store = new EncryptedWorkspaceStore(new SystemIdentifierGenerator(), new WorkspaceFileOperations(), time);
        string plannedId;
        string activeId;

        using (var session = store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!)
        {
            var category = session.Work.Read().Categories[0];
            var project = session.Work.CreateProject("Garden", "", category.Id, null);
            plannedId = session.Work.CreateTask(project.Id, "Planned").Id;
            activeId = session.Work.CreateStandaloneTask("Active", "", category.Id, new DateOnly(2026, 9, 29)).Id;
            session.Work.SetTaskTodayLane(plannedId, TodayLane.Planned);
            session.Work.SetTaskTodayLane(activeId, TodayLane.InProgress);
            session.Work.UpdateTask(activeId, "Active", "", category.Id, new DateOnly(2027, 1, 1));
            Assert.Equal([activeId, plannedId], session.Work.Read().Tasks.Select(task => task.Id));
        }

        time.SetUtcNow(new DateTimeOffset(2026, 9, 30, 0, 5, 0, TimeSpan.Zero));
        using var reopened = store.Open(WorkspacePath, _passphrase).Session!;
        var tasks = reopened.Work.Read().Tasks;
        Assert.Equal(TodayLane.Planned, tasks.Single(task => task.Id == plannedId).TodayLane);
        Assert.Equal(TodayLane.InProgress, tasks.Single(task => task.Id == activeId).TodayLane);
        Assert.Equal(new DateOnly(2027, 1, 1), tasks.Single(task => task.Id == activeId).DueDate);
        Assert.Equal([activeId, plannedId], tasks.Select(task => task.Id));
    }

    [Fact]
    public void TodayLaneMovementClearCompletionAndReopenPreserveIndependentState()
    {
        using var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;
        var category = session.Work.Read().Categories[0];
        var project = session.Work.CreateProject("Garden", "", category.Id, null);
        var hidden = session.Work.CreateTask(project.Id, "Hidden");
        var plannedOlder = session.Work.CreateTask(project.Id, "Planned older");
        var active = session.Work.CreateTask(project.Id, "Active");
        var plannedNewer = session.Work.CreateTask(project.Id, "Planned newer");
        session.Work.MoveTaskInSharedOrder(hidden.Id, 1);
        session.Work.SetTaskTodayLane(plannedOlder.Id, TodayLane.Planned);
        session.Work.SetTaskTodayLane(active.Id, TodayLane.InProgress);
        session.Work.SetTaskTodayLane(plannedNewer.Id, TodayLane.Planned);

        var beforeLaneMove = session.Work.Read().Tasks.Select(task => task.Id).ToArray();
        session.Work.SetTaskTodayLane(active.Id, TodayLane.Planned);
        Assert.Equal(beforeLaneMove, session.Work.Read().Tasks.Select(task => task.Id));

        var change = session.Work.MoveTaskInTodayLane(plannedOlder.Id, 0);
        Assert.Equal(new TodayLaneOrderChange(plannedOlder.Id, TodayLane.Planned, 1, 3), change);
        Assert.Equal([plannedOlder.Id, hidden.Id, plannedNewer.Id, active.Id], session.Work.Read().Tasks.Select(task => task.Id));

        session.Work.CompleteTask(active.Id);
        Assert.Null(session.Work.Read().Tasks.Single(task => task.Id == active.Id).TodayLane);
        session.Work.ReopenTask(active.Id);
        Assert.Null(session.Work.Read().Tasks.Single(task => task.Id == active.Id).TodayLane);
        Assert.Throws<InvalidOperationException>(() =>
        {
            session.Work.CompleteTask(active.Id);
            session.Work.SetTaskTodayLane(active.Id, TodayLane.Planned);
        });
        session.Work.ReopenTask(active.Id);

        Assert.Equal(2, session.Work.ClearToday());
        Assert.All(session.Work.Read().Tasks, task => Assert.Null(task.TodayLane));
    }

    [Fact]
    public void CompletionFailureRollsBackBothCapturedFieldsWithoutExposingPrivateDatabaseDetails()
    {
        using var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;
        var project = session.Work.CreateProject("Garden", "", session.Work.Read().Categories[0].Id, null);
        var task = session.Work.CreateTask(project.Id, "Private task");
        session.Work.SetTaskTodayLane(task.Id, TodayLane.Planned);
        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_completion BEFORE UPDATE OF completion_date ON tasks BEGIN SELECT RAISE(ABORT, 'private completion'); END;";
            command.ExecuteNonQuery();
        }

        var error = Assert.Throws<WorkspaceWorkException>(() => session.Work.CompleteTask(task.Id));
        Assert.Equal("The workspace operation could not be completed.", error.Message);
        var unchanged = Assert.Single(session.Work.Read().Tasks);
        Assert.Null(unchanged.CompletedAt);
        Assert.Null(unchanged.CompletionDate);
        Assert.Equal(TodayLane.Planned, unchanged.TodayLane);

        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER reject_completion;";
            command.ExecuteNonQuery();
        }
        var completed = session.Work.CompleteTask(task.Id);
        Assert.Null(completed.TodayLane);
        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_reopen BEFORE UPDATE OF completion_instant ON tasks BEGIN SELECT RAISE(ABORT, 'private reopen'); END;";
            command.ExecuteNonQuery();
        }
        Assert.Throws<WorkspaceWorkException>(() => session.Work.ReopenTask(task.Id));
        var stillCompleted = Assert.Single(session.Work.Read().Tasks);
        Assert.Equal(completed.CompletedAt, stillCompleted.CompletedAt);
        Assert.Equal(completed.CompletionDate, stillCompleted.CompletionDate);
    }

    [Fact]
    public void ProjectAndPerProjectTaskOrdersPersistIndependentlyAndRollBackOnFailure()
    {
        using (var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!)
        {
            var category = session.Work.Read().Categories[0];
            var firstProject = session.Work.CreateProject("First", "", category.Id, null);
            var secondProject = session.Work.CreateProject("Second", "", category.Id, null);
            var firstTask = session.Work.CreateTask(firstProject.Id, "First task");
            var secondTask = session.Work.CreateTask(firstProject.Id, "Second task");
            var otherTask = session.Work.CreateTask(secondProject.Id, "Other task");

            Assert.Equal(new ProjectOrderChange(secondProject.Id, 1, 2), session.Work.MoveProject(secondProject.Id, 0));
            Assert.Equal(new ProjectTaskOrderChange(firstProject.Id, secondTask.Id, 1, 2), session.Work.MoveTaskInProject(firstProject.Id, secondTask.Id, 0));
            Assert.Equal([secondProject.Id, firstProject.Id], session.Work.Read().Projects.Select(project => project.Id));
            Assert.Equal([secondTask.Id, firstTask.Id], session.Work.Read().Tasks.Where(task => task.ProjectId == firstProject.Id).OrderBy(task => task.ProjectPosition).Select(task => task.Id));
            Assert.Equal(0, session.Work.Read().Tasks.Single(task => task.Id == otherTask.Id).ProjectPosition);

            using var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite);
            using var trigger = connection.CreateCommand();
            trigger.CommandText = "CREATE TRIGGER reject_project_order BEFORE UPDATE OF position ON projects BEGIN SELECT RAISE(ABORT, 'private order'); END;";
            trigger.ExecuteNonQuery();
            Assert.Throws<WorkspaceWorkException>(() => session.Work.MoveProject(firstProject.Id, 0));
            Assert.Equal([secondProject.Id, firstProject.Id], session.Work.Read().Projects.Select(project => project.Id));
        }

        using var reopened = _store.Open(WorkspacePath, _passphrase).Session!;
        Assert.Equal(["Second", "First"], reopened.Work.Read().Projects.Select(project => project.Title));
        Assert.Equal(["Second task", "First task"], reopened.Work.Read().Tasks.Where(task => task.ProjectId == reopened.Work.Read().Projects[1].Id).OrderBy(task => task.ProjectPosition).Select(task => task.Title));
    }

    [Fact]
    public void CategoriesAreCreatedRenamedAndReorderedWithCaseInsensitiveUniqueNames()
    {
        using var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;

        var work = session.Work.CreateCategory(" Work ");
        var someday = session.Work.CreateCategory("Someday");
        Assert.Equal(["Home", "Work", "Someday"], session.Work.Read().Categories.Select(category => category.Name));
        Assert.Throws<ArgumentException>(() => session.Work.CreateCategory("  "));
        Assert.Throws<ArgumentException>(() => session.Work.CreateCategory("work"));
        var resume = session.Work.CreateCategory("Résumé");
        Assert.Throws<ArgumentException>(() => session.Work.CreateCategory("RÉSUMÉ"));
        session.Work.DeleteCategory(resume.Id);
        Assert.Throws<ArgumentException>(() => session.Work.RenameCategory(someday.Id, "\t"));
        Assert.Throws<ArgumentException>(() => session.Work.RenameCategory(someday.Id, "HOME"));

        var renamed = session.Work.RenameCategory(someday.Id, "Personal");
        Assert.Equal("Personal", renamed.Name);
        Assert.Equal(new CategoryOrderChange(renamed.Id, 1, 3), session.Work.MoveCategory(renamed.Id, 0));
        Assert.Equal(["Personal", "Home", "Work"], session.Work.Read().Categories.Select(category => category.Name));

        session.Work.DeleteCategory(work.Id);
        Assert.Equal(["Personal", "Home"], session.Work.Read().Categories.Select(category => category.Name));
        session.Work.DeleteCategory(renamed.Id);
        Assert.Throws<InvalidOperationException>(() => session.Work.DeleteCategory(session.Work.Read().Categories[0].Id));
        Assert.Equal("Home", Assert.Single(session.Work.Read().Categories).Name);
    }

    [Fact]
    public void ReferencedCategoryDeletionReassignsEveryReferenceAtomicallyAndRollsBackOnFailure()
    {
        using var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;
        var home = session.Work.Read().Categories[0];
        var work = session.Work.CreateCategory("Work");
        var project = session.Work.CreateProject("Home project", "", home.Id, null);
        session.Work.CreateTask(project.Id, "Inherited");
        var overridden = session.Work.CreateTask(session.Work.CreateProject("Work project", "", work.Id, null).Id, "Override");
        session.Work.UpdateTask(overridden.Id, overridden.Title, "", home.Id, null);
        session.Work.CreateStandaloneTask("Standalone", "", home.Id, null);

        Assert.Throws<ArgumentException>(() => session.Work.DeleteCategory(home.Id));
        Assert.Throws<ArgumentException>(() => session.Work.DeleteCategory(home.Id, home.Id));
        session.Work.DeleteCategory(home.Id, work.Id);

        var replaced = session.Work.Read();
        Assert.DoesNotContain(replaced.Categories, category => category.Id == home.Id);
        Assert.All(replaced.Projects, item => Assert.Equal(work.Id, item.CategoryId));
        Assert.Equal(work.Id, replaced.Tasks.Single(task => task.Id == overridden.Id).ExplicitCategoryId);
        Assert.Equal(work.Id, replaced.Tasks.Single(task => task.ProjectId is null).ExplicitCategoryId);

        var legacy = session.Work.CreateCategory("Legacy");
        var legacyProject = session.Work.CreateProject("Legacy project", "", legacy.Id, null);
        session.Work.CreateStandaloneTask("Legacy task", "", legacy.Id, null);
        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite))
        {
            using var trigger = connection.CreateCommand();
            trigger.CommandText = "CREATE TRIGGER reject_category_replacement BEFORE UPDATE OF explicit_category_id ON tasks BEGIN SELECT RAISE(ABORT, 'private category'); END;";
            trigger.ExecuteNonQuery();
        }

        var error = Assert.Throws<WorkspaceWorkException>(() => session.Work.DeleteCategory(legacy.Id, work.Id));
        Assert.Equal("The workspace operation could not be completed.", error.Message);
        var unchanged = session.Work.Read();
        Assert.Contains(unchanged.Categories, category => category.Id == legacy.Id);
        Assert.Equal(legacy.Id, unchanged.Projects.Single(item => item.Id == legacyProject.Id).CategoryId);
        Assert.Equal(legacy.Id, unchanged.Tasks.Single(task => task.Title == "Legacy task").ExplicitCategoryId);
    }

    [Fact]
    public void AttachDetachAndCrossProjectMovePreserveSharedOrderAndMaintainDenseProjectOrders()
    {
        using (var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!)
        {
            var home = session.Work.Read().Categories[0];
            var work = session.Work.CreateCategory("Work");
            var homeProject = session.Work.CreateProject("Home project", "", home.Id, null);
            var workProject = session.Work.CreateProject("Work project", "", work.Id, null);
            var homeFirst = session.Work.CreateTask(homeProject.Id, "Home first");
            var moving = session.Work.CreateTask(homeProject.Id, "Moving");
            var homeLast = session.Work.CreateTask(homeProject.Id, "Home last");
            var workFirst = session.Work.CreateTask(workProject.Id, "Work first");
            var sharedOrder = session.Work.Read().Tasks.Select(task => task.Id).ToArray();

            var detached = session.Work.DetachTask(moving.Id);
            Assert.Null(detached.ProjectId);
            Assert.Equal(home.Id, detached.ExplicitCategoryId);
            Assert.Null(detached.ProjectPosition);
            Assert.Equal([homeFirst.Id, homeLast.Id], session.Work.Read().Tasks.Where(task => task.ProjectId == homeProject.Id)
                .OrderBy(task => task.ProjectPosition).Select(task => task.Id));
            Assert.Equal([0, 1], session.Work.Read().Tasks.Where(task => task.ProjectId == homeProject.Id)
                .OrderBy(task => task.ProjectPosition).Select(task => task.ProjectPosition));
            Assert.Equal(sharedOrder, session.Work.Read().Tasks.Select(task => task.Id));

            var reattached = session.Work.AttachTask(moving.Id, homeProject.Id);
            Assert.Equal(homeProject.Id, reattached.ProjectId);
            Assert.Null(reattached.ExplicitCategoryId);
            Assert.Equal(2, reattached.ProjectPosition);

            session.Work.DetachTask(moving.Id);
            Assert.Throws<ArgumentException>(() => session.Work.AttachTask(moving.Id, workProject.Id));
            Assert.Null(session.Work.Read().Tasks.Single(task => task.Id == moving.Id).ProjectId);
            var preserved = session.Work.AttachTask(moving.Id, workProject.Id, TaskAttachmentCategoryChoice.PreserveEffectiveCategory);
            Assert.Equal(home.Id, preserved.ExplicitCategoryId);
            Assert.Equal(1, preserved.ProjectPosition);

            var adopted = session.Work.AttachTask(moving.Id, homeProject.Id, TaskAttachmentCategoryChoice.AdoptProjectCategory);
            Assert.Equal(homeProject.Id, adopted.ProjectId);
            Assert.Null(adopted.ExplicitCategoryId);
            Assert.Equal(2, adopted.ProjectPosition);
            Assert.Equal(0, session.Work.Read().Tasks.Single(task => task.Id == workFirst.Id).ProjectPosition);
            Assert.Equal(sharedOrder, session.Work.Read().Tasks.Select(task => task.Id));
        }

        using var reopened = _store.Open(WorkspacePath, _passphrase).Session!;
        var snapshot = reopened.Work.Read();
        Assert.Equal(["Home first", "Home last", "Moving"], snapshot.Tasks.Where(task => task.ProjectId == snapshot.Projects.Single(project => project.Title == "Home project").Id)
            .OrderBy(task => task.ProjectPosition).Select(task => task.Title));
        Assert.Equal(["Work first"], snapshot.Tasks.Where(task => task.ProjectId == snapshot.Projects.Single(project => project.Title == "Work project").Id)
            .OrderBy(task => task.ProjectPosition).Select(task => task.Title));
    }

    [Fact]
    public void CrossProjectMoveFailureRollsBackMembershipAndBothOrders()
    {
        using var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;
        var category = session.Work.Read().Categories[0];
        var source = session.Work.CreateProject("Source", "", category.Id, null);
        var destination = session.Work.CreateProject("Destination", "", category.Id, null);
        var sourceFirst = session.Work.CreateTask(source.Id, "Source first");
        var moving = session.Work.CreateTask(source.Id, "Moving");
        var sourceLast = session.Work.CreateTask(source.Id, "Source last");
        var destinationFirst = session.Work.CreateTask(destination.Id, "Destination first");
        var before = session.Work.Read();
        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite))
        {
            using var trigger = connection.CreateCommand();
            trigger.CommandText = "CREATE TRIGGER reject_attach BEFORE UPDATE OF project_id ON tasks WHEN NEW.project_id IS NOT NULL BEGIN SELECT RAISE(ABORT, 'private attach'); END;";
            trigger.ExecuteNonQuery();
        }

        var error = Assert.Throws<WorkspaceWorkException>(() => session.Work.AttachTask(moving.Id, destination.Id));
        Assert.Equal("The workspace operation could not be completed.", error.Message);
        var after = session.Work.Read();
        Assert.Equal(before.Tasks, after.Tasks);
        Assert.Equal([sourceFirst.Id, moving.Id, sourceLast.Id], after.Tasks.Where(task => task.ProjectId == source.Id)
            .OrderBy(task => task.ProjectPosition).Select(task => task.Id));
        Assert.Equal([0, 1, 2], after.Tasks.Where(task => task.ProjectId == source.Id)
            .OrderBy(task => task.ProjectPosition).Select(task => task.ProjectPosition));
        Assert.Equal([destinationFirst.Id], after.Tasks.Where(task => task.ProjectId == destination.Id)
            .OrderBy(task => task.ProjectPosition).Select(task => task.Id));
        Assert.Equal(before.Tasks.Select(task => task.Id), after.Tasks.Select(task => task.Id));
    }

    [Fact]
    public void InvalidReferencesAndBlankTitlesNeverChangeStoredWork()
    {
        using var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;
        var category = session.Work.Read().Categories[0];
        var project = session.Work.CreateProject("Original", "", category.Id, null);
        Assert.Throws<ArgumentException>(() => session.Work.CreateProject(" ", "", category.Id, null));
        Assert.Throws<ArgumentException>(() => session.Work.CreateProject("Bad", "", "missing", null));
        Assert.Throws<ArgumentException>(() => session.Work.CreateTask("missing", "Bad"));
        Assert.Throws<ArgumentException>(() => session.Work.CreateStandaloneTask("Bad", "", "missing", null));
        Assert.Throws<ArgumentException>(() => session.Work.UpdateProject(project.Id, "Changed", "", "missing", null));
        var task = session.Work.CreateTask(project.Id, "Original task");
        Assert.Throws<ArgumentException>(() => session.Work.UpdateTask(task.Id, "Changed", "", "missing", null));
        var standalone = session.Work.CreateStandaloneTask("Standalone", "", category.Id, null);
        Assert.Throws<ArgumentException>(() => session.Work.UpdateTask(standalone.Id, "Changed", "", null, null));
        Assert.Throws<ArgumentException>(() => session.Work.CompleteTask("missing"));
        Assert.Throws<ArgumentException>(() => session.Work.ReopenTask("missing"));
        Assert.Throws<ArgumentException>(() => session.Work.MoveTaskInSharedOrder("missing", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Work.MoveTaskInSharedOrder(task.Id, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Work.MoveTaskInSharedOrder(task.Id, 2));
        Assert.Throws<ArgumentException>(() => session.Work.MoveProject("missing", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Work.MoveProject(project.Id, 1));
        Assert.Throws<ArgumentException>(() => session.Work.MoveTaskInProject(project.Id, standalone.Id, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Work.MoveTaskInProject(project.Id, task.Id, 1));
        Assert.Equal(project, Assert.Single(session.Work.Read().Projects));
        Assert.Equal([standalone.Id, task.Id], session.Work.Read().Tasks.Select(item => item.Id));
    }

    [Fact]
    public void ReleasedSchemaTwoMigratesWithRecoverableCategoryAndPersistsNewWork()
    {
        Directory.CreateDirectory(_directory);
        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWriteCreate))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE categories(id TEXT NOT NULL PRIMARY KEY,name TEXT NOT NULL COLLATE NOCASE UNIQUE,position INTEGER NOT NULL CHECK(position>=0)); CREATE INDEX ix_categories_position ON categories(position); INSERT INTO categories VALUES ('original','Home',0); PRAGMA user_version=2;";
            command.ExecuteNonQuery();
        }
        using (var session = _store.Open(WorkspacePath, _passphrase).Session!)
        {
            Assert.Equal(EncryptedWorkspaceStore.CurrentSchemaVersion, session.SchemaVersion);
            Assert.Equal("original", Assert.Single(session.Work.Read().Categories).Id);
            var project = session.Work.CreateProject("Migrated project", "", "original", null);
            session.Work.CreateTask(project.Id, "Migrated task");
        }
        var recovery = Assert.Single(Directory.GetFiles(_directory, "*.dotorbit-recovery"));
        using (var connection = EncryptedWorkspaceStore.OpenConnection(recovery, _passphrase, SqliteOpenMode.ReadOnly))
        {
            Assert.Equal(2L, EncryptedWorkspaceStore.ExecuteScalar<long>(connection, "PRAGMA user_version;"));
            Assert.Equal("Home", EncryptedWorkspaceStore.ExecuteScalar<string>(connection, "SELECT name FROM categories;"));
        }
        using var reopened = _store.Open(WorkspacePath, _passphrase).Session!;
        Assert.Equal("Migrated task", Assert.Single(reopened.Work.Read().Tasks).Title);
    }

    [Fact]
    public void WritesScheduleEncryptedRecoveryAndClosedSessionsRejectWork()
    {
        var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;
        var recoveryDirectory = Path.Combine(_directory, "recovery");
        Assert.Equal(RecoveryDirectoryConfigurationStatus.Configured,
            session.Recovery.ConfigureAutomaticRecoveryDirectory(recoveryDirectory).Status);
        var category = session.Work.Read().Categories[0];
        session.Work.CreateProject("Recovery content", "", category.Id, null);
        Assert.NotEmpty(Directory.GetFiles(recoveryDirectory, "*.dotorbit-recovery"));
        session.Dispose();
        Assert.Throws<ObjectDisposedException>(() => session.Work.Read());
        Assert.Throws<ObjectDisposedException>(() => session.Work.CreateProject("Closed", "", category.Id, null));
    }

    [Fact]
    public void DatabaseWriteFailuresExposeNoTaskOrSqlContentAndRetainStoredState()
    {
        using var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;
        var category = session.Work.Read().Categories[0];
        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_project BEFORE INSERT ON projects BEGIN SELECT RAISE(ABORT, 'private database details'); END;";
            command.ExecuteNonQuery();
        }
        var error = Assert.Throws<WorkspaceWorkException>(() => session.Work.CreateProject("Private title", "Secret description", category.Id, null));
        Assert.Equal("The workspace operation could not be completed.", error.Message);
        Assert.Null(error.InnerException);
        Assert.Empty(session.Work.Read().Projects);
    }

    [Theory]
    [InlineData(" REFERENCES categories(id)", "")]
    [InlineData(" REFERENCES projects(id)", "")]
    [InlineData("NOT NULL UNIQUE", "NOT NULL")]
    [InlineData(" CHECK(position >= 0)", "")]
    [InlineData(" CHECK(project_position >= 0)", "")]
    [InlineData("UNIQUE(project_id, project_position)", "CHECK(project_position >= 0)")]
    [InlineData(" CHECK(length(trim(title)) > 0)", "")]
    [InlineData("title TEXT", "title INTEGER")]
    [InlineData("description TEXT NOT NULL", "description TEXT")]
    [InlineData("completion_instant TEXT", "completion_instant INTEGER")]
    [InlineData("CHECK((completion_instant IS NULL AND completion_date IS NULL)", "CHECK((completion_instant IS NULL OR completion_date IS NULL)")]
    [InlineData("label TEXT NOT NULL CHECK(length(trim(label)) > 0)", "label TEXT NOT NULL")]
    [InlineData("participant_id TEXT NOT NULL REFERENCES participants(id)", "participant_id TEXT NOT NULL")]
    [InlineData("PRIMARY KEY(task_id, participant_id)", "UNIQUE(task_id, participant_id)")]
    [InlineData("UNIQUE(task_id, position)", "UNIQUE(participant_id, position)")]
    [InlineData("task_id TEXT NOT NULL PRIMARY KEY REFERENCES tasks(id)", "task_id TEXT NOT NULL PRIMARY KEY")]
    [InlineData("archived_instant TEXT NOT NULL", "archived_instant TEXT")]
    [InlineData("archived_instant TEXT NOT NULL", "archived_instant INTEGER NOT NULL")]
    [InlineData("archive_date TEXT NOT NULL", "archive_date TEXT")]
    [InlineData("archive_date TEXT NOT NULL", "archive_date INTEGER NOT NULL")]
    [InlineData("tokenize = 'unicode61 remove_diacritics 2'", "tokenize = 'ascii'")]
    public void OpenRejectsCurrentSchemaWithChangedCanonicalDefinitions(string original, string replacement)
    {
        _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!.Dispose();
        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite))
        {
            using var command = connection.CreateCommand();
            // Rebuild the empty work tables with valid SQL that weakens one schema guarantee.
            command.CommandText = "DROP TABLE project_bins; DROP TABLE task_bin_order_anchors; DROP TABLE task_bins; DROP TABLE archive_search; DROP TABLE project_archives; DROP TABLE task_archives; DROP TABLE today_tasks; DROP TABLE task_participants; DROP TABLE participants; DROP TABLE tasks; DROP TABLE projects;" +
                SqliteWorkspaceWork.Schema.Replace(original, replacement, StringComparison.Ordinal);
            command.ExecuteNonQuery();
            Assert.Equal("ok", EncryptedWorkspaceStore.ExecuteScalar<string>(connection, "PRAGMA integrity_check;"));
        }
        var before = File.ReadAllBytes(WorkspacePath);
        var result = _store.Open(WorkspacePath, _passphrase);
        Assert.Equal(WorkspaceOpenStatus.InvalidPassphraseOrStore, result.Status);
        Assert.Null(result.Session);
        Assert.Equal(before, File.ReadAllBytes(WorkspacePath));
    }

    [Fact]
    public void OpenRejectsCompletedTaskThatStillHasTodayMembership()
    {
        string taskId;
        using (var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!)
        {
            var project = session.Work.CreateProject("Garden", "", session.Work.Read().Categories[0].Id, null);
            taskId = session.Work.CreateTask(project.Id, "Dig").Id;
            session.Work.SetTaskTodayLane(taskId, TodayLane.Planned);
        }
        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE tasks
                SET completion_instant='2026-09-29T12:00:00.0000000+00:00', completion_date='2026-09-29'
                WHERE id=$id;
                """;
            command.Parameters.AddWithValue("$id", taskId);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        var result = _store.Open(WorkspacePath, _passphrase);

        Assert.Equal(WorkspaceOpenStatus.InvalidPassphraseOrStore, result.Status);
        Assert.Null(result.Session);
    }

    [Theory]
    [InlineData(false, "2026-10-03T12:00:00.0000000+00:00", "2026-10-03")]
    [InlineData(true, "not-an-instant", "2026-10-03")]
    [InlineData(true, "2026-10-03T12:00:00.0000000+00:00", "not-a-date")]
    public void InvalidPersistedArchiveStateIsRejectedOnOpenAndRead(
        bool complete,
        string archivedInstant,
        string archiveDate)
    {
        var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;
        var task = session.Work.CreateStandaloneTask("Task", "", session.Work.Read().Categories[0].Id, null);
        if (complete) session.Work.CompleteTask(task.Id);
        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO task_archives (task_id,archived_instant,archive_date) VALUES ($id,$instant,$date);";
            command.Parameters.AddWithValue("$id", task.Id);
            command.Parameters.AddWithValue("$instant", archivedInstant);
            command.Parameters.AddWithValue("$date", archiveDate);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        var error = Assert.Throws<WorkspaceWorkException>(() => session.Work.Read());
        Assert.Equal("The workspace operation could not be completed.", error.Message);
        Assert.Null(error.InnerException);
        session.Dispose();

        var result = _store.Open(WorkspacePath, _passphrase);
        Assert.Equal(WorkspaceOpenStatus.InvalidPassphraseOrStore, result.Status);
        Assert.Null(result.Session);
    }

    [Theory]
    [InlineData("not-an-instant", "2026-10-03")]
    [InlineData("2026-10-03T12:00:00.0000000+00:00", "not-a-date")]
    public void InvalidPersistedProjectArchiveStateIsRejectedOnOpenAndRead(
        string archivedInstant,
        string archiveDate)
    {
        var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;
        var project = session.Work.CreateProject("Project", "", session.Work.Read().Categories[0].Id, null);
        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO project_archives (project_id,archived_instant,archive_date) VALUES ($id,$instant,$date);";
            command.Parameters.AddWithValue("$id", project.Id);
            command.Parameters.AddWithValue("$instant", archivedInstant);
            command.Parameters.AddWithValue("$date", archiveDate);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        var error = Assert.Throws<WorkspaceWorkException>(() => session.Work.Read());
        Assert.Equal("The workspace operation could not be completed.", error.Message);
        Assert.Null(error.InnerException);
        session.Dispose();

        var result = _store.Open(WorkspacePath, _passphrase);
        Assert.Equal(WorkspaceOpenStatus.InvalidPassphraseOrStore, result.Status);
        Assert.Null(result.Session);
    }

    [Fact]
    public void ArchivedProjectWithPersistedTodayTaskIsRejectedOnOpenAndRead()
    {
        var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;
        var project = session.Work.CreateProject("Project", "", session.Work.Read().Categories[0].Id, null);
        var task = session.Work.CreateTask(project.Id, "Task");
        session.Work.ArchiveProject(project.Id);
        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO today_tasks (task_id,lane) VALUES ($id,'planned');";
            command.Parameters.AddWithValue("$id", task.Id);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        var error = Assert.Throws<WorkspaceWorkException>(() => session.Work.Read());
        Assert.Equal("The workspace operation could not be completed.", error.Message);
        Assert.Null(error.InnerException);
        session.Dispose();

        var result = _store.Open(WorkspacePath, _passphrase);
        Assert.Equal(WorkspaceOpenStatus.InvalidPassphraseOrStore, result.Status);
        Assert.Null(result.Session);
    }

    [Theory]
    [InlineData("projects", "target_date", "private-invalid-date")]
    [InlineData("projects", "target_date", "2027-02-29")]
    [InlineData("tasks", "due_date", "2030-13-01")]
    [InlineData("tasks", "due_date", "2030-01-01T12:00:00")]
    public void InvalidPersistedDatesAreRejectedOnOpenAndReadWithoutExposingValues(string table, string column, string value)
    {
        var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;
        var project = session.Work.CreateProject("Project", "", session.Work.Read().Categories[0].Id, null);
        session.Work.CreateTask(project.Id, "Task");
        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite))
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE {table} SET {column}=$value;";
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
            Assert.Equal("ok", EncryptedWorkspaceStore.ExecuteScalar<string>(connection, "PRAGMA integrity_check;"));
        }
        var error = Assert.Throws<WorkspaceWorkException>(() => session.Work.Read());
        Assert.Equal("The workspace operation could not be completed.", error.Message);
        Assert.Null(error.InnerException);
        session.Dispose();
        var before = File.ReadAllBytes(WorkspacePath);
        var result = _store.Open(WorkspacePath, _passphrase);
        Assert.Equal(WorkspaceOpenStatus.InvalidPassphraseOrStore, result.Status);
        Assert.Null(result.Session);
        Assert.Equal(before, File.ReadAllBytes(WorkspacePath));
    }

    [Theory]
    [InlineData("invalid-instant", "2026-09-29")]
    [InlineData("2026-09-29T12:00:00.0000000+00:00", "2026-02-29")]
    public void InvalidPersistedCompletionPairIsRejectedOnOpenAndRead(string instant, string date)
    {
        var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;
        var project = session.Work.CreateProject("Project", "", session.Work.Read().Categories[0].Id, null);
        session.Work.CreateTask(project.Id, "Task");
        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE tasks SET completion_instant=$instant, completion_date=$date;";
            command.Parameters.AddWithValue("$instant", instant);
            command.Parameters.AddWithValue("$date", date);
            command.ExecuteNonQuery();
        }
        Assert.Throws<WorkspaceWorkException>(() => session.Work.Read());
        session.Dispose();
        Assert.Equal(WorkspaceOpenStatus.InvalidPassphraseOrStore, _store.Open(WorkspacePath, _passphrase).Status);
    }

    [Fact]
    public void ParticipantIdentityAssociationsRenameAndReferencedDeletePersistAcrossReopen()
    {
        string participantId;
        string taskId;
        using (var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!)
        {
            var category = Assert.Single(session.Work.Read().Categories);
            var participant = session.Work.CreateParticipant("  SD  ");
            participantId = participant.Id;
            var second = session.Work.CreateParticipant("AB");
            var task = session.Work.CreateStandaloneTask("Call", "", category.Id, null,
                new([participant.Id, second.Id], []));
            taskId = task.Id;
            Assert.Equal([participant.Id, second.Id], task.Participants);
            Assert.Throws<InvalidOperationException>(() => session.Work.DeleteParticipant(participant.Id));

            var renamed = session.Work.RenameParticipant(participant.Id, "Ste");
            Assert.Equal(participant.Id, renamed.Id);
            Assert.Equal("Ste", renamed.Label);
            Assert.Equal([participant.Id, second.Id], session.Work.Read().Tasks.Single().Participants);
            session.Work.UpdateTask(task.Id, "Call again", "", category.Id, null);
            Assert.Equal([participant.Id, second.Id], session.Work.Read().Tasks.Single().Participants);
        }

        using var reopened = _store.Open(WorkspacePath, _passphrase).Session!;
        var snapshot = reopened.Work.Read();
        Assert.Equal("Ste", snapshot.Participants.Single(item => item.Id == participantId).Label);
        var taskAfterReopen = snapshot.Tasks.Single(item => item.Id == taskId);
        Assert.Contains(participantId, taskAfterReopen.Participants);
        reopened.Work.UpdateTask(taskId, taskAfterReopen.Title, taskAfterReopen.Description,
            taskAfterReopen.ExplicitCategoryId, taskAfterReopen.DueDate,
            new(taskAfterReopen.Participants.Where(id => id != participantId).ToArray(), []));
        reopened.Work.DeleteParticipant(participantId);
        Assert.DoesNotContain(reopened.Work.Read().Participants, item => item.Id == participantId);
    }

    [Fact]
    public void ParticipantLabelsAreRequiredAndEquivalentLabelsAreRejectedAtomically()
    {
        using var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;
        var first = session.Work.CreateParticipant("SD");
        var second = session.Work.CreateParticipant("AB");

        Assert.Throws<ArgumentException>(() => session.Work.CreateParticipant("  "));
        Assert.Throws<ArgumentException>(() => session.Work.CreateParticipant("  sd  "));
        Assert.Throws<ArgumentException>(() => session.Work.CreateParticipant("ＳＤ"));
        Assert.Throws<ArgumentException>(() => session.Work.RenameParticipant(second.Id, " sD "));
        Assert.Equal("SD", session.Work.Read().Participants.Single(item => item.Id == first.Id).Label);
        Assert.Equal("AB", session.Work.Read().Participants.Single(item => item.Id == second.Id).Label);
        Assert.Throws<ArgumentException>(() => session.Work.RenameParticipant(second.Id, "\t"));
    }

    [Fact]
    public void DuplicateParticipantInTaskDraftRollsBackTaskAndAssociations()
    {
        using var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;
        var category = Assert.Single(session.Work.Read().Categories);
        var participant = session.Work.CreateParticipant("SD");
        var task = session.Work.CreateStandaloneTask("Original", "", category.Id, null,
            new([participant.Id], []));

        Assert.Throws<ArgumentException>(() => session.Work.UpdateTask(
            task.Id, "Changed", "changed", category.Id, null,
            new([participant.Id], [" sd "])));

        var unchanged = session.Work.Read();
        Assert.Single(unchanged.Participants);
        Assert.Equal("Original", Assert.Single(unchanged.Tasks).Title);
        Assert.Equal([participant.Id], unchanged.Tasks.Single().Participants);
    }

    [Fact]
    public void TaskSaveCreatesNewParticipantAndAssociationInOneTransaction()
    {
        using var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;
        var category = Assert.Single(session.Work.Read().Categories);
        var task = session.Work.CreateStandaloneTask("Call", "", category.Id, null,
            new([], ["SD"]));

        var snapshot = session.Work.Read();
        var participant = Assert.Single(snapshot.Participants);
        Assert.Equal("SD", participant.Label);
        Assert.Equal([participant.Id], snapshot.Tasks.Single(item => item.Id == task.Id).Participants);
    }

    [Fact]
    public void NewParticipantAndTaskCreationRollBackTogetherWhenAssociationFails()
    {
        using var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;
        var category = Assert.Single(session.Work.Read().Categories);
        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_association BEFORE INSERT ON task_participants BEGIN SELECT RAISE(ABORT, 'private'); END;";
            command.ExecuteNonQuery();
        }

        Assert.Throws<WorkspaceWorkException>(() => session.Work.CreateStandaloneTask(
            "Call", "private description", category.Id, null, new([], ["SD"])));
        var unchanged = session.Work.Read();
        Assert.Empty(unchanged.Tasks);
        Assert.Empty(unchanged.Participants);
    }

    [Fact]
    public void ParticipantSchemaContainsIdentityLabelUniqueComparisonKeyAndAssociationKeys()
    {
        using var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;
        using var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadOnly);
        Assert.Equal("id,label,comparison_key", EncryptedWorkspaceStore.ExecuteScalar<string>(connection,
            "SELECT group_concat(name, ',') FROM pragma_table_info('participants');"));
        Assert.Equal("task_id,participant_id,position", EncryptedWorkspaceStore.ExecuteScalar<string>(connection,
            "SELECT group_concat(name, ',') FROM pragma_table_info('task_participants');"));
    }

    [Fact]
    public void ParticipantComparisonKeyMismatchIsRejectedWhenOpeningTheWorkspace()
    {
        using (var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!)
            session.Work.CreateParticipant("SD");
        using (var connection = EncryptedWorkspaceStore.OpenConnection(
                   WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE participants SET comparison_key='WRONG';";
            command.ExecuteNonQuery();
        }

        Assert.Equal(
            WorkspaceOpenStatus.InvalidPassphraseOrStore,
            _store.Open(WorkspacePath, _passphrase).Status);
    }

    [Fact]
    public void AssociationFailureRollsBackTaskFieldsAndParticipantReferences()
    {
        using var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;
        var category = Assert.Single(session.Work.Read().Categories);
        var first = session.Work.CreateParticipant("First");
        var rejected = session.Work.CreateParticipant("Rejected");
        var task = session.Work.CreateStandaloneTask("Original", "", category.Id, null, new([first.Id], []));
        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite))
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TRIGGER reject_association BEFORE INSERT ON task_participants WHEN NEW.participant_id='{rejected.Id}' BEGIN SELECT RAISE(ABORT, 'private'); END;";
            command.ExecuteNonQuery();
        }

        Assert.Throws<WorkspaceWorkException>(() => session.Work.UpdateTask(task.Id, "Changed", "changed", category.Id,
            new DateOnly(2030, 1, 1), new([rejected.Id], [])));
        var unchanged = Assert.Single(session.Work.Read().Tasks);
        Assert.Equal("Original", unchanged.Title);
        Assert.Empty(unchanged.Description);
        Assert.Null(unchanged.DueDate);
        Assert.Equal([first.Id], unchanged.Participants);
    }

    [Fact]
    public void RenameFailureRollsBackTheParticipantLabel()
    {
        using var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;
        var participant = session.Work.CreateParticipant("SD");
        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_rename BEFORE UPDATE ON participants BEGIN SELECT RAISE(ABORT, 'private'); END;";
            command.ExecuteNonQuery();
        }

        Assert.Throws<WorkspaceWorkException>(() => session.Work.RenameParticipant(participant.Id, "Changed"));
        Assert.Equal("SD", Assert.Single(session.Work.Read().Participants).Label);
    }

    [Fact]
    public void ReleasedSchemaFiveMigratesToParticipantsWithoutChangingExistingWork()
    {
        Directory.CreateDirectory(_directory);
        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWriteCreate))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE categories(id TEXT NOT NULL PRIMARY KEY,name TEXT NOT NULL COLLATE NOCASE UNIQUE,position INTEGER NOT NULL CHECK(position>=0)); CREATE INDEX ix_categories_position ON categories(position); INSERT INTO categories VALUES ('home','Home',0);"
                + SqliteWorkspaceWork.SchemaFive
                + "INSERT INTO projects VALUES ('project','Garden','','home',NULL,0); INSERT INTO tasks VALUES ('task','project','Dig','',NULL,NULL,0,0,NULL,NULL);";
            command.ExecuteNonQuery();
        }

        using var session = _store.Open(WorkspacePath, _passphrase).Session!;
        Assert.Equal(EncryptedWorkspaceStore.CurrentSchemaVersion, session.SchemaVersion);
        Assert.Equal("Dig", Assert.Single(session.Work.Read().Tasks).Title);
        using var migrated = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadOnly);
        Assert.Equal("id,label,comparison_key", EncryptedWorkspaceStore.ExecuteScalar<string>(migrated,
            "SELECT group_concat(name, ',') FROM pragma_table_info('participants');"));
        Assert.Empty(session.Work.Read().Participants);
    }

    [Fact]
    public void MalformedSchemaFiveCompletionDataIsRejectedBeforeMigration()
    {
        Directory.CreateDirectory(_directory);
        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWriteCreate))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE categories(id TEXT NOT NULL PRIMARY KEY,name TEXT NOT NULL COLLATE NOCASE UNIQUE,position INTEGER NOT NULL CHECK(position>=0)); CREATE INDEX ix_categories_position ON categories(position); INSERT INTO categories VALUES ('home','Home',0);"
                + SqliteWorkspaceWork.SchemaFive
                + "INSERT INTO projects VALUES ('project','Garden','','home',NULL,0); INSERT INTO tasks VALUES ('task','project','Dig','',NULL,NULL,0,0,'not-an-instant','2026-10-03');";
            command.ExecuteNonQuery();
        }

        Assert.Equal(WorkspaceOpenStatus.InvalidPassphraseOrStore, _store.Open(WorkspacePath, _passphrase).Status);
    }

    [Fact]
    public void MoveTaskToBinExcludesItFromSnapshotAndArchiveSearchThenRestoresItsStateAndOrders()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 5, 9, 30, 0, TimeSpan.Zero));
        var store = new EncryptedWorkspaceStore(new SystemIdentifierGenerator(), new WorkspaceFileOperations(), time);
        string targetId;
        string participantId;
        using (var session = store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!)
        {
            var category = Assert.Single(session.Work.Read().Categories);
            var project = session.Work.CreateProject("Garden", "", category.Id, null);
            participantId = session.Work.CreateParticipant("SD").Id;
            var first = session.Work.CreateTask(project.Id, "First");
            var target = session.Work.CreateTask(project.Id, "Private tulip note");
            var last = session.Work.CreateTask(project.Id, "Last");
            targetId = target.Id;
            session.Work.UpdateTask(target.Id, target.Title, "Sensitive archive words", category.Id,
                new DateOnly(2026, 10, 8), new([participantId], []));
            session.Work.SetTaskTodayLane(target.Id, TodayLane.InProgress);

            var removed = session.Work.MoveTaskToBin(target.Id);

            Assert.Equal(time.GetUtcNow(), removed.RemovedAt);
            Assert.Equal(TodayLane.InProgress, removed.Task.TodayLane);
            Assert.DoesNotContain(session.Work.Read().Tasks, task => task.Id == target.Id);
            Assert.Empty(session.Work.SearchArchive("Sensitive"));
            Assert.Equal([last.Id, first.Id], session.Work.Read().Tasks.Select(task => task.Id));
            Assert.Equal([first.Id, last.Id], session.Work.Read().Tasks.OrderBy(task => task.ProjectPosition).Select(task => task.Id));
            Assert.Throws<InvalidOperationException>(() => session.Work.UpdateTask(target.Id, "Changed", "", category.Id, null));
        }

        time.SetUtcNow(time.GetUtcNow().AddHours(1));
        using var reopened = store.Open(WorkspacePath, _passphrase).Session!;
        var binned = Assert.Single(reopened.Work.ReadTaskBin());
        Assert.Equal(targetId, binned.Task.Id);
        Assert.Equal(participantId, Assert.Single(binned.Task.Participants));
        Assert.Equal(new DateOnly(2026, 10, 8), binned.Task.DueDate);
        Assert.Equal(TodayLane.InProgress, binned.Task.TodayLane);

        var restored = reopened.Work.RestoreTaskFromBin(targetId);

        Assert.Equal(TodayLane.InProgress, restored.TodayLane);
        Assert.Equal(participantId, Assert.Single(restored.Participants));
        Assert.Empty(reopened.Work.ReadTaskBin());
        Assert.Equal(["Last", "Private tulip note", "First"], reopened.Work.Read().Tasks.Select(task => task.Title));
        Assert.Equal(["First", "Private tulip note", "Last"], reopened.Work.Read().Tasks
            .OrderBy(task => task.ProjectPosition).Select(task => task.Title));
    }

    [Fact]
    public void BinnedArchivedTaskRetainsCompletionAndArchiveButLeavesSearchUntilRestored()
    {
        using var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;
        var category = Assert.Single(session.Work.Read().Categories);
        var task = session.Work.CreateStandaloneTask("Historic receipt", "Searchable lavender", category.Id, null);
        session.Work.CompleteTask(task.Id);
        var archived = session.Work.ArchiveTask(task.Id);
        Assert.Equal(task.Id, Assert.Single(session.Work.SearchArchive("lavender")).Id);

        session.Work.MoveTaskToBin(task.Id);

        Assert.Empty(session.Work.SearchArchive("lavender"));
        var binned = Assert.Single(session.Work.ReadTaskBin()).Task;
        Assert.Equal(archived.CompletedAt, binned.CompletedAt);
        Assert.Equal(archived.CompletionDate, binned.CompletionDate);
        Assert.Equal(archived.ArchivedAt, binned.ArchivedAt);
        Assert.Equal(archived.ArchiveDate, binned.ArchiveDate);

        var restored = session.Work.RestoreTaskFromBin(task.Id);
        Assert.True(restored.IsComplete);
        Assert.True(restored.IsArchived);
        Assert.Equal(task.Id, Assert.Single(session.Work.SearchArchive("lavender")).Id);
    }

    [Fact]
    public void ReadTaskBinPersistsMultipleRemovalsNewestFirstAcrossReopen()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero));
        var store = new EncryptedWorkspaceStore(new SystemIdentifierGenerator(), new WorkspaceFileOperations(), time);
        using (var session = store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!)
        {
            var category = Assert.Single(session.Work.Read().Categories);
            var first = session.Work.CreateStandaloneTask("First removed", "", category.Id, null);
            var second = session.Work.CreateStandaloneTask("Second removed", "", category.Id, null);
            session.Work.MoveTaskToBin(first.Id);
            time.SetUtcNow(new DateTimeOffset(2026, 10, 5, 11, 30, 0, TimeSpan.Zero));
            session.Work.MoveTaskToBin(second.Id);
        }

        using var reopened = store.Open(WorkspacePath, _passphrase).Session!;

        Assert.Equal(["Second removed", "First removed"], reopened.Work.ReadTaskBin().Select(item => item.Task.Title));
        Assert.Equal(
            [new DateTimeOffset(2026, 10, 5, 11, 30, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero)],
            reopened.Work.ReadTaskBin().Select(item => item.RemovedAt));
    }

    [Fact]
    public void RestoreTaskFromBinRejectsBinnedParentWithoutChangingTaskBin()
    {
        using var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;
        var project = session.Work.CreateProject("Garden", "", session.Work.Read().Categories[0].Id, null);
        var task = session.Work.CreateTask(project.Id, "Dig");
        session.Work.MoveTaskToBin(task.Id);
        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO project_bins (project_id,removed_instant) VALUES ($id,$instant);";
            command.Parameters.AddWithValue("$id", project.Id);
            command.Parameters.AddWithValue("$instant", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        var blocked = Assert.Single(session.Work.ReadTaskBin());
        Assert.Equal("Garden", blocked.ProjectTitle);
        Assert.Equal("Home", blocked.CategoryName);
        Assert.False(blocked.CanRestore);
        Assert.Contains("parent Project", blocked.RestoreBlockedReason!, StringComparison.Ordinal);

        var exception = Assert.Throws<InvalidOperationException>(() => session.Work.RestoreTaskFromBin(task.Id));

        Assert.Contains("parent Project", exception.Message, StringComparison.Ordinal);
        Assert.Equal(task.Id, Assert.Single(session.Work.ReadTaskBin()).Task.Id);
    }

    [Fact]
    public void MoveAndRestoreTaskFromBinRollBackAtomicallyWhenPersistenceFails()
    {
        using var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;
        var project = session.Work.CreateProject("Garden", "", session.Work.Read().Categories[0].Id, null);
        var first = session.Work.CreateTask(project.Id, "First");
        var task = session.Work.CreateTask(project.Id, "Keep");
        var last = session.Work.CreateTask(project.Id, "Last");
        session.Work.SetTaskTodayLane(task.Id, TodayLane.Planned);
        var originalSharedOrder = session.Work.Read().Tasks.Select(item => item.Id).ToArray();
        var originalProjectOrder = session.Work.Read().Tasks.OrderBy(item => item.ProjectPosition).Select(item => item.Id).ToArray();
        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_bin_rewrite BEFORE UPDATE OF shared_position ON tasks BEGIN SELECT RAISE(ABORT, 'private'); END;";
            command.ExecuteNonQuery();
        }

        Assert.Throws<WorkspaceWorkException>(() => session.Work.MoveTaskToBin(task.Id));
        Assert.Equal(TodayLane.Planned, session.Work.Read().Tasks.Single(item => item.Id == task.Id).TodayLane);
        Assert.Equal(originalSharedOrder, session.Work.Read().Tasks.Select(item => item.Id));
        Assert.Equal(originalProjectOrder, session.Work.Read().Tasks
            .OrderBy(item => item.ProjectPosition).Select(item => item.Id));
        Assert.Empty(session.Work.ReadTaskBin());

        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER reject_bin_rewrite;";
            command.ExecuteNonQuery();
        }
        session.Work.MoveTaskToBin(task.Id);
        var activeSharedOrder = session.Work.Read().Tasks.Select(item => item.Id).ToArray();
        var activeProjectOrder = session.Work.Read().Tasks.OrderBy(item => item.ProjectPosition).Select(item => item.Id).ToArray();
        Assert.Equal([last.Id, first.Id], activeSharedOrder);
        Assert.Equal([first.Id, last.Id], activeProjectOrder);
        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_restore_today BEFORE INSERT ON today_tasks BEGIN SELECT RAISE(ABORT, 'private'); END;";
            command.ExecuteNonQuery();
        }

        Assert.Throws<WorkspaceWorkException>(() => session.Work.RestoreTaskFromBin(task.Id));
        Assert.DoesNotContain(session.Work.Read().Tasks, item => item.Id == task.Id);
        Assert.Equal(activeSharedOrder, session.Work.Read().Tasks.Select(item => item.Id));
        Assert.Equal(activeProjectOrder, session.Work.Read().Tasks
            .OrderBy(item => item.ProjectPosition).Select(item => item.Id));
        var binned = Assert.Single(session.Work.ReadTaskBin());
        Assert.Equal(task.Id, binned.Task.Id);
        Assert.Equal(TodayLane.Planned, binned.Task.TodayLane);
    }

    [Fact]
    public void RestoreTaskFromBinUsesSurvivingNeighboursAndFallsBackToEndInBothOrders()
    {
        using var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;
        var project = session.Work.CreateProject("Garden", "", session.Work.Read().Categories[0].Id, null);
        var first = session.Work.CreateTask(project.Id, "First");
        var target = session.Work.CreateTask(project.Id, "Target");
        var third = session.Work.CreateTask(project.Id, "Third");
        session.Work.MoveTaskToBin(target.Id);
        var newest = session.Work.CreateTask(project.Id, "Newest");

        session.Work.RestoreTaskFromBin(target.Id);

        Assert.Equal([newest.Id, third.Id, target.Id, first.Id], session.Work.Read().Tasks.Select(task => task.Id));
        Assert.Equal([first.Id, target.Id, third.Id, newest.Id], session.Work.Read().Tasks
            .OrderBy(task => task.ProjectPosition).Select(task => task.Id));

        session.Work.MoveTaskToBin(target.Id);
        session.Work.MoveTaskToBin(first.Id);
        session.Work.MoveTaskToBin(third.Id);
        session.Work.RestoreTaskFromBin(target.Id);

        Assert.Equal([newest.Id, target.Id], session.Work.Read().Tasks.Select(task => task.Id));
        Assert.Equal([target.Id, newest.Id], session.Work.Read().Tasks
            .OrderBy(task => task.ProjectPosition).Select(task => task.Id));

        session.Work.MoveTaskToBin(target.Id);
        session.Work.MoveTaskToBin(newest.Id);
        session.Work.RestoreTaskFromBin(target.Id);

        Assert.Equal([target.Id], session.Work.Read().Tasks.Select(task => task.Id));
        Assert.Equal([target.Id], session.Work.Read().Tasks
            .OrderBy(task => task.ProjectPosition).Select(task => task.Id));
    }

    [Fact]
    public void RestoreTaskFromBinChoosesNearestSurvivingAnchorAcrossBothDirections()
    {
        using var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;
        var project = session.Work.CreateProject("Garden", "", session.Work.Read().Categories[0].Id, null);
        var first = session.Work.CreateTask(project.Id, "First");
        var removedNeighbour = session.Work.CreateTask(project.Id, "Removed neighbour");
        var target = session.Work.CreateTask(project.Id, "Target");
        var third = session.Work.CreateTask(project.Id, "Third");
        var fourth = session.Work.CreateTask(project.Id, "Fourth");
        session.Work.MoveTaskToBin(target.Id);
        session.Work.MoveTaskToBin(removedNeighbour.Id);
        session.Work.MoveTaskInProject(project.Id, first.Id, 2);

        Assert.Equal([third.Id, fourth.Id, first.Id], session.Work.Read().Tasks
            .OrderBy(task => task.ProjectPosition).Select(task => task.Id));

        session.Work.RestoreTaskFromBin(target.Id);

        Assert.Equal([fourth.Id, third.Id, target.Id, first.Id],
            session.Work.Read().Tasks.Select(task => task.Id));
        Assert.Equal([target.Id, third.Id, fourth.Id, first.Id], session.Work.Read().Tasks
            .OrderBy(task => task.ProjectPosition).Select(task => task.Id));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
