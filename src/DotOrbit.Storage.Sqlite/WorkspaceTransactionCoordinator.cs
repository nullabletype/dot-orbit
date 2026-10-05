using DotOrbit.Core.Workspaces;
using Microsoft.Data.Sqlite;

namespace DotOrbit.Storage.Sqlite;

internal sealed class WorkspaceTransactionCoordinator
{
    private readonly object _gate;
    private readonly EncryptedWorkspaceRecovery _recovery;
    private readonly Action<SqliteConnection, SqliteTransaction> _synchroniseDerivedStorage;
    private WorkspacePassphrase? _passphrase;
    private readonly string _workspacePath;

    internal WorkspaceTransactionCoordinator(
        string workspacePath,
        WorkspacePassphrase passphrase,
        EncryptedWorkspaceRecovery recovery,
        object gate,
        Action<SqliteConnection, SqliteTransaction> synchroniseDerivedStorage)
    {
        _workspacePath = workspacePath;
        _passphrase = passphrase;
        _recovery = recovery;
        _gate = gate;
        _synchroniseDerivedStorage = synchroniseDerivedStorage;
    }

    internal AutomaticRecoveryAttempt Execute(
        Action<SqliteConnection, SqliteTransaction> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        lock (_gate)
        {
            var passphrase = _passphrase
                ?? throw new ObjectDisposedException(nameof(IWorkspaceSession));
            using var connection = EncryptedWorkspaceStore.OpenConnection(
                _workspacePath,
                passphrase,
                SqliteOpenMode.ReadWrite);
            EncryptedWorkspaceStore.ConfigureConnection(connection);
            using var transaction = connection.BeginTransaction();
            change(connection, transaction);
            _synchroniseDerivedStorage(connection, transaction);
            transaction.Commit();
            return _recovery.StoredDataChangeCompleted(StoredDataChangeOutcome.Committed);
        }
    }

    internal T Read<T>(Func<SqliteConnection, SqliteTransaction, T> read)
    {
        lock (_gate)
        {
            var passphrase = _passphrase ?? throw new ObjectDisposedException(nameof(IWorkspaceSession));
            using var connection = EncryptedWorkspaceStore.OpenConnection(_workspacePath, passphrase, SqliteOpenMode.ReadOnly);
            EncryptedWorkspaceStore.ConfigureConnection(connection);
            using var transaction = connection.BeginTransaction();
            var result = read(connection, transaction);
            transaction.Commit();
            return result;
        }
    }

    internal void Close()
    {
        lock (_gate)
        {
            _passphrase = null;
        }
    }
}
