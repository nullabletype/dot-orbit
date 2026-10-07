using System.Globalization;
using System.Text.Json;
using DotOrbit.Core.Workspaces;
using Microsoft.Data.Sqlite;

namespace DotOrbit.Storage.Sqlite;

internal enum WorkspaceMigrationCheckpoint
{
    RecoveryPointValidated,
    PreRestoreRecoveryValidated,
    RestoreApplied,
    BeforeCommit,
}

internal readonly record struct WorkspaceMigrationResult(
    bool Succeeded,
    string? RecoveryPointPath)
{
    public static WorkspaceMigrationResult Success(string? recoveryPointPath) =>
        new(true, recoveryPointPath);

    public static WorkspaceMigrationResult Failed(string? recoveryPointPath = null) =>
        new(false, recoveryPointPath);
}

internal static class WorkspaceMigrationRunner
{
    private const string RecoveryPointExtension = ".dotorbit-recovery";
    private const int RecoveryStateVersion = 1;

    internal static WorkspaceMigrationResult Run(
        EncryptedWorkspaceStore store,
        string workspacePath,
        WorkspacePassphrase passphrase,
        int inspectedSchemaVersion,
        IWorkspaceFileOperations fileOperations,
        TimeProvider timeProvider,
        Action<WorkspaceMigrationCheckpoint, SqliteConnection>? checkpoint,
        Func<SqliteConnection, string>? integrityCheck)
    {
        string? recoveryPointPath = null;
        try
        {
            using var connection = EncryptedWorkspaceStore.OpenConnection(
                workspacePath,
                passphrase,
                SqliteOpenMode.ReadWrite);
            EncryptedWorkspaceStore.ConfigureConnection(connection);
            EnterExclusiveLock(connection);
            var transactionActive = true;
            try
            {
                var lockedInspection = EncryptedWorkspaceStore.InspectWorkspace(connection);
                if (lockedInspection.Status == EncryptedWorkspaceStore.WorkspaceInspectionStatus.UnsupportedSchema)
                {
                    return WorkspaceMigrationResult.Failed();
                }

                if (lockedInspection.SchemaVersion == EncryptedWorkspaceStore.CurrentSchemaVersion)
                {
                    ExecuteNonQuery(connection, "ROLLBACK;");
                    transactionActive = false;
                    return WorkspaceMigrationResult.Success(null);
                }

                if (lockedInspection.SchemaVersion != inspectedSchemaVersion)
                {
                    return WorkspaceMigrationResult.Failed();
                }

                ExecuteNonQuery(connection, "ROLLBACK;");
                transactionActive = false;
                recoveryPointPath = PublishRecoveryPoint(
                    store,
                    connection,
                    passphrase,
                    lockedInspection.SchemaVersion,
                    GetPreMigrationRecoveryDirectory(workspacePath, fileOperations),
                    $"dot-orbit-pre-migration-v{lockedInspection.SchemaVersion}-",
                    fileOperations,
                    timeProvider);
                checkpoint?.Invoke(
                    WorkspaceMigrationCheckpoint.RecoveryPointValidated,
                    connection);

                ExecuteNonQuery(connection, "BEGIN EXCLUSIVE;");
                transactionActive = true;
                var migrationInspection = EncryptedWorkspaceStore.InspectWorkspace(connection);
                if (migrationInspection.SchemaVersion != inspectedSchemaVersion
                    || migrationInspection.Status != EncryptedWorkspaceStore.WorkspaceInspectionStatus.RequiresMigration)
                {
                    throw new InvalidDataException();
                }

                ApplyMigrations(
                    connection,
                    lockedInspection.SchemaVersion,
                    integrityCheck);
                EncryptedWorkspaceStore.ValidateIntegrity(
                    connection,
                    integrityCheck: integrityCheck);
                EncryptedWorkspaceStore.ValidateWorkspaceShape(
                    connection,
                    EncryptedWorkspaceStore.CurrentSchemaVersion);
                checkpoint?.Invoke(WorkspaceMigrationCheckpoint.BeforeCommit, connection);
                ExecuteNonQuery(connection, "COMMIT;");
                transactionActive = false;
                return WorkspaceMigrationResult.Success(recoveryPointPath);
            }
            finally
            {
                if (transactionActive)
                {
                    TryRollback(connection);
                }
            }
        }
        catch (SqliteException)
        {
            return WorkspaceMigrationResult.Failed(recoveryPointPath);
        }
        catch (IOException)
        {
            return WorkspaceMigrationResult.Failed(recoveryPointPath);
        }
        catch (UnauthorizedAccessException)
        {
            return WorkspaceMigrationResult.Failed(recoveryPointPath);
        }
        catch (InvalidDataException)
        {
            return WorkspaceMigrationResult.Failed(recoveryPointPath);
        }
    }

