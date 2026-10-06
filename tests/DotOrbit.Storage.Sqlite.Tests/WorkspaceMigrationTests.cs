using System.Globalization;
using System.Text.Json;
using DotOrbit.Core.Workspaces;
using DotOrbit.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DotOrbit.Storage.Sqlite.Tests;

public sealed class WorkspaceMigrationTests
{
    private const string ValidPassphrase = "correct horse battery";

    [Fact]
    public void CreatePublishesTheCurrentSchemaWithTheOrderedCategoryAndArchiveSearchIndexes()
    {
        using var fixture = new MigrationFixture();

        using var session = fixture.CreateCurrentWorkspace();

        Assert.Equal(EncryptedWorkspaceStore.CurrentSchemaVersion, session.SchemaVersion);
        using var connection = OpenInspectionConnection(fixture.WorkspacePath, ValidPassphrase);
        Assert.Equal(EncryptedWorkspaceStore.CurrentSchemaVersion, ExecuteScalar<long>(connection, "PRAGMA user_version;"));
        Assert.Equal(
            "index",
            ExecuteScalar<string>(
                connection,
                "SELECT type FROM sqlite_schema WHERE name = 'ix_categories_position';"));
        Assert.Contains(
            "CREATE VIRTUAL TABLE archive_search USING fts5",
            ExecuteScalar<string>(connection, "SELECT sql FROM sqlite_schema WHERE name = 'archive_search';"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void OpenUpgradesTheReleasedSchemaOneFixtureAndPublishesAValidatedRecoveryPoint()
    {
        using var fixture = new MigrationFixture();
        fixture.CreateSchemaOneWorkspace("Personal Admin");

        var result = fixture.Store.Open(fixture.WorkspacePath, UnlockPassphrase());
        using var session = result.Session;

        Assert.Equal(WorkspaceOpenStatus.Opened, result.Status);
        Assert.NotNull(session);
        Assert.Equal(EncryptedWorkspaceStore.CurrentSchemaVersion, session.SchemaVersion);
        Assert.Equal("Personal Admin", session.FirstCategoryName);
        var recoveryPath = Assert.Single(
            Directory.GetFiles(
                fixture.DirectoryPath,
                "dot-orbit-pre-migration-v1-*.dotorbit-recovery"));
        AssertSchemaOneWorkspace(recoveryPath, "Personal Admin");

        using var migrated = OpenInspectionConnection(fixture.WorkspacePath, ValidPassphrase);
        Assert.Equal(EncryptedWorkspaceStore.CurrentSchemaVersion, ExecuteScalar<long>(migrated, "PRAGMA user_version;"));
        Assert.Equal(
            1L,
            ExecuteScalar<long>(
                migrated,
                "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'index' AND name = 'ix_categories_position';"));
    }

    [Fact]
    public void OpenUpgradesReleasedSchemaThreeTasksWithoutChangingMembershipOrRelativeOrder()
    {
        using var fixture = new MigrationFixture();
        fixture.CreateSchemaThreeWorkspaceWithTasks();

        var result = fixture.Store.Open(fixture.WorkspacePath, UnlockPassphrase());
        using var session = result.Session;

        Assert.Equal(WorkspaceOpenStatus.Opened, result.Status);
        Assert.Equal(EncryptedWorkspaceStore.CurrentSchemaVersion, session?.SchemaVersion);
        var snapshot = session!.Work.Read();
        Assert.Equal(["First", "Second"], snapshot.Tasks.Select(task => task.Title));
        Assert.All(snapshot.Tasks, task => Assert.Equal("project", task.ProjectId));
        Assert.Equal([0L, 1L], snapshot.Tasks.Select(task => task.SharedPosition));
        Assert.Single(
            Directory.GetFiles(
                fixture.DirectoryPath,
                "dot-orbit-pre-migration-v3-*.dotorbit-recovery"));
    }

    [Fact]
    public void OpenUpgradesReleasedSchemaFourWorkAsIncompleteWithoutChangingAnyOrder()
    {
        using var fixture = new MigrationFixture();
        fixture.CreateSchemaFourWorkspaceWithOrderedWork();

        var result = fixture.Store.Open(fixture.WorkspacePath, UnlockPassphrase());
        using var session = result.Session;

        Assert.Equal(WorkspaceOpenStatus.Opened, result.Status);
        Assert.Equal(EncryptedWorkspaceStore.CurrentSchemaVersion, session?.SchemaVersion);
        var snapshot = session!.Work.Read();
        Assert.Equal(["project-two", "project-one"], snapshot.Projects.Select(project => project.Id));
        Assert.Equal([0L, 1L], snapshot.Projects.Select(project => project.Position));
        Assert.Equal(
            ["one-second", "standalone", "two-first", "one-first", "two-second"],
            snapshot.Tasks.Select(task => task.Id));
        Assert.Equal([0L, 1L, 2L, 3L, 4L], snapshot.Tasks.Select(task => task.SharedPosition));
        Assert.Equal(["one-first", "one-second"], snapshot.Tasks.Where(task => task.ProjectId == "project-one")
            .OrderBy(task => task.ProjectPosition).Select(task => task.Id));
        Assert.Equal(["two-first", "two-second"], snapshot.Tasks.Where(task => task.ProjectId == "project-two")
            .OrderBy(task => task.ProjectPosition).Select(task => task.Id));
        var standalone = snapshot.Tasks.Single(task => task.Id == "standalone");
        Assert.Null(standalone.ProjectId);
        Assert.Null(standalone.ProjectPosition);
        Assert.Equal("category", standalone.ExplicitCategoryId);
        Assert.All(snapshot.Tasks, task =>
        {
            Assert.Null(task.CompletedAt);
            Assert.Null(task.CompletionDate);
        });
        Assert.Single(Directory.GetFiles(fixture.DirectoryPath, "dot-orbit-pre-migration-v4-*.dotorbit-recovery"));
    }

    [Fact]
    public void OpenUpgradesReleasedSchemaSixWithoutChangingWorkOrParticipants()
    {
        using var fixture = new MigrationFixture();
        string taskId;
        string participantId;
        using (var current = fixture.CreateCurrentWorkspace())
        {
            var category = current.Work.Read().Categories[0];
            var project = current.Work.CreateProject("Garden", "", category.Id, null);
            var task = current.Work.CreateTask(project.Id, "Dig");
            var participant = current.Work.CreateParticipant("SD");
            current.Work.UpdateTask(task.Id, task.Title, task.Description, task.ExplicitCategoryId, task.DueDate,
                new([participant.Id], []));
            taskId = task.Id;
            participantId = participant.Id;
        }
        fixture.DowngradeCurrentToSchemaSix();

        var result = fixture.Store.Open(fixture.WorkspacePath, UnlockPassphrase());
        using var session = result.Session;

        Assert.Equal(WorkspaceOpenStatus.Opened, result.Status);
        Assert.Equal(EncryptedWorkspaceStore.CurrentSchemaVersion, session?.SchemaVersion);
        var restored = Assert.Single(session!.Work.Read().Tasks);
        Assert.Equal(taskId, restored.Id);
        Assert.Equal([participantId], restored.Participants);
        Assert.Null(restored.TodayLane);
        Assert.Single(Directory.GetFiles(fixture.DirectoryPath, "dot-orbit-pre-migration-v6-*.dotorbit-recovery"));
    }

    [Fact]
    public void OpenUpgradesReleasedSchemaSevenWithoutChangingWorkParticipantsCompletionOrToday()
    {
        using var fixture = new MigrationFixture();
        string completedId;
        string plannedId;
        string participantId;
        using (var current = fixture.CreateCurrentWorkspace())
        {
            var category = current.Work.Read().Categories[0];
            var project = current.Work.CreateProject("Garden", "", category.Id, null);
            var participant = current.Work.CreateParticipant("SD");
            var completed = current.Work.CreateTask(project.Id, "Filed receipt");
            current.Work.UpdateTask(completed.Id, completed.Title, completed.Description, completed.ExplicitCategoryId,
                completed.DueDate, new([participant.Id], []));
            current.Work.CompleteTask(completed.Id);
            var planned = current.Work.CreateStandaloneTask("Plan", "", category.Id, new DateOnly(2026, 10, 8));
            current.Work.SetTaskTodayLane(planned.Id, TodayLane.Planned);
            completedId = completed.Id;
            plannedId = planned.Id;
            participantId = participant.Id;
        }
        fixture.DowngradeCurrentToSchemaSeven();

        var result = fixture.Store.Open(fixture.WorkspacePath, UnlockPassphrase());
        using var session = result.Session;

        Assert.Equal(WorkspaceOpenStatus.Opened, result.Status);
        Assert.Equal(EncryptedWorkspaceStore.CurrentSchemaVersion, session?.SchemaVersion);
        var snapshot = session!.Work.Read();
        var completedTask = snapshot.Tasks.Single(task => task.Id == completedId);
        Assert.True(completedTask.IsComplete);
        Assert.False(completedTask.IsArchived);
        Assert.Equal([participantId], completedTask.Participants);
        Assert.Equal(TodayLane.Planned, snapshot.Tasks.Single(task => task.Id == plannedId).TodayLane);
        Assert.Single(Directory.GetFiles(fixture.DirectoryPath, "dot-orbit-pre-migration-v7-*.dotorbit-recovery"));
        using var connection = OpenInspectionConnection(fixture.WorkspacePath, ValidPassphrase);
        Assert.Equal(EncryptedWorkspaceStore.CurrentSchemaVersion, ExecuteScalar<long>(connection, "PRAGMA user_version;"));
        Assert.Equal("task_id,archived_instant,archive_date", ExecuteScalar<string>(connection,
            "SELECT group_concat(name, ',') FROM pragma_table_info('task_archives');"));
    }

    [Fact]
    public void OpenUpgradesReleasedSchemaEightWithoutChangingTaskArchiveState()
    {
        using var fixture = new MigrationFixture();
        string archivedTaskId;
        using (var current = fixture.CreateCurrentWorkspace())
        {
            var category = current.Work.Read().Categories[0];
            var project = current.Work.CreateProject("Garden", "", category.Id, null);
            var task = current.Work.CreateTask(project.Id, "Filed receipt");
            current.Work.CompleteTask(task.Id);
            current.Work.ArchiveTask(task.Id);
            archivedTaskId = task.Id;
        }
        fixture.DowngradeCurrentToSchemaEight();

        var result = fixture.Store.Open(fixture.WorkspacePath, UnlockPassphrase());
        using var session = result.Session;

        Assert.Equal(WorkspaceOpenStatus.Opened, result.Status);
        Assert.Equal(EncryptedWorkspaceStore.CurrentSchemaVersion, session?.SchemaVersion);
        var snapshot = session!.Work.Read();
        Assert.True(snapshot.Tasks.Single(task => task.Id == archivedTaskId).IsArchived);
        Assert.All(snapshot.Projects, project => Assert.False(project.IsArchived));
        Assert.Single(Directory.GetFiles(fixture.DirectoryPath, "dot-orbit-pre-migration-v8-*.dotorbit-recovery"));
        using var connection = OpenInspectionConnection(fixture.WorkspacePath, ValidPassphrase);
        Assert.Equal(EncryptedWorkspaceStore.CurrentSchemaVersion, ExecuteScalar<long>(connection, "PRAGMA user_version;"));
        Assert.Equal("project_id,archived_instant,archive_date", ExecuteScalar<string>(connection,
            "SELECT group_concat(name, ',') FROM pragma_table_info('project_archives');"));
    }

    [Fact]
    public void OpenUpgradesSchemaNineAndRebuildsArchivedSearchDocuments()
    {
        using var fixture = new MigrationFixture();
        string projectId;
        string taskId;
        using (var current = fixture.CreateCurrentWorkspace())
        {
            var category = current.Work.Read().Categories[0];
            var project = current.Work.CreateProject("Garden history", "Archived project notes", category.Id, null);
            var task = current.Work.CreateTaskDraft(project.Id, "Filed receipt", "Blue tulip receipt", null, null);
            current.Work.CompleteTask(task.Id);
            current.Work.ArchiveTask(task.Id);
            current.Work.ArchiveProject(project.Id);
            projectId = project.Id;
            taskId = task.Id;
        }
        fixture.DowngradeCurrentToSchemaNine();

        var result = fixture.Store.Open(fixture.WorkspacePath, UnlockPassphrase());
        using var session = result.Session;

        Assert.Equal(WorkspaceOpenStatus.Opened, result.Status);
        Assert.Equal(EncryptedWorkspaceStore.CurrentSchemaVersion, session?.SchemaVersion);
        Assert.Equal(projectId, Assert.Single(session!.Work.SearchArchive("project notes")).Id);
        Assert.Equal(taskId, Assert.Single(session.Work.SearchArchive("tulip")).Id);
        Assert.Single(Directory.GetFiles(fixture.DirectoryPath, "dot-orbit-pre-migration-v9-*.dotorbit-recovery"));
        using var connection = OpenInspectionConnection(fixture.WorkspacePath, ValidPassphrase);
        Assert.Equal(EncryptedWorkspaceStore.CurrentSchemaVersion, ExecuteScalar<long>(connection, "PRAGMA user_version;"));
        Assert.Equal(2L, ExecuteScalar<long>(connection, "SELECT COUNT(*) FROM archive_search;"));
    }

    [Fact]
    public void MigrationUsesThePersistedAutomaticRecoveryDirectory()
    {
        using var fixture = new MigrationFixture();
        fixture.CreateSchemaOneWorkspace("Home");
        fixture.ConfigureRecoveryDirectory();

        var result = fixture.Store.Open(fixture.WorkspacePath, UnlockPassphrase());
        using var session = result.Session;

        Assert.Equal(WorkspaceOpenStatus.Opened, result.Status);
        Assert.Single(
            Directory.GetFiles(
                fixture.RecoveryDirectoryPath,
                "dot-orbit-pre-migration-v1-*.dotorbit-recovery"));
        Assert.Empty(
            Directory.GetFiles(
                fixture.DirectoryPath,
                "dot-orbit-pre-migration-v1-*.dotorbit-recovery"));
    }

    [Fact]
    public void OpenCurrentSchemaDoesNotPublishAMigrationRecoveryPoint()
    {
        using var fixture = new MigrationFixture();
        fixture.CreateCurrentWorkspace().Dispose();

        var result = fixture.Store.Open(fixture.WorkspacePath, UnlockPassphrase());
        using var session = result.Session;

        Assert.Equal(WorkspaceOpenStatus.Opened, result.Status);
        Assert.Equal(EncryptedWorkspaceStore.CurrentSchemaVersion, session?.SchemaVersion);
        Assert.Empty(
            Directory.GetFiles(
                fixture.DirectoryPath,
                "dot-orbit-pre-migration-*.dotorbit-recovery"));
    }

    [Fact]
    public void UnavailableConfiguredRecoveryDirectoryBlocksMigrationWithoutFallingBack()
    {
        using var fixture = new MigrationFixture();
        fixture.CreateSchemaOneWorkspace("Home");
        File.WriteAllText(fixture.RecoveryDirectoryPath, "not a directory");
        fixture.WriteRecoveryConfiguration(fixture.RecoveryDirectoryPath);

        var result = fixture.Store.Open(fixture.WorkspacePath, UnlockPassphrase());

        Assert.Equal(WorkspaceOpenStatus.MigrationFailed, result.Status);
        Assert.Null(result.RecoveryPointPath);
        AssertSchemaOneWorkspace(fixture.WorkspacePath, "Home");
        Assert.Empty(
            Directory.GetFiles(
                fixture.DirectoryPath,
                "dot-orbit-pre-migration-*.dotorbit-recovery"));
    }

    [Fact]
    public void InvalidRecoveryStateDoesNotBlockFallbackMigration()
    {
        using var fixture = new MigrationFixture();
        fixture.CreateSchemaOneWorkspace("Home");
        File.WriteAllText(fixture.WorkspacePath + ".recovery-state.json", "{not-json");

        var result = fixture.Store.Open(fixture.WorkspacePath, UnlockPassphrase());
        using var session = result.Session;

        Assert.Equal(WorkspaceOpenStatus.Opened, result.Status);
        Assert.Equal(EncryptedWorkspaceStore.CurrentSchemaVersion, session?.SchemaVersion);
        Assert.Single(
            Directory.GetFiles(
                fixture.DirectoryPath,
                "dot-orbit-pre-migration-v1-*.dotorbit-recovery"));
    }

    [Fact]
    public void InterruptionAfterRecoveryPublicationLeavesSchemaOneAndTheRecoveryUsable()
    {
        using var fixture = new MigrationFixture(
            (checkpoint, _) =>
            {
                if (checkpoint == WorkspaceMigrationCheckpoint.RecoveryPointValidated)
                {
                    throw new IOException("Injected interruption after recovery publication.");
                }
            });
        fixture.CreateSchemaOneWorkspace("Home");

        var result = fixture.Store.Open(fixture.WorkspacePath, UnlockPassphrase());

        Assert.Equal(WorkspaceOpenStatus.MigrationFailed, result.Status);
        var recoveryPath = Assert.IsType<string>(result.RecoveryPointPath);
        AssertSchemaOneWorkspace(fixture.WorkspacePath, "Home");
        AssertSchemaOneWorkspace(recoveryPath, "Home");
    }

    [Fact]
    public void FailureBeforeCommitRollsBackAndReturnsTheRecoveryPointForRestoration()
    {
        using var fixture = new MigrationFixture(
            (checkpoint, _) =>
            {
                if (checkpoint == WorkspaceMigrationCheckpoint.BeforeCommit)
                {
                    throw new IOException("Injected migration interruption.");
                }
            });
        fixture.CreateSchemaOneWorkspace("Home");

        var result = fixture.Store.Open(fixture.WorkspacePath, UnlockPassphrase());

        Assert.Equal(WorkspaceOpenStatus.MigrationFailed, result.Status);
        Assert.Null(result.Session);
        Assert.NotNull(result.RecoveryPointPath);
        Assert.True(File.Exists(result.RecoveryPointPath));
        AssertSchemaOneWorkspace(fixture.WorkspacePath, "Home");
        AssertSchemaOneWorkspace(result.RecoveryPointPath, "Home");
    }

    [Fact]
    public void IntegrityValidationFailureRollsBackTheSchemaVersionAndIndex()
    {
        using var fixture = new MigrationFixture(
            integrityCheck: _ => "injected integrity failure");
        fixture.CreateSchemaOneWorkspace("Home");

        var result = fixture.Store.Open(fixture.WorkspacePath, UnlockPassphrase());

        Assert.Equal(WorkspaceOpenStatus.MigrationFailed, result.Status);
        Assert.NotNull(result.RecoveryPointPath);
        AssertSchemaOneWorkspace(fixture.WorkspacePath, "Home");
    }

    [Fact]
    public void PostMigrationReopenRefusesASchemaAdvancedAfterTheExclusiveLockIsReleased()
    {
        MigrationFixture fixture = null!;
        fixture = new MigrationFixture(
            afterMigration: () => fixture.SetSchemaVersion(EncryptedWorkspaceStore.CurrentSchemaVersion + 1));
        using (fixture)
        {
            fixture.CreateSchemaOneWorkspace("Home");

            var result = fixture.Store.Open(fixture.WorkspacePath, UnlockPassphrase());

            Assert.Equal(WorkspaceOpenStatus.UnsupportedSchema, result.Status);
            Assert.Null(result.Session);
            Assert.NotNull(result.RecoveryPointPath);
        }
    }

    [Theory]
    [InlineData("CREATE UNIQUE INDEX ix_categories_position ON categories(position);")]
    [InlineData("CREATE TABLE other (position INTEGER NOT NULL); CREATE INDEX ix_categories_position ON other(position);")]
    [InlineData("CREATE INDEX ix_categories_position ON categories((position + 0));")]
    public void MalformedSchemaTwoIndexIsRefusedWithoutChangingTheWorkspace(string indexSql)
    {
        using var fixture = new MigrationFixture();
        fixture.CreateSchemaTwoWorkspace(indexSql);
        var original = File.ReadAllBytes(fixture.WorkspacePath);

        var result = fixture.Store.Open(fixture.WorkspacePath, UnlockPassphrase());

        Assert.Equal(WorkspaceOpenStatus.InvalidPassphraseOrStore, result.Status);
        Assert.Null(result.Session);
        Assert.Equal(original, File.ReadAllBytes(fixture.WorkspacePath));
    }

    [Fact]
    public void RecoveryPublicationFailurePreventsTheFirstSchemaChange()
    {
        using var fixture = new MigrationFixture(failRecoveryPublication: true);
        fixture.CreateSchemaOneWorkspace("Home");

        var result = fixture.Store.Open(fixture.WorkspacePath, UnlockPassphrase());

        Assert.Equal(WorkspaceOpenStatus.MigrationFailed, result.Status);
        Assert.Null(result.Session);
        Assert.Null(result.RecoveryPointPath);
        AssertSchemaOneWorkspace(fixture.WorkspacePath, "Home");
    }

    [Fact]
    public void MigrationHoldsAnExclusiveStoreLockUntilValidationCompletes()
    {
        SqliteConnection? competing = null;
        using var fixture = new MigrationFixture(
            (checkpoint, _) =>
            {
                if (checkpoint != WorkspaceMigrationCheckpoint.RecoveryPointValidated)
                {
                    return;
                }

                using var command = competing!.CreateCommand();
                command.CommandText = "INSERT INTO categories (id, name, position) VALUES ('other', 'Other', 1);";
                var error = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
                Assert.Equal(5, error.SqliteErrorCode);
            });
        fixture.CreateSchemaOneWorkspace("Home");
        using (competing = OpenInspectionConnection(fixture.WorkspacePath, ValidPassphrase))
        {
            using var timeout = competing.CreateCommand();
            timeout.CommandText = "PRAGMA busy_timeout = 1;";
            timeout.ExecuteNonQuery();

            var result = fixture.Store.Open(fixture.WorkspacePath, UnlockPassphrase());
            using var session = result.Session;

            Assert.Equal(WorkspaceOpenStatus.Opened, result.Status);
            Assert.Equal(EncryptedWorkspaceStore.CurrentSchemaVersion, session?.SchemaVersion);
        }
    }

    [Fact]
    public void NewerSchemaIsRefusedWithoutPublishingRecoveryOrChangingBytes()
    {
        using var fixture = new MigrationFixture();
        fixture.CreateSchemaOneWorkspace("Home", schemaVersion: EncryptedWorkspaceStore.CurrentSchemaVersion + 1);
        var original = File.ReadAllBytes(fixture.WorkspacePath);

        var result = fixture.Store.Open(fixture.WorkspacePath, UnlockPassphrase());

        Assert.Equal(WorkspaceOpenStatus.UnsupportedSchema, result.Status);
        Assert.Null(result.Session);
        Assert.Null(result.RecoveryPointPath);
        Assert.Equal(original, File.ReadAllBytes(fixture.WorkspacePath));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.dotorbit-recovery"));
    }

    [Fact]
    public void SchemaTenMigratesTaskBinShapeToCurrentSchema()
    {
        using var fixture = new MigrationFixture();
        using (fixture.CreateCurrentWorkspace()) { }
        fixture.DowngradeCurrentToSchemaTen();

        var result = fixture.Store.Open(fixture.WorkspacePath, UnlockPassphrase());
        using var session = result.Session;

        Assert.Equal(WorkspaceOpenStatus.Opened, result.Status);
        Assert.Equal(EncryptedWorkspaceStore.CurrentSchemaVersion, session?.SchemaVersion);
        Assert.Empty(session!.Work.ReadTaskBin());
        using var connection = OpenInspectionConnection(fixture.WorkspacePath, ValidPassphrase);
        Assert.Equal("task_id,removed_instant,today_lane",
            ExecuteScalar<string>(connection, "SELECT group_concat(name, ',') FROM pragma_table_info('task_bins');"));
        Assert.Equal("task_id,scope,anchor_task_id,relative_position",
            ExecuteScalar<string>(connection, "SELECT group_concat(name, ',') FROM pragma_table_info('task_bin_order_anchors');"));
        Assert.Equal("project_id,removed_instant",
            ExecuteScalar<string>(connection, "SELECT group_concat(name, ',') FROM pragma_table_info('project_bins');"));
        Assert.Equal("project_id,task_id",
            ExecuteScalar<string>(connection, "SELECT group_concat(name, ',') FROM pragma_table_info('project_bin_tasks');"));
        Assert.Equal("project_id,anchor_project_id,relative_position",
            ExecuteScalar<string>(connection, "SELECT group_concat(name, ',') FROM pragma_table_info('project_bin_order_anchors');"));
    }

    [Fact]
    public void SchemaElevenMigratesProjectBinAggregateShapeToCurrentSchema()
    {
        using var fixture = new MigrationFixture();
        using (fixture.CreateCurrentWorkspace()) { }
        fixture.DowngradeCurrentToSchemaEleven();

        var result = fixture.Store.Open(fixture.WorkspacePath, UnlockPassphrase());
        using var session = result.Session;

        Assert.Equal(WorkspaceOpenStatus.Opened, result.Status);
        Assert.Equal(EncryptedWorkspaceStore.CurrentSchemaVersion, session?.SchemaVersion);
        Assert.Empty(session!.Work.ReadProjectBin());
        using var connection = OpenInspectionConnection(fixture.WorkspacePath, ValidPassphrase);
        Assert.Equal("project_id,task_id",
            ExecuteScalar<string>(connection, "SELECT group_concat(name, ',') FROM pragma_table_info('project_bin_tasks');"));
        Assert.Equal("project_id,anchor_project_id,relative_position",
            ExecuteScalar<string>(connection, "SELECT group_concat(name, ',') FROM pragma_table_info('project_bin_order_anchors');"));
    }

    [Fact]
    public void RestoreOfASchemaOneRecoveryPointReopensThroughTheSupportedMigration()
    {
        using var fixture = new MigrationFixture();
        using var session = fixture.CreateCurrentWorkspace();
        Directory.CreateDirectory(fixture.RecoveryDirectoryPath);
        var schemaOneRecoveryPath = Path.Combine(
            fixture.RecoveryDirectoryPath,
            "released-schema-one.dotorbit-recovery");
        fixture.CreateSchemaOneWorkspace(
            "Restored category",
            path: schemaOneRecoveryPath);

        var result = session.Recovery.Restore(
            schemaOneRecoveryPath,
            fixture.RecoveryDirectoryPath);
        using var restored = result.Session;

        Assert.Equal(WorkspaceRestoreStatus.Restored, result.Status);
        Assert.NotNull(restored);
        Assert.Equal(EncryptedWorkspaceStore.CurrentSchemaVersion, restored.SchemaVersion);
        Assert.Equal("Restored category", restored.FirstCategoryName);
        using var inspection = OpenInspectionConnection(
            fixture.WorkspacePath,
            ValidPassphrase);
        Assert.Equal(EncryptedWorkspaceStore.CurrentSchemaVersion, ExecuteScalar<long>(inspection, "PRAGMA user_version;"));
        Assert.Equal(
            1L,
            ExecuteScalar<long>(
                inspection,
                "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'ix_categories_position';"));
    }

    [Fact]
    public void FailedMigrationOffersAnActionableValidatedRestore()
    {
        SqliteConnection? competing = null;
        using var fixture = new MigrationFixture(
            (checkpoint, _) =>
            {
                if (checkpoint == WorkspaceMigrationCheckpoint.BeforeCommit)
                {
                    throw new IOException("Injected migration interruption.");
                }

                if (checkpoint == WorkspaceMigrationCheckpoint.PreRestoreRecoveryValidated)
                {
                    using var command = competing!.CreateCommand();
                    command.CommandText = "UPDATE categories SET name = 'Competing write' WHERE position = 0;";
                    var error = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
                    Assert.Equal(5, error.SqliteErrorCode);
                }
            });
        fixture.CreateSchemaOneWorkspace("Home");
        var failed = fixture.Store.Open(fixture.WorkspacePath, UnlockPassphrase());
        var recoveryPath = Assert.IsType<string>(failed.RecoveryPointPath);
        fixture.SetFirstCategory("Changed after failure");

        MigrationRecoveryRestoreResult restored;
        using (competing = OpenInspectionConnection(fixture.WorkspacePath, ValidPassphrase))
        {
            using var timeout = competing.CreateCommand();
            timeout.CommandText = "PRAGMA busy_timeout = 1;";
            timeout.ExecuteNonQuery();

            restored = fixture.Store.RestoreMigrationRecovery(
                fixture.WorkspacePath,
                UnlockPassphrase(),
                recoveryPath);
        }

        Assert.Equal(MigrationRecoveryRestoreStatus.Restored, restored.Status);
        AssertSchemaOneWorkspace(fixture.WorkspacePath, "Home");
        var preRestorePath = Assert.Single(
            Directory.GetFiles(
                fixture.DirectoryPath,
                "dot-orbit-pre-restore-v1-*.dotorbit-recovery"));
        AssertSchemaOneWorkspace(preRestorePath, "Changed after failure");
    }

    [Fact]
    public void FailureAfterRestoreApplicationDoesNotClaimTheWorkspaceWasUntouched()
    {
        using var fixture = new MigrationFixture(
            (checkpoint, _) =>
            {
                if (checkpoint == WorkspaceMigrationCheckpoint.BeforeCommit)
                {
                    throw new IOException("Injected migration interruption.");
                }

                if (checkpoint == WorkspaceMigrationCheckpoint.RestoreApplied)
                {
                    throw new InvalidDataException("Injected post-restore validation failure.");
                }
            });
        fixture.CreateSchemaOneWorkspace("Home");
        var failed = fixture.Store.Open(fixture.WorkspacePath, UnlockPassphrase());
        var recoveryPath = Assert.IsType<string>(failed.RecoveryPointPath);
        fixture.SetFirstCategory("Changed after failure");

        var restored = fixture.Store.RestoreMigrationRecovery(
            fixture.WorkspacePath,
            UnlockPassphrase(),
            recoveryPath);

        Assert.Equal(MigrationRecoveryRestoreStatus.Failed, restored.Status);
        AssertSchemaOneWorkspace(fixture.WorkspacePath, "Home");
    }

    [Fact]
    public void TamperedMigrationRecoveryIsRejectedWithoutReplacingTheWorkspace()
    {
        using var fixture = new MigrationFixture(
            (checkpoint, _) =>
            {
                if (checkpoint == WorkspaceMigrationCheckpoint.BeforeCommit)
                {
                    throw new IOException("Injected migration interruption.");
                }
            });
        fixture.CreateSchemaOneWorkspace("Home");
        var failed = fixture.Store.Open(fixture.WorkspacePath, UnlockPassphrase());
        var recoveryPath = Assert.IsType<string>(failed.RecoveryPointPath);
        var bytes = File.ReadAllBytes(recoveryPath);
        bytes[Math.Min(128, bytes.Length - 1)] ^= 0x5A;
        File.WriteAllBytes(recoveryPath, bytes);
        fixture.SetFirstCategory("Current remains");

        var restored = fixture.Store.RestoreMigrationRecovery(
            fixture.WorkspacePath,
            UnlockPassphrase(),
            recoveryPath);

        Assert.Equal(MigrationRecoveryRestoreStatus.InvalidRecoveryPoint, restored.Status);
        AssertSchemaOneWorkspace(fixture.WorkspacePath, "Current remains");
        Assert.Empty(
            Directory.GetFiles(
                fixture.DirectoryPath,
                "dot-orbit-pre-restore-*.dotorbit-recovery"));
    }

    private static void AssertSchemaOneWorkspace(string path, string expectedCategory)
    {
        using var connection = OpenInspectionConnection(path, ValidPassphrase);
        Assert.Equal(1L, ExecuteScalar<long>(connection, "PRAGMA user_version;"));
        Assert.Equal(expectedCategory, ExecuteScalar<string>(connection, "SELECT name FROM categories;"));
        Assert.Equal(
            0L,
            ExecuteScalar<long>(
                connection,
                "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'ix_categories_position';"));
        Assert.Equal("ok", ExecuteScalar<string>(connection, "PRAGMA integrity_check;"));
    }

    private static WorkspacePassphrase UnlockPassphrase() =>
        Assert.IsType<WorkspacePassphrase>(WorkspacePassphrase.ForUnlock(ValidPassphrase));

    private static T ExecuteScalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        Assert.NotNull(value);
        return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }

    private static SqliteConnection OpenInspectionConnection(
        string path,
        string passphrase,
        SqliteOpenMode mode = SqliteOpenMode.ReadWrite)
    {
        var uri = new Uri(path).AbsoluteUri
            + "?cipher=chacha20&legacy=0&kdf_iter=64007&plaintext_header_size=0&hmac_check=1";
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = uri,
            Mode = mode,
            Pooling = false,
            Password = passphrase,
            DefaultTimeout = 1,
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    private sealed class MigrationFixture : IDisposable
    {
        public MigrationFixture(
            Action<WorkspaceMigrationCheckpoint, SqliteConnection>? checkpoint = null,
            bool failRecoveryPublication = false,
            Func<SqliteConnection, string>? integrityCheck = null,
            Action? afterMigration = null)
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                $"dot-orbit-migration-tests-{Guid.NewGuid():N}");
            RecoveryDirectoryPath = Path.Combine(DirectoryPath, "recovery");
            Directory.CreateDirectory(DirectoryPath);
            var fileOperations = new MigrationFileOperations(failRecoveryPublication);
            Store = new EncryptedWorkspaceStore(
                new SystemIdentifierGenerator(),
                fileOperations,
                TimeProvider.System,
                checkpoint,
                integrityCheck,
                afterMigration);
            WorkspacePath = Path.Combine(DirectoryPath, "workspace.db");
        }

        public string DirectoryPath { get; }

        public string RecoveryDirectoryPath { get; }

        public EncryptedWorkspaceStore Store { get; }

        public string WorkspacePath { get; }

        public IWorkspaceSession CreateCurrentWorkspace()
        {
            var passphrase = Assert.IsType<WorkspacePassphrase>(
                WorkspacePassphrase.Create(ValidPassphrase, ValidPassphrase).Passphrase);
            var category = Assert.IsType<CategoryName>(CategoryName.Create("Home").CategoryName);
            var result = Store.Create(WorkspacePath, passphrase, category);
            Assert.Equal(WorkspaceCreationStatus.Created, result.Status);
            return Assert.IsAssignableFrom<IWorkspaceSession>(result.Session);
        }

        public void CreateSchemaOneWorkspace(
            string category,
            int schemaVersion = 1,
            string? path = null)
        {
            using var connection = OpenInspectionConnection(
                path ?? WorkspacePath,
                ValidPassphrase,
                SqliteOpenMode.ReadWriteCreate);
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE TABLE categories (
                    id TEXT NOT NULL PRIMARY KEY,
                    name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    position INTEGER NOT NULL CHECK (position >= 0)
                );
                INSERT INTO categories (id, name, position)
                VALUES ('first-category', $name, 0);
                PRAGMA user_version = {schemaVersion.ToString(CultureInfo.InvariantCulture)};
                """;
            command.Parameters.AddWithValue("$name", category);
            command.ExecuteNonQuery();
        }

        public void ConfigureRecoveryDirectory()
        {
            Directory.CreateDirectory(RecoveryDirectoryPath);
            WriteRecoveryConfiguration(RecoveryDirectoryPath);
        }

        public void CreateSchemaTwoWorkspace(string indexSql)
        {
            CreateSchemaOneWorkspace("Home", schemaVersion: 2);
            using var connection = OpenInspectionConnection(
                WorkspacePath,
                ValidPassphrase);
            using var command = connection.CreateCommand();
            command.CommandText = indexSql;
            command.ExecuteNonQuery();
        }

        public void CreateSchemaThreeWorkspaceWithTasks()
        {
            using var connection = OpenInspectionConnection(
                WorkspacePath,
                ValidPassphrase,
                SqliteOpenMode.ReadWriteCreate);
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE categories (
                    id TEXT NOT NULL PRIMARY KEY,
                    name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    position INTEGER NOT NULL CHECK (position >= 0)
                );
                CREATE INDEX ix_categories_position ON categories(position);
                INSERT INTO categories VALUES ('category', 'Home', 0);
                """ + SqliteWorkspaceWork.SchemaThree + """
                INSERT INTO projects VALUES ('project', 'Garden', '', 'category', NULL, 0);
                INSERT INTO tasks VALUES ('second', 'project', 'Second', '', NULL, NULL, -1, 1);
                INSERT INTO tasks VALUES ('first', 'project', 'First', '', NULL, NULL, -2, 0);
                """;
            command.ExecuteNonQuery();
        }

        public void CreateSchemaFourWorkspaceWithOrderedWork()
        {
            using var connection = OpenInspectionConnection(WorkspacePath, ValidPassphrase, SqliteOpenMode.ReadWriteCreate);
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE categories (
                    id TEXT NOT NULL PRIMARY KEY,
                    name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    position INTEGER NOT NULL CHECK (position >= 0)
                );
                CREATE INDEX ix_categories_position ON categories(position);
                INSERT INTO categories VALUES ('category', 'Home', 0);
                """ + SqliteWorkspaceWork.SchemaFour + """
                INSERT INTO projects VALUES ('project-two', 'Second project', '', 'category', NULL, 0);
                INSERT INTO projects VALUES ('project-one', 'First project', '', 'category', NULL, 1);
                INSERT INTO tasks VALUES ('one-second', 'project-one', 'One second', '', NULL, NULL, 0, 1);
                INSERT INTO tasks VALUES ('standalone', NULL, 'Standalone', '', 'category', NULL, 1, NULL);
                INSERT INTO tasks VALUES ('two-first', 'project-two', 'Two first', '', NULL, NULL, 2, 0);
                INSERT INTO tasks VALUES ('one-first', 'project-one', 'One first', '', NULL, NULL, 3, 0);
                INSERT INTO tasks VALUES ('two-second', 'project-two', 'Two second', '', NULL, NULL, 4, 1);
                """;
            command.ExecuteNonQuery();
        }

        public void SetSchemaVersion(int version)
        {
            using var connection = OpenInspectionConnection(
                WorkspacePath,
                ValidPassphrase);
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA user_version = {version.ToString(CultureInfo.InvariantCulture)};";
            command.ExecuteNonQuery();
        }

        public void DowngradeCurrentToSchemaSix()
        {
            using var connection = OpenInspectionConnection(WorkspacePath, ValidPassphrase);
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE project_bin_order_anchors; DROP TABLE project_bin_tasks; DROP TABLE project_bins; DROP TABLE task_bin_order_anchors; DROP TABLE task_bins; DROP TABLE archive_search; DROP TABLE project_archives; DROP TABLE task_archives; DROP TABLE today_tasks; PRAGMA user_version = 6;";
            command.ExecuteNonQuery();
        }

        public void DowngradeCurrentToSchemaSeven()
        {
            using var connection = OpenInspectionConnection(WorkspacePath, ValidPassphrase);
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE project_bin_order_anchors; DROP TABLE project_bin_tasks; DROP TABLE project_bins; DROP TABLE task_bin_order_anchors; DROP TABLE task_bins; DROP TABLE archive_search; DROP TABLE project_archives; DROP TABLE task_archives; PRAGMA user_version = 7;";
            command.ExecuteNonQuery();
        }

        public void DowngradeCurrentToSchemaEight()
        {
            using var connection = OpenInspectionConnection(WorkspacePath, ValidPassphrase);
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE project_bin_order_anchors; DROP TABLE project_bin_tasks; DROP TABLE project_bins; DROP TABLE task_bin_order_anchors; DROP TABLE task_bins; DROP TABLE archive_search; DROP TABLE project_archives; PRAGMA user_version = 8;";
            command.ExecuteNonQuery();
        }

        public void DowngradeCurrentToSchemaNine()
        {
            using var connection = OpenInspectionConnection(WorkspacePath, ValidPassphrase);
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE project_bin_order_anchors; DROP TABLE project_bin_tasks; DROP TABLE project_bins; DROP TABLE task_bin_order_anchors; DROP TABLE task_bins; DROP TABLE archive_search; PRAGMA user_version = 9;";
            command.ExecuteNonQuery();
        }

        public void DowngradeCurrentToSchemaTen()
        {
            using var connection = OpenInspectionConnection(WorkspacePath, ValidPassphrase);
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE project_bin_order_anchors; DROP TABLE project_bin_tasks; DROP TABLE project_bins; DROP TABLE task_bin_order_anchors; DROP TABLE task_bins; PRAGMA user_version = 10;";
            command.ExecuteNonQuery();
        }

        public void DowngradeCurrentToSchemaEleven()
        {
            using var connection = OpenInspectionConnection(WorkspacePath, ValidPassphrase);
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE project_bin_order_anchors; DROP TABLE project_bin_tasks; PRAGMA user_version = 11;";
            command.ExecuteNonQuery();
        }

        public void SetFirstCategory(string category)
        {
            using var connection = OpenInspectionConnection(
                WorkspacePath,
                ValidPassphrase);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE categories SET name = $name WHERE position = 0;";
            command.Parameters.AddWithValue("$name", category);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        public void WriteRecoveryConfiguration(string directoryPath)
        {
            File.WriteAllText(
                WorkspacePath + ".recovery-state.json",
                JsonSerializer.Serialize(
                    new
                    {
                        Version = 1,
                        DirectoryPath = directoryPath,
                        PendingChangeUtc = (DateTimeOffset?)null,
                        ChangeGeneration = 0,
                        PendingChangeGeneration = (long?)null,
                        RecoverySetIdentifier = (string?)null,
                    }));
        }

        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }

    private sealed class MigrationFileOperations(bool failRecoveryPublication)
        : IWorkspaceFileOperations
    {
        private readonly WorkspaceFileOperations _inner = new();

        public string ResolvePath(string path) => _inner.ResolvePath(path);
        public bool Exists(string path) => _inner.Exists(path);
        public void EnsureParentDirectory(string path) => _inner.EnsureParentDirectory(path);
        public void EnsureDirectory(string path) => _inner.EnsureDirectory(path);
        public string GetCandidatePath(string targetPath, string identifier) =>
            _inner.GetCandidatePath(targetPath, identifier);

        public void Publish(string candidatePath, string targetPath)
        {
            if (failRecoveryPublication
                && targetPath.Contains("pre-migration", StringComparison.Ordinal))
            {
                throw new IOException("Injected recovery publication interruption.");
            }

            _inner.Publish(candidatePath, targetPath);
        }

        public void Copy(string sourcePath, string candidatePath) =>
            _inner.Copy(sourcePath, candidatePath);
        public void CreateHardLink(string existingPath, string linkPath) =>
            _inner.CreateHardLink(existingPath, linkPath);
        public string ComputeSha256(string path) => _inner.ComputeSha256(path);
        public void Flush(string path) => _inner.Flush(path);
        public void Replace(string candidatePath, string targetPath) =>
            _inner.Replace(candidatePath, targetPath);
        public void DeleteCandidate(string candidatePath) => _inner.DeleteCandidate(candidatePath);
        public IReadOnlyList<string> EnumerateFiles(string directoryPath, string searchPattern) =>
            _inner.EnumerateFiles(directoryPath, searchPattern);
        public string ReadAllText(string path) => _inner.ReadAllText(path);
        public void WriteAllText(string path, string contents) =>
            _inner.WriteAllText(path, contents);
        public void PublishOrReplace(string candidatePath, string targetPath) =>
            _inner.PublishOrReplace(candidatePath, targetPath);
        public void DeleteFile(string path) => _inner.DeleteFile(path);
    }
}
