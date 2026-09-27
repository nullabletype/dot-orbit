using System.Globalization;
using DotOrbit.Core.Workspaces;
using Microsoft.Data.Sqlite;

namespace DotOrbit.Storage.Sqlite;

internal sealed class EncryptedWorkspaceRecovery : IWorkspaceRecovery
{
    internal const string RecoveryPointExtension = ".dotorbit-recovery";

    private readonly Action _closeWorkspace;
    private readonly SqliteConnection _connection;
    private readonly IWorkspaceFileOperations _fileOperations;
    private readonly WorkspacePassphrase _passphrase;
    private readonly EncryptedWorkspaceStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly string _workspacePath;

    public EncryptedWorkspaceRecovery(
        EncryptedWorkspaceStore store,
        SqliteConnection connection,
        string workspacePath,
        WorkspacePassphrase passphrase,
        IWorkspaceFileOperations fileOperations,
        TimeProvider timeProvider,
        Action closeWorkspace)
    {
        _store = store;
        _connection = connection;
        _workspacePath = workspacePath;
        _passphrase = passphrase;
        _fileOperations = fileOperations;
        _timeProvider = timeProvider;
        _closeWorkspace = closeWorkspace;
    }

    public RecoveryPointCreationResult CreateRecoveryPoint(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        var directory = _fileOperations.ResolvePath(directoryPath);
        var recoveryPointPath = Path.Combine(
            directory,
            string.Create(
                CultureInfo.InvariantCulture,
                $"dot-orbit-recovery-{_timeProvider.GetUtcNow():yyyyMMdd'T'HHmmssfffffff'Z'}-{_store.GetIdentifier()}{RecoveryPointExtension}"));
        var candidatePath = Path.Combine(
            directory,
            $".{Path.GetFileName(recoveryPointPath)}.creating");

        try
        {
            _fileOperations.EnsureDirectory(directory);
            using (var candidate = EncryptedWorkspaceStore.OpenConnection(
                       candidatePath,
                       _passphrase,
                       SqliteOpenMode.ReadWriteCreate))
            {
                EncryptedWorkspaceStore.ConfigureConnection(candidate);
                EncryptedWorkspaceStore.AssertEncryptionProfile(candidate);
                _connection.BackupDatabase(candidate);
                EncryptedWorkspaceStore.ValidateIntegrity(candidate);
                EncryptedWorkspaceStore.ValidateWorkspaceShape(candidate);
            }

            _fileOperations.Flush(candidatePath);
            if (Validate(candidatePath) != RecoveryValidation.Valid)
            {
                return RecoveryPointCreationResult.Failed();
            }

            _fileOperations.Publish(candidatePath, recoveryPointPath);
            return RecoveryPointCreationResult.Created(recoveryPointPath);
        }
        catch (SqliteException)
        {
            return RecoveryPointCreationResult.Failed();
        }
        catch (IOException)
        {
            return RecoveryPointCreationResult.Failed();
        }
        catch (UnauthorizedAccessException)
        {
            return RecoveryPointCreationResult.Failed();
        }
        catch (InvalidDataException)
        {
            return RecoveryPointCreationResult.Failed();
        }
        finally
        {
            _fileOperations.DeleteCandidate(candidatePath);
        }
    }