    internal static MigrationRecoveryRestoreResult Restore(
        EncryptedWorkspaceStore store,
        string workspacePath,
        WorkspacePassphrase passphrase,
        string recoveryPointPath,
        IWorkspaceFileOperations fileOperations,
        TimeProvider timeProvider,
        Action<WorkspaceMigrationCheckpoint, SqliteConnection>? checkpoint)
    {
        if (!fileOperations.Exists(workspacePath)
            || !fileOperations.Exists(recoveryPointPath))
        {
            return MigrationRecoveryRestoreResult.InvalidRecoveryPoint();
        }

        var restoreCandidatePath = fileOperations.GetCandidatePath(
            workspacePath,
            $"migration-restore-{store.GetIdentifier()}");
        var replacementStarted = false;
        try
        {
            fileOperations.Copy(recoveryPointPath, restoreCandidatePath);
            fileOperations.Flush(restoreCandidatePath);
            var recoveryInspection = InspectRecoveryPoint(
                restoreCandidatePath,
                passphrase);
            if (recoveryInspection.Status
                    != EncryptedWorkspaceStore.WorkspaceInspectionStatus.RequiresMigration)
            {
                return MigrationRecoveryRestoreResult.InvalidRecoveryPoint();
            }

            var recoveryDirectoryPath = Path.GetDirectoryName(recoveryPointPath)
                ?? throw new InvalidDataException();
            using var target = EncryptedWorkspaceStore.OpenConnection(
                workspacePath,
                passphrase,
                SqliteOpenMode.ReadWrite);
            EncryptedWorkspaceStore.ConfigureConnection(target);
            EnterExclusiveLock(target);
            try
            {
                var sourceInspection = EncryptedWorkspaceStore.InspectWorkspace(target);
                ExecuteNonQuery(target, "ROLLBACK;");
                PublishRecoveryPoint(
                    store,
                    target,
                    passphrase,
                    sourceInspection.SchemaVersion,
                    recoveryDirectoryPath,
                    $"dot-orbit-pre-restore-v{sourceInspection.SchemaVersion}-",
                    fileOperations,
                    timeProvider);
                checkpoint?.Invoke(
                    WorkspaceMigrationCheckpoint.PreRestoreRecoveryValidated,
                    target);
            }
            catch (SqliteException)
            {
                return MigrationRecoveryRestoreResult.PreRestoreRecoveryFailed();
            }
            catch (IOException)
            {
                return MigrationRecoveryRestoreResult.PreRestoreRecoveryFailed();
            }
            catch (UnauthorizedAccessException)
            {
                return MigrationRecoveryRestoreResult.PreRestoreRecoveryFailed();
            }
            catch (InvalidDataException)
            {
                return MigrationRecoveryRestoreResult.PreRestoreRecoveryFailed();
            }

            using var recoverySource = EncryptedWorkspaceStore.OpenConnection(
                restoreCandidatePath,
                passphrase,
                SqliteOpenMode.ReadOnly);
            EncryptedWorkspaceStore.ConfigureConnection(recoverySource);
            replacementStarted = true;
            recoverySource.BackupDatabase(target);
            checkpoint?.Invoke(WorkspaceMigrationCheckpoint.RestoreApplied, target);
            var restoredInspection = EncryptedWorkspaceStore.InspectWorkspace(target);
            return restoredInspection.Status
                       == EncryptedWorkspaceStore.WorkspaceInspectionStatus.RequiresMigration
                   && restoredInspection.SchemaVersion == recoveryInspection.SchemaVersion
                ? MigrationRecoveryRestoreResult.Restored()
                : MigrationRecoveryRestoreResult.Failed();

        }
        catch (SqliteException)
        {
            return replacementStarted
                ? MigrationRecoveryRestoreResult.Failed()
                : MigrationRecoveryRestoreResult.InvalidRecoveryPoint();
        }
        catch (IOException)
        {
            return MigrationRecoveryRestoreResult.Failed();
        }
        catch (UnauthorizedAccessException)
        {
            return MigrationRecoveryRestoreResult.Failed();
        }
        catch (InvalidDataException)
        {
            return replacementStarted
                ? MigrationRecoveryRestoreResult.Failed()
                : MigrationRecoveryRestoreResult.InvalidRecoveryPoint();
        }
        finally
        {
            fileOperations.DeleteCandidate(restoreCandidatePath);
        }
    }

