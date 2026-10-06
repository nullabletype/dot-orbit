using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DotOrbit.Core.Workspaces;
using DotOrbit.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DotOrbit.Storage.Sqlite.Tests;

public sealed class EncryptedWorkspaceRecoveryTests
{
    private const string ValidPassphrase = "correct horse battery";

    [Fact]
    public void AutomaticRecoveryDirectoryPersistsAcrossWorkspaceSessions()
    {
        using var fixture = new RecoveryFixture();
        var session = fixture.CreateWorkspace("Current");

        var configured = session.Recovery.ConfigureAutomaticRecoveryDirectory(
            fixture.RecoveryDirectory);
        session.Dispose();
        using var reopened = fixture.OpenWorkspace();

        Assert.Equal(RecoveryDirectoryConfigurationStatus.Configured, configured.Status);
        Assert.Equal(
            Path.GetFullPath(fixture.RecoveryDirectory),
            reopened.Recovery.AutomaticRecoveryDirectoryPath);
    }

    [Fact]
    public void FailedDirectoryConfigurationPreservesThePreviousSelection()
    {
        using var fixture = new RecoveryFixture();
        using var session = fixture.CreateWorkspace("Current");
        var first = session.Recovery.ConfigureAutomaticRecoveryDirectory(
            fixture.RecoveryDirectory);
        fixture.FileOperations.Failure = FailurePoint.StatePublish;

        var failed = session.Recovery.ConfigureAutomaticRecoveryDirectory(
            Path.Combine(fixture.Directory, "other-recovery"));

        Assert.Equal(RecoveryDirectoryConfigurationStatus.Configured, first.Status);
        Assert.Equal(RecoveryDirectoryConfigurationStatus.Failed, failed.Status);
        Assert.Equal(
            Path.GetFullPath(fixture.RecoveryDirectory),
            session.Recovery.AutomaticRecoveryDirectoryPath);
    }

