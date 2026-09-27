using System.Globalization;
using DotOrbit.Core.Workspaces;
using Microsoft.Data.Sqlite;

namespace DotOrbit.Storage.Sqlite;

public sealed class EncryptedWorkspaceStore : IWorkspaceStore
{
    public const int CurrentSchemaVersion = 3;

    internal const string CipherName = "chacha20";
    internal const int KdfIterations = 64007;

    private const string CipherQuery =
        "cipher=chacha20&legacy=0&kdf_iter=64007&plaintext_header_size=0&hmac_check=1";

    private static readonly object InitialisationLock = new();
    private static bool _initialised;
    private readonly IWorkspaceFileOperations _fileOperations;
    private readonly IIdentifierGenerator _identifierGenerator;
    private readonly TimeProvider _timeProvider;
    private readonly Action<WorkspaceMigrationCheckpoint, SqliteConnection>? _migrationCheckpoint;
    private readonly Func<SqliteConnection, string>? _migrationIntegrityCheck;
    private readonly Action? _afterMigration;

    public EncryptedWorkspaceStore()
        : this(
            new SystemIdentifierGenerator(),
            new WorkspaceFileOperations(),
            TimeProvider.System)
    {
    }

    public EncryptedWorkspaceStore(IIdentifierGenerator identifierGenerator)
        : this(identifierGenerator, new WorkspaceFileOperations(), TimeProvider.System)
    {
    }

    internal EncryptedWorkspaceStore(
        IIdentifierGenerator identifierGenerator,
        IWorkspaceFileOperations fileOperations,
        TimeProvider? timeProvider = null,
        Action<WorkspaceMigrationCheckpoint, SqliteConnection>? migrationCheckpoint = null,
        Func<SqliteConnection, string>? migrationIntegrityCheck = null,
        Action? afterMigration = null)
    {
        ArgumentNullException.ThrowIfNull(identifierGenerator);
        ArgumentNullException.ThrowIfNull(fileOperations);
        _identifierGenerator = identifierGenerator;
        _fileOperations = fileOperations;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _migrationCheckpoint = migrationCheckpoint;
        _migrationIntegrityCheck = migrationIntegrityCheck;
        _afterMigration = afterMigration;
        EnsureProviderInitialised();
    }