    private static void ApplyMigrations(
        SqliteConnection connection,
        int startingVersion,
        Func<SqliteConnection, string>? integrityCheck)
    {
        var version = startingVersion;
        while (version < EncryptedWorkspaceStore.CurrentSchemaVersion)
        {
            version = version switch
            {
                1 => ApplySchemaOneToTwo(connection),
                2 => ApplySchemaTwoToThree(connection),
                3 => ApplySchemaThreeToFour(connection),
                4 => ApplySchemaFourToFive(connection),
                5 => ApplySchemaFiveToSix(connection),
                6 => ApplySchemaSixToSeven(connection),
                7 => ApplySchemaSevenToEight(connection),
                8 => ApplySchemaEightToNine(connection),
                9 => ApplySchemaNineToTen(connection),
                10 => ApplySchemaTenToEleven(connection),
                11 => ApplySchemaElevenToTwelve(connection),
                12 => ApplySchemaTwelveToThirteen(connection),
                13 => ApplySchemaThirteenToFourteen(connection),
                _ => throw new InvalidDataException(),
            };

            var storedVersion = EncryptedWorkspaceStore.ExecuteScalar<long>(
                connection,
                "PRAGMA user_version;");
            if (storedVersion != version)
            {
                throw new InvalidDataException();
            }

            EncryptedWorkspaceStore.ValidateWorkspaceShape(connection, version);
            EncryptedWorkspaceStore.ValidateIntegrity(
                connection,
                integrityCheck: integrityCheck);
        }
    }

    private static int ApplySchemaTwoToThree(SqliteConnection connection)
    {
        ExecuteNonQuery(connection, SqliteWorkspaceWork.SchemaThree);
        return 3;
    }

    private static int ApplySchemaThreeToFour(SqliteConnection connection)
    {
        ExecuteNonQuery(connection, """
            ALTER TABLE tasks RENAME TO tasks_schema_three;
            """ + SqliteWorkspaceWork.TaskSchemaFour + """
            INSERT INTO tasks
                (id, project_id, title, description, explicit_category_id, due_date, shared_position, project_position)
            SELECT id, project_id, title, description, category_override_id, due_date,
                row_number() OVER (ORDER BY shared_position, id) - 1, project_position
            FROM tasks_schema_three;
            DROP TABLE tasks_schema_three;
            PRAGMA user_version = 4;
            """);
        return 4;
    }

    private static int ApplySchemaFourToFive(SqliteConnection connection)
    {
        ExecuteNonQuery(connection, """
            ALTER TABLE tasks RENAME TO tasks_schema_four;
            """ + SqliteWorkspaceWork.TaskSchema + """
            INSERT INTO tasks
                (id, project_id, title, description, explicit_category_id, due_date,
                 shared_position, project_position, completion_instant, completion_date)
            SELECT id, project_id, title, description, explicit_category_id, due_date,
                shared_position, project_position, NULL, NULL
            FROM tasks_schema_four;
            DROP TABLE tasks_schema_four;
            PRAGMA user_version = 5;
            """);
        return 5;
    }

    private static int ApplySchemaFiveToSix(SqliteConnection connection)
    {
        ExecuteNonQuery(connection, SqliteWorkspaceWork.ParticipantSchema
            + SqliteWorkspaceWork.TaskParticipantSchema
            + "PRAGMA user_version = 6;");
        return 6;
    }

