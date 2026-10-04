using System.Security.Cryptography;
using System.Text;
using DotOrbit.Core.Workspaces;
using DotOrbit.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DotOrbit.Storage.Sqlite.Tests;

public sealed class PassphraseRotationTests
{
    private const string CurrentPassphrase = "correct horse battery";
    private const string NewPassphrase = "new portable passphrase";

    [Fact]
    public void WrongCurrentPassphraseDoesNotCreateRecoveryOrCandidateOrChangeWorkspace()
    {
        using var fixture = new WorkspaceFixture();
        using var session = fixture.CreateWorkspace();
        var originalHash = Hash(fixture.WorkspacePath);

        var result = session.RotatePassphrase(
            Unlock("this is not the passphrase"),
            Create(NewPassphrase));

        Assert.Equal(PassphraseRotationStatus.InvalidCurrentPassphraseOrStore, result.Status);
        Assert.Null(result.Session);
        Assert.Null(result.RecoveryPointPath);
        Assert.Equal(originalHash, Hash(fixture.WorkspacePath));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.dotorbit-recovery"));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.creating"));
        Assert.Equal("Home", session.Work.Read().Categories.Single().Name);
        AssertWorkspaceOpens(fixture.Store, fixture.WorkspacePath, CurrentPassphrase);
        AssertWorkspaceDoesNotOpen(fixture.Store, fixture.WorkspacePath, NewPassphrase);
    }

    [Fact]
    public void RecoveryPublicationFailureLeavesTheCurrentSessionAndStoreUsable()
    {
        using var fixture = new WorkspaceFixture(
            fileOperations: new RotationFileOperations(failRecoveryPublication: true));
        using var session = fixture.CreateWorkspace();
        var originalHash = Hash(fixture.WorkspacePath);

        var result = session.RotatePassphrase(
            Unlock(CurrentPassphrase),
            Create(NewPassphrase));

        Assert.Equal(PassphraseRotationStatus.RecoveryPointCreationFailed, result.Status);
        Assert.Null(result.Session);
        Assert.Equal(originalHash, Hash(fixture.WorkspacePath));
        Assert.Equal("Home", session.Work.Read().Categories.Single().Name);
        AssertWorkspaceOpens(fixture.Store, fixture.WorkspacePath, CurrentPassphrase);
        AssertWorkspaceDoesNotOpen(fixture.Store, fixture.WorkspacePath, NewPassphrase);
    }

    [Fact]
    public void MatchingNewPassphraseIsRejectedBeforeRecoveryOrMutation()
    {
        using var fixture = new WorkspaceFixture();
        using var session = fixture.CreateWorkspace();
        var originalHash = Hash(fixture.WorkspacePath);

        var result = session.RotatePassphrase(
            Unlock(CurrentPassphrase),
            Create(CurrentPassphrase));

        Assert.Equal(PassphraseRotationStatus.NewPassphraseMatchesCurrent, result.Status);
        Assert.Null(result.Session);
        Assert.Equal(originalHash, Hash(fixture.WorkspacePath));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.dotorbit-recovery"));
        Assert.Equal("Home", session.Work.Read().Categories.Single().Name);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("long but not confirmed")]
    public void UnlockOnlyPassphraseIsRejectedAtTheSessionBoundary(string candidate)
    {
        using var fixture = new WorkspaceFixture();
        using var session = fixture.CreateWorkspace();
        var originalHash = Hash(fixture.WorkspacePath);

        var result = session.RotatePassphrase(
            Unlock(CurrentPassphrase),
            Unlock(candidate));

        Assert.Equal(PassphraseRotationStatus.InvalidNewPassphrase, result.Status);
        Assert.Null(result.Session);
        Assert.Equal(originalHash, Hash(fixture.WorkspacePath));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.dotorbit-recovery"));
        Assert.Equal("Home", session.Work.Read().Categories.Single().Name);
    }

    [Theory]
    [InlineData((int)EncryptedWorkspaceStore.WorkspacePassphraseRotationCheckpoint.RecoveryPointCreated)]
    [InlineData((int)EncryptedWorkspaceStore.WorkspacePassphraseRotationCheckpoint.CandidateWritten)]
    [InlineData((int)EncryptedWorkspaceStore.WorkspacePassphraseRotationCheckpoint.CandidateFlushed)]
    [InlineData((int)EncryptedWorkspaceStore.WorkspacePassphraseRotationCheckpoint.CandidateValidated)]
    [InlineData((int)EncryptedWorkspaceStore.WorkspacePassphraseRotationCheckpoint.BeforeReplacement)]
    public void InterruptionBeforeAtomicReplacementLeavesOldStoreAndSessionUsable(
        int interruptionValue)
    {
        var interruption =
            (EncryptedWorkspaceStore.WorkspacePassphraseRotationCheckpoint)interruptionValue;
        using var fixture = new WorkspaceFixture(
            checkpoint: checkpoint =>
            {
                if (checkpoint == interruption)
                {
                    throw new IOException("Injected passphrase rotation interruption.");
                }
            });
        using var session = fixture.CreateWorkspace();
        var originalHash = Hash(fixture.WorkspacePath);

        var result = session.RotatePassphrase(
            Unlock(CurrentPassphrase),
            Create(NewPassphrase));

        Assert.Equal(PassphraseRotationStatus.Failed, result.Status);
        Assert.Null(result.Session);
        Assert.NotNull(result.RecoveryPointPath);
        Assert.Equal(originalHash, Hash(fixture.WorkspacePath));
        Assert.Equal("Home", session.Work.Read().Categories.Single().Name);
        AssertWorkspaceOpens(fixture.Store, fixture.WorkspacePath, CurrentPassphrase);
        AssertWorkspaceDoesNotOpen(fixture.Store, fixture.WorkspacePath, NewPassphrase);
        AssertRecoveryUsesOldPassphrase(fixture.Store, result.RecoveryPointPath!);
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.creating"));
    }

    [Fact]
    public void CandidateIntegrityFailureLeavesOldStoreAndSessionUsable()
    {
        using var fixture = new WorkspaceFixture(integrityCheck: _ => "not ok");
        using var session = fixture.CreateWorkspace();
        var originalHash = Hash(fixture.WorkspacePath);

        var result = session.RotatePassphrase(
            Unlock(CurrentPassphrase),
            Create(NewPassphrase));

        Assert.Equal(PassphraseRotationStatus.Failed, result.Status);
        Assert.Equal(originalHash, Hash(fixture.WorkspacePath));
        Assert.Equal("Home", session.Work.Read().Categories.Single().Name);
        AssertWorkspaceOpens(fixture.Store, fixture.WorkspacePath, CurrentPassphrase);
        AssertWorkspaceDoesNotOpen(fixture.Store, fixture.WorkspacePath, NewPassphrase);
        AssertRecoveryUsesOldPassphrase(fixture.Store, Assert.IsType<string>(result.RecoveryPointPath));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.creating"));
    }

    [Fact]
    public void ReplacementFailureReturnsAReopenedOldPassphraseSession()
    {
        using var fixture = new WorkspaceFixture(
            fileOperations: new RotationFileOperations(failBeforeReplacement: true));
        var originalSession = fixture.CreateWorkspace();

        var result = originalSession.RotatePassphrase(
            Unlock(CurrentPassphrase),
            Create(NewPassphrase));
        using var continuedSession = result.Session;

        Assert.Equal(PassphraseRotationStatus.Failed, result.Status);
        Assert.NotNull(continuedSession);
        Assert.Throws<ObjectDisposedException>(() => originalSession.Work.Read());
        Assert.Equal("Home", continuedSession.Work.Read().Categories.Single().Name);
        AssertWorkspaceOpens(fixture.Store, fixture.WorkspacePath, CurrentPassphrase);
        AssertWorkspaceDoesNotOpen(fixture.Store, fixture.WorkspacePath, NewPassphrase);
        AssertRecoveryUsesOldPassphrase(fixture.Store, Assert.IsType<string>(result.RecoveryPointPath));
    }

    [Fact]
    public void ExceptionAfterAtomicReplacementIsResolvedAsSuccessfulRotation()
    {
        using var fixture = new WorkspaceFixture(
            fileOperations: new RotationFileOperations(failAfterReplacement: true));
        var originalSession = fixture.CreateWorkspace();

        var result = originalSession.RotatePassphrase(
            Unlock(CurrentPassphrase),
            Create(NewPassphrase));
        using var rotatedSession = result.Session;

        Assert.Equal(PassphraseRotationStatus.Rotated, result.Status);
        Assert.NotNull(rotatedSession);
        Assert.Throws<ObjectDisposedException>(() => originalSession.Work.Read());
        Assert.Equal("Home", rotatedSession.Work.Read().Categories.Single().Name);
        AssertWorkspaceDoesNotOpen(fixture.Store, fixture.WorkspacePath, CurrentPassphrase);
        AssertWorkspaceOpens(fixture.Store, fixture.WorkspacePath, NewPassphrase);
        AssertRecoveryUsesOldPassphrase(fixture.Store, Assert.IsType<string>(result.RecoveryPointPath));
    }

    [Fact]
    public void ReopenFailureAfterReplacementReturnsExplicitUnavailableState()
    {
        using var fixture = new WorkspaceFixture(reopenBlocked: _ => true);
        var originalSession = fixture.CreateWorkspace();

        var result = originalSession.RotatePassphrase(
            Unlock(CurrentPassphrase),
            Create(NewPassphrase));

        Assert.Equal(PassphraseRotationStatus.WorkspaceUnavailable, result.Status);
        Assert.Null(result.Session);
        Assert.Throws<ObjectDisposedException>(() => originalSession.Work.Read());
        AssertWorkspaceDoesNotOpen(fixture.Store, fixture.WorkspacePath, CurrentPassphrase);
        AssertWorkspaceOpens(fixture.Store, fixture.WorkspacePath, NewPassphrase);
        AssertRecoveryUsesOldPassphrase(fixture.Store, Assert.IsType<string>(result.RecoveryPointPath));
    }

    [Fact]
    public void SuccessfulRotationPreservesDataUsesConfiguredRecoveryAndPersistsNoSecrets()
    {
        using var fixture = new WorkspaceFixture();
        var originalSession = fixture.CreateWorkspace();
        var recoveryDirectory = Path.Combine(fixture.DirectoryPath, "configured-recovery");
        Assert.Equal(
            RecoveryDirectoryConfigurationStatus.Configured,
            originalSession.Recovery.ConfigureAutomaticRecoveryDirectory(recoveryDirectory).Status);
        originalSession.Work.CreateStandaloneTask(
            "Keep this task",
            "",
            originalSession.Work.Read().Categories.Single().Id,
            null);

        var result = originalSession.RotatePassphrase(
            Unlock(CurrentPassphrase),
            Create(NewPassphrase));
        using var rotatedSession = result.Session;

        Assert.Equal(PassphraseRotationStatus.Rotated, result.Status);
        Assert.NotNull(rotatedSession);
        Assert.StartsWith(recoveryDirectory, result.RecoveryPointPath, StringComparison.Ordinal);
        Assert.Contains(
            rotatedSession.Work.Read().Tasks,
            task => task.Title == "Keep this task");
        AssertWorkspaceDoesNotOpen(fixture.Store, fixture.WorkspacePath, CurrentPassphrase);
        AssertWorkspaceOpens(fixture.Store, fixture.WorkspacePath, NewPassphrase);
        AssertRecoveryUsesOldPassphrase(fixture.Store, Assert.IsType<string>(result.RecoveryPointPath));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.creating", SearchOption.AllDirectories));

        foreach (var path in Directory.GetFiles(fixture.DirectoryPath, "*", SearchOption.AllDirectories))
        {
            var contents = Encoding.UTF8.GetString(File.ReadAllBytes(path));
            Assert.DoesNotContain(CurrentPassphrase, contents, StringComparison.Ordinal);
            Assert.DoesNotContain(NewPassphrase, contents, StringComparison.Ordinal);
        }
    }

    private static void AssertRecoveryUsesOldPassphrase(
        EncryptedWorkspaceStore store,
        string recoveryPointPath)
    {
        AssertWorkspaceOpens(store, recoveryPointPath, CurrentPassphrase);
        AssertWorkspaceDoesNotOpen(store, recoveryPointPath, NewPassphrase);
    }

    private static void AssertWorkspaceOpens(
        EncryptedWorkspaceStore store,
        string path,
        string passphrase)
    {
        var result = store.Open(path, Unlock(passphrase));
        using var session = result.Session;
        Assert.Equal(WorkspaceOpenStatus.Opened, result.Status);
        Assert.NotNull(session);
    }

    private static void AssertWorkspaceDoesNotOpen(
        EncryptedWorkspaceStore store,
        string path,
        string passphrase)
    {
        var result = store.Open(path, Unlock(passphrase));
        using var session = result.Session;
        Assert.Equal(WorkspaceOpenStatus.InvalidPassphraseOrStore, result.Status);
        Assert.Null(session);
    }

    private static WorkspacePassphrase Create(string value) =>
        Assert.IsType<WorkspacePassphrase>(WorkspacePassphrase.Create(value, value).Passphrase);

    private static WorkspacePassphrase Unlock(string value) =>
        Assert.IsType<WorkspacePassphrase>(WorkspacePassphrase.ForUnlock(value));

    private static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class WorkspaceFixture : IDisposable
    {
        public WorkspaceFixture(
            IWorkspaceFileOperations? fileOperations = null,
            Action<EncryptedWorkspaceStore.WorkspacePassphraseRotationCheckpoint>? checkpoint = null,
            Func<SqliteConnection, string>? integrityCheck = null,
            Func<WorkspacePassphrase, bool>? reopenBlocked = null)
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                $"dot-orbit-passphrase-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(DirectoryPath);
            WorkspacePath = Path.Combine(DirectoryPath, "workspace.db");
            Store = new EncryptedWorkspaceStore(
                new SystemIdentifierGenerator(),
                fileOperations ?? new WorkspaceFileOperations(),
                passphraseRotationCheckpoint: checkpoint,
                passphraseRotationIntegrityCheck: integrityCheck,
                passphraseRotationReopenBlocked: reopenBlocked);
        }

        public string DirectoryPath { get; }

        public string WorkspacePath { get; }

        public EncryptedWorkspaceStore Store { get; }

        public IWorkspaceSession CreateWorkspace()
        {
            var result = Store.Create(
                WorkspacePath,
                Create(CurrentPassphrase),
                Assert.IsType<CategoryName>(CategoryName.Create("Home").CategoryName));
            Assert.Equal(WorkspaceCreationStatus.Created, result.Status);
            return Assert.IsAssignableFrom<IWorkspaceSession>(result.Session);
        }

        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }

    private sealed class RotationFileOperations(
        bool failRecoveryPublication = false,
        bool failBeforeReplacement = false,
        bool failAfterReplacement = false) : IWorkspaceFileOperations
    {
        private readonly WorkspaceFileOperations _inner = new();

        public string ResolvePath(string path) => _inner.ResolvePath(path);

        public bool Exists(string path) => _inner.Exists(path);

        public void EnsureParentDirectory(string path) => _inner.EnsureParentDirectory(path);

        public void EnsureDirectory(string path) => _inner.EnsureDirectory(path);

        public string GetCandidatePath(string targetPath, string identifier) =>
            _inner.GetCandidatePath(targetPath, identifier);

        public void Publish(string candidatePath, string targetPath)
        {
            if (failRecoveryPublication
                && targetPath.EndsWith(".dotorbit-recovery", StringComparison.Ordinal))
            {
                throw new IOException("Injected recovery publication failure.");
            }

            _inner.Publish(candidatePath, targetPath);
        }

        public void Copy(string sourcePath, string candidatePath) =>
            _inner.Copy(sourcePath, candidatePath);

        public void Flush(string path) => _inner.Flush(path);

        public void Replace(string candidatePath, string targetPath)
        {
            if (failBeforeReplacement)
            {
                throw new IOException("Injected replacement failure.");
            }

            _inner.Replace(candidatePath, targetPath);
            if (failAfterReplacement)
            {
                throw new IOException("Injected post-replacement interruption.");
            }
        }

        public void DeleteCandidate(string candidatePath) => _inner.DeleteCandidate(candidatePath);

        public IReadOnlyList<string> EnumerateFiles(string directoryPath, string searchPattern) =>
            _inner.EnumerateFiles(directoryPath, searchPattern);

        public string ReadAllText(string path) => _inner.ReadAllText(path);

        public void WriteAllText(string path, string contents) =>
            _inner.WriteAllText(path, contents);

        public void PublishOrReplace(string candidatePath, string targetPath) =>
            _inner.PublishOrReplace(candidatePath, targetPath);

        public void DeleteFile(string path) => _inner.DeleteFile(path);
    }
}