    [Fact]
    public void CommittedChangesCreateImmediatelyThenCoalesceUntilTheHourlyBoundary()
    {
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero));
        using var fixture = new RecoveryFixture(timeProvider: time);
        using var session = fixture.CreateWorkspace("Before");
        var concreteSession = Assert.IsType<EncryptedWorkspaceStore.WorkspaceSession>(session);
        session.Recovery.ConfigureAutomaticRecoveryDirectory(fixture.RecoveryDirectory);

        var first = ChangeFirstCategory(
            concreteSession.Transactions,
            "First committed value");
        time.Advance(TimeSpan.FromMinutes(30));
        var second = ChangeFirstCategory(
            concreteSession.Transactions,
            "Second committed value");
        time.Advance(TimeSpan.FromMinutes(29) + TimeSpan.FromSeconds(59));

        Assert.Equal(AutomaticRecoveryAttempt.Created, first);
        Assert.Equal(AutomaticRecoveryAttempt.Scheduled, second);
        Assert.Single(GetAutomaticRecoveryFiles(fixture.RecoveryDirectory));

        time.Advance(TimeSpan.FromSeconds(1));

        var automaticPoints = GetAutomaticRecoveryFiles(fixture.RecoveryDirectory);
        Assert.Equal(2, automaticPoints.Length);
        var latest = automaticPoints.Order(StringComparer.Ordinal).Last();
        var opened = fixture.Store.Open(latest, UnlockPassphrase(ValidPassphrase));
        using var latestSession = opened.Session;
        Assert.Equal(WorkspaceOpenStatus.Opened, opened.Status);
        Assert.Equal("Second committed value", latestSession?.FirstCategoryName);
    }

    [Fact]
    public void AutomaticRecoveryFileNameFitsPortablePathBudget()
    {
        using var fixture = new RecoveryFixture();
        using var session = fixture.CreateWorkspace("Before");
        var concreteSession = Assert.IsType<EncryptedWorkspaceStore.WorkspaceSession>(session);
        session.Recovery.ConfigureAutomaticRecoveryDirectory(fixture.RecoveryDirectory);
        fixture.FileOperations.MaximumPathLength = fixture.RecoveryDirectory.Length + 128;

        var result = ChangeFirstCategory(concreteSession.Transactions, "After");

        Assert.Equal(AutomaticRecoveryAttempt.Created, result);
        var recoveryPath = Assert.Single(GetAutomaticRecoveryFiles(fixture.RecoveryDirectory));
        Assert.True(OpensWithCategory(fixture.Store, recoveryPath, "After"));
    }

    [Fact]
    public void TransactionCoordinatorNotifiesRecoveryOnlyAfterCommit()
    {
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero));
        using var fixture = new RecoveryFixture(timeProvider: time);
        using var session = fixture.CreateWorkspace("Before");
        var concreteSession = Assert.IsType<EncryptedWorkspaceStore.WorkspaceSession>(session);
        session.Recovery.ConfigureAutomaticRecoveryDirectory(fixture.RecoveryDirectory);

        var result = concreteSession.Transactions.Execute(
            (connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE categories SET name = 'After commit';";
                Assert.Equal(1, command.ExecuteNonQuery());
            });

        Assert.Equal(AutomaticRecoveryAttempt.Created, result);
        var recoveryPath = Assert.Single(GetAutomaticRecoveryFiles(fixture.RecoveryDirectory));
        Assert.True(OpensWithCategory(fixture.Store, recoveryPath, "After commit"));
    }

    [Fact]
    public void SuccessiveCommitsAtTheSameInstantUseDistinctRecoveryGenerations()
    {
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero));
        using var fixture = new RecoveryFixture(timeProvider: time);
        using var session = fixture.CreateWorkspace("Before");
        var concreteSession = Assert.IsType<EncryptedWorkspaceStore.WorkspaceSession>(session);
        session.Recovery.ConfigureAutomaticRecoveryDirectory(fixture.RecoveryDirectory);

        var first = ChangeFirstCategory(concreteSession.Transactions, "First");
        var second = ChangeFirstCategory(concreteSession.Transactions, "Second");

        Assert.Equal(AutomaticRecoveryAttempt.Created, first);
        Assert.Equal(AutomaticRecoveryAttempt.Scheduled, second);
        Assert.Single(GetAutomaticRecoveryFiles(fixture.RecoveryDirectory));

        time.Advance(TimeSpan.FromHours(1));

        var points = GetAutomaticRecoveryFiles(fixture.RecoveryDirectory);
        Assert.Equal(2, points.Length);
        Assert.True(
            OpensWithCategory(
                fixture.Store,
                points.Order(StringComparer.Ordinal).Last(),
                "Second"));
    }

    [Fact]
    public void TransactionFailureRollsBackAndDoesNotScheduleRecovery()
    {
        using var fixture = new RecoveryFixture();
        using var session = fixture.CreateWorkspace("Before");
        var concreteSession = Assert.IsType<EncryptedWorkspaceStore.WorkspaceSession>(session);
        session.Recovery.ConfigureAutomaticRecoveryDirectory(fixture.RecoveryDirectory);

        Assert.Throws<InvalidOperationException>(
            () => concreteSession.Transactions.Execute(
                (connection, transaction) =>
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "UPDATE categories SET name = 'Must roll back';";
                    Assert.Equal(1, command.ExecuteNonQuery());
                    throw new InvalidOperationException("Injected operation failure.");
                }));

        Assert.Empty(GetAutomaticRecoveryFiles(fixture.RecoveryDirectory));
        using var reopened = fixture.OpenWorkspace();
        Assert.Equal("Before", reopened.FirstCategoryName);
    }

    [Fact]
    public void FailedStoredDataChangeDoesNotScheduleRecovery()
    {
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero));
        using var fixture = new RecoveryFixture(timeProvider: time);
        using var session = fixture.CreateWorkspace("Current");
        var recovery = Assert.IsType<EncryptedWorkspaceRecovery>(session.Recovery);
        session.Recovery.ConfigureAutomaticRecoveryDirectory(fixture.RecoveryDirectory);

        var result = recovery.StoredDataChangeCompleted(StoredDataChangeOutcome.Failed);
        time.Advance(TimeSpan.FromHours(2));

        Assert.Equal(AutomaticRecoveryAttempt.Ignored, result);
        Assert.Empty(GetAutomaticRecoveryFiles(fixture.RecoveryDirectory));
    }

    [Fact]
    public void WorkspacesSharingDirectoryAndPassphraseKeepIndependentCadenceAndRetention()
    {
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        using var first = new RecoveryFixture(timeProvider: time);
        using var second = new RecoveryFixture(
            timeProvider: time,
            recoveryDirectoryPath: first.RecoveryDirectory);
        using var firstSession = first.CreateWorkspace("First workspace");
        using var secondSession = second.CreateWorkspace("Second workspace");
        var firstRecovery = Assert.IsType<EncryptedWorkspaceRecovery>(firstSession.Recovery);
        var secondRecovery = Assert.IsType<EncryptedWorkspaceRecovery>(secondSession.Recovery);
        firstSession.Recovery.ConfigureAutomaticRecoveryDirectory(first.RecoveryDirectory);
        secondSession.Recovery.ConfigureAutomaticRecoveryDirectory(first.RecoveryDirectory);

        Assert.Equal(
            AutomaticRecoveryAttempt.Created,
            firstRecovery.StoredDataChangeCompleted(StoredDataChangeOutcome.Committed));
        Assert.Equal(
            AutomaticRecoveryAttempt.Created,
            secondRecovery.StoredDataChangeCompleted(StoredDataChangeOutcome.Committed));
        var secondPoint = Assert.Single(
            GetAutomaticRecoveryFiles(first.RecoveryDirectory),
            path => OpensWithCategory(second.Store, path, "Second workspace"));

        for (var hour = 0; hour < 25; hour++)
        {
            time.Advance(TimeSpan.FromHours(1));
            firstRecovery.StoredDataChangeCompleted(StoredDataChangeOutcome.Committed);
        }

        Assert.True(File.Exists(secondPoint));
        Assert.True(OpensWithCategory(second.Store, secondPoint, "Second workspace"));
    }

    [Fact]
    public void ClockRollbackLimitsCoalescingDelayToOneHour()
    {
        var start = new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(start);
        using var fixture = new RecoveryFixture(timeProvider: time);
        using var session = fixture.CreateWorkspace("Current");
        var recovery = Assert.IsType<EncryptedWorkspaceRecovery>(session.Recovery);
        session.Recovery.ConfigureAutomaticRecoveryDirectory(fixture.RecoveryDirectory);
        recovery.StoredDataChangeCompleted(StoredDataChangeOutcome.Committed);
        time.SetUtcNow(start.AddYears(-5));

        var scheduled = recovery.StoredDataChangeCompleted(StoredDataChangeOutcome.Committed);
        time.Advance(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(59));
        Assert.Single(GetAutomaticRecoveryFiles(fixture.RecoveryDirectory));

        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(AutomaticRecoveryAttempt.Scheduled, scheduled);
        Assert.Equal(2, GetAutomaticRecoveryFiles(fixture.RecoveryDirectory).Length);
    }

    [Fact]
    public void PendingChangeCreatesRecoveryWhenDirectoryIsConfiguredLater()
    {
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero));
        using var fixture = new RecoveryFixture(timeProvider: time);
        using var session = fixture.CreateWorkspace("Current");
        var recovery = Assert.IsType<EncryptedWorkspaceRecovery>(session.Recovery);

        var pending = recovery.StoredDataChangeCompleted(StoredDataChangeOutcome.Committed);
        var configured = session.Recovery.ConfigureAutomaticRecoveryDirectory(
            fixture.RecoveryDirectory);

        Assert.Equal(AutomaticRecoveryAttempt.NotConfigured, pending);
        Assert.Equal(RecoveryDirectoryConfigurationStatus.Configured, configured.Status);
        Assert.Single(GetAutomaticRecoveryFiles(fixture.RecoveryDirectory));
    }

    [Fact]
    public void CreationFailureKeepsPendingWorkForTheNextCommittedChange()
    {
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero));
        using var fixture = new RecoveryFixture(timeProvider: time);
        using var session = fixture.CreateWorkspace("Current");
        var recovery = Assert.IsType<EncryptedWorkspaceRecovery>(session.Recovery);
        session.Recovery.ConfigureAutomaticRecoveryDirectory(fixture.RecoveryDirectory);
        fixture.FileOperations.Failure = FailurePoint.Publish;

        var failed = recovery.StoredDataChangeCompleted(StoredDataChangeOutcome.Committed);
        fixture.FileOperations.Failure = FailurePoint.None;
        var retried = recovery.StoredDataChangeCompleted(StoredDataChangeOutcome.Committed);

        Assert.Equal(AutomaticRecoveryAttempt.Failed, failed);
        Assert.Equal(AutomaticRecoveryAttempt.Created, retried);
        Assert.Single(GetAutomaticRecoveryFiles(fixture.RecoveryDirectory));
    }

    [Fact]
    public void PendingStatePersistenceFailurePreservesGenerationAcrossRestart()
    {
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero));
        using var fixture = new RecoveryFixture(timeProvider: time);
        var session = fixture.CreateWorkspace("Current");
        var recovery = Assert.IsType<EncryptedWorkspaceRecovery>(session.Recovery);
        session.Recovery.ConfigureAutomaticRecoveryDirectory(fixture.RecoveryDirectory);

        Assert.Equal(
            AutomaticRecoveryAttempt.Created,
            recovery.StoredDataChangeCompleted(StoredDataChangeOutcome.Committed));
        time.Advance(TimeSpan.FromMinutes(10));
        fixture.FileOperations.Failure = FailurePoint.StateWrite;

        var result = recovery.StoredDataChangeCompleted(StoredDataChangeOutcome.Committed);

        Assert.Equal(AutomaticRecoveryAttempt.Created, result);
        var latestPath = Assert.Single(GetAutomaticRecoveryFiles(fixture.RecoveryDirectory));
        Assert.Contains("20260927T101000", Path.GetFileName(latestPath), StringComparison.Ordinal);
        session.Dispose();
        fixture.FileOperations.Failure = FailurePoint.Enumeration;

        using var reopened = fixture.OpenWorkspace();
        fixture.FileOperations.Failure = FailurePoint.None;
        var reopenedRecovery = Assert.IsType<EncryptedWorkspaceRecovery>(reopened.Recovery);
        Assert.Equal(
            AutomaticRecoveryAttempt.Scheduled,
            reopenedRecovery.StoredDataChangeCompleted(StoredDataChangeOutcome.Committed));

        time.Advance(TimeSpan.FromHours(1));

        Assert.Equal(2, GetAutomaticRecoveryFiles(fixture.RecoveryDirectory).Length);
    }

    [Fact]
    public void UnavailableDirectoryAtNextCommitRotatesRecoverySetAndResumes()
    {
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero));
        using var fixture = new RecoveryFixture(timeProvider: time);
        var session = fixture.CreateWorkspace("Before");
        var concreteSession = Assert.IsType<EncryptedWorkspaceStore.WorkspaceSession>(session);
        session.Recovery.ConfigureAutomaticRecoveryDirectory(fixture.RecoveryDirectory);

        Assert.Equal(
            AutomaticRecoveryAttempt.Created,
            ChangeFirstCategory(concreteSession.Transactions, "First"));
        time.Advance(TimeSpan.FromMinutes(10));
        fixture.FileOperations.Failure = FailurePoint.StateWrite;
        Assert.Equal(
            AutomaticRecoveryAttempt.Created,
            ChangeFirstCategory(concreteSession.Transactions, "Second"));
        var oldSetPoint = Assert.Single(GetAutomaticRecoveryFiles(fixture.RecoveryDirectory));
        session.Dispose();

        fixture.FileOperations.Failure = FailurePoint.Enumeration;
        var reopened = fixture.OpenWorkspace();
        var reopenedSession = Assert.IsType<EncryptedWorkspaceStore.WorkspaceSession>(reopened);
        Assert.Equal(
            AutomaticRecoveryAttempt.Failed,
            ChangeFirstCategory(reopenedSession.Transactions, "Third"));
        reopened.Dispose();

        Assert.True(File.Exists(oldSetPoint));
        Assert.Single(GetAutomaticRecoveryFiles(fixture.RecoveryDirectory));

        fixture.FileOperations.Failure = FailurePoint.None;
        using var resumed = fixture.OpenWorkspace();

        var allPoints = GetAutomaticRecoveryFiles(fixture.RecoveryDirectory);
        Assert.Equal(2, allPoints.Length);
        Assert.True(File.Exists(oldSetPoint));
        var newSetPoint = Assert.Single(allPoints, path => path != oldSetPoint);
        Assert.True(OpensWithCategory(fixture.Store, newSetPoint, "Third"));
    }

    [Fact]
    public void StateClearFailureDoesNotCreateARedundantPointAfterReopen()
    {
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero));
        using var fixture = new RecoveryFixture(timeProvider: time);
        fixture.FileOperations.FailStatePublishCall = 3;
        var session = fixture.CreateWorkspace("Current");
        var recovery = Assert.IsType<EncryptedWorkspaceRecovery>(session.Recovery);
        session.Recovery.ConfigureAutomaticRecoveryDirectory(fixture.RecoveryDirectory);

        Assert.Equal(
            AutomaticRecoveryAttempt.Created,
            recovery.StoredDataChangeCompleted(StoredDataChangeOutcome.Committed));
        session.Dispose();
        using var reopened = fixture.OpenWorkspace();
        time.Advance(TimeSpan.FromHours(2));

        Assert.Single(GetAutomaticRecoveryFiles(fixture.RecoveryDirectory));
        Assert.Equal(
            Path.GetFullPath(fixture.RecoveryDirectory),
            reopened.Recovery.AutomaticRecoveryDirectoryPath);
    }

    [Fact]
    public void RestartAfterClockRollbackClampsPersistedPendingWorkToOneHour()
    {
        var originalTime = new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(originalTime);
        using var fixture = new RecoveryFixture(timeProvider: time);
        var session = fixture.CreateWorkspace("Current");
        var recovery = Assert.IsType<EncryptedWorkspaceRecovery>(session.Recovery);
        session.Recovery.ConfigureAutomaticRecoveryDirectory(fixture.RecoveryDirectory);
        recovery.StoredDataChangeCompleted(StoredDataChangeOutcome.Committed);
        time.Advance(TimeSpan.FromMinutes(30));
        recovery.StoredDataChangeCompleted(StoredDataChangeOutcome.Committed);
        session.Dispose();
        time.SetUtcNow(originalTime.AddYears(-5));

        using var reopened = fixture.OpenWorkspace();
        time.Advance(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(59));
        Assert.Single(GetAutomaticRecoveryFiles(fixture.RecoveryDirectory));

        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(2, GetAutomaticRecoveryFiles(fixture.RecoveryDirectory).Length);
        Assert.NotNull(reopened);
    }

    [Fact]
    public async Task SessionDisposalDrainsCommittedTransactionBeforeClosingRecovery()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = new RecoveryFixture();
        var session = fixture.CreateWorkspace("Before");
        var concreteSession = Assert.IsType<EncryptedWorkspaceStore.WorkspaceSession>(session);
        session.Recovery.ConfigureAutomaticRecoveryDirectory(fixture.RecoveryDirectory);
        using var mutationReady = new SemaphoreSlim(0, 1);
        using var allowCommit = new SemaphoreSlim(0, 1);
        var transactionTask = Task.Run(
            () => concreteSession.Transactions.Execute(
                (connection, transaction) =>
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "UPDATE categories SET name = 'After commit';";
                    Assert.Equal(1, command.ExecuteNonQuery());
                    mutationReady.Release();
                    allowCommit.Wait(cancellationToken);
                }),
            cancellationToken);
        Assert.True(await mutationReady.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
        var disposeTask = Task.Run(session.Dispose, cancellationToken);

        try
        {
            var firstCompleted = await Task.WhenAny(
                disposeTask,
                Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken));
            Assert.NotSame(disposeTask, firstCompleted);
        }
        finally
        {
            allowCommit.Release();
        }

        Assert.Equal(AutomaticRecoveryAttempt.Created, await transactionTask);
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        var recoveryPath = Assert.Single(GetAutomaticRecoveryFiles(fixture.RecoveryDirectory));
        Assert.True(OpensWithCategory(fixture.Store, recoveryPath, "After commit"));
    }

    [Fact]
    public void DisposingSessionCancelsScheduledAutomaticRecovery()
    {
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero));
        using var fixture = new RecoveryFixture(timeProvider: time);
        var session = fixture.CreateWorkspace("Current");
        var recovery = Assert.IsType<EncryptedWorkspaceRecovery>(session.Recovery);
        session.Recovery.ConfigureAutomaticRecoveryDirectory(fixture.RecoveryDirectory);
        Assert.Equal(
            AutomaticRecoveryAttempt.Created,
            recovery.StoredDataChangeCompleted(StoredDataChangeOutcome.Committed));
        time.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(
            AutomaticRecoveryAttempt.Scheduled,
            recovery.StoredDataChangeCompleted(StoredDataChangeOutcome.Committed));

        session.Dispose();
        time.Advance(TimeSpan.FromHours(1));

        Assert.Single(GetAutomaticRecoveryFiles(fixture.RecoveryDirectory));
    }

    [Fact]
    public void ReopeningWorkspaceResumesDurablePendingRecoveryAtTheDueBoundary()
    {
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero));
        using var fixture = new RecoveryFixture(timeProvider: time);
        var session = fixture.CreateWorkspace("Initial");
        var recovery = Assert.IsType<EncryptedWorkspaceRecovery>(session.Recovery);
        session.Recovery.ConfigureAutomaticRecoveryDirectory(fixture.RecoveryDirectory);
        recovery.StoredDataChangeCompleted(StoredDataChangeOutcome.Committed);
        time.Advance(TimeSpan.FromMinutes(30));
        fixture.ChangeFirstCategory("Pending across restart");
        recovery.StoredDataChangeCompleted(StoredDataChangeOutcome.Committed);
        session.Dispose();

        using var reopened = fixture.OpenWorkspace();
        Assert.Equal(
            Path.GetFullPath(fixture.RecoveryDirectory),
            reopened.Recovery.AutomaticRecoveryDirectoryPath);
        Assert.Single(GetAutomaticRecoveryFiles(fixture.RecoveryDirectory));

        time.Advance(TimeSpan.FromMinutes(30));

        var automaticPoints = GetAutomaticRecoveryFiles(fixture.RecoveryDirectory);
        Assert.Equal(2, automaticPoints.Length);
        var latest = fixture.Store.Open(
            automaticPoints.Order(StringComparer.Ordinal).Last(),
            UnlockPassphrase(ValidPassphrase));
        using var latestSession = latest.Session;
        Assert.Equal(WorkspaceOpenStatus.Opened, latest.Status);
        Assert.Equal("Pending across restart", latestSession?.FirstCategoryName);
    }

    [Fact]
    public void CorruptRecoveryStateDoesNotPreventWorkspaceUnlock()
    {
        using var fixture = new RecoveryFixture();
        var session = fixture.CreateWorkspace("Current");
        session.Recovery.ConfigureAutomaticRecoveryDirectory(fixture.RecoveryDirectory);
        session.Dispose();
        File.WriteAllText(fixture.WorkspacePath + ".recovery-state.json", "{not-json");

        var opened = fixture.Store.Open(fixture.WorkspacePath, UnlockPassphrase(ValidPassphrase));
        using var reopened = opened.Session;

        Assert.Equal(WorkspaceOpenStatus.Opened, opened.Status);
        Assert.NotNull(reopened);
        Assert.Null(reopened.Recovery.AutomaticRecoveryDirectoryPath);
    }

    [Fact]
    public void UnavailableRecoveryDirectoryDoesNotPreventWorkspaceUnlockWithPendingWork()
    {
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero));
        using var fixture = new RecoveryFixture(timeProvider: time);
        var session = fixture.CreateWorkspace("Current");
        var recovery = Assert.IsType<EncryptedWorkspaceRecovery>(session.Recovery);
        session.Recovery.ConfigureAutomaticRecoveryDirectory(fixture.RecoveryDirectory);
        recovery.StoredDataChangeCompleted(StoredDataChangeOutcome.Committed);
        time.Advance(TimeSpan.FromMinutes(30));
        recovery.StoredDataChangeCompleted(StoredDataChangeOutcome.Committed);
        session.Dispose();
        fixture.FileOperations.Failure = FailurePoint.Enumeration;

        var opened = fixture.Store.Open(fixture.WorkspacePath, UnlockPassphrase(ValidPassphrase));
        using var reopened = opened.Session;

        Assert.Equal(WorkspaceOpenStatus.Opened, opened.Status);
        Assert.NotNull(reopened);
        Assert.Equal(
            Path.GetFullPath(fixture.RecoveryDirectory),
            reopened.Recovery.AutomaticRecoveryDirectoryPath);
    }

    [Fact]
    public void AutomaticCreationFailurePreservesTheExistingValidatedPoint()
    {
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero));
        using var fixture = new RecoveryFixture(timeProvider: time);
        using var session = fixture.CreateWorkspace("Initial");
        var recovery = Assert.IsType<EncryptedWorkspaceRecovery>(session.Recovery);
        session.Recovery.ConfigureAutomaticRecoveryDirectory(fixture.RecoveryDirectory);
        recovery.StoredDataChangeCompleted(StoredDataChangeOutcome.Committed);
        var existingPath = Assert.Single(GetAutomaticRecoveryFiles(fixture.RecoveryDirectory));
        var existingHash = Hash(existingPath);
        time.Advance(TimeSpan.FromHours(1));
        fixture.ChangeFirstCategory("Later");
        fixture.FileOperations.Failure = FailurePoint.Publish;

        var failed = recovery.StoredDataChangeCompleted(StoredDataChangeOutcome.Committed);

        Assert.Equal(AutomaticRecoveryAttempt.Failed, failed);
        Assert.Equal(existingPath, Assert.Single(GetAutomaticRecoveryFiles(fixture.RecoveryDirectory)));
        Assert.Equal(existingHash, Hash(existingPath));
    }

    [Fact]
    public void PruningFailureLeavesExtraValidatedPointsAndManualRecoveryUntouched()
    {
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        using var fixture = new RecoveryFixture(timeProvider: time);
        using var session = fixture.CreateWorkspace("Current");
        var recovery = Assert.IsType<EncryptedWorkspaceRecovery>(session.Recovery);
        session.Recovery.ConfigureAutomaticRecoveryDirectory(fixture.RecoveryDirectory);
        var manual = session.Recovery.CreateRecoveryPoint(fixture.RecoveryDirectory);
        var manualPath = Assert.IsType<string>(manual.RecoveryPointPath);
        var corruptAutomaticPath = Path.Combine(
            fixture.RecoveryDirectory,
            $"dot-orbit-auto-20200101T0000000000000Z-corrupt{EncryptedWorkspaceRecovery.RecoveryPointExtension}");
        File.WriteAllText(corruptAutomaticPath, "not an encrypted database");

        for (var hour = 0; hour < 24; hour++)
        {
            Assert.Equal(
                AutomaticRecoveryAttempt.Created,
                recovery.StoredDataChangeCompleted(StoredDataChangeOutcome.Committed));
            if (hour == 0)
            {
                var automaticName = Path.GetFileName(
                    Assert.Single(
                        GetAutomaticRecoveryFiles(fixture.RecoveryDirectory),
                        path => !string.Equals(
                            path,
                            corruptAutomaticPath,
                            StringComparison.Ordinal)));
                var lookalikeName = automaticName.Replace(
                    EncryptedWorkspaceRecovery.RecoveryPointExtension,
                    $"-lookalike{EncryptedWorkspaceRecovery.RecoveryPointExtension}",
                    StringComparison.Ordinal);
                File.Copy(
                    manualPath,
                    Path.Combine(fixture.RecoveryDirectory, lookalikeName));
            }

            time.Advance(TimeSpan.FromHours(1));
        }

        Assert.Equal(
            AutomaticRecoveryAttempt.Created,
            recovery.StoredDataChangeCompleted(StoredDataChangeOutcome.Committed));

        var prunedPoints = GetAutomaticRecoveryFiles(fixture.RecoveryDirectory);
        Assert.Equal(26, prunedPoints.Length);
        foreach (var path in prunedPoints)
        {
            if (string.Equals(path, corruptAutomaticPath, StringComparison.Ordinal)
                || path.Contains("-lookalike", StringComparison.Ordinal))
            {
                continue;
            }

            var opened = fixture.Store.Open(path, UnlockPassphrase(ValidPassphrase));
            using var recoverySession = opened.Session;
            Assert.Equal(WorkspaceOpenStatus.Opened, opened.Status);
        }

        time.Advance(TimeSpan.FromHours(1));
        fixture.FileOperations.Failure = FailurePoint.DeleteAutomatic;
        Assert.Equal(
            AutomaticRecoveryAttempt.Created,
            recovery.StoredDataChangeCompleted(StoredDataChangeOutcome.Committed));

        var automaticPoints = GetAutomaticRecoveryFiles(fixture.RecoveryDirectory);
        Assert.Equal(27, automaticPoints.Length);
        Assert.True(File.Exists(manualPath));
        Assert.True(File.Exists(corruptAutomaticPath));
        Assert.Contains(
            automaticPoints,
            path => path.Contains("-lookalike", StringComparison.Ordinal));
        foreach (var path in automaticPoints)
        {
            if (string.Equals(path, corruptAutomaticPath, StringComparison.Ordinal)
                || path.Contains("-lookalike", StringComparison.Ordinal))
            {
                continue;
            }

            var opened = fixture.Store.Open(path, UnlockPassphrase(ValidPassphrase));
            using var recoverySession = opened.Session;
            Assert.Equal(WorkspaceOpenStatus.Opened, opened.Status);
        }
    }

    [Fact]
    public void CreateRecoveryPointPublishesOnlyAValidatedEncryptedDatabase()
    {
        using var fixture = new RecoveryFixture();
        using var session = fixture.CreateWorkspace("Sensitive Category");
        var observedCandidate = false;
        fixture.FileOperations.BeforePublish = (candidate, target) =>
        {
            if (!target.EndsWith(EncryptedWorkspaceRecovery.RecoveryPointExtension, StringComparison.Ordinal))
            {
                return;
            }

            observedCandidate = true;
            Assert.False(File.Exists(target));
            Assert.True(File.Exists(candidate));
            AssertEncryptedWithoutPlaintext(candidate, "Sensitive Category");
        };

        var result = session.Recovery.CreateRecoveryPoint(fixture.RecoveryDirectory);

        Assert.Equal(RecoveryPointCreationStatus.Created, result.Status);
        var recoveryPointPath = Assert.IsType<string>(result.RecoveryPointPath);
        Assert.True(observedCandidate);
        Assert.True(File.Exists(recoveryPointPath));
        Assert.EndsWith(
            EncryptedWorkspaceRecovery.RecoveryPointExtension,
            recoveryPointPath,
            StringComparison.Ordinal);
        AssertEncryptedWithoutPlaintext(recoveryPointPath, "Sensitive Category");
        Assert.Empty(Directory.GetFiles(fixture.RecoveryDirectory, "*.creating"));

        var opened = fixture.Store.Open(recoveryPointPath, UnlockPassphrase(ValidPassphrase));
        using var recoverySession = opened.Session;
        Assert.Equal(WorkspaceOpenStatus.Opened, opened.Status);
        Assert.Equal("Sensitive Category", recoverySession?.FirstCategoryName);
    }

    [Fact]
    public void RestoreCreatesCurrentRecoveryThenAtomicallyReplacesAndReopensWorkspace()
    {
        using var fixture = new RecoveryFixture();
        var originalSession = fixture.CreateWorkspace("Original");
        var selected = originalSession.Recovery.CreateRecoveryPoint(fixture.RecoveryDirectory);
        originalSession.Dispose();
        fixture.ChangeFirstCategory("Current");
        using var currentSession = fixture.OpenWorkspace();

        var result = currentSession.Recovery.Restore(
            Assert.IsType<string>(selected.RecoveryPointPath),
            fixture.RecoveryDirectory);
        using var restoredSession = result.Session;

        Assert.Equal(WorkspaceRestoreStatus.Restored, result.Status);
        Assert.NotNull(restoredSession);
        Assert.Equal("Original", restoredSession.FirstCategoryName);
        var recoveryPoints = Directory.GetFiles(
            fixture.RecoveryDirectory,
            $"*{EncryptedWorkspaceRecovery.RecoveryPointExtension}");
        Assert.Equal(2, recoveryPoints.Length);
        var preRestorePath = Assert.Single(
            recoveryPoints,
            path => !string.Equals(path, selected.RecoveryPointPath, StringComparison.Ordinal));
        var preRestoreOpen = fixture.Store.Open(preRestorePath, UnlockPassphrase(ValidPassphrase));
        using var preRestoreSession = preRestoreOpen.Session;
        Assert.Equal(WorkspaceOpenStatus.Opened, preRestoreOpen.Status);
        Assert.Equal("Current", preRestoreSession?.FirstCategoryName);
        Assert.Empty(Directory.GetFiles(fixture.Directory, "*.rollback"));
        Assert.Empty(Directory.GetFiles(fixture.Directory, "*.creating"));
    }

    [Fact]
    public void RecoveryPointUsingAnotherPassphraseDoesNotChangeCurrentWorkspace()
    {
        using var fixture = new RecoveryFixture();
        using var currentSession = fixture.CreateWorkspace("Current");
        using var other = new RecoveryFixture("different valid passphrase");
        using var otherSession = other.CreateWorkspace("Other");
        var selected = otherSession.Recovery.CreateRecoveryPoint(other.RecoveryDirectory);
        var originalHash = Hash(fixture.WorkspacePath);

        var result = currentSession.Recovery.Restore(
            Assert.IsType<string>(selected.RecoveryPointPath),
            fixture.RecoveryDirectory);

        Assert.Equal(WorkspaceRestoreStatus.InvalidRecoveryPoint, result.Status);
        Assert.Null(result.Session);
        Assert.Equal(originalHash, Hash(fixture.WorkspacePath));
        Assert.Empty(GetFiles(fixture.RecoveryDirectory));
    }

    [Fact]
    public void WrongPassphraseWithRollbackNamedDatabaseDoesNotReplaceCurrentWorkspace()
    {
        using var fixture = new RecoveryFixture();
        using var currentSession = fixture.CreateWorkspace("Current");
        using var other = new RecoveryFixture("different valid passphrase");
        using var otherSession = other.CreateWorkspace("Other");
        var originalHash = Hash(fixture.WorkspacePath);
        var rollbackPath = Path.Combine(
            fixture.Directory,
            $".{Path.GetFileName(fixture.WorkspacePath)}.restore-planted.rollback");
        File.Copy(other.WorkspacePath, rollbackPath);

        var wrongPassphraseResult = fixture.Store.Open(
            fixture.WorkspacePath,
            UnlockPassphrase("different valid passphrase"));

        Assert.Equal(
            WorkspaceOpenStatus.InvalidPassphraseOrStore,
            wrongPassphraseResult.Status);
        Assert.Null(wrongPassphraseResult.Session);
        Assert.Equal(originalHash, Hash(fixture.WorkspacePath));
        var correctPassphraseResult = fixture.Store.Open(
            fixture.WorkspacePath,
            UnlockPassphrase(ValidPassphrase));
        using var reopenedSession = correctPassphraseResult.Session;
        Assert.Equal(WorkspaceOpenStatus.Opened, correctPassphraseResult.Status);
        Assert.Equal("Current", reopenedSession?.FirstCategoryName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DamagedOrTamperedRecoveryPointDoesNotChangeCurrentWorkspace(bool truncate)
    {
        using var fixture = new RecoveryFixture();
        using var session = fixture.CreateWorkspace("Current");
        var selected = session.Recovery.CreateRecoveryPoint(fixture.RecoveryDirectory);
        var selectedPath = Assert.IsType<string>(selected.RecoveryPointPath);
        var bytes = File.ReadAllBytes(selectedPath);
        if (truncate)
        {
            File.WriteAllBytes(selectedPath, bytes[..Math.Min(bytes.Length, 64)]);
        }
        else
        {
            bytes[Math.Min(128, bytes.Length - 1)] ^= 0x5A;
            File.WriteAllBytes(selectedPath, bytes);
        }

        var originalHash = Hash(fixture.WorkspacePath);
        var result = session.Recovery.Restore(selectedPath, fixture.RecoveryDirectory);

        Assert.Equal(WorkspaceRestoreStatus.InvalidRecoveryPoint, result.Status);
        Assert.Null(result.Session);
        Assert.Equal(originalHash, Hash(fixture.WorkspacePath));
        Assert.Single(Directory.GetFiles(fixture.RecoveryDirectory));
    }

    [Fact]
    public void NewerSchemaRecoveryPointIsRefusedWithoutChangingCurrentWorkspace()
    {
        using var fixture = new RecoveryFixture();
        using var session = fixture.CreateWorkspace("Current");
        var selected = session.Recovery.CreateRecoveryPoint(fixture.RecoveryDirectory);
        var selectedPath = Assert.IsType<string>(selected.RecoveryPointPath);
        fixture.SetSchemaVersion(
            selectedPath,
            EncryptedWorkspaceStore.CurrentSchemaVersion + 1);
        var originalHash = Hash(fixture.WorkspacePath);

        var result = session.Recovery.Restore(selectedPath, fixture.RecoveryDirectory);

        Assert.Equal(WorkspaceRestoreStatus.UnsupportedSchema, result.Status);
        Assert.Null(result.Session);
        Assert.Equal(originalHash, Hash(fixture.WorkspacePath));
    }

    [Theory]
    [InlineData(FailurePoint.Directory)]
    [InlineData(FailurePoint.Flush)]
    [InlineData(FailurePoint.CorruptAfterFlush)]
    [InlineData(FailurePoint.Publish)]
    public void RecoveryCreationFailureNeverPublishesAPartialRecoveryPoint(FailurePoint failure)
    {
        using var fixture = new RecoveryFixture();
        using var session = fixture.CreateWorkspace("Current");
        fixture.FileOperations.Failure = failure;
        var originalHash = Hash(fixture.WorkspacePath);

        var result = session.Recovery.CreateRecoveryPoint(fixture.RecoveryDirectory);

        Assert.Equal(RecoveryPointCreationStatus.Failed, result.Status);
        Assert.Null(result.RecoveryPointPath);
        Assert.Equal(originalHash, Hash(fixture.WorkspacePath));
        Assert.Empty(GetFiles(fixture.RecoveryDirectory));
    }

    [Fact]
    public void InterruptionAfterAtomicPublicationLeavesAValidatedRecoveryPointUsable()
    {
        using var fixture = new RecoveryFixture();
        using var session = fixture.CreateWorkspace("Current");
        fixture.FileOperations.Failure = FailurePoint.AfterPublish;

        var result = session.Recovery.CreateRecoveryPoint(fixture.RecoveryDirectory);

        Assert.Equal(RecoveryPointCreationStatus.Failed, result.Status);
        var recoveryPointPath = Assert.Single(GetFiles(fixture.RecoveryDirectory));
        Assert.EndsWith(
            EncryptedWorkspaceRecovery.RecoveryPointExtension,
            recoveryPointPath,
            StringComparison.Ordinal);
        var opened = fixture.Store.Open(recoveryPointPath, UnlockPassphrase(ValidPassphrase));
        using var recoverySession = opened.Session;
        Assert.Equal(WorkspaceOpenStatus.Opened, opened.Status);
        Assert.Equal("Current", recoverySession?.FirstCategoryName);
    }

    [Theory]
    [InlineData(FailurePoint.Copy)]
    [InlineData(FailurePoint.PermissionCopy)]
    public void InterruptedRestoreCopyLeavesCurrentWorkspaceAndSelectedRecoveryUsable(
        FailurePoint failure)
    {
        using var fixture = new RecoveryFixture();
        using var session = fixture.CreateWorkspace("Current");
        var selected = session.Recovery.CreateRecoveryPoint(fixture.RecoveryDirectory);
        var selectedPath = Assert.IsType<string>(selected.RecoveryPointPath);
        var selectedHash = Hash(selectedPath);
        var originalHash = Hash(fixture.WorkspacePath);
        fixture.FileOperations.Failure = failure;

        var result = session.Recovery.Restore(selectedPath, fixture.RecoveryDirectory);

        Assert.Equal(WorkspaceRestoreStatus.Failed, result.Status);
        Assert.Equal(originalHash, Hash(fixture.WorkspacePath));
        Assert.Equal(selectedHash, Hash(selectedPath));
        Assert.Empty(Directory.GetFiles(fixture.Directory, "*.creating"));
    }

    [Theory]
    [InlineData(FailurePoint.Replace)]
    [InlineData(FailurePoint.PermissionReplace)]
    public void AtomicReplacementFailureReopensCurrentWorkspaceAndKeepsPreRestoreRecovery(
        FailurePoint failure)
    {
        using var fixture = new RecoveryFixture();
        var originalSession = fixture.CreateWorkspace("Selected");
        var selected = originalSession.Recovery.CreateRecoveryPoint(fixture.RecoveryDirectory);
        originalSession.Dispose();
        fixture.ChangeFirstCategory("Current");
        using var currentSession = fixture.OpenWorkspace();
        var originalHash = Hash(fixture.WorkspacePath);
        fixture.FileOperations.Failure = failure;

        var result = currentSession.Recovery.Restore(
            Assert.IsType<string>(selected.RecoveryPointPath),
            fixture.RecoveryDirectory);
        using var reopenedSession = result.Session;

        Assert.Equal(WorkspaceRestoreStatus.Failed, result.Status);
        Assert.NotNull(reopenedSession);
        Assert.Equal("Current", reopenedSession.FirstCategoryName);
        Assert.Equal(originalHash, Hash(fixture.WorkspacePath));
        Assert.Equal(
            2,
            Directory.GetFiles(
                fixture.RecoveryDirectory,
                $"*{EncryptedWorkspaceRecovery.RecoveryPointExtension}").Length);
    }

    [Fact]
    public void InterruptionAfterAtomicReplacementLeavesPublishedWorkspaceUsable()
    {
        using var fixture = new RecoveryFixture();
        var originalSession = fixture.CreateWorkspace("Selected");
        var selected = originalSession.Recovery.CreateRecoveryPoint(fixture.RecoveryDirectory);
        originalSession.Dispose();
        fixture.ChangeFirstCategory("Current");
        using var currentSession = fixture.OpenWorkspace();
        fixture.FileOperations.Failure = FailurePoint.ThrowAfterReplacementPublication;

        var result = currentSession.Recovery.Restore(
            Assert.IsType<string>(selected.RecoveryPointPath),
            fixture.RecoveryDirectory);
        using var restoredSession = result.Session;

        Assert.Equal(WorkspaceRestoreStatus.Restored, result.Status);
        Assert.NotNull(restoredSession);
        Assert.Equal("Selected", restoredSession.FirstCategoryName);
        Assert.Empty(Directory.GetFiles(fixture.Directory, "*.rollback"));
        Assert.Empty(Directory.GetFiles(fixture.Directory, "*.creating"));
    }

    [Fact]
    public void DisposedSessionInvalidatesItsRecoveryCapability()
    {
        using var fixture = new RecoveryFixture();
        var session = fixture.CreateWorkspace("Current");
        session.Dispose();

        Assert.Throws<ObjectDisposedException>(
            () => session.Recovery.CreateRecoveryPoint(fixture.RecoveryDirectory));
    }

    [Fact]
    public void PreRestoreRecoveryFailureStopsBeforeWorkspaceReplacement()
    {
        using var fixture = new RecoveryFixture();
        using var session = fixture.CreateWorkspace("Current");
        var selected = session.Recovery.CreateRecoveryPoint(fixture.RecoveryDirectory);
        var selectedPath = Assert.IsType<string>(selected.RecoveryPointPath);
        var originalHash = Hash(fixture.WorkspacePath);
        fixture.FileOperations.Failure = FailurePoint.Publish;

        var result = session.Recovery.Restore(selectedPath, fixture.RecoveryDirectory);

        Assert.Equal(WorkspaceRestoreStatus.PreRestoreRecoveryFailed, result.Status);
        Assert.Null(result.Session);
        Assert.Equal(originalHash, Hash(fixture.WorkspacePath));
        Assert.Equal(selectedPath, Assert.Single(Directory.GetFiles(fixture.RecoveryDirectory)));
    }

    private static void AssertEncryptedWithoutPlaintext(string path, string plaintext)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.AsSpan().StartsWith("SQLite format 3\0"u8));
        Assert.DoesNotContain(plaintext, Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    private static WorkspacePassphrase UnlockPassphrase(string value) =>
        Assert.IsType<WorkspacePassphrase>(WorkspacePassphrase.ForUnlock(value));

    private static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string[] GetFiles(string directory) =>
        System.IO.Directory.Exists(directory)
            ? System.IO.Directory.GetFiles(directory)
            : [];

    private static string[] GetAutomaticRecoveryFiles(string directory) =>
        System.IO.Directory.Exists(directory)
            ? System.IO.Directory.GetFiles(
                directory,
                $"dot-orbit-auto-*{EncryptedWorkspaceRecovery.RecoveryPointExtension}")
            : [];

    private static bool OpensWithCategory(
        EncryptedWorkspaceStore store,
        string path,
        string expectedCategory)
    {
        var opened = store.Open(path, UnlockPassphrase(ValidPassphrase));
        using var session = opened.Session;
        return opened.Status == WorkspaceOpenStatus.Opened
            && string.Equals(
                expectedCategory,
                session?.FirstCategoryName,
                StringComparison.Ordinal);
    }

    private static AutomaticRecoveryAttempt ChangeFirstCategory(
        WorkspaceTransactionCoordinator transactions,
        string category) =>
        transactions.Execute(
            (connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE categories SET name = $name;";
                command.Parameters.AddWithValue("$name", category);
                Assert.Equal(1, command.ExecuteNonQuery());
            });

    public enum FailurePoint
    {
        None,
        Directory,
        Flush,
        CorruptAfterFlush,
        Publish,
        AfterPublish,
        Copy,
        PermissionCopy,
        Replace,
        PermissionReplace,
        ThrowAfterReplacementPublication,
        StateWrite,
        StatePublish,
        DeleteAutomatic,
        Enumeration,
    }

    private sealed class RecoveryFixture : IDisposable
    {
        private readonly string _passphrase;

        public RecoveryFixture(
            string passphrase = ValidPassphrase,
            TimeProvider? timeProvider = null,
            string? recoveryDirectoryPath = null)
        {
            _passphrase = passphrase;
            Directory = Path.Combine(
                Path.GetTempPath(),
                $"dot-orbit-recovery-tests-{Guid.NewGuid():N}");
            RecoveryDirectory = recoveryDirectoryPath ?? Path.Combine(Directory, "recovery");
            WorkspacePath = Path.Combine(Directory, "workspace.db");
            System.IO.Directory.CreateDirectory(Directory);
            FileOperations = new FaultInjectingFileOperations();
            Store = new EncryptedWorkspaceStore(
                new SystemIdentifierGenerator(),
                FileOperations,
                timeProvider);
        }

        public string Directory { get; }

        public FaultInjectingFileOperations FileOperations { get; }

        public string RecoveryDirectory { get; }

        public EncryptedWorkspaceStore Store { get; }

        public string WorkspacePath { get; }

        public IWorkspaceSession CreateWorkspace(string category)
        {
            var result = Store.Create(
                WorkspacePath,
                CreatePassphrase(_passphrase),
                CreateCategory(category));
            Assert.Equal(WorkspaceCreationStatus.Created, result.Status);
            return Assert.IsAssignableFrom<IWorkspaceSession>(result.Session);
        }

        public IWorkspaceSession OpenWorkspace()
        {
            var result = Store.Open(WorkspacePath, UnlockPassphrase(_passphrase));
            Assert.Equal(WorkspaceOpenStatus.Opened, result.Status);
            return Assert.IsAssignableFrom<IWorkspaceSession>(result.Session);
        }

        public void ChangeFirstCategory(string category)
        {
            using var connection = OpenInspectionConnection(WorkspacePath, _passphrase);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE categories SET name = $name WHERE position = 0;";
            command.Parameters.AddWithValue("$name", category);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        public void SetSchemaVersion(string path, int version)
        {
            using var connection = OpenInspectionConnection(path, _passphrase);
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA user_version = {version.ToString(CultureInfo.InvariantCulture)};";
            command.ExecuteNonQuery();
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);

        private static WorkspacePassphrase CreatePassphrase(string value) =>
            Assert.IsType<WorkspacePassphrase>(WorkspacePassphrase.Create(value, value).Passphrase);

        private static CategoryName CreateCategory(string value) =>
            Assert.IsType<CategoryName>(CategoryName.Create(value).CategoryName);

        private static SqliteConnection OpenInspectionConnection(string path, string passphrase)
        {
            var uri = new Uri(path).AbsoluteUri
                + "?cipher=chacha20&legacy=0&kdf_iter=64007&plaintext_header_size=0&hmac_check=1";
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = uri,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                Password = passphrase,
            };
            var connection = new SqliteConnection(builder.ConnectionString);
            connection.Open();
            return connection;
        }
    }

    private sealed class FaultInjectingFileOperations : IWorkspaceFileOperations
    {
        private readonly WorkspaceFileOperations _inner = new();
        private int _statePublishCalls;

        public Action<string, string>? BeforePublish { get; set; }

        public FailurePoint Failure { get; set; }

        public int? FailStatePublishCall { get; set; }

        public int? MaximumPathLength { get; set; }

        public string ResolvePath(string path) => _inner.ResolvePath(path);

        public bool Exists(string path) => _inner.Exists(path);

        public void EnsureParentDirectory(string path) => _inner.EnsureParentDirectory(path);

        public void EnsureDirectory(string path)
        {
            if (Failure == FailurePoint.Directory)
            {
                throw new UnauthorizedAccessException();
            }

            _inner.EnsureDirectory(path);
        }

        public string GetCandidatePath(string targetPath, string identifier) =>
            _inner.GetCandidatePath(targetPath, identifier);

        public void Publish(string candidatePath, string targetPath)
        {
            BeforePublish?.Invoke(candidatePath, targetPath);
            if (Failure == FailurePoint.Publish)
            {
                throw new IOException("Injected publication interruption.");
            }

            _inner.Publish(candidatePath, targetPath);
            if (Failure == FailurePoint.AfterPublish)
            {
                throw new IOException("Injected interruption after atomic publication.");
            }
        }

        public void Copy(string sourcePath, string candidatePath)
        {
            if (Failure == FailurePoint.Copy)
            {
                throw new IOException("Injected copy interruption.");
            }

            if (Failure == FailurePoint.PermissionCopy)
            {
                throw new UnauthorizedAccessException();
            }

            _inner.Copy(sourcePath, candidatePath);
        }

        public void CreateHardLink(string existingPath, string linkPath) =>
            _inner.CreateHardLink(existingPath, linkPath);

        public string ComputeSha256(string path) => _inner.ComputeSha256(path);

        public void Flush(string path)
        {
            if (MaximumPathLength is { } maximumPathLength
                && path.Length > maximumPathLength)
            {
                throw new PathTooLongException("Injected portable path budget.");
            }

            if (Failure == FailurePoint.Flush)
            {
                throw new IOException("Injected flush interruption.");
            }

            _inner.Flush(path);
            if (Failure == FailurePoint.CorruptAfterFlush)
            {
                var bytes = File.ReadAllBytes(path);
                bytes[Math.Min(128, bytes.Length - 1)] ^= 0x5A;
                File.WriteAllBytes(path, bytes);
            }
        }

        public void Replace(string candidatePath, string targetPath)
        {
            if (Failure == FailurePoint.Replace)
            {
                Failure = FailurePoint.None;
                throw new IOException("Injected replacement interruption.");
            }

            if (Failure == FailurePoint.PermissionReplace)
            {
                Failure = FailurePoint.None;
                throw new UnauthorizedAccessException();
            }

            _inner.Replace(candidatePath, targetPath);
            if (Failure == FailurePoint.ThrowAfterReplacementPublication)
            {
                Failure = FailurePoint.None;
                throw new IOException("Injected interruption after atomic replacement.");
            }
        }

        public void DeleteCandidate(string candidatePath) => _inner.DeleteCandidate(candidatePath);

        public IReadOnlyList<string> EnumerateFiles(string directoryPath, string searchPattern) =>
            Failure == FailurePoint.Enumeration
                ? throw new IOException("Injected recovery-directory enumeration failure.")
                : _inner.EnumerateFiles(directoryPath, searchPattern);

        public string ReadAllText(string path) => _inner.ReadAllText(path);

        public void WriteAllText(string path, string contents)
        {
            if (Failure == FailurePoint.StateWrite)
            {
                throw new IOException("Injected recovery-state write interruption.");
            }

            _inner.WriteAllText(path, contents);
        }

        public void PublishOrReplace(string candidatePath, string targetPath)
        {
            _statePublishCalls++;
            if (Failure == FailurePoint.StatePublish
                || _statePublishCalls == FailStatePublishCall)
            {
                throw new IOException("Injected recovery-state publication interruption.");
            }

            _inner.PublishOrReplace(candidatePath, targetPath);
        }

        public void DeleteFile(string path)
        {
            if (Failure == FailurePoint.DeleteAutomatic)
            {
                throw new IOException("Injected automatic-recovery deletion interruption.");
            }

            _inner.DeleteFile(path);
        }
    }
}