    public bool Exists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return _fileOperations.Exists(_fileOperations.ResolvePath(path));
    }

    public WorkspaceCreationResult Create(
        string path,
        WorkspacePassphrase passphrase,
        CategoryName firstCategory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(passphrase);
        ArgumentNullException.ThrowIfNull(firstCategory);

        var fullPath = _fileOperations.ResolvePath(path);
        if (_fileOperations.Exists(fullPath))
        {
            return WorkspaceCreationResult.AlreadyExists();
        }

        var candidatePath = string.Empty;

        try
        {
            _fileOperations.EnsureParentDirectory(fullPath);
            candidatePath = _fileOperations.GetCandidatePath(fullPath, GetIdentifier());
            using (var connection = OpenConnection(
                       candidatePath,
                       passphrase,
                       SqliteOpenMode.ReadWriteCreate))
            {
                ConfigureConnection(connection);
                AssertEncryptionProfile(connection);

                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    CREATE TABLE categories (
                        id TEXT NOT NULL PRIMARY KEY,
                        name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                        position INTEGER NOT NULL CHECK (position >= 0)
                    );
                    CREATE INDEX ix_categories_position ON categories(position);
                    INSERT INTO categories (id, name, position)
                    VALUES ($id, $name, 0);
                    PRAGMA user_version = 2;
                    """;
                command.Parameters.AddWithValue("$id", GetIdentifier());
                command.Parameters.AddWithValue("$name", firstCategory.Value);
                command.ExecuteNonQuery();
                using var workSchema = connection.CreateCommand();
                workSchema.Transaction = transaction;
                workSchema.CommandText = SqliteWorkspaceWork.Schema;
                workSchema.ExecuteNonQuery();
                transaction.Commit();

                ValidateIntegrity(connection);
                ValidateWorkspaceShape(connection);
            }

            try
            {
                _fileOperations.Publish(candidatePath, fullPath);
            }
            catch (IOException) when (_fileOperations.Exists(fullPath))
            {
                return WorkspaceCreationResult.AlreadyExists();
            }

            var opened = Open(fullPath, passphrase);
            return opened.Status == WorkspaceOpenStatus.Opened && opened.Session is not null
                ? WorkspaceCreationResult.Created(opened.Session)
                : WorkspaceCreationResult.Failed();
        }
        catch (SqliteException)
        {
            return WorkspaceCreationResult.Failed();
        }
        catch (IOException)
        {
            return WorkspaceCreationResult.Failed();
        }
        catch (InvalidDataException)
        {
            return WorkspaceCreationResult.Failed();
        }
        finally
        {
            if (!string.IsNullOrEmpty(candidatePath))
            {
                _fileOperations.DeleteCandidate(candidatePath);
            }
        }
    }

    public WorkspaceOpenResult Open(string path, WorkspacePassphrase passphrase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(passphrase);

        var fullPath = _fileOperations.ResolvePath(path);
        if (!_fileOperations.Exists(fullPath))
        {
            return WorkspaceOpenResult.InvalidPassphraseOrStore();
        }

        return OpenResolved(fullPath, passphrase);
    }

    public MigrationRecoveryRestoreResult RestoreMigrationRecovery(
        string workspacePath,
        WorkspacePassphrase passphrase,
        string recoveryPointPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentNullException.ThrowIfNull(passphrase);
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryPointPath);
        return WorkspaceMigrationRunner.Restore(
            this,
            _fileOperations.ResolvePath(workspacePath),
            passphrase,
            _fileOperations.ResolvePath(recoveryPointPath),
            _fileOperations,
            _timeProvider,
            _migrationCheckpoint);
    }

    private WorkspaceOpenResult OpenResolved(
        string fullPath,
        WorkspacePassphrase passphrase)
    {
        SqliteConnection? connection = null;
        string? migrationRecoveryPointPath = null;
        try
        {
            connection = OpenConnection(fullPath, passphrase, SqliteOpenMode.ReadOnly);
            ConfigureConnection(connection);
            var inspection = InspectWorkspace(connection);
            if (inspection.Status == WorkspaceInspectionStatus.UnsupportedSchema)
            {
                connection.Dispose();
                connection = null;
                return WorkspaceOpenResult.UnsupportedSchema();
            }

            if (inspection.Status == WorkspaceInspectionStatus.RequiresMigration)
            {
                connection.Dispose();
                connection = null;
                var migration = WorkspaceMigrationRunner.Run(
                    this,
                    fullPath,
                    passphrase,
                    inspection.SchemaVersion,
                    _fileOperations,
                    _timeProvider,
                    _migrationCheckpoint,
                    _migrationIntegrityCheck);
                if (!migration.Succeeded)
                {
                    return WorkspaceOpenResult.MigrationFailed(migration.RecoveryPointPath);
                }

                migrationRecoveryPointPath = migration.RecoveryPointPath;
                _afterMigration?.Invoke();
                connection = OpenConnection(fullPath, passphrase, SqliteOpenMode.ReadOnly);
                ConfigureConnection(connection);
                inspection = InspectWorkspace(connection);
                if (inspection.Status == WorkspaceInspectionStatus.UnsupportedSchema)
                {
                    return WorkspaceOpenResult.UnsupportedSchema(migration.RecoveryPointPath);
                }

                if (inspection.Status != WorkspaceInspectionStatus.Valid)
                {
                    return WorkspaceOpenResult.MigrationFailed(migration.RecoveryPointPath);
                }
            }

            var session = new WorkspaceSession(
                this,
                connection,
                fullPath,
                passphrase,
                inspection.SchemaVersion,
                inspection.FirstCategoryName);
            connection = null;
            return WorkspaceOpenResult.Opened(session);
        }
        catch (SqliteException)
        {
            return migrationRecoveryPointPath is null
                ? WorkspaceOpenResult.InvalidPassphraseOrStore()
                : WorkspaceOpenResult.MigrationFailed(migrationRecoveryPointPath);
        }
        catch (InvalidDataException)
        {
            return migrationRecoveryPointPath is null
                ? WorkspaceOpenResult.InvalidPassphraseOrStore()
                : WorkspaceOpenResult.MigrationFailed(migrationRecoveryPointPath);
        }
        catch (IOException)
        {
            return WorkspaceOpenResult.Failed();
        }
        finally
        {
            connection?.Dispose();
        }
    }

    internal WorkspaceOpenResult OpenExistingWorkspace(
        string path,
        WorkspacePassphrase passphrase) =>
        OpenResolved(_fileOperations.ResolvePath(path), passphrase);

    private static void EnsureProviderInitialised()
    {
        lock (InitialisationLock)
        {
            if (_initialised)
            {
                return;
            }

            SQLitePCL.Batteries_V2.Init();
            _initialised = true;
        }
    }

    internal string GetIdentifier()
    {
        var identifier = _identifierGenerator.NewIdentifier();
        return string.IsNullOrWhiteSpace(identifier)
            ? throw new InvalidDataException()
            : identifier;
    }

    internal static SqliteConnection OpenConnection(
        string path,
        WorkspacePassphrase passphrase,
        SqliteOpenMode mode) =>
        passphrase.Use(value =>
        {
            var databaseUri = new Uri(path).AbsoluteUri + "?" + CipherQuery;
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = databaseUri,
                Mode = mode,
                Pooling = false,
                Password = value,
            };
            var connection = new SqliteConnection(builder.ConnectionString);
            try
            {
                connection.Open();
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        });

    internal static void ConfigureConnection(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA temp_store = MEMORY; PRAGMA memory_security = 1; PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
    }

    internal static void AssertEncryptionProfile(
        SqliteConnection connection,
        SqliteTransaction? transaction = null)
    {
        if (!string.Equals(
                ExecuteScalar<string>(connection, "PRAGMA cipher;", transaction),
                CipherName,
                StringComparison.Ordinal)
            || ExecuteScalar<long>(connection, "PRAGMA legacy;", transaction) != 0
            || ExecuteScalar<long>(connection, "PRAGMA kdf_iter;", transaction) != KdfIterations
            || ExecuteScalar<long>(connection, "PRAGMA plaintext_header_size;", transaction) != 0
            || ExecuteScalar<long>(connection, "PRAGMA hmac_check;", transaction) != 1)
        {
            throw new InvalidDataException();
        }
    }

    internal static void ValidateIntegrity(
        SqliteConnection connection,
        SqliteTransaction? transaction = null,
        Func<SqliteConnection, string>? integrityCheck = null)
    {
        var result = integrityCheck is null
            ? ExecuteScalar<string>(connection, "PRAGMA integrity_check;", transaction)
            : integrityCheck(connection);
        if (!string.Equals(result, "ok", StringComparison.Ordinal))
        {
            throw new InvalidDataException();
        }
    }

    internal static string ValidateWorkspaceShape(
        SqliteConnection connection,
        int schemaVersion,
        SqliteTransaction? transaction = null)
    {
        string name;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT name FROM categories ORDER BY position LIMIT 1;";
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidDataException();
            }

            name = reader.GetString(0);
        }

        if (schemaVersion >= 2)
        {
            ValidateCategoryPositionIndex(connection, transaction);
        }

        if (schemaVersion >= 3)
        {
            SqliteWorkspaceWork.ValidateShape(connection, transaction);
        }

        return name;
    }

    internal static string ValidateWorkspaceShape(SqliteConnection connection) =>
        ValidateWorkspaceShape(connection, CurrentSchemaVersion);

    private static void ValidateCategoryPositionIndex(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using (var list = connection.CreateCommand())
        {
            list.Transaction = transaction;
            list.CommandText = "PRAGMA index_list('categories');";
            using var reader = list.ExecuteReader();
            var matched = false;
            while (reader.Read())
            {
                if (!string.Equals(
                        reader.GetString(1),
                        "ix_categories_position",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                if (matched || reader.GetInt64(2) != 0 || reader.GetInt64(4) != 0)
                {
                    throw new InvalidDataException();
                }

                matched = true;
            }

            if (!matched)
            {
                throw new InvalidDataException();
            }
        }

        using var info = connection.CreateCommand();
        info.Transaction = transaction;
        info.CommandText = "PRAGMA index_info('ix_categories_position');";
        using var infoReader = info.ExecuteReader();
        if (!infoReader.Read()
            || infoReader.GetInt64(0) != 0
            || infoReader.IsDBNull(2)
            || !string.Equals(infoReader.GetString(2), "position", StringComparison.Ordinal)
            || infoReader.Read())
        {
            throw new InvalidDataException();
        }
    }

    internal static WorkspaceInspection InspectWorkspace(
        SqliteConnection connection,
        SqliteTransaction? transaction = null)
    {
        AssertEncryptionProfile(connection, transaction);
        var schemaVersion = ExecuteScalar<long>(connection, "PRAGMA user_version;", transaction);
        if (schemaVersion > CurrentSchemaVersion)
        {
            return new WorkspaceInspection(
                WorkspaceInspectionStatus.UnsupportedSchema,
                checked((int)schemaVersion),
                string.Empty);
        }

        if (schemaVersion < 1)
        {
            throw new InvalidDataException();
        }

        ValidateIntegrity(connection, transaction);
        var checkedSchemaVersion = checked((int)schemaVersion);
        return new WorkspaceInspection(
            checkedSchemaVersion == CurrentSchemaVersion
                ? WorkspaceInspectionStatus.Valid
                : WorkspaceInspectionStatus.RequiresMigration,
            checkedSchemaVersion,
            ValidateWorkspaceShape(connection, checkedSchemaVersion, transaction));
    }

    internal static T ExecuteScalar<T>(
        SqliteConnection connection,
        string sql,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        if (value is null || value is DBNull)
        {
            throw new InvalidDataException();
        }

        return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }

    internal enum WorkspaceInspectionStatus
    {
        Valid,
        RequiresMigration,
        UnsupportedSchema,
    }

    internal readonly record struct WorkspaceInspection(
        WorkspaceInspectionStatus Status,
        int SchemaVersion,
        string FirstCategoryName);

    internal sealed class WorkspaceSession : IWorkspaceSession
    {
        private SqliteConnection? _connection;
        private readonly object _gate = new();
        private readonly EncryptedWorkspaceRecovery _recovery;
        private readonly WorkspaceTransactionCoordinator _transactions;
        private bool _disposed;

        public WorkspaceSession(
            EncryptedWorkspaceStore store,
            SqliteConnection connection,
            string workspacePath,
            WorkspacePassphrase passphrase,
            int schemaVersion,
            string firstCategoryName)
        {
            _connection = connection;
            SchemaVersion = schemaVersion;
            FirstCategoryName = firstCategoryName;
            _recovery = new EncryptedWorkspaceRecovery(
                store,
                workspacePath,
                passphrase,
                store._fileOperations,
                store._timeProvider,
                _gate,
                Dispose);
            _transactions = new WorkspaceTransactionCoordinator(
                workspacePath,
                passphrase,
                _recovery,
                _gate);
            Work = new SqliteWorkspaceWork(store, _transactions);
        }

        public int SchemaVersion { get; }

        public string FirstCategoryName { get; }

        public IWorkspaceRecovery Recovery => _recovery;

        public IWorkspaceWork Work { get; }

        internal WorkspaceTransactionCoordinator Transactions => _transactions;

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _transactions.Close();
                _recovery.Close();
                _connection?.Dispose();
                _connection = null;
                _disposed = true;
            }
        }
    }
}
