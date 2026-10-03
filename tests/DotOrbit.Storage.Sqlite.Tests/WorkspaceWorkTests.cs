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
            Assert.Equal(6, session.SchemaVersion);
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
    public void OpenRejectsSchemaSixWithMissingConstraintsOrChangedTypes(string original, string replacement)
    {
        _store.Create(WorkspacePath, _passphrase, CategoryName.Create("Home").CategoryName!).Session!.Dispose();
        using (var connection = EncryptedWorkspaceStore.OpenConnection(WorkspacePath, _passphrase, SqliteOpenMode.ReadWrite))
        {
            using var command = connection.CreateCommand();
            // Rebuild the empty work tables with valid SQL that weakens one schema guarantee.
            command.CommandText = "DROP TABLE task_participants; DROP TABLE participants; DROP TABLE tasks; DROP TABLE projects;" +
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
        Assert.Equal(6, session.SchemaVersion);
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

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