    public WorkspaceRestoreResult Restore(
        string recoveryPointPath,
        string preRestoreRecoveryDirectoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryPointPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(preRestoreRecoveryDirectoryPath);

        var sourcePath = _fileOperations.ResolvePath(recoveryPointPath);
        var identifier = _store.GetIdentifier();
        var restoreCandidatePath = _fileOperations.GetCandidatePath(
            _workspacePath,
            $"restore-{identifier}");
        var rollbackPath = Path.Combine(
            Path.GetDirectoryName(_workspacePath) ?? throw new InvalidDataException(),
            $".{Path.GetFileName(_workspacePath)}.restore-{identifier}.rollback");
        var failedRestorePath = restoreCandidatePath + ".failed";

        try
        {
            _fileOperations.Copy(sourcePath, restoreCandidatePath);
            _fileOperations.Flush(restoreCandidatePath);

            var validation = Validate(restoreCandidatePath);
            if (validation == RecoveryValidation.UnsupportedSchema)
            {
                return WorkspaceRestoreResult.UnsupportedSchema();
            }

            if (validation != RecoveryValidation.Valid)
            {
                return WorkspaceRestoreResult.InvalidRecoveryPoint();
            }

            var preRestoreRecovery = CreateRecoveryPoint(preRestoreRecoveryDirectoryPath);
            if (preRestoreRecovery.Status != RecoveryPointCreationStatus.Created)
            {
                return WorkspaceRestoreResult.PreRestoreRecoveryFailed();
            }

            _closeWorkspace();
            try
            {
                _fileOperations.Replace(restoreCandidatePath, _workspacePath, rollbackPath);
            }
            catch (IOException)
            {
                return WorkspaceRestoreResult.Failed(ReopenCurrentWorkspace());
            }
            catch (UnauthorizedAccessException)
            {
                return WorkspaceRestoreResult.Failed(ReopenCurrentWorkspace());
            }

            var restored = _store.Open(_workspacePath, _passphrase);
            if (restored.Status == WorkspaceOpenStatus.Opened && restored.Session is not null)
            {
                _fileOperations.DeleteCandidate(rollbackPath);
                return WorkspaceRestoreResult.Restored(restored.Session);
            }

            return RollBackReplacement(rollbackPath, failedRestorePath);
        }
        catch (SqliteException)
        {
            return WorkspaceRestoreResult.InvalidRecoveryPoint();
        }
        catch (InvalidDataException)
        {
            return WorkspaceRestoreResult.InvalidRecoveryPoint();
        }
        catch (IOException)
        {
            return WorkspaceRestoreResult.Failed();
        }
        catch (UnauthorizedAccessException)
        {
            return WorkspaceRestoreResult.Failed();
        }
        finally
        {
            _fileOperations.DeleteCandidate(restoreCandidatePath);
        }
    }

    private IWorkspaceSession? ReopenCurrentWorkspace()
    {
        var opened = _store.Open(_workspacePath, _passphrase);
        return opened.Status == WorkspaceOpenStatus.Opened ? opened.Session : null;
    }

    private WorkspaceRestoreResult RollBackReplacement(
        string rollbackPath,
        string failedRestorePath)
    {
        try
        {
            _fileOperations.Replace(rollbackPath, _workspacePath, failedRestorePath);
            _fileOperations.DeleteCandidate(failedRestorePath);
            return WorkspaceRestoreResult.Failed(ReopenCurrentWorkspace());
        }
        catch (IOException)
        {
            return WorkspaceRestoreResult.Failed();
        }
        catch (UnauthorizedAccessException)
        {
            return WorkspaceRestoreResult.Failed();
        }
    }

    private RecoveryValidation Validate(string path)
    {
        try
        {
            using var connection = EncryptedWorkspaceStore.OpenConnection(
                path,
                _passphrase,
                SqliteOpenMode.ReadOnly);
            EncryptedWorkspaceStore.ConfigureConnection(connection);
            EncryptedWorkspaceStore.AssertEncryptionProfile(connection);

            using var versionCommand = connection.CreateCommand();
            versionCommand.CommandText = "PRAGMA user_version;";
            var version = Convert.ToInt64(
                versionCommand.ExecuteScalar(),
                CultureInfo.InvariantCulture);
            if (version > EncryptedWorkspaceStore.CurrentSchemaVersion)
            {
                return RecoveryValidation.UnsupportedSchema;
            }

            if (version != EncryptedWorkspaceStore.CurrentSchemaVersion)
            {
                return RecoveryValidation.Invalid;
            }

            EncryptedWorkspaceStore.ValidateIntegrity(connection);
            EncryptedWorkspaceStore.ValidateWorkspaceShape(connection);
            return RecoveryValidation.Valid;
        }
        catch (SqliteException)
        {
            return RecoveryValidation.Invalid;
        }
        catch (InvalidDataException)
        {
            return RecoveryValidation.Invalid;
        }
        catch (IOException)
        {
            return RecoveryValidation.Invalid;
        }
    }

    private enum RecoveryValidation
    {
        Valid,
        Invalid,
        UnsupportedSchema,
    }
}