    private static int ApplySchemaSixToSeven(SqliteConnection connection)
    {
        ExecuteNonQuery(connection, SqliteWorkspaceWork.TodayTaskSchema + "PRAGMA user_version = 7;");
        return 7;
    }

    private static int ApplySchemaSevenToEight(SqliteConnection connection)
    {
        ExecuteNonQuery(connection, SqliteWorkspaceWork.TaskArchiveSchema + "PRAGMA user_version = 8;");
        return 8;
    }

    private static int ApplySchemaEightToNine(SqliteConnection connection)
    {
        ExecuteNonQuery(connection, SqliteWorkspaceWork.ProjectArchiveSchema + "PRAGMA user_version = 9;");
        return 9;
    }

    private static int ApplySchemaNineToTen(SqliteConnection connection)
    {
        ExecuteNonQuery(connection, SqliteWorkspaceWork.ArchiveSearchSchema);
        SqliteWorkspaceWork.RebuildArchiveSearchIndex(connection);
        ExecuteNonQuery(connection, "PRAGMA user_version = 10;");
        return 10;
    }

    private static int ApplySchemaTenToEleven(SqliteConnection connection)
    {
        ExecuteNonQuery(connection, SqliteWorkspaceWork.TaskBinSchema
            + SqliteWorkspaceWork.ProjectBinSchema
            + "PRAGMA user_version = 11;");
        return 11;
    }

    private static int ApplySchemaElevenToTwelve(SqliteConnection connection)
    {
        ExecuteNonQuery(connection, SqliteWorkspaceWork.ProjectBinAggregateSchema
            + "PRAGMA user_version = 12;");
        return 12;
    }

    private static int ApplySchemaTwelveToThirteen(SqliteConnection connection)
    {
        ExecuteNonQuery(connection, SqliteWorkspaceWork.CategoryIdentitySchemaThirteen + """
            INSERT INTO category_identities (category_id, colour_key)
            SELECT id,
                CASE ((row_number() OVER (ORDER BY position, id) - 1) % 8)
                    WHEN 0 THEN 'orchid'
                    WHEN 1 THEN 'violet'
                    WHEN 2 THEN 'indigo'
                    WHEN 3 THEN 'ocean'
                    WHEN 4 THEN 'teal'
                    WHEN 5 THEN 'lime'
                    WHEN 6 THEN 'tangerine'
                    ELSE 'rose'
                END
            FROM categories;
            PRAGMA user_version = 13;
            """);
        return 13;
    }

    private static int ApplySchemaThirteenToFourteen(SqliteConnection connection)
    {
        ExecuteNonQuery(connection, """
            ALTER TABLE category_identities RENAME TO category_identities_v13;
            """ + SqliteWorkspaceWork.CategoryIdentitySchema + """
            INSERT INTO category_identities (category_id, colour_key)
            SELECT category_id, colour_key FROM category_identities_v13;
            DROP TABLE category_identities_v13;
            """ + SqliteWorkspaceWork.ProjectIdentitySchema + """
            INSERT INTO project_identities (project_id, colour_key)
            SELECT id,
                CASE ((row_number() OVER (ORDER BY position, id) + 4) % 16)
                    WHEN 0 THEN 'orchid'
                    WHEN 1 THEN 'violet'
                    WHEN 2 THEN 'indigo'
                    WHEN 3 THEN 'ocean'
                    WHEN 4 THEN 'teal'
                    WHEN 5 THEN 'lime'
                    WHEN 6 THEN 'tangerine'
                    WHEN 7 THEN 'rose'
                    WHEN 8 THEN 'cobalt'
                    WHEN 9 THEN 'cyan'
                    WHEN 10 THEN 'emerald'
                    WHEN 11 THEN 'gold'
                    WHEN 12 THEN 'amber'
                    WHEN 13 THEN 'coral'
                    WHEN 14 THEN 'magenta'
                    ELSE 'slate'
                END
            FROM projects;
            PRAGMA user_version = 14;
            """);
        return 14;
    }

