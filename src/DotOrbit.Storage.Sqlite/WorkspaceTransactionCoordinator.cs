using DotOrbit.Core.Workspaces;
using DotOrbit.Core.Diagnostics;
using DotOrbit.Core.Interchange;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace DotOrbit.Storage.Sqlite;

internal sealed class WorkspaceTransactionCoordinator
{
    private readonly object _gate;
    private readonly EncryptedWorkspaceRecovery _recovery;
    private readonly Action<EmptyBinCheckpoint>? _emptyBinCheckpoint;
    private readonly Action<ProposalApplyCheckpoint, SqliteConnection, int?>? _proposalApplyCheckpoint;
    private readonly WorkspaceConnectionFactory _connectionFactory;
    private SqliteConnection? _readConnection;
    private SqliteConnection? _writeConnection;
    private WorkspacePassphrase? _passphrase;
    private readonly string _workspacePath;
    private SqliteConnection? _proposalConnection;
    private SqliteTransaction? _proposalTransaction;
    private TimeProvider? _proposalTimeProvider;

    internal WorkspaceTransactionCoordinator(
        string workspacePath,
        WorkspacePassphrase passphrase,
        SqliteConnection readConnection,
        SqliteConnection writeConnection,
        WorkspaceConnectionFactory connectionFactory,
        EncryptedWorkspaceRecovery recovery,
        object gate,
        Action<EmptyBinCheckpoint>? emptyBinCheckpoint = null,
        Action<ProposalApplyCheckpoint, SqliteConnection, int?>? proposalApplyCheckpoint = null)
    {
        _workspacePath = workspacePath;
        _passphrase = passphrase;
        _readConnection = readConnection;
        _writeConnection = writeConnection;
        _connectionFactory = connectionFactory;
        _recovery = recovery;
        _gate = gate;
        _emptyBinCheckpoint = emptyBinCheckpoint;
        _proposalApplyCheckpoint = proposalApplyCheckpoint;
        WorkspaceRevisionStorage.InstallChangeTracking(writeConnection);
    }

