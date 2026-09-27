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
        fixture.SetSchemaVersion(selectedPath, 2);
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
    public void FailedReopenRollsBackTheReplacementAndReturnsAUsableCurrentSession()
    {
        using var fixture = new RecoveryFixture();
        var originalSession = fixture.CreateWorkspace("Selected");
        var selected = originalSession.Recovery.CreateRecoveryPoint(fixture.RecoveryDirectory);
        originalSession.Dispose();
        fixture.ChangeFirstCategory("Current");
        using var currentSession = fixture.OpenWorkspace();
        var originalHash = Hash(fixture.WorkspacePath);
        fixture.FileOperations.Failure = FailurePoint.CorruptAfterReplace;

        var result = currentSession.Recovery.Restore(
            Assert.IsType<string>(selected.RecoveryPointPath),
            fixture.RecoveryDirectory);
        using var reopenedSession = result.Session;

        Assert.Equal(WorkspaceRestoreStatus.Failed, result.Status);
        Assert.NotNull(reopenedSession);
        Assert.Equal("Current", reopenedSession.FirstCategoryName);
        Assert.Equal(originalHash, Hash(fixture.WorkspacePath));
        Assert.Empty(Directory.GetFiles(fixture.Directory, "*.failed"));
        Assert.Empty(Directory.GetFiles(fixture.Directory, "*.rollback"));
    }

    [Fact]
    public void RollbackPermissionFailureReturnsAUsableSessionForTheKnownValidRollbackFile()
    {
        using var fixture = new RecoveryFixture();
        var originalSession = fixture.CreateWorkspace("Selected");
        var selected = originalSession.Recovery.CreateRecoveryPoint(fixture.RecoveryDirectory);
        originalSession.Dispose();
        fixture.ChangeFirstCategory("Current");
        using var currentSession = fixture.OpenWorkspace();
        fixture.FileOperations.Failure = FailurePoint.CorruptAfterReplaceAndFailRollback;

        var result = currentSession.Recovery.Restore(
            Assert.IsType<string>(selected.RecoveryPointPath),
            fixture.RecoveryDirectory);
        using var rollbackSession = result.Session;

        Assert.Equal(WorkspaceRestoreStatus.Failed, result.Status);
        Assert.NotNull(rollbackSession);
        Assert.Equal("Current", rollbackSession.FirstCategoryName);
        Assert.Single(Directory.GetFiles(fixture.Directory, "*.rollback"));
        Assert.Equal(
            2,
            Directory.GetFiles(
                fixture.RecoveryDirectory,
                $"*{EncryptedWorkspaceRecovery.RecoveryPointExtension}").Length);
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
        CorruptAfterReplace,
        CorruptAfterReplaceAndFailRollback,
    }

    private sealed class RecoveryFixture : IDisposable
    {
        private readonly string _passphrase;

        public RecoveryFixture(string passphrase = ValidPassphrase)
        {
            _passphrase = passphrase;
            Directory = Path.Combine(
                Path.GetTempPath(),
                $"dot-orbit-recovery-tests-{Guid.NewGuid():N}");
            RecoveryDirectory = Path.Combine(Directory, "recovery");
            WorkspacePath = Path.Combine(Directory, "workspace.db");
            System.IO.Directory.CreateDirectory(Directory);
            FileOperations = new FaultInjectingFileOperations();
            Store = new EncryptedWorkspaceStore(
                new SystemIdentifierGenerator(),
                FileOperations);
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

        public Action<string, string>? BeforePublish { get; set; }

        public FailurePoint Failure { get; set; }

        private int ReplaceCallCount { get; set; }

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

        public void Flush(string path)
        {
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

        public void Replace(string candidatePath, string targetPath, string rollbackPath)
        {
            ReplaceCallCount++;
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

            if (Failure == FailurePoint.CorruptAfterReplaceAndFailRollback
                && ReplaceCallCount > 1)
            {
                throw new UnauthorizedAccessException();
            }

            _inner.Replace(candidatePath, targetPath, rollbackPath);
            if (Failure is FailurePoint.CorruptAfterReplace
                or FailurePoint.CorruptAfterReplaceAndFailRollback)
            {
                if (Failure == FailurePoint.CorruptAfterReplace)
                {
                    Failure = FailurePoint.None;
                }

                var bytes = File.ReadAllBytes(targetPath);
                bytes[Math.Min(128, bytes.Length - 1)] ^= 0x5A;
                File.WriteAllBytes(targetPath, bytes);
            }
        }

        public void DeleteCandidate(string candidatePath) => _inner.DeleteCandidate(candidatePath);
    }
}
