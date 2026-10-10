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
    private readonly WorkspaceConnectionFactory _connectionFactory;
    private SqliteConnection? _readConnection;
    private SqliteConnection? _writeConnection;
    private WorkspacePassphrase? _passphrase;
    private readonly string _workspacePath;

    internal WorkspaceTransactionCoordinator(
        string workspacePath,
        WorkspacePassphrase passphrase,
        SqliteConnection readConnection,
        SqliteConnection writeConnection,
        WorkspaceConnectionFactory connectionFactory,
        EncryptedWorkspaceRecovery recovery,
        object gate,
        Action<SqliteConnection, SqliteTransaction?> synchroniseDerivedStorage,
        Action<EmptyBinCheckpoint>? emptyBinCheckpoint = null)
    {
        _workspacePath = workspacePath;
        _passphrase = passphrase;
        _readConnection = readConnection;
        _writeConnection = writeConnection;
        _connectionFactory = connectionFactory;
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
            var connection = GetWriteConnection();
            using (var transaction = BeginTransaction(connection))
            {
                using (PerformanceTrace.Measure(PerformanceStage.Mutation, operation))
                    change(connection, transaction);
                using (PerformanceTrace.Measure(PerformanceStage.DerivedStorage, operation))
                    _synchroniseDerivedStorage(connection, transaction);
                using (PerformanceTrace.Measure(PerformanceStage.Commit, operation))
                    transaction.Commit();
            }
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
            T result;
            var connection = GetReadConnection();
            using (var transaction = BeginTransaction(connection))
            {
                using (PerformanceTrace.Measure(PerformanceStage.Query, operation))
                    result = read(connection, transaction);
                using (PerformanceTrace.Measure(PerformanceStage.Commit, operation))
                    transaction.Commit();
            }
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
            using var connection = _connectionFactory.OpenConfigured(
                _workspacePath,
                passphrase,
                SqliteOpenMode.ReadWrite);
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

    internal void CloseConnections()
    {
        lock (_gate)
        {
            if (_readConnection is null && _writeConnection is null)
            {
                return;
            }

            try
            {
                _readConnection?.Dispose();
            }
            finally
            {
                _readConnection = null;
                try
                {
                    _writeConnection?.Dispose();
                }
                finally
                {
                    _writeConnection = null;
                }
            }
        }
    }

    internal void ClearPassphrase()
    {
        lock (_gate)
        {
            _passphrase = null;
        }
    }

    private SqliteConnection GetReadConnection()
    {
        ObjectDisposedException.ThrowIf(_passphrase is null, typeof(IWorkspaceSession));
        return _readConnection ?? throw new ObjectDisposedException(nameof(IWorkspaceSession));
    }

    private SqliteConnection GetWriteConnection()
    {
        ObjectDisposedException.ThrowIf(_passphrase is null, typeof(IWorkspaceSession));
        return _writeConnection ?? throw new ObjectDisposedException(nameof(IWorkspaceSession));
    }
}

internal enum EmptyBinCheckpoint
{
    RecoveryPointValidated,
    BeforeCommit,
}
