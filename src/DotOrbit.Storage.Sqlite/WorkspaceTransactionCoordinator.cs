using DotOrbit.Core.Workspaces;
using Microsoft.Data.Sqlite;

namespace DotOrbit.Storage.Sqlite;

internal sealed class WorkspaceTransactionCoordinator
{
    private readonly object _gate;
    private readonly EncryptedWorkspaceRecovery _recovery;
    private WorkspacePassphrase? _passphrase;
    private readonly string _workspacePath;

    internal WorkspaceTransactionCoordinator(
        string workspacePath,
        WorkspacePassphrase passphrase,
        EncryptedWorkspaceRecovery recovery,
        object gate)
    {
        _workspacePath = workspacePath;
        _passphrase = passphrase;
        _recovery = recovery;
        _gate = gate;
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
            transaction.Commit();
            return _recovery.StoredDataChangeCompleted(StoredDataChangeOutcome.Committed);
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