    private static int ApplySchemaOneToTwo(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE INDEX ix_categories_position ON categories(position);
            PRAGMA user_version = 2;
            """;
        command.ExecuteNonQuery();
        return 2;
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void EnterExclusiveLock(SqliteConnection connection)
    {
        if (!string.Equals(
                EncryptedWorkspaceStore.ExecuteScalar<string>(
                    connection,
                    "PRAGMA locking_mode = EXCLUSIVE;"),
                "exclusive",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException();
        }

        ExecuteNonQuery(connection, "BEGIN EXCLUSIVE;");
    }

    private static void TryRollback(SqliteConnection connection)
    {
        try
        {
            ExecuteNonQuery(connection, "ROLLBACK;");
        }
        catch (SqliteException)
        {
            // Connection disposal still closes any unfinished transaction.
        }
    }

    private static string PublishRecoveryPoint(
        EncryptedWorkspaceStore store,
        SqliteConnection source,
        WorkspacePassphrase passphrase,
        int schemaVersion,
        string directoryPath,
        string fileNamePrefix,
        IWorkspaceFileOperations fileOperations,
        TimeProvider timeProvider)
    {
        directoryPath = fileOperations.ResolvePath(directoryPath);
        var recoveryPointPath = Path.Combine(
            directoryPath,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{fileNamePrefix}{timeProvider.GetUtcNow():yyyyMMdd'T'HHmmssfffffff'Z'}-{store.GetIdentifier()}{RecoveryPointExtension}"));
        var candidatePath = Path.Combine(
            directoryPath,
            $".{Path.GetFileName(recoveryPointPath)}.creating");

        try
        {
            fileOperations.EnsureDirectory(directoryPath);
            using (var candidate = EncryptedWorkspaceStore.OpenConnection(
                       candidatePath,
                       passphrase,
                       SqliteOpenMode.ReadWriteCreate))
            {
                EncryptedWorkspaceStore.ConfigureConnection(candidate);
                EncryptedWorkspaceStore.AssertEncryptionProfile(candidate);
                source.BackupDatabase(candidate);
            }

            fileOperations.Flush(candidatePath);
            ValidateRecoveryPoint(candidatePath, passphrase, schemaVersion);
            fileOperations.Publish(candidatePath, recoveryPointPath);
            ValidateRecoveryPoint(recoveryPointPath, passphrase, schemaVersion);
            return recoveryPointPath;
        }
        finally
        {
            fileOperations.DeleteCandidate(candidatePath);
        }
    }

    private static void ValidateRecoveryPoint(
        string path,
        WorkspacePassphrase passphrase,
        int expectedSchemaVersion)
    {
        var inspection = InspectRecoveryPoint(path, passphrase);
        if (inspection.Status == EncryptedWorkspaceStore.WorkspaceInspectionStatus.UnsupportedSchema
            || inspection.SchemaVersion != expectedSchemaVersion)
        {
            throw new InvalidDataException();
        }
    }

    private static EncryptedWorkspaceStore.WorkspaceInspection InspectRecoveryPoint(
        string path,
        WorkspacePassphrase passphrase)
    {
        using var connection = EncryptedWorkspaceStore.OpenConnection(
            path,
            passphrase,
            SqliteOpenMode.ReadOnly);
        EncryptedWorkspaceStore.ConfigureConnection(connection);
        return EncryptedWorkspaceStore.InspectWorkspace(connection);
    }

    private static string GetPreMigrationRecoveryDirectory(
        string workspacePath,
        IWorkspaceFileOperations fileOperations) =>
        GetConfiguredRecoveryDirectory(workspacePath, fileOperations)
        ?? Path.GetDirectoryName(workspacePath)
        ?? throw new InvalidDataException();

    private static string? GetConfiguredRecoveryDirectory(
        string workspacePath,
        IWorkspaceFileOperations fileOperations)
    {
        var statePath = workspacePath + ".recovery-state.json";
        if (!fileOperations.Exists(statePath))
        {
            return null;
        }

        try
        {
            var state = JsonSerializer.Deserialize<RecoveryStateDocument>(
                fileOperations.ReadAllText(statePath));
            return state is { Version: RecoveryStateVersion }
                   && !string.IsNullOrWhiteSpace(state.DirectoryPath)
                ? fileOperations.ResolvePath(state.DirectoryPath)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private sealed record RecoveryStateDocument(int Version, string? DirectoryPath);
}