    internal AutomaticRecoveryAttempt Execute(
        Func<SqliteConnection, SqliteTransaction, ArchiveSearchIndexEffects> change,
        [CallerMemberName] string operation = "")
    {
        ArgumentNullException.ThrowIfNull(change);
        using var timing = PerformanceTrace.Measure(PerformanceStage.StorageWrite, operation);
        var wait = PerformanceTrace.Measure(PerformanceStage.GateWait, operation);
        lock (_gate)
        {
            wait.Dispose();
            if (_proposalConnection is not null && _proposalTransaction is not null)
            {
                var effects = change(_proposalConnection, _proposalTransaction);
                ArchiveSearchIndex.Apply(_proposalConnection, _proposalTransaction, effects);
                return AutomaticRecoveryAttempt.Ignored;
            }
            var connection = GetWriteConnection();
            var changed = false;
            using (var transaction = BeginTransaction(connection))
            {
                WorkspaceRevisionStorage.ResetChangeTracking(connection, transaction);
                ArchiveSearchIndexEffects effects;
                using (PerformanceTrace.Measure(PerformanceStage.Mutation, operation))
                    effects = change(connection, transaction);
                using (PerformanceTrace.Measure(PerformanceStage.DerivedStorage, operation))
                    ArchiveSearchIndex.Apply(connection, transaction, effects);
                changed = WorkspaceRevisionStorage.HasTrackedChanges(connection, transaction);
                if (changed)
                    WorkspaceRevisionStorage.AdvanceChecked(connection, transaction);
                using (PerformanceTrace.Measure(PerformanceStage.Commit, operation))
                    transaction.Commit();
            }
            if (!changed)
                return AutomaticRecoveryAttempt.Ignored;
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
            if (_proposalConnection is not null && _proposalTransaction is not null)
            {
                return read(_proposalConnection, _proposalTransaction);
            }
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
        Func<SqliteConnection, SqliteTransaction?, ArchiveSearchIndexEffects> delete)
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
            WorkspaceRevisionStorage.InstallChangeTracking(connection);
            var transactionActive = false;
            try
            {
                EnterExclusiveLock(connection);
                transactionActive = true;
                var currentPreview = readPreview(connection, null);
                if (!confirmedPreview.Matches(currentPreview))
                    return new(EmptyBinStatus.PreviewChanged, currentPreview);
                if (currentPreview.IsEmpty)
                {
                    ExecuteNonQuery(connection, "ROLLBACK;");
                    transactionActive = false;
                    return new(EmptyBinStatus.Emptied, currentPreview);
                }

                ExecuteNonQuery(connection, "ROLLBACK;");
                transactionActive = false;
                var recovery = _recovery.CreateEmptyBinRecoveryPoint(connection);
                if (recovery.Status != RecoveryPointCreationStatus.Created)
                    return new(EmptyBinStatus.RecoveryPointCreationFailed, confirmedPreview);
                _emptyBinCheckpoint?.Invoke(EmptyBinCheckpoint.RecoveryPointValidated);

                ExecuteNonQuery(connection, "BEGIN EXCLUSIVE;");
                transactionActive = true;
                WorkspaceRevisionStorage.ResetChangeTracking(connection, null);
                currentPreview = readPreview(connection, null);
                if (!confirmedPreview.Matches(currentPreview))
                    return new(EmptyBinStatus.PreviewChanged, currentPreview);
                var effects = delete(connection, null);
                ArchiveSearchIndex.Apply(connection, null, effects);
                if (!WorkspaceRevisionStorage.HasTrackedChanges(connection, null))
                    throw new InvalidDataException();
                WorkspaceRevisionStorage.AdvanceChecked(connection, null);
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
            catch (OverflowException)
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

    internal ProposalStorageApplyStatus ExecuteProposal(
        ProposalExecutionPlan plan,
        Action apply,
        Func<bool>? consumePendingProposal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(apply);

        lock (_gate)
        {
            var passphrase = _passphrase
                ?? throw new ObjectDisposedException(nameof(IWorkspaceSession));
            SqliteConnection? connection = null;
            try
            {
                connection = _connectionFactory.OpenConfigured(
                    _workspacePath,
                    passphrase,
                    SqliteOpenMode.ReadWrite);
                WorkspaceRevisionStorage.InstallChangeTracking(connection);
            }
            catch (Exception error) when (IsExpectedApplyFailure(error))
            {
                connection?.Dispose();
                return ProposalStorageApplyStatus.Failed;
            }

            if (connection is null)
            {
                return ProposalStorageApplyStatus.Failed;
            }

            using (connection)
            {
                var inspectionTransactionActive = false;
                var committed = false;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnterExclusiveLock(connection);
                    inspectionTransactionActive = true;
                    if (!plan.MatchesCurrentSource(SqliteProposalWorkspaceSource.Read(connection, null)))
                    {
                        return ProposalStorageApplyStatus.StaleSource;
                    }

                    _proposalApplyCheckpoint?.Invoke(ProposalApplyCheckpoint.FirstSourceValidated, connection, null);
                    cancellationToken.ThrowIfCancellationRequested();
                    ExecuteNonQuery(connection, "ROLLBACK;");
                    inspectionTransactionActive = false;
                    _proposalApplyCheckpoint?.Invoke(
                        ProposalApplyCheckpoint.ExclusiveLockRetainedForRecovery,
                        connection,
                        null);

                    var recovery = _recovery.CreateProposalImportRecoveryPoint(connection);
                    if (recovery.Status != RecoveryPointCreationStatus.Created)
                    {
                        return ProposalStorageApplyStatus.RecoveryPointCreationFailed;
                    }

                    _proposalApplyCheckpoint?.Invoke(ProposalApplyCheckpoint.RecoveryPointValidated, connection, null);
                    cancellationToken.ThrowIfCancellationRequested();
                    using var transaction = BeginTransaction(connection);
                    if (!plan.MatchesCurrentSource(SqliteProposalWorkspaceSource.Read(connection, transaction)))
                    {
                        return ProposalStorageApplyStatus.StaleSource;
                    }

                    _proposalApplyCheckpoint?.Invoke(ProposalApplyCheckpoint.SecondSourceValidated, connection, null);
                    WorkspaceRevisionStorage.ResetChangeTracking(connection, transaction);
                    _proposalConnection = connection;
                    _proposalTransaction = transaction;
                    _proposalTimeProvider = new ProposalTimeProvider(
                        plan.ExecutionInstant,
                        plan.ExecutionTimeZone);
                    try
                    {
                        apply();
                        cancellationToken.ThrowIfCancellationRequested();
                        if (consumePendingProposal is not null && !consumePendingProposal())
                        {
                            return ProposalStorageApplyStatus.Failed;
                        }

                        if (!WorkspaceRevisionStorage.HasTrackedChanges(connection, transaction))
                        {
                            return ProposalStorageApplyStatus.Failed;
                        }

                        WorkspaceRevisionStorage.AdvanceChecked(connection, transaction);
                        _proposalApplyCheckpoint?.Invoke(ProposalApplyCheckpoint.BeforeCommit, connection, null);
                        cancellationToken.ThrowIfCancellationRequested();
                        transaction.Commit();
                        committed = true;
                    }
                    finally
                    {
                        _proposalTimeProvider = null;
                        _proposalTransaction = null;
                        _proposalConnection = null;
                    }
                }
                catch (OperationCanceledException)
                {
                    return ProposalStorageApplyStatus.Cancelled;
                }
                catch (Exception error) when (IsExpectedApplyFailure(error))
                {
                    return ProposalStorageApplyStatus.Failed;
                }
                finally
                {
                    if (inspectionTransactionActive)
                    {
                        TryRollback(connection);
                    }
                }

                if (!committed)
                {
                    return ProposalStorageApplyStatus.Failed;
                }

                try
                {
                    _proposalApplyCheckpoint?.Invoke(ProposalApplyCheckpoint.AfterCommit, connection, null);
                }
                catch (Exception error) when (IsExpectedApplyFailure(error))
                {
                    connection.Dispose();
                    return ProposalStorageApplyStatus.CommittedMaintenanceRequired;
                }

                // Commit is the success boundary. Release the operation-scoped exclusive
                // connection before automatic recovery inspects the committed database.
                connection.Dispose();
                try
                {
                    return _recovery.StoredDataChangeCompleted(StoredDataChangeOutcome.Committed)
                        == AutomaticRecoveryAttempt.Failed
                            ? ProposalStorageApplyStatus.CommittedMaintenanceRequired
                            : ProposalStorageApplyStatus.Committed;
                }
                catch (Exception error) when (IsExpectedApplyFailure(error))
                {
                    return ProposalStorageApplyStatus.CommittedMaintenanceRequired;
                }
            }
        }
    }

    internal TimeProvider EffectiveTimeProvider(TimeProvider fallback) =>
        _proposalTimeProvider ?? fallback;

    internal void ProposalOperationCompleted(int operationIndex) =>
        _proposalApplyCheckpoint?.Invoke(
            ProposalApplyCheckpoint.OperationCompleted,
            _proposalConnection ?? throw new InvalidOperationException(),
            operationIndex);

    internal bool RetryProposalRecoveryMaintenance() =>
        _recovery.ReconcilePendingStoredDataChange() != AutomaticRecoveryAttempt.Failed;

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

    private static bool IsExpectedApplyFailure(Exception error) => error is
        SqliteException
        or WorkspaceWorkException
        or IOException
        or UnauthorizedAccessException
        or InvalidOperationException
        or ArgumentException
        or FormatException
        or OverflowException;

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

internal enum ProposalApplyCheckpoint
{
    FirstSourceValidated,
    ExclusiveLockRetainedForRecovery,
    RecoveryPointValidated,
    SecondSourceValidated,
    OperationCompleted,
    BeforeCommit,
    AfterCommit,
}

internal enum ProposalStorageApplyStatus
{
    Committed,
    CommittedMaintenanceRequired,
    StaleSource,
    RecoveryPointCreationFailed,
    Cancelled,
    Failed,
}

internal sealed class ProposalTimeProvider(
    DateTimeOffset instant,
    TimeZoneInfo timeZone) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => instant;

    public override TimeZoneInfo LocalTimeZone => timeZone;
}
