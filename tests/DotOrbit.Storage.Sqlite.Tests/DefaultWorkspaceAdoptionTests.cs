using System.Security.Cryptography;
using System.Text.Json;
using DotOrbit.Core.Workspaces;
using DotOrbit.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DotOrbit.Storage.Sqlite.Tests;

public sealed class DefaultWorkspaceAdoptionTests
{
    private const string ValidPassphrase = "correct horse battery";

    [Fact]
    public void DefaultPathUsesTheOrbExtension()
    {
        var path = WorkspacePathDefaults.GetDefaultWorkspacePath();

        Assert.Equal("workspace.orb", Path.GetFileName(path));
        Assert.Equal("dot-orbit", Path.GetFileName(Path.GetDirectoryName(path)));
    }

    [Fact]
    public void FreshDirectoryResolvesCurrentNameWithoutCreatingAnything()
    {
        using var fixture = new AdoptionFixture();

        var result = fixture.Resolve();

        Assert.Equal(DefaultWorkspaceResolutionStatus.Ready, result.Status);
        Assert.Equal(fixture.CurrentPath, result.WorkspacePath);
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath));
    }

    [Fact]
    public void CurrentWorkspaceResolvesWithoutMutation()
    {
        using var fixture = new AdoptionFixture();
        File.WriteAllBytes(fixture.CurrentPath, [1, 2, 3]);
        var before = fixture.Snapshot();

        var result = fixture.Resolve();

        Assert.Equal(DefaultWorkspaceResolutionStatus.Ready, result.Status);
        Assert.Equal(fixture.CurrentPath, result.WorkspacePath);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public void ExternalOrbOpenReturnsSuccessfulInnerResultWithoutAdoptionVerification()
    {
        using var fixture = new AdoptionFixture();
        var externalDirectory = Path.Combine(fixture.DirectoryPath, "sample");
        Directory.CreateDirectory(externalDirectory);
        var externalPath = Path.Combine(externalDirectory, "workspace.orb");
        var inner = new SuccessfulOpenStore();
        var store = new DefaultWorkspaceStore(
            inner,
            new WorkspaceFileOperations(),
            new FixedIdentifierGenerator(),
            fixture.DirectoryPath);

        var result = store.Open(externalPath, UnlockPassphrase(ValidPassphrase));
        using var session = result.Session;

        Assert.Equal(WorkspaceOpenStatus.Opened, result.Status);
        Assert.NotNull(session);
        Assert.Equal(1, inner.OpenCallCount);
        Assert.Equal(externalPath, inner.LastOpenPath);
    }

    [Fact]
    public void LegacyWorkspaceIsOnlyClassifiedBeforeUnlock()
    {
        using var fixture = new AdoptionFixture();
        File.WriteAllBytes(fixture.LegacyPath, [1, 2, 3]);
        var before = fixture.Snapshot();

        var result = fixture.Resolve();

        Assert.Equal(DefaultWorkspaceResolutionStatus.Ready, result.Status);
        Assert.Equal(fixture.LegacyPath, result.WorkspacePath);
        Assert.Equal(before, fixture.Snapshot());
        Assert.False(File.Exists(fixture.AdoptionStatePath));
    }

    [Fact]
    public void ValidatedEncryptedLegacyWorkspaceIsAdoptedAndReopened()
    {
        using var fixture = new AdoptionFixture();
        var store = new EncryptedWorkspaceStore();
        var created = store.Create(
            fixture.LegacyPath,
            CreatePassphrase(ValidPassphrase),
            CreateCategory("Home"));
        Assert.Equal(WorkspaceCreationStatus.Created, created.Status);
        created.Session?.Dispose();
        var originalHash = Hash(fixture.LegacyPath);
        var adoptingStore = new DefaultWorkspaceStore(
            store,
            new WorkspaceFileOperations(),
            new FixedIdentifierGenerator());

        var result = adoptingStore.Open(
            fixture.LegacyPath,
            UnlockPassphrase(ValidPassphrase));
        using var session = result.Session;

        Assert.Equal(WorkspaceOpenStatus.Opened, result.Status);
        Assert.NotNull(session);
        Assert.Equal("Home", session.FirstCategoryName);
        Assert.False(File.Exists(fixture.LegacyPath));
        Assert.Equal(originalHash, Hash(fixture.CurrentPath));
        Assert.False(File.Exists(fixture.AdoptionStatePath));
    }

    [Fact]
    public void InvalidLegacyWorkspaceDoesNotBeginAdoption()
    {
        using var fixture = new AdoptionFixture();
        File.WriteAllText(fixture.LegacyPath, "not an encrypted workspace");
        var before = fixture.Snapshot();
        var store = new DefaultWorkspaceStore(
            new EncryptedWorkspaceStore(),
            new WorkspaceFileOperations(),
            new FixedIdentifierGenerator());

        var result = store.Open(fixture.LegacyPath, UnlockPassphrase(ValidPassphrase));

        Assert.Equal(WorkspaceOpenStatus.InvalidPassphraseOrStore, result.Status);
        Assert.Equal(before, fixture.Snapshot());
        Assert.False(File.Exists(fixture.AdoptionStatePath));
    }

    [Fact]
    public void WrongPassphraseDoesNotBeginAdoption()
    {
        using var fixture = new AdoptionFixture();
        var encryptedStore = new EncryptedWorkspaceStore();
        var created = encryptedStore.Create(
            fixture.LegacyPath,
            CreatePassphrase(ValidPassphrase),
            CreateCategory("Home"));
        created.Session?.Dispose();
        var before = fixture.Snapshot();
        var store = new DefaultWorkspaceStore(
            encryptedStore,
            new WorkspaceFileOperations(),
            new FixedIdentifierGenerator());

        var result = store.Open(
            fixture.LegacyPath,
            UnlockPassphrase("this is the wrong passphrase"));

        Assert.Equal(WorkspaceOpenStatus.InvalidPassphraseOrStore, result.Status);
        Assert.Equal(before, fixture.Snapshot());
        Assert.False(File.Exists(fixture.AdoptionStatePath));
    }

    [Fact]
    public void CreateRechecksForALegacyWorkspaceInsteadOfTrustingStartupState()
    {
        using var fixture = new AdoptionFixture();
        File.WriteAllBytes(fixture.LegacyPath, [1, 2, 3]);
        var before = fixture.Snapshot();
        var store = new DefaultWorkspaceStore(
            new EncryptedWorkspaceStore(),
            new WorkspaceFileOperations(),
            new FixedIdentifierGenerator());

        var result = store.Create(
            fixture.CurrentPath,
            CreatePassphrase(ValidPassphrase),
            CreateCategory("Home"));

        Assert.Equal(WorkspaceCreationStatus.AlreadyExists, result.Status);
        Assert.Equal(before, fixture.Snapshot());
        Assert.False(File.Exists(fixture.CurrentPath));
    }

    [Fact]
    public void CreateFailsClosedWhenTheLegacyNameAppearsDuringPublication()
    {
        using var fixture = new AdoptionFixture();
        var session = new TrackingSession();
        var inner = new RacingWorkspaceStore(
            onCreate: () =>
            {
                File.WriteAllBytes(fixture.CurrentPath, [1, 2, 3]);
                File.WriteAllBytes(fixture.LegacyPath, [4, 5, 6]);
            },
            createdSession: session);
        var store = new DefaultWorkspaceStore(
            inner,
            new WorkspaceFileOperations(),
            new FixedIdentifierGenerator());

        var result = store.Create(
            fixture.CurrentPath,
            CreatePassphrase(ValidPassphrase),
            CreateCategory("Home"));

        Assert.Equal(WorkspaceCreationStatus.Failed, result.Status);
        Assert.Null(result.Session);
        Assert.True(session.IsDisposed);
        Assert.True(File.Exists(fixture.LegacyPath));
        Assert.True(File.Exists(fixture.CurrentPath));
    }

    [Fact]
    public void OpenRechecksForBothWorkspaceNamesInsteadOfTrustingStartupState()
    {
        using var fixture = new AdoptionFixture();
        File.WriteAllBytes(fixture.LegacyPath, [1, 2, 3]);
        File.WriteAllBytes(fixture.CurrentPath, [4, 5, 6]);
        var before = fixture.Snapshot();
        var inner = new SuccessfulOpenStore();
        var store = new DefaultWorkspaceStore(
            inner,
            new WorkspaceFileOperations(),
            new FixedIdentifierGenerator());

        var result = store.Open(
            fixture.CurrentPath,
            UnlockPassphrase(ValidPassphrase));

        Assert.Equal(WorkspaceOpenStatus.AdoptionConflict, result.Status);
        Assert.Equal(0, inner.OpenCallCount);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public void OpenFailsClosedWhenTheLegacyNameAppearsDuringUnlock()
    {
        using var fixture = new AdoptionFixture();
        File.WriteAllBytes(fixture.CurrentPath, [1, 2, 3]);
        var session = new TrackingSession();
        var inner = new RacingWorkspaceStore(
            onOpen: () => File.WriteAllBytes(fixture.LegacyPath, [4, 5, 6]),
            openedSession: session);
        var store = new DefaultWorkspaceStore(
            inner,
            new WorkspaceFileOperations(),
            new FixedIdentifierGenerator());

        var result = store.Open(
            fixture.CurrentPath,
            UnlockPassphrase(ValidPassphrase));

        Assert.Equal(WorkspaceOpenStatus.AdoptionConflict, result.Status);
        Assert.Null(result.Session);
        Assert.True(session.IsDisposed);
        Assert.True(File.Exists(fixture.LegacyPath));
        Assert.True(File.Exists(fixture.CurrentPath));
    }

    [Fact]
    public void MigrationRecoveryUsesTheCurrentlyResolvedLegacyPath()
    {
        using var fixture = new AdoptionFixture();
        File.WriteAllBytes(fixture.LegacyPath, [1, 2, 3]);
        var inner = new StubWorkspaceStore(WorkspaceOpenStatus.Failed);
        var store = new DefaultWorkspaceStore(
            inner,
            new WorkspaceFileOperations(),
            new FixedIdentifierGenerator());

        _ = store.RestoreMigrationRecovery(
            fixture.CurrentPath,
            UnlockPassphrase(ValidPassphrase),
            Path.Combine(fixture.DirectoryPath, "migration.dotorbit-recovery"));

        Assert.Equal(1, inner.MigrationRestoreCallCount);
        Assert.Equal(fixture.LegacyPath, inner.LastMigrationRestoreWorkspacePath);
    }

    [Fact]
    public void MigrationRecoveryConflictFailsClosedWithoutCallingStorage()
    {
        using var fixture = new AdoptionFixture();
        File.WriteAllBytes(fixture.LegacyPath, [1, 2, 3]);
        File.WriteAllBytes(fixture.CurrentPath, [4, 5, 6]);
        var before = fixture.Snapshot();
        var inner = new StubWorkspaceStore(WorkspaceOpenStatus.Failed);
        var store = new DefaultWorkspaceStore(
            inner,
            new WorkspaceFileOperations(),
            new FixedIdentifierGenerator());

        var result = store.RestoreMigrationRecovery(
            fixture.CurrentPath,
            UnlockPassphrase(ValidPassphrase),
            Path.Combine(fixture.DirectoryPath, "migration.dotorbit-recovery"));

        Assert.Equal(MigrationRecoveryRestoreStatus.Failed, result.Status);
        Assert.Equal(0, inner.MigrationRestoreCallCount);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public void ContentChangedAfterInitialValidationIsRevalidatedBeforeAdoption()
    {
        using var fixture = new AdoptionFixture();
        var encryptedStore = new EncryptedWorkspaceStore();
        var created = encryptedStore.Create(
            fixture.LegacyPath,
            CreatePassphrase(ValidPassphrase),
            CreateCategory("Home"));
        created.Session?.Dispose();
        byte[] changedBytes = [1, 2, 3, 4, 5];
        var store = new DefaultWorkspaceStore(
            encryptedStore,
            new WorkspaceFileOperations(),
            new FixedIdentifierGenerator(),
            afterLegacyValidation: () => File.WriteAllBytes(fixture.LegacyPath, changedBytes));

        var result = store.Open(
            fixture.LegacyPath,
            UnlockPassphrase(ValidPassphrase));

        Assert.Equal(WorkspaceOpenStatus.AdoptionFailed, result.Status);
        Assert.Null(result.Session);
        Assert.Equal(changedBytes, File.ReadAllBytes(fixture.LegacyPath));
        Assert.False(File.Exists(fixture.AdoptionStatePath));
        Assert.False(File.Exists(fixture.CurrentPath));
    }

    [Fact]
    public void AdoptionLinksTheValidatedBytesWhileTheExclusiveStoreLockIsHeld()
    {
        using var fixture = new AdoptionFixture();
        var operations = new WorkspaceFileOperations();
        var encryptedStore = new EncryptedWorkspaceStore(
            new FixedIdentifierGenerator(),
            operations);
        var created = encryptedStore.Create(
            fixture.LegacyPath,
            CreatePassphrase(ValidPassphrase),
            CreateCategory("Home"));
        created.Session?.Dispose();
        var observedExclusiveTransaction = false;

        var transition = encryptedStore.ExecuteValidatedAdoption(
            fixture.LegacyPath,
            UnlockPassphrase(ValidPassphrase),
            connection =>
            {
                using var lockingMode = connection.CreateCommand();
                lockingMode.CommandText = "PRAGMA locking_mode;";
                Assert.Equal("exclusive", lockingMode.ExecuteScalar() as string);
                Assert.Throws<SqliteException>(() => connection.BeginTransaction());
                observedExclusiveTransaction = true;
                return WorkspacePathDefaults.LinkValidatedWorkspace(
                    fixture.LegacyPath,
                    operations,
                    new FixedIdentifierGenerator(),
                    removeLegacyNames: false);
            },
            OperatingSystem.IsWindows()
                ? null
                : () => WorkspacePathDefaults.RemoveLegacyNames(
                    fixture.DirectoryPath,
                    operations));
        var completion = WorkspacePathDefaults.CompleteAdoption(
            fixture.DirectoryPath,
            operations);
        var reopened = encryptedStore.Open(
            fixture.CurrentPath,
            UnlockPassphrase(ValidPassphrase));
        reopened.Session?.Dispose();

        Assert.Equal(DefaultWorkspaceResolutionStatus.Ready, transition);
        Assert.Equal(DefaultWorkspaceResolutionStatus.Ready, completion);
        Assert.True(observedExclusiveTransaction);
        Assert.Equal(WorkspaceOpenStatus.Opened, reopened.Status);
        Assert.False(File.Exists(fixture.LegacyPath));
        Assert.True(File.Exists(fixture.CurrentPath));
    }

    [Fact]
    public void WindowsHandleWithoutDeleteSharingLeavesAResumableAdoption()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new AdoptionFixture();
        var encryptedStore = new EncryptedWorkspaceStore();
        var created = encryptedStore.Create(
            fixture.LegacyPath,
            CreatePassphrase(ValidPassphrase),
            CreateCategory("Home"));
        created.Session?.Dispose();
        var store = new DefaultWorkspaceStore(
            encryptedStore,
            new WorkspaceFileOperations(),
            new FixedIdentifierGenerator());

        using (var heldLegacyName = new FileStream(
                   fixture.LegacyPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.ReadWrite))
        {
            var interrupted = store.Open(
                fixture.LegacyPath,
                UnlockPassphrase(ValidPassphrase));

            Assert.Equal(WorkspaceOpenStatus.AdoptionFailed, interrupted.Status);
            Assert.True(File.Exists(fixture.LegacyPath));
            Assert.True(File.Exists(fixture.CurrentPath));
            Assert.True(File.Exists(fixture.AdoptionStatePath));
        }

        var resumed = store.Open(
            fixture.LegacyPath,
            UnlockPassphrase(ValidPassphrase));
        resumed.Session?.Dispose();

        Assert.Equal(WorkspaceOpenStatus.Opened, resumed.Status);
        Assert.False(File.Exists(fixture.LegacyPath));
        Assert.True(File.Exists(fixture.CurrentPath));
        Assert.False(File.Exists(fixture.AdoptionStatePath));
    }

    [Theory]
    [InlineData(WorkspaceOpenStatus.UnsupportedSchema)]
    [InlineData(WorkspaceOpenStatus.MigrationFailed)]
    [InlineData(WorkspaceOpenStatus.Failed)]
    public void NonOpenedLegacyResultDoesNotBeginAdoption(WorkspaceOpenStatus status)
    {
        using var fixture = new AdoptionFixture();
        File.WriteAllBytes(fixture.LegacyPath, [1, 2, 3]);
        var before = fixture.Snapshot();
        var inner = new StubWorkspaceStore(status);
        var store = new DefaultWorkspaceStore(
            inner,
            new WorkspaceFileOperations(),
            new FixedIdentifierGenerator());

        var result = store.Open(fixture.LegacyPath, UnlockPassphrase(ValidPassphrase));

        Assert.Equal(status, result.Status);
        Assert.Equal(1, inner.OpenCallCount);
        Assert.Equal(before, fixture.Snapshot());
        Assert.False(File.Exists(fixture.AdoptionStatePath));
    }

    [Fact]
    public void AdoptionMovesEveryExactCompanionByteForByteAndLeavesRecoveryFilesAlone()
    {
        using var fixture = new AdoptionFixture();
        var expected = fixture.SeedLegacyFileSet();
        var portableRecoveryPath = Path.Combine(
            fixture.DirectoryPath,
            "manual.dotorbit-recovery");
        File.WriteAllBytes(portableRecoveryPath, [91, 92, 93]);
        var inner = new SuccessfulOpenStore();
        var store = new DefaultWorkspaceStore(
            inner,
            new WorkspaceFileOperations(),
            new FixedIdentifierGenerator());

        var result = store.Open(fixture.LegacyPath, UnlockPassphrase(ValidPassphrase));
        result.Session?.Dispose();

        Assert.Equal(WorkspaceOpenStatus.Opened, result.Status);
        Assert.Equal(2, inner.OpenCallCount);
        Assert.Equal(fixture.CurrentPath, inner.LastOpenPath);
        Assert.False(File.Exists(fixture.LegacyPath));
        foreach (var entry in expected)
        {
            Assert.False(File.Exists(entry.Key));
            var currentPath = fixture.CurrentPath + entry.Key[fixture.LegacyPath.Length..];
            Assert.Equal(entry.Value, File.ReadAllBytes(currentPath));
        }
        Assert.Equal([91, 92, 93], File.ReadAllBytes(portableRecoveryPath));
        Assert.False(File.Exists(fixture.AdoptionStatePath));
        Assert.True(Directory.Exists(fixture.LegacyPath + ".recovery-state.json"));
    }

    [Fact]
    public void RecoveryStateReplacedDuringCleanupIsPreservedAndBlocksCompletion()
    {
        using var fixture = new AdoptionFixture();
        var expected = fixture.SeedLegacyFileSet();
        byte[] replacement = [91, 92, 93, 94];
        var operations = new InterruptingFileOperations(
            failAfterPublish: null,
            beforePublish: (source, target) =>
            {
                if (target.EndsWith(
                        WorkspacePathDefaults.PreservedRecoveryStateFileName,
                        StringComparison.Ordinal))
                {
                    var replacementPath = source + ".replacement";
                    File.WriteAllBytes(replacementPath, replacement);
                    File.Move(replacementPath, source, overwrite: true);
                }
            });
        var store = new DefaultWorkspaceStore(
            new SuccessfulOpenStore(),
            operations,
            new FixedIdentifierGenerator());

        var result = store.Open(fixture.LegacyPath, UnlockPassphrase(ValidPassphrase));

        Assert.Equal(WorkspaceOpenStatus.AdoptionConflict, result.Status);
        Assert.Null(result.Session);
        Assert.True(File.Exists(fixture.AdoptionStatePath));
        Assert.Equal(
            replacement,
            File.ReadAllBytes(Path.Combine(
                fixture.DirectoryPath,
                WorkspacePathDefaults.PreservedRecoveryStateFileName)));
        Assert.Equal(
            expected[fixture.LegacyPath + ".recovery-state.json"],
            File.ReadAllBytes(fixture.CurrentPath + ".recovery-state.json"));
    }

    [Fact]
    public void AdoptionPreservesConfiguredRecoveryStatePendingGenerationAndHistory()
    {
        using var fixture = new AdoptionFixture();
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero));
        var encryptedStore = new EncryptedWorkspaceStore(
            new SystemIdentifierGenerator(),
            time);
        var created = encryptedStore.Create(
            fixture.LegacyPath,
            CreatePassphrase(ValidPassphrase),
            CreateCategory("Before"));
        using (var session = Assert.IsAssignableFrom<IWorkspaceSession>(created.Session))
        {
            Assert.Equal(
                RecoveryDirectoryConfigurationStatus.Configured,
                session.Recovery.ConfigureAutomaticRecoveryDirectory(
                    fixture.RecoveryDirectoryPath).Status);
            var categoryId = Assert.Single(session.Work.Read().Categories).Id;
            session.Work.RenameCategory(categoryId, "First committed value");
            time.Advance(TimeSpan.FromMinutes(30));
            session.Work.RenameCategory(categoryId, "Second committed value");
        }

        var legacyStatePath = fixture.LegacyPath + ".recovery-state.json";
        var expectedState = File.ReadAllBytes(legacyStatePath);
        using (var document = JsonDocument.Parse(expectedState))
        {
            var root = document.RootElement;
            Assert.Equal(2, root.GetProperty("ChangeGeneration").GetInt64());
            Assert.Equal(2, root.GetProperty("PendingChangeGeneration").GetInt64());
            Assert.Equal(
                Path.GetFullPath(fixture.RecoveryDirectoryPath),
                root.GetProperty("DirectoryPath").GetString());
            Assert.False(string.IsNullOrWhiteSpace(
                root.GetProperty("RecoverySetIdentifier").GetString()));
            Assert.NotEqual(JsonValueKind.Null, root.GetProperty("PendingChangeUtc").ValueKind);
        }
        var expectedRecoveryHistory = fixture.SnapshotRecoveryDirectory();
        var adoptingStore = new DefaultWorkspaceStore(
            encryptedStore,
            new WorkspaceFileOperations(),
            new SystemIdentifierGenerator());

        var result = adoptingStore.Open(
            fixture.LegacyPath,
            UnlockPassphrase(ValidPassphrase));
        using var reopened = result.Session;

        Assert.Equal(WorkspaceOpenStatus.Opened, result.Status);
        Assert.NotNull(reopened);
        Assert.Equal(
            Path.GetFullPath(fixture.RecoveryDirectoryPath),
            reopened.Recovery.AutomaticRecoveryDirectoryPath);
        Assert.Equal(expectedState, File.ReadAllBytes(fixture.CurrentPath + ".recovery-state.json"));
        Assert.Equal(expectedRecoveryHistory, fixture.SnapshotRecoveryDirectory());
        Assert.False(File.Exists(legacyStatePath));
    }

    [Fact]
    public void AdoptionFlushesTheMarkerCandidateBeforeAtomicPublication()
    {
        using var fixture = new AdoptionFixture();
        fixture.SeedLegacyFileSet();
        var operations = new InterruptingFileOperations(failAfterPublish: null);
        var store = new DefaultWorkspaceStore(
            new SuccessfulOpenStore(),
            operations,
            new FixedIdentifierGenerator());

        var result = store.Open(fixture.LegacyPath, UnlockPassphrase(ValidPassphrase));
        result.Session?.Dispose();

        var markerFlush = Assert.Single(
            operations.Events,
            entry => entry.StartsWith("flush:", StringComparison.Ordinal));
        var markerPublish = operations.Events.Single(entry => entry.EndsWith(
            $"->{fixture.AdoptionStatePath}",
            StringComparison.Ordinal));
        Assert.True(
            operations.Events.IndexOf(markerFlush) < operations.Events.IndexOf(markerPublish));
        Assert.Equal(
            Path.GetDirectoryName(fixture.AdoptionStatePath),
            Path.GetDirectoryName(markerFlush["flush:".Length..]));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void FailureBeforeMarkerPublicationLeavesTheLegacyFileSetUnchanged(
        bool failMarkerFlush,
        bool failBeforeMarkerPublish)
    {
        using var fixture = new AdoptionFixture();
        fixture.SeedLegacyFileSet();
        var before = fixture.Snapshot();
        var operations = new InterruptingFileOperations(
            failAfterPublish: null,
            failMarkerFlush: failMarkerFlush,
            failBeforeMarkerPublish: failBeforeMarkerPublish);
        var store = new DefaultWorkspaceStore(
            new SuccessfulOpenStore(),
            operations,
            new FixedIdentifierGenerator());

        var result = store.Open(fixture.LegacyPath, UnlockPassphrase(ValidPassphrase));

        Assert.Equal(WorkspaceOpenStatus.AdoptionFailed, result.Status);
        Assert.Null(result.Session);
        Assert.Equal(before, fixture.Snapshot());
        Assert.False(File.Exists(fixture.AdoptionStatePath));
        Assert.False(File.Exists(fixture.CurrentPath));
    }

    [Fact]
    public void FailureToReopenTheAdoptedWorkspaceIsReportedAsAnAdoptionFailure()
    {
        using var fixture = new AdoptionFixture();
        fixture.SeedLegacyFileSet();
        var store = new DefaultWorkspaceStore(
            new SuccessfulOpenStore(WorkspaceOpenResult.Failed()),
            new WorkspaceFileOperations(),
            new FixedIdentifierGenerator());

        var result = store.Open(fixture.LegacyPath, UnlockPassphrase(ValidPassphrase));

        Assert.Equal(WorkspaceOpenStatus.AdoptionFailed, result.Status);
        Assert.Null(result.Session);
        Assert.True(File.Exists(fixture.CurrentPath));
        Assert.False(File.Exists(fixture.LegacyPath));
        Assert.False(File.Exists(fixture.AdoptionStatePath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void EveryLinkedAdoptionCheckpointResumesWithoutChangingBytes(int checkpoint)
    {
        using var fixture = new AdoptionFixture();
        var expected = fixture.SeedLegacyFileSet();
        var operations = new InterruptingFileOperations(
            failAfterPublish: checkpoint == 0 ? 1 : null,
            failAfterHardLink: checkpoint == 0 ? null : checkpoint);
        var store = new DefaultWorkspaceStore(
            new SuccessfulOpenStore(),
            operations,
            new FixedIdentifierGenerator());

        var interrupted = store.Open(fixture.LegacyPath, UnlockPassphrase(ValidPassphrase));

        Assert.Equal(WorkspaceOpenStatus.AdoptionFailed, interrupted.Status);
        Assert.True(File.Exists(fixture.AdoptionStatePath));
        using (var marker = JsonDocument.Parse(File.ReadAllText(fixture.AdoptionStatePath)))
        {
            Assert.Equal(1, marker.RootElement.GetProperty("Version").GetInt32());
            Assert.Equal(5, marker.RootElement.GetProperty("Files").GetArrayLength());
        }

        var resolution = WorkspacePathDefaults.ResolveDefaultWorkspace(
            fixture.DirectoryPath,
            operations);
        WorkspaceOpenResult reopened;
        if (string.Equals(resolution.WorkspacePath, fixture.LegacyPath, StringComparison.Ordinal))
        {
            reopened = store.Open(fixture.LegacyPath, UnlockPassphrase(ValidPassphrase));
        }
        else
        {
            reopened = new SuccessfulOpenStore().Open(
                fixture.CurrentPath,
                UnlockPassphrase(ValidPassphrase));
        }
        reopened.Session?.Dispose();

        Assert.Equal(DefaultWorkspaceResolutionStatus.Ready, resolution.Status);
        Assert.Equal(WorkspaceOpenStatus.Opened, reopened.Status);
        Assert.False(File.Exists(fixture.LegacyPath));
        Assert.False(File.Exists(fixture.AdoptionStatePath));
        foreach (var entry in expected)
        {
            var currentPath = fixture.CurrentPath + entry.Key[fixture.LegacyPath.Length..];
            Assert.Equal(entry.Value, File.ReadAllBytes(currentPath));
        }
    }

    [Fact]
    public void MarkerCleanupInterruptionResumesAfterAllFilesMoved()
    {
        using var fixture = new AdoptionFixture();
        var expected = fixture.SeedLegacyFileSet();
        var operations = new InterruptingFileOperations(failAfterPublish: null, failStateDelete: true);
        var store = new DefaultWorkspaceStore(
            new SuccessfulOpenStore(),
            operations,
            new FixedIdentifierGenerator());

        var interrupted = store.Open(fixture.LegacyPath, UnlockPassphrase(ValidPassphrase));
        var resolution = fixture.Resolve(operations);
        var resumed = store.Open(fixture.CurrentPath, UnlockPassphrase(ValidPassphrase));
        resumed.Session?.Dispose();

        Assert.Equal(WorkspaceOpenStatus.AdoptionFailed, interrupted.Status);
        Assert.Equal(DefaultWorkspaceResolutionStatus.Ready, resolution.Status);
        Assert.Equal(fixture.CurrentPath, resolution.WorkspacePath);
        Assert.Equal(WorkspaceOpenStatus.Opened, resumed.Status);
        Assert.False(File.Exists(fixture.AdoptionStatePath));
        foreach (var entry in expected)
        {
            var currentPath = fixture.CurrentPath + entry.Key[fixture.LegacyPath.Length..];
            Assert.Equal(entry.Value, File.ReadAllBytes(currentPath));
        }
    }

    [Fact]
    public void BothWorkspaceNamesFailClosedWithoutMutation()
    {
        using var fixture = new AdoptionFixture();
        File.WriteAllBytes(fixture.LegacyPath, [1, 2, 3]);
        File.WriteAllBytes(fixture.CurrentPath, [4, 5, 6]);
        var before = fixture.Snapshot();

        var result = fixture.Resolve();

        Assert.Equal(DefaultWorkspaceResolutionStatus.Conflict, result.Status);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public void ResumingMarkerWithBothCompanionNamesFailsClosedWithoutMutation()
    {
        using var fixture = new AdoptionFixture();
        File.WriteAllBytes(fixture.CurrentPath, [1, 2, 3]);
        File.WriteAllBytes(fixture.LegacyPath + "-wal", [4, 5, 6]);
        File.WriteAllBytes(fixture.CurrentPath + "-wal", [7, 8, 9]);
        fixture.WriteAdoptionMarker("", "-wal");
        var before = fixture.Snapshot();

        var result = fixture.Resolve();

        Assert.Equal(DefaultWorkspaceResolutionStatus.Conflict, result.Status);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public void ResumingMarkerPreflightsEveryCompanionConflictBeforeMovingAnything()
    {
        using var fixture = new AdoptionFixture();
        File.WriteAllBytes(fixture.CurrentPath, [1, 2, 3]);
        File.WriteAllBytes(fixture.LegacyPath + "-journal", [4, 5, 6]);
        File.WriteAllBytes(fixture.LegacyPath + "-wal", [7, 8, 9]);
        File.WriteAllBytes(fixture.CurrentPath + "-wal", [10, 11, 12]);
        fixture.WriteAdoptionMarker("", "-journal", "-wal");
        var before = fixture.Snapshot();

        var result = fixture.Resolve();

        Assert.Equal(DefaultWorkspaceResolutionStatus.Conflict, result.Status);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Theory]
    [InlineData("workspace.db", "workspace.orb-wal")]
    [InlineData("workspace.orb", "workspace.db-shm")]
    [InlineData(null, "workspace.db.recovery-state.json")]
    [InlineData(null, "workspace.orb-journal")]
    public void AmbiguousCompanionFilesFailClosedWithoutMutation(
        string? workspaceName,
        string companionName)
    {
        using var fixture = new AdoptionFixture();
        if (workspaceName is not null)
        {
            File.WriteAllBytes(Path.Combine(fixture.DirectoryPath, workspaceName), [1, 2, 3]);
        }
        File.WriteAllBytes(Path.Combine(fixture.DirectoryPath, companionName), [4, 5, 6]);
        var before = fixture.Snapshot();

        var result = fixture.Resolve();

        Assert.Equal(DefaultWorkspaceResolutionStatus.Conflict, result.Status);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("{\"Version\":2}")]
    [InlineData("{\"Version\":1,\"Files\":[null]}")]
    public void InvalidAdoptionMarkerFailsClosedWithoutMutation(string markerContents)
    {
        using var fixture = new AdoptionFixture();
        File.WriteAllBytes(fixture.LegacyPath, [1, 2, 3]);
        File.WriteAllText(fixture.AdoptionStatePath, markerContents);
        var before = fixture.Snapshot();

        var result = fixture.Resolve();

        Assert.Equal(DefaultWorkspaceResolutionStatus.Conflict, result.Status);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public void ValidMarkerRejectsACompanionThatWasAbsentFromItsManifest()
    {
        using var fixture = new AdoptionFixture();
        File.WriteAllBytes(fixture.CurrentPath, [1, 2, 3]);
        fixture.WriteAdoptionMarker("");
        File.WriteAllBytes(fixture.LegacyPath + "-wal", [4, 5, 6]);
        var before = fixture.Snapshot();

        var result = fixture.Resolve();

        Assert.Equal(DefaultWorkspaceResolutionStatus.Conflict, result.Status);
        Assert.Equal(before, fixture.Snapshot());
    }

    private static WorkspacePassphrase CreatePassphrase(string value) =>
        Assert.IsType<WorkspacePassphrase>(WorkspacePassphrase.Create(value, value).Passphrase);

    private static WorkspacePassphrase UnlockPassphrase(string value) =>
        Assert.IsType<WorkspacePassphrase>(WorkspacePassphrase.ForUnlock(value));

    private static CategoryName CreateCategory(string value) =>
        Assert.IsType<CategoryName>(CategoryName.Create(value).CategoryName);

    private static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class AdoptionFixture : IDisposable
    {
        public AdoptionFixture()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                $"dot-orbit-adoption-tésts-{Guid.NewGuid():N}");
            Directory.CreateDirectory(DirectoryPath);
            RecoveryDirectoryPath = Path.Combine(DirectoryPath, "recovery");
            LegacyPath = Path.Combine(DirectoryPath, "workspace.db");
            CurrentPath = Path.Combine(DirectoryPath, "workspace.orb");
            AdoptionStatePath = Path.Combine(DirectoryPath, "workspace.orb.adoption-state.json");
        }

        public string DirectoryPath { get; }

        public string LegacyPath { get; }

        public string RecoveryDirectoryPath { get; }

        public string CurrentPath { get; }

        public string AdoptionStatePath { get; }

        public DefaultWorkspaceResolution Resolve(
            IWorkspaceFileOperations? operations = null) =>
            WorkspacePathDefaults.ResolveDefaultWorkspace(
                DirectoryPath,
                operations ?? new WorkspaceFileOperations());

        public Dictionary<string, byte[]> SeedLegacyFileSet()
        {
            var files = new Dictionary<string, byte[]>
            {
                [LegacyPath] = [1, 2, 3, 4],
                [LegacyPath + "-journal"] = [11, 12],
                [LegacyPath + "-wal"] = [21, 22],
                [LegacyPath + "-shm"] = [31, 32],
                [LegacyPath + ".recovery-state.json"] = [41, 42, 43],
            };
            foreach (var entry in files)
            {
                File.WriteAllBytes(entry.Key, entry.Value);
            }
            return files;
        }

        public void WriteAdoptionMarker(params string[] suffixes)
        {
            var operations = new WorkspaceFileOperations();
            var files = suffixes.Select(suffix =>
            {
                var legacyFile = LegacyPath + suffix;
                var currentFile = CurrentPath + suffix;
                var path = File.Exists(currentFile) ? currentFile : legacyFile;
                return new WorkspacePathDefaults.AdoptionFileDocument(
                    suffix,
                    operations.ComputeSha256(path));
            }).ToArray();
            File.WriteAllText(
                AdoptionStatePath,
                JsonSerializer.Serialize(
                    new WorkspacePathDefaults.AdoptionStateDocument(1, files)));
        }

        public string Snapshot() => string.Join(
            "|",
            Directory.GetFiles(DirectoryPath)
                .Order(StringComparer.Ordinal)
                .Select(path => $"{Path.GetFileName(path)}:{Hash(path)}"));

        public string SnapshotRecoveryDirectory() => string.Join(
            "|",
            Directory.Exists(RecoveryDirectoryPath)
                ? Directory.GetFiles(RecoveryDirectoryPath)
                    .Order(StringComparer.Ordinal)
                    .Select(path => $"{Path.GetFileName(path)}:{Hash(path)}")
                : []);

        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }

    private sealed class FixedIdentifierGenerator : IIdentifierGenerator
    {
        public string NewIdentifier() => "adoption";
    }

    private sealed class StubWorkspaceStore(WorkspaceOpenStatus status) : IWorkspaceStore
    {
        public int MigrationRestoreCallCount { get; private set; }

        public int OpenCallCount { get; private set; }

        public string? LastMigrationRestoreWorkspacePath { get; private set; }

        public bool Exists(string path) => true;

        public WorkspaceCreationResult Create(
            string path,
            WorkspacePassphrase passphrase,
            CategoryName firstCategory) => WorkspaceCreationResult.Failed();

        public WorkspaceOpenResult Open(string path, WorkspacePassphrase passphrase)
        {
            OpenCallCount++;
            return status switch
            {
                WorkspaceOpenStatus.UnsupportedSchema => WorkspaceOpenResult.UnsupportedSchema(),
                WorkspaceOpenStatus.MigrationFailed => WorkspaceOpenResult.MigrationFailed(),
                _ => WorkspaceOpenResult.Failed(),
            };
        }

        public MigrationRecoveryRestoreResult RestoreMigrationRecovery(
            string workspacePath,
            WorkspacePassphrase passphrase,
            string recoveryPointPath)
        {
            MigrationRestoreCallCount++;
            LastMigrationRestoreWorkspacePath = workspacePath;
            return MigrationRecoveryRestoreResult.Failed();
        }
    }

    private sealed class SuccessfulOpenStore(WorkspaceOpenResult? secondOpenResult = null)
        : IWorkspaceStore
    {
        public int OpenCallCount { get; private set; }

        public string? LastOpenPath { get; private set; }

        public bool Exists(string path) => true;

        public WorkspaceCreationResult Create(
            string path,
            WorkspacePassphrase passphrase,
            CategoryName firstCategory) => WorkspaceCreationResult.Failed();

        public WorkspaceOpenResult Open(string path, WorkspacePassphrase passphrase)
        {
            OpenCallCount++;
            LastOpenPath = path;
            if (OpenCallCount == 2 && secondOpenResult is not null)
            {
                return secondOpenResult;
            }
            return WorkspaceOpenResult.Opened(new StubSession());
        }

        public MigrationRecoveryRestoreResult RestoreMigrationRecovery(
            string workspacePath,
            WorkspacePassphrase passphrase,
            string recoveryPointPath) => MigrationRecoveryRestoreResult.Failed();
    }

    private class StubSession : IWorkspaceSession
    {
        public int SchemaVersion => EncryptedWorkspaceStore.CurrentSchemaVersion;

        public string FirstCategoryName => "Home";

        public IWorkspaceRecovery Recovery => throw new NotSupportedException();

        public IWorkspaceWork Work => throw new NotSupportedException();

        public virtual void Dispose()
        {
        }

        public PassphraseRotationResult RotatePassphrase(
            WorkspacePassphrase currentPassphrase,
            WorkspacePassphrase newPassphrase) => throw new NotSupportedException();
    }

    private sealed class TrackingSession : StubSession
    {
        public bool IsDisposed { get; private set; }

        public override void Dispose()
        {
            IsDisposed = true;
            base.Dispose();
        }
    }

    private sealed class RacingWorkspaceStore(
        Action? onCreate = null,
        IWorkspaceSession? createdSession = null,
        Action? onOpen = null,
        IWorkspaceSession? openedSession = null) : IWorkspaceStore
    {
        public bool Exists(string path) => false;

        public WorkspaceCreationResult Create(
            string path,
            WorkspacePassphrase passphrase,
            CategoryName firstCategory)
        {
            onCreate?.Invoke();
            return WorkspaceCreationResult.Created(createdSession ?? new StubSession());
        }

        public WorkspaceOpenResult Open(string path, WorkspacePassphrase passphrase)
        {
            onOpen?.Invoke();
            return WorkspaceOpenResult.Opened(openedSession ?? new StubSession());
        }

        public MigrationRecoveryRestoreResult RestoreMigrationRecovery(
            string workspacePath,
            WorkspacePassphrase passphrase,
            string recoveryPointPath) => MigrationRecoveryRestoreResult.Failed();
    }

    private sealed class InterruptingFileOperations(
        int? failAfterPublish,
        bool failStateDelete = false,
        bool failMarkerFlush = false,
        bool failBeforeMarkerPublish = false,
        int? failAfterHardLink = null,
        Action<string, string>? beforePublish = null) : IWorkspaceFileOperations
    {
        private readonly WorkspaceFileOperations _inner = new();
        private bool _deleteFailed;
        private bool _hardLinkFailed;
        private bool _publishFailed;
        private int _hardLinkCount;
        private int _publishCount;

        public List<string> Events { get; } = [];

        public string ResolvePath(string path) => _inner.ResolvePath(path);

        public bool Exists(string path) => _inner.Exists(path);

        public void EnsureParentDirectory(string path) => _inner.EnsureParentDirectory(path);

        public void EnsureDirectory(string path) => _inner.EnsureDirectory(path);

        public string GetCandidatePath(string targetPath, string identifier) =>
            _inner.GetCandidatePath(targetPath, identifier);

        public void Publish(string candidatePath, string targetPath)
        {
            beforePublish?.Invoke(candidatePath, targetPath);
            if (failBeforeMarkerPublish
                && targetPath.EndsWith(AdoptionStateFileName, StringComparison.Ordinal))
            {
                throw new IOException("Injected interruption before marker publication.");
            }

            _inner.Publish(candidatePath, targetPath);
            Events.Add($"publish:{candidatePath}->{targetPath}");
            _publishCount++;
            if (!_publishFailed && _publishCount == failAfterPublish)
            {
                _publishFailed = true;
                throw new IOException("Injected interruption after publication.");
            }
        }

        public void Copy(string sourcePath, string candidatePath) =>
            _inner.Copy(sourcePath, candidatePath);

        public void CreateHardLink(string existingPath, string linkPath)
        {
            _inner.CreateHardLink(existingPath, linkPath);
            _hardLinkCount++;
            if (!_hardLinkFailed && _hardLinkCount == failAfterHardLink)
            {
                _hardLinkFailed = true;
                throw new IOException("Injected interruption after hard-link publication.");
            }
        }

        public string ComputeSha256(string path) => _inner.ComputeSha256(path);

        public void Flush(string path)
        {
            if (failMarkerFlush
                && path.Contains(AdoptionStateFileName, StringComparison.Ordinal))
            {
                throw new IOException("Injected interruption before marker flush.");
            }

            _inner.Flush(path);
            Events.Add($"flush:{path}");
        }

        public void Replace(string candidatePath, string targetPath) =>
            _inner.Replace(candidatePath, targetPath);

        public void DeleteCandidate(string candidatePath) => _inner.DeleteCandidate(candidatePath);

        public IReadOnlyList<string> EnumerateFiles(string directoryPath, string searchPattern) =>
            _inner.EnumerateFiles(directoryPath, searchPattern);

        public string ReadAllText(string path) => _inner.ReadAllText(path);

        public void WriteAllText(string path, string contents) =>
            _inner.WriteAllText(path, contents);

        public void PublishOrReplace(string candidatePath, string targetPath) =>
            _inner.PublishOrReplace(candidatePath, targetPath);

        public void DeleteFile(string path)
        {
            if (failStateDelete
                && !_deleteFailed
                && path.EndsWith(AdoptionStateFileName, StringComparison.Ordinal))
            {
                _deleteFailed = true;
                throw new IOException("Injected marker cleanup interruption.");
            }

            _inner.DeleteFile(path);
        }

        private const string AdoptionStateFileName = "workspace.orb.adoption-state.json";
    }
}
