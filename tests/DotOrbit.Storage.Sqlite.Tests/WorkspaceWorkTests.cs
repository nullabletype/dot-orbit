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
    public void CompletionFailureRollsBackBothCapturedFieldsWithoutExposingPrivateDatabaseDetails()
    {
        using var session = _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!;
        var project = session.Work.CreateProject("Garden", "", session.Work.Read().Categories[0].Id, null);
        var task = session.Work.CreateTask(project.Id, "Private task");
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

        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER reject_completion;";
            command.ExecuteNonQuery();
        }
        var completed = session.Work.CompleteTask(task.Id);
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
            Assert.Equal(5, session.SchemaVersion);
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
    public void OpenRejectsSchemaFiveWithMissingConstraintsOrChangedTypes(string original, string replacement)
    {
        _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!.Dispose();
        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite))
        {
            using var command = connection.CreateCommand();
            // Rebuild the empty work tables with valid SQL that weakens one schema guarantee.
            command.CommandText = "DROP TABLE tasks; DROP TABLE projects;" +
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

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
