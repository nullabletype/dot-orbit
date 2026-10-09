using DotOrbit.Core.Workspaces;
using DotOrbit.Core.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace DotOrbit.Storage.Sqlite;

internal sealed class WorkspaceTransactionCoordinator
{
    private readonly object _gate;
    private readonly EncryptedWorkspaceRecovery _recovery;
    private readonly Action<SqliteConnection, SqliteTransaction?> _synchroniseDerivedStorage;
    private readonly Action<EmptyBinCheckpoint>? _emptyBinCheckpoint;
    private WorkspacePassphrase? _passphrase;
    private readonly string _workspacePath;

    internal WorkspaceTransactionCoordinator(
        string workspacePath,
        WorkspacePassphrase passphrase,
        EncryptedWorkspaceRecovery recovery,
        object gate,
        Action<SqliteConnection, SqliteTransaction?> synchroniseDerivedStorage,
        Action<EmptyBinCheckpoint>? emptyBinCheckpoint = null)
    {
        _workspacePath = workspacePath;
        _passphrase = passphrase;
        _recovery = recovery;
        _gate = gate;
        _synchroniseDerivedStorage = synchroniseDerivedStorage;
        _emptyBinCheckpoint = emptyBinCheckpoint;
    }

    internal AutomaticRecoveryAttempt Execute(
        Action<SqliteConnection, SqliteTransaction> change,
        [CallerMemberName] string operation = "")
    {
        ArgumentNullException.ThrowIfNull(change);
        using var timing = PerformanceTrace.Measure(PerformanceStage.StorageWrite, operation);
        var wait = PerformanceTrace.Measure(PerformanceStage.GateWait, operation);
        lock (_gate)
        {
            wait.Dispose();
            var passphrase = _passphrase
                ?? throw new ObjectDisposedException(nameof(IWorkspaceSession));
            using var connection = EncryptedWorkspaceStore.OpenConnection(
                _workspacePath,
                passphrase,
                SqliteOpenMode.ReadWrite);
            EncryptedWorkspaceStore.ConfigureConnection(connection);
            using var transaction = BeginTransaction(connection);
            using (PerformanceTrace.Measure(PerformanceStage.Mutation, operation))
                change(connection, transaction);
            using (PerformanceTrace.Measure(PerformanceStage.DerivedStorage, operation))
                _synchroniseDerivedStorage(connection, transaction);
            using (PerformanceTrace.Measure(PerformanceStage.Commit, operation))
                transaction.Commit();
            using (PerformanceTrace.Measure(PerformanceStage.Recovery, operation))
                return _recovery.StoredDataChangeCompleted(StoredDataChangeOutcome.Committed);
        }
    }

    internal T Read<T>(Func<SqliteConnection, SqliteTransaction, T> read,
        [CallerMemberName] string operation = "")
    {
        using var timing = PerformanceTrace.Measure(PerformanceStage.StorageRead, operation);
        var wait = PerformanceTrace.Measure(PerformanceStage.GateWait, operation);
        lock (_gate)
        {
            wait.Dispose();
            var passphrase = _passphrase ?? throw new ObjectDisposedException(nameof(IWorkspaceSession));
            using var connection = EncryptedWorkspaceStore.OpenConnection(_workspacePath, passphrase, SqliteOpenMode.ReadOnly);
            EncryptedWorkspaceStore.ConfigureConnection(connection);
            using var transaction = BeginTransaction(connection);
            T result;
            using (PerformanceTrace.Measure(PerformanceStage.Query, operation))
                result = read(connection, transaction);
            using (PerformanceTrace.Measure(PerformanceStage.Commit, operation))
                transaction.Commit();
            return result;
        }
    }

    internal EmptyBinResult ExecuteEmptyBin(
        EmptyBinPreview confirmedPreview,
        Func<SqliteConnection, SqliteTransaction?, EmptyBinPreview> readPreview,
        Action<SqliteConnection, SqliteTransaction?> delete)
    {
        ArgumentNullException.ThrowIfNull(confirmedPreview);
        ArgumentNullException.ThrowIfNull(readPreview);
        ArgumentNullException.ThrowIfNull(delete);

        lock (_gate)
        {
            var passphrase = _passphrase
                ?? throw new ObjectDisposedException(nameof(IWorkspaceSession));
            using var connection = EncryptedWorkspaceStore.OpenConnection(
                _workspacePath,
                passphrase,
                SqliteOpenMode.ReadWrite);
            EncryptedWorkspaceStore.ConfigureConnection(connection);
            var transactionActive = false;
            try
            {
                EnterExclusiveLock(connection);
                transactionActive = true;
                var currentPreview = readPreview(connection, null);
                if (!confirmedPreview.Matches(currentPreview))
                    return new(EmptyBinStatus.PreviewChanged, currentPreview);

                ExecuteNonQuery(connection, "ROLLBACK;");
                transactionActive = false;
                var recovery = _recovery.CreateEmptyBinRecoveryPoint(connection);
                if (recovery.Status != RecoveryPointCreationStatus.Created)
                    return new(EmptyBinStatus.RecoveryPointCreationFailed, confirmedPreview);
                _emptyBinCheckpoint?.Invoke(EmptyBinCheckpoint.RecoveryPointValidated);

                ExecuteNonQuery(connection, "BEGIN EXCLUSIVE;");
                transactionActive = true;
                currentPreview = readPreview(connection, null);
                if (!confirmedPreview.Matches(currentPreview))
                    return new(EmptyBinStatus.PreviewChanged, currentPreview);
                delete(connection, null);
                _synchroniseDerivedStorage(connection, null);
                _emptyBinCheckpoint?.Invoke(EmptyBinCheckpoint.BeforeCommit);
                ExecuteNonQuery(connection, "COMMIT;");
                transactionActive = false;
            }
            catch (SqliteException)
            {
                return new(EmptyBinStatus.Failed, confirmedPreview);
            }
            catch (InvalidDataException)
            {
                return new(EmptyBinStatus.Failed, confirmedPreview);
            }
            catch (InvalidOperationException)
            {
                return new(EmptyBinStatus.Failed, confirmedPreview);
            }
            finally
            {
                if (transactionActive) TryRollback(connection);
            }

            _recovery.StoredDataChangeCompleted(StoredDataChangeOutcome.Committed);
            return new(EmptyBinStatus.Emptied, confirmedPreview);
        }
    }

    private static SqliteTransaction BeginTransaction(SqliteConnection connection)
    {
        using var timing = PerformanceTrace.Measure(PerformanceStage.TransactionBegin);
        return connection.BeginTransaction();
    }

    private static void EnterExclusiveLock(SqliteConnection connection)
    {
        using var lockingMode = connection.CreateCommand();
        lockingMode.CommandText = "PRAGMA locking_mode = EXCLUSIVE;";
        if (!string.Equals(lockingMode.ExecuteScalar() as string, "exclusive", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException();
        ExecuteNonQuery(connection, "BEGIN EXCLUSIVE;");
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void TryRollback(SqliteConnection connection)
    {
        try
        {
            ExecuteNonQuery(connection, "ROLLBACK;");
        }
        catch (SqliteException)
        {
            // Connection disposal still closes any unfinished transaction and releases the exclusive lock.
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

internal enum EmptyBinCheckpoint
{
    RecoveryPointValidated,
    BeforeCommit,
}
