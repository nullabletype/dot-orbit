using System.Data;
using DotOrbit.Core.Diagnostics;
using DotOrbit.Core.Workspaces;
using DotOrbit.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DotOrbit.Storage.Sqlite.Tests;

[Collection("Performance trace")]
public sealed class WorkspaceConnectionReuseTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"dot-orbit-connection-reuse-{Guid.NewGuid():N}");
    private readonly WorkspacePassphrase _passphrase = WorkspacePassphrase.Create(
        "private synthetic password",
        "private synthetic password").Passphrase!;

    [Fact]
    public void CurrentSchemaUnlockOpensBoundedPairAndWarmWorkReusesIt()
    {
        var path = WorkspacePath;
        var openedModes = new List<SqliteOpenMode>();
        var store = CreateCountingStore(openedModes);
        store.Create(path, _passphrase, CategoryName.Create("Home").CategoryName!).Session!.Dispose();
        openedModes.Clear();
        using var recording = PerformanceTrace.Start();

        using var session = Assert.IsType<EncryptedWorkspaceStore.WorkspaceSession>(
            store.Open(path, _passphrase).Session);
        var category = Assert.Single(session.Work.Read().Categories);
        session.Work.CreateProject("Garden", "", category.Id, null);
        session.Work.Read();
        recording.Dispose();

        Assert.Equal([SqliteOpenMode.ReadOnly, SqliteOpenMode.ReadWrite], openedModes);
        var samples = recording.Snapshot();
        Assert.Equal(2, samples.Count(sample => sample.Stage == PerformanceStage.ConnectionOpen));
        Assert.Equal(2, samples.Count(sample => sample.Stage == PerformanceStage.ConnectionConfigure));
        Assert.DoesNotContain(samples, sample =>
            (sample.Stage is PerformanceStage.ConnectionOpen or PerformanceStage.ConnectionConfigure)
            && sample.ParentId is not null);
    }

    [Fact]
    public void UnsupportedSchemaIsRejectedBeforeTheWriteConnectionOpens()
    {
        var path = WorkspacePath;
        var openedModes = new List<SqliteOpenMode>();
        var store = CreateCountingStore(openedModes);
        store.Create(path, _passphrase, CategoryName.Create("Home").CategoryName!).Session!.Dispose();
        using (var connection = EncryptedWorkspaceStore.OpenConnection(
                   path,
                   _passphrase,
                   SqliteOpenMode.ReadWrite))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA user_version = {EncryptedWorkspaceStore.CurrentSchemaVersion + 1};";
            command.ExecuteNonQuery();
        }

        openedModes.Clear();
        using var recording = PerformanceTrace.Start();
        var result = store.Open(path, _passphrase);
        recording.Dispose();

        Assert.Equal(WorkspaceOpenStatus.UnsupportedSchema, result.Status);
        Assert.Null(result.Session);
        Assert.Equal([SqliteOpenMode.ReadOnly], openedModes);
        var samples = recording.Snapshot();
        Assert.Single(samples, sample => sample.Stage == PerformanceStage.ConnectionOpen);
        Assert.Single(samples, sample => sample.Stage == PerformanceStage.ConnectionConfigure);
    }

    [Fact]
    public void WriteConnectionConfigurationFailureClosesBothPartiallyOwnedConnections()
    {
        var path = WorkspacePath;
        var seedStore = new EncryptedWorkspaceStore();
        seedStore.Create(path, _passphrase, CategoryName.Create("Home").CategoryName!).Session!.Dispose();
        SqliteConnection? readConnection = null;
        SqliteConnection? writeConnection = null;
        var store = new EncryptedWorkspaceStore(
            new SystemIdentifierGenerator(),
            new WorkspaceFileOperations(),
            TimeProvider.System,
            connectionOpener: (connectionPath, passphrase, mode) =>
            {
                var connection = EncryptedWorkspaceStore.OpenConnection(connectionPath, passphrase, mode);
                if (mode == SqliteOpenMode.ReadOnly)
                {
                    readConnection = connection;
                }
                else
                {
                    writeConnection = connection;
                    connection.Close();
                }

                return connection;
            });

        Assert.Throws<InvalidOperationException>(() => store.Open(path, _passphrase));

        Assert.Equal(ConnectionState.Closed, readConnection!.State);
        Assert.Equal(ConnectionState.Closed, writeConnection!.State);
    }

    [Fact]
    public void ReadConnectionRejectsWritesAndRemainsReusable()
    {
        using var session = CreateSession();
        var concrete = Assert.IsType<EncryptedWorkspaceStore.WorkspaceSession>(session);

        var error = Assert.Throws<SqliteException>(() => concrete.Transactions.Read(
            static (connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE categories SET name='Changed';";
                command.ExecuteNonQuery();
                return 0;
            }));

        Assert.Equal(8, error.SqliteErrorCode);
        Assert.Equal("Home", Assert.Single(session.Work.Read().Categories).Name);
    }

    [Fact]
    public void RepeatedOperationsReuseTheSameConnectionForEachRole()
    {
        using var session = CreateSession();
        var concrete = Assert.IsType<EncryptedWorkspaceStore.WorkspaceSession>(session);
        SqliteConnection? firstRead = null;
        SqliteConnection? firstWrite = null;

        concrete.Transactions.Read((connection, _) =>
        {
            firstRead = connection;
            return 0;
        });
        concrete.Transactions.Execute((connection, _) => firstWrite = connection);

        concrete.Transactions.Read((connection, _) =>
        {
            Assert.Same(firstRead, connection);
            return 0;
        });
        concrete.Transactions.Execute((connection, _) => Assert.Same(firstWrite, connection));

        Assert.NotSame(firstRead, firstWrite);
    }

    [Fact]
    public void FailedWriteRollsBackAndRetainedConnectionAcceptsLaterWork()
    {
        using var session = CreateSession();
        var concrete = Assert.IsType<EncryptedWorkspaceStore.WorkspaceSession>(session);

        Assert.Throws<InvalidOperationException>(() => concrete.Transactions.Execute(
            static (connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE categories SET name='Rolled back';";
                command.ExecuteNonQuery();
                throw new InvalidOperationException("Injected failure.");
            }));

        Assert.Equal("Home", Assert.Single(session.Work.Read().Categories).Name);
        var category = Assert.Single(session.Work.Read().Categories);
        session.Work.CreateProject("Garden", "", category.Id, null);
        Assert.Equal("Garden", Assert.Single(session.Work.Read().Projects).Title);
    }

    [Fact]
    public async Task ConcurrentCallersEnterOnlyOneConnectionCallbackAtATime()
    {
        using var session = CreateSession();
        var concrete = Assert.IsType<EncryptedWorkspaceStore.WorkspaceSession>(session);
        using var firstEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        var cancellationToken = TestContext.Current.CancellationToken;
        var activeCallbacks = 0;
        var maximumActiveCallbacks = 0;

        var first = Task.Run(() => concrete.Transactions.Read((_, _) =>
        {
            var active = Interlocked.Increment(ref activeCallbacks);
            InterlockedExtensions.Max(ref maximumActiveCallbacks, active);
            firstEntered.Set();
            Assert.True(releaseFirst.Wait(TimeSpan.FromSeconds(5), cancellationToken));
            Interlocked.Decrement(ref activeCallbacks);
            return 0;
        }), cancellationToken);
        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(5), cancellationToken));

        var second = Task.Run(() =>
        {
            secondStarted.Set();
            return concrete.Transactions.Read((_, _) =>
            {
                var active = Interlocked.Increment(ref activeCallbacks);
                InterlockedExtensions.Max(ref maximumActiveCallbacks, active);
                secondEntered.Set();
                Interlocked.Decrement(ref activeCallbacks);
                return 0;
            });
        }, cancellationToken);

        Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(5), cancellationToken));
        Assert.False(secondEntered.IsSet);
        releaseFirst.Set();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        Assert.Equal(1, maximumActiveCallbacks);
        Assert.True(secondEntered.IsSet);
    }

    [Fact]
    public async Task DisposeDrainsActiveWorkClosesBothConnectionsAndRejectsLaterWork()
    {
        var session = CreateSession();
        var concrete = Assert.IsType<EncryptedWorkspaceStore.WorkspaceSession>(session);
        SqliteConnection? readConnection = null;
        SqliteConnection? writeConnection = null;
        using var operationEntered = new ManualResetEventSlim();
        using var releaseOperation = new ManualResetEventSlim();
        using var disposeStarted = new ManualResetEventSlim();
        var cancellationToken = TestContext.Current.CancellationToken;

        concrete.Transactions.Execute((connection, _) => writeConnection = connection);
        var operation = Task.Run(() => concrete.Transactions.Read((connection, _) =>
        {
            readConnection = connection;
            operationEntered.Set();
            Assert.True(releaseOperation.Wait(TimeSpan.FromSeconds(5), cancellationToken));
            return 0;
        }), cancellationToken);
        Assert.True(operationEntered.Wait(TimeSpan.FromSeconds(5), cancellationToken));
        var disposal = Task.Run(() =>
        {
            disposeStarted.Set();
            session.Dispose();
        }, cancellationToken);
        Assert.True(disposeStarted.Wait(TimeSpan.FromSeconds(5), cancellationToken));
        Assert.False(disposal.IsCompleted);

        releaseOperation.Set();
        await Task.WhenAll(operation, disposal).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        Assert.Equal(ConnectionState.Closed, readConnection!.State);
        Assert.Equal(ConnectionState.Closed, writeConnection!.State);
        Assert.Throws<ObjectDisposedException>(() => session.Work.Read());
        Assert.Throws<ObjectDisposedException>(() => concrete.Transactions.Execute(static (_, _) => { }));
        session.Dispose();
    }

    [Fact]
    public void PassphraseRotationClosesOriginalPairAndReturnsFreshUsablePair()
    {
        var session = CreateSession();
        var concrete = Assert.IsType<EncryptedWorkspaceStore.WorkspaceSession>(session);
        var originalConnections = CaptureConnections(concrete);
        var currentPassphrase = Assert.IsType<WorkspacePassphrase>(
            WorkspacePassphrase.ForUnlock("private synthetic password"));
        var newPassphrase = Assert.IsType<WorkspacePassphrase>(
            WorkspacePassphrase.Create(
                "different private synthetic password",
                "different private synthetic password").Passphrase);

        var result = session.RotatePassphrase(currentPassphrase, newPassphrase);
        using var rotated = Assert.IsType<EncryptedWorkspaceStore.WorkspaceSession>(result.Session);
        var rotatedConnections = CaptureConnections(rotated);

        Assert.Equal(PassphraseRotationStatus.Rotated, result.Status);
        Assert.Equal(ConnectionState.Closed, originalConnections.Read.State);
        Assert.Equal(ConnectionState.Closed, originalConnections.Write.State);
        Assert.NotSame(originalConnections.Read, rotatedConnections.Read);
        Assert.NotSame(originalConnections.Write, rotatedConnections.Write);
        Assert.Equal("Home", Assert.Single(rotated.Work.Read().Categories).Name);
    }

    [Fact]
    public void RecoveryRestoreClosesOriginalPairAndReturnsFreshUsablePair()
    {
        var recoveryDirectory = Path.Combine(_directory, "recovery");
        var openedModes = new List<SqliteOpenMode>();
        var store = CreateCountingStore(openedModes);
        var session = store.Create(
            WorkspacePath,
            _passphrase,
            CategoryName.Create("Home").CategoryName!).Session!;
        openedModes.Clear();
        var point = session.Recovery.CreateRecoveryPoint(recoveryDirectory);
        Assert.Empty(openedModes);
        var category = Assert.Single(session.Work.Read().Categories);
        session.Work.CreateProject("Discarded by restore", "", category.Id, null);
        var concrete = Assert.IsType<EncryptedWorkspaceStore.WorkspaceSession>(session);
        var originalConnections = CaptureConnections(concrete);

        var result = session.Recovery.Restore(
            Assert.IsType<string>(point.RecoveryPointPath),
            recoveryDirectory);
        using var restored = Assert.IsType<EncryptedWorkspaceStore.WorkspaceSession>(result.Session);
        var restoredConnections = CaptureConnections(restored);

        Assert.Equal(WorkspaceRestoreStatus.Restored, result.Status);
        Assert.Equal(ConnectionState.Closed, originalConnections.Read.State);
        Assert.Equal(ConnectionState.Closed, originalConnections.Write.State);
        Assert.NotSame(originalConnections.Read, restoredConnections.Read);
        Assert.NotSame(originalConnections.Write, restoredConnections.Write);
        Assert.Equal([SqliteOpenMode.ReadOnly, SqliteOpenMode.ReadWrite], openedModes);
        Assert.Empty(restored.Work.Read().Projects);
    }

    [Fact]
    public void EmptyBinUsesBoundedMaintenanceConnectionWithoutChangingWriterLockMode()
    {
        var openedModes = new List<SqliteOpenMode>();
        var store = CreateCountingStore(openedModes);
        using var session = store.Create(
            WorkspacePath,
            _passphrase,
            CategoryName.Create("Home").CategoryName!).Session!;
        var concrete = Assert.IsType<EncryptedWorkspaceStore.WorkspaceSession>(session);
        var retainedConnections = CaptureConnections(concrete);
        var recoveryDirectory = Path.Combine(_directory, "recovery");
        Assert.Equal(
            RecoveryDirectoryConfigurationStatus.Configured,
            session.Recovery.ConfigureAutomaticRecoveryDirectory(recoveryDirectory).Status);
        var category = Assert.Single(session.Work.Read().Categories);
        var task = session.Work.CreateStandaloneTask("Remove", "", category.Id, null);
        session.Work.MoveTaskToBin(task.Id);
        var preview = session.Work.PreviewEmptyBin();
        openedModes.Clear();

        var result = session.Work.EmptyBin(preview);
        string? writerLockMode = null;
        concrete.Transactions.Execute((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "PRAGMA locking_mode;";
            writerLockMode = Assert.IsType<string>(command.ExecuteScalar());
        });

        Assert.Equal(EmptyBinStatus.Emptied, result.Status);
        Assert.Equal([SqliteOpenMode.ReadWrite], openedModes);
        Assert.Equal(ConnectionState.Open, retainedConnections.Write.State);
        Assert.Equal("normal", writerLockMode, ignoreCase: true);
        Assert.True(session.Work.PreviewEmptyBin().IsEmpty);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string WorkspacePath => Path.Combine(_directory, "workspace.orb");

    private IWorkspaceSession CreateSession() =>
        new EncryptedWorkspaceStore().Create(
            WorkspacePath,
            _passphrase,
            CategoryName.Create("Home").CategoryName!).Session!;

    private static EncryptedWorkspaceStore CreateCountingStore(List<SqliteOpenMode> openedModes) =>
        new(
            new SystemIdentifierGenerator(),
            new WorkspaceFileOperations(),
            TimeProvider.System,
            connectionOpener: (path, passphrase, mode) =>
            {
                openedModes.Add(mode);
                return EncryptedWorkspaceStore.OpenConnection(path, passphrase, mode);
            });

    private static (SqliteConnection Read, SqliteConnection Write) CaptureConnections(
        EncryptedWorkspaceStore.WorkspaceSession session)
    {
        SqliteConnection? read = null;
        SqliteConnection? write = null;
        session.Transactions.Read((connection, _) =>
        {
            read = connection;
            return 0;
        });
        session.Transactions.Execute((connection, _) => write = connection);
        return (Assert.IsType<SqliteConnection>(read), Assert.IsType<SqliteConnection>(write));
    }

    private static class InterlockedExtensions
    {
        internal static void Max(ref int location, int value)
        {
            var current = Volatile.Read(ref location);
            while (current < value)
            {
                var observed = Interlocked.CompareExchange(ref location, value, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }
}
