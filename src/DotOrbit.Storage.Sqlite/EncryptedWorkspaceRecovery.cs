using System.Globalization;
using DotOrbit.Core.Workspaces;
using Microsoft.Data.Sqlite;

namespace DotOrbit.Storage.Sqlite;

internal sealed class EncryptedWorkspaceRecovery : IWorkspaceRecovery
{
    internal const string RecoveryPointExtension = ".dotorbit-recovery";

    private readonly Action _closeWorkspace;
    private SqliteConnection? _connection;
    private readonly IWorkspaceFileOperations _fileOperations;
    private WorkspacePassphrase? _passphrase;
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

        var connection = GetConnection();
        var passphrase = GetPassphrase();
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
                       passphrase,
                       SqliteOpenMode.ReadWriteCreate))
            {
                EncryptedWorkspaceStore.ConfigureConnection(candidate);
                EncryptedWorkspaceStore.AssertEncryptionProfile(candidate);
                connection.BackupDatabase(candidate);
                EncryptedWorkspaceStore.ValidateIntegrity(candidate);
                EncryptedWorkspaceStore.ValidateWorkspaceShape(candidate);
            }

            _fileOperations.Flush(candidatePath);
            if (Validate(candidatePath, passphrase) != RecoveryValidation.Valid)
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
        var passphrase = GetPassphrase();
        var identifier = _store.GetIdentifier();
        var restoreCandidatePath = _fileOperations.GetCandidatePath(
            _workspacePath,
            $"restore-{identifier}");
        try
        {
            _fileOperations.Copy(sourcePath, restoreCandidatePath);
            _fileOperations.Flush(restoreCandidatePath);

            var validation = Validate(restoreCandidatePath, passphrase);
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
                _fileOperations.Replace(restoreCandidatePath, _workspacePath);
            }
            catch (IOException)
            {
                return ResolveReplacementInterruption(
                    restoreCandidatePath,
                    passphrase);
            }
            catch (UnauthorizedAccessException)
            {
                return ResolveReplacementInterruption(
                    restoreCandidatePath,
                    passphrase);
            }

            var restored = _store.OpenExistingWorkspace(_workspacePath, passphrase);
            if (restored.Status == WorkspaceOpenStatus.Opened && restored.Session is not null)
            {
                return WorkspaceRestoreResult.Restored(restored.Session);
            }

            return WorkspaceRestoreResult.Failed();
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

    internal void Close()
    {
        _connection = null;
        _passphrase = null;
    }

    private SqliteConnection GetConnection() =>
        _connection ?? throw new ObjectDisposedException(nameof(IWorkspaceSession));

    private WorkspacePassphrase GetPassphrase() =>
        _passphrase ?? throw new ObjectDisposedException(nameof(IWorkspaceSession));

    private IWorkspaceSession? ReopenWorkspace(
        string path,
        WorkspacePassphrase passphrase)
    {
        var opened = _store.Open(path, passphrase);
        return opened.Status == WorkspaceOpenStatus.Opened ? opened.Session : null;
    }

    private WorkspaceRestoreResult ResolveReplacementInterruption(
        string restoreCandidatePath,
        WorkspacePassphrase passphrase)
    {
        var reopened = ReopenWorkspace(_workspacePath, passphrase);
        if (!_fileOperations.Exists(restoreCandidatePath) && reopened is not null)
        {
            return WorkspaceRestoreResult.Restored(reopened);
        }

        return WorkspaceRestoreResult.Failed(reopened);
    }

    private static RecoveryValidation Validate(
        string path,
        WorkspacePassphrase passphrase)
    {
        try
        {
            using var connection = EncryptedWorkspaceStore.OpenConnection(
                path,
                passphrase,
                SqliteOpenMode.ReadOnly);
            EncryptedWorkspaceStore.ConfigureConnection(connection);
            var inspection = EncryptedWorkspaceStore.InspectWorkspace(connection);
            return inspection.Status == EncryptedWorkspaceStore.WorkspaceInspectionStatus.Valid
                ? RecoveryValidation.Valid
                : RecoveryValidation.UnsupportedSchema;
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
