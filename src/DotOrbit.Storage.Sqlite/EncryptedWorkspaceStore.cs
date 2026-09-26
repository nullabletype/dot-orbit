using System.Globalization;
using DotOrbit.Core.Workspaces;
using Microsoft.Data.Sqlite;

namespace DotOrbit.Storage.Sqlite;

public sealed class EncryptedWorkspaceStore : IWorkspaceStore
{
    public const int CurrentSchemaVersion = 1;

    internal const string CipherName = "chacha20";
    internal const int KdfIterations = 64007;

    private const string CipherQuery =
        "cipher=chacha20&legacy=0&kdf_iter=64007&plaintext_header_size=0&hmac_check=1";

    private static readonly object InitialisationLock = new();
    private static bool _initialised;
    private readonly IWorkspaceFileOperations _fileOperations;
    private readonly IIdentifierGenerator _identifierGenerator;

    public EncryptedWorkspaceStore()
        : this(new SystemIdentifierGenerator(), new WorkspaceFileOperations())
    {
    }

    public EncryptedWorkspaceStore(IIdentifierGenerator identifierGenerator)
        : this(identifierGenerator, new WorkspaceFileOperations())
    {
    }

    internal EncryptedWorkspaceStore(
        IIdentifierGenerator identifierGenerator,
        IWorkspaceFileOperations fileOperations)
    {
        ArgumentNullException.ThrowIfNull(identifierGenerator);
        ArgumentNullException.ThrowIfNull(fileOperations);
        _identifierGenerator = identifierGenerator;
        _fileOperations = fileOperations;
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
                    INSERT INTO categories (id, name, position)
                    VALUES ($id, $name, 0);
                    PRAGMA user_version = 1;
                    """;
                command.Parameters.AddWithValue("$id", GetIdentifier());
                command.Parameters.AddWithValue("$name", firstCategory.Value);
                command.ExecuteNonQuery();
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

        SqliteConnection? connection = null;
        try
        {
            connection = OpenConnection(fullPath, passphrase, SqliteOpenMode.ReadOnly);
            ConfigureConnection(connection);
            AssertEncryptionProfile(connection);

            var schemaVersion = ExecuteScalar<long>(connection, "PRAGMA user_version;");
            if (schemaVersion > CurrentSchemaVersion)
            {
                connection.Dispose();
                connection = null;
                return WorkspaceOpenResult.UnsupportedSchema();
            }

            if (schemaVersion != CurrentSchemaVersion)
            {
                throw new InvalidDataException();
            }

            ValidateIntegrity(connection);
            var firstCategory = ValidateWorkspaceShape(connection);
            var session = new WorkspaceSession(connection, (int)schemaVersion, firstCategory);
            connection = null;
            return WorkspaceOpenResult.Opened(session);
        }
        catch (SqliteException)
        {
            return WorkspaceOpenResult.InvalidPassphraseOrStore();
        }
        catch (InvalidDataException)
        {
            return WorkspaceOpenResult.InvalidPassphraseOrStore();
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

    private string GetIdentifier()
    {
        var identifier = _identifierGenerator.NewIdentifier();
        return string.IsNullOrWhiteSpace(identifier)
            ? throw new InvalidDataException()
            : identifier;
    }

    private static SqliteConnection OpenConnection(
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

    private static void ConfigureConnection(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA temp_store = MEMORY; PRAGMA memory_security = 1;";
        command.ExecuteNonQuery();
    }

    private static void AssertEncryptionProfile(SqliteConnection connection)
    {
        if (!string.Equals(
                ExecuteScalar<string>(connection, "PRAGMA cipher;"),
                CipherName,
                StringComparison.Ordinal)
            || ExecuteScalar<long>(connection, "PRAGMA legacy;") != 0
            || ExecuteScalar<long>(connection, "PRAGMA kdf_iter;") != KdfIterations
            || ExecuteScalar<long>(connection, "PRAGMA plaintext_header_size;") != 0
            || ExecuteScalar<long>(connection, "PRAGMA hmac_check;") != 1)
        {
            throw new InvalidDataException();
        }
    }

    private static void ValidateIntegrity(SqliteConnection connection)
    {
        var result = ExecuteScalar<string>(connection, "PRAGMA integrity_check;");
        if (!string.Equals(result, "ok", StringComparison.Ordinal))
        {
            throw new InvalidDataException();
        }
    }

    private static string ValidateWorkspaceShape(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM categories ORDER BY position LIMIT 1;";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidDataException();
        }

        var name = reader.GetString(0);
        return name;
    }

    private static T ExecuteScalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        if (value is null || value is DBNull)
        {
            throw new InvalidDataException();
        }

        return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }

    private sealed class WorkspaceSession(
        SqliteConnection connection,
        int schemaVersion,
        string firstCategoryName) : IWorkspaceSession
    {
        public int SchemaVersion { get; } = schemaVersion;

        public string FirstCategoryName { get; } = firstCategoryName;

        public void Dispose() => connection.Dispose();
    }
}
