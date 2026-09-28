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
        Assert.Throws<ArgumentException>(() => session.Work.MoveTaskInSharedOrder("missing", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Work.MoveTaskInSharedOrder(task.Id, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Work.MoveTaskInSharedOrder(task.Id, 2));
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
            Assert.Equal(4, session.SchemaVersion);
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
    public void OpenRejectsSchemaFourWithMissingConstraintsOrChangedTypes(string original, string replacement)
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

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
