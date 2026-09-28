using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DotOrbit.Core.Workspaces;
using DotOrbit.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DotOrbit.Storage.Sqlite.Tests;

public sealed class EncryptedWorkspaceStoreTests
{
    private const string ValidPassphrase = "correct horse battery";

    [Fact]
    public void CreatePublishesAnEncryptedSchemaWithOnlyTheNamedFirstCategory()
    {
        using var fixture = new WorkspaceFixture();

        var result = fixture.Store.Create(
            fixture.Path,
            CreatePassphrase(ValidPassphrase),
            CreateCategory("Personal Admin"));
        using var session = result.Session;

        Assert.Equal(WorkspaceCreationStatus.Created, result.Status);
        Assert.NotNull(result.Session);
        Assert.Equal(4, result.Session.SchemaVersion);
        Assert.Equal("Personal Admin", result.Session.FirstCategoryName);

        var fileBytes = File.ReadAllBytes(fixture.Path);
        Assert.False(fileBytes.AsSpan().StartsWith("SQLite format 3\0"u8));
        Assert.All(Directory.GetFiles(fixture.Directory), file =>
            Assert.DoesNotContain(
                "Personal Admin",
                Encoding.UTF8.GetString(File.ReadAllBytes(file)),
                StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(fixture.Directory, "*.creating*"));

        using var inspection = OpenInspectionConnection(fixture.Path, ValidPassphrase, readOnly: true);
        Assert.Equal(4L, ExecuteScalar<long>(inspection, "PRAGMA user_version;"));
        Assert.Equal("chacha20", ExecuteScalar<string>(inspection, "PRAGMA cipher;"));
        Assert.Equal(0L, ExecuteScalar<long>(inspection, "PRAGMA legacy;"));
        Assert.Equal(64007L, ExecuteScalar<long>(inspection, "PRAGMA kdf_iter;"));
        Assert.Equal(0L, ExecuteScalar<long>(inspection, "PRAGMA plaintext_header_size;"));
        Assert.Equal(1L, ExecuteScalar<long>(inspection, "PRAGMA hmac_check;"));
        Assert.Equal("Personal Admin", ExecuteScalar<string>(inspection, "SELECT name FROM categories;"));
        Assert.Equal(1L, ExecuteScalar<long>(inspection, "SELECT COUNT(*) FROM categories;"));
        Assert.Equal(
            "categories,projects,tasks",
            ExecuteScalar<string>(
                inspection,
                "SELECT group_concat(name, ',') FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_%';"));
    }

    [Fact]
    public void OpenWithTheCorrectPassphraseReopensTheWorkspace()
    {
        using var fixture = new WorkspaceFixture();
        CreateAndClose(fixture, "Home");

        var result = fixture.Store.Open(fixture.Path, UnlockPassphrase(ValidPassphrase));
        using var session = result.Session;

        Assert.Equal(WorkspaceOpenStatus.Opened, result.Status);
        Assert.NotNull(result.Session);
        Assert.Equal(4, result.Session.SchemaVersion);
        Assert.Equal("Home", result.Session.FirstCategoryName);
    }

    [Fact]
    public void CreateNeverReplacesAnExistingWorkspace()
    {
        using var fixture = new WorkspaceFixture();
        CreateAndClose(fixture, "Original");

        var originalHash = Hash(fixture.Path);

        var result = fixture.Store.Create(
            fixture.Path,
            CreatePassphrase("different valid password"),
            CreateCategory("Replacement"));

        Assert.Equal(WorkspaceCreationStatus.AlreadyExists, result.Status);
        Assert.Null(result.Session);
        Assert.Equal(originalHash, Hash(fixture.Path));
    }

    [Fact]
    public void WrongPassphraseFailsWithoutChangingTheWorkspace()
    {
        using var fixture = CreateWorkspace();

        AssertInvalidOpenLeavesBytesUnchanged(
            fixture,
            UnlockPassphrase("this is the wrong passphrase"));
    }

    [Fact]
    public void DamagedStoreFailsWithoutChangingTheWorkspace()
    {
        using var fixture = CreateWorkspace();
        var bytes = File.ReadAllBytes(fixture.Path);
        File.WriteAllBytes(fixture.Path, bytes[..Math.Min(bytes.Length, 64)]);

        AssertInvalidOpenLeavesBytesUnchanged(fixture, UnlockPassphrase(ValidPassphrase));
    }

    [Fact]
    public void TamperedStoreFailsWithoutChangingTheWorkspace()
    {
        using var fixture = CreateWorkspace();
        var bytes = File.ReadAllBytes(fixture.Path);
        bytes[Math.Min(128, bytes.Length - 1)] ^= 0x5A;
        File.WriteAllBytes(fixture.Path, bytes);

        AssertInvalidOpenLeavesBytesUnchanged(fixture, UnlockPassphrase(ValidPassphrase));
    }

    [Fact]
    public void NewerSchemaIsRefusedWithoutChangingTheWorkspace()
    {
        using var fixture = CreateWorkspace();
        using (var connection = OpenInspectionConnection(fixture.Path, ValidPassphrase, readOnly: false))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version = 5;";
            command.ExecuteNonQuery();
        }

        var originalHash = Hash(fixture.Path);

        var result = fixture.Store.Open(fixture.Path, UnlockPassphrase(ValidPassphrase));

        Assert.Equal(WorkspaceOpenStatus.UnsupportedSchema, result.Status);
        Assert.Null(result.Session);
        Assert.Equal(originalHash, Hash(fixture.Path));
    }

    [Fact]
    public void MissingStoreFailsWithoutCreatingAFile()
    {
        using var fixture = new WorkspaceFixture();

        var result = fixture.Store.Open(
            fixture.Path,
            UnlockPassphrase(ValidPassphrase));

        Assert.Equal(WorkspaceOpenStatus.InvalidPassphraseOrStore, result.Status);
        Assert.Null(result.Session);
        Assert.False(File.Exists(fixture.Path));
        Assert.Empty(Directory.GetFiles(fixture.Directory));
    }

    [Fact]
    public void FailedCategorySeedDoesNotPublishOrLeaveAPlaintextCandidate()
    {
        using var fixture = new WorkspaceFixture();
        var store = new EncryptedWorkspaceStore(
            new SequenceIdentifierGenerator("candidate", null));

        var result = store.Create(
            fixture.Path,
            CreatePassphrase(ValidPassphrase),
            CreateCategory("Home"));

        Assert.Equal(WorkspaceCreationStatus.Failed, result.Status);
        Assert.Null(result.Session);
        Assert.False(File.Exists(fixture.Path));
        Assert.Empty(Directory.GetFiles(fixture.Directory));
    }

    [Fact]
    public void OpenAcceptsAWorkspaceWithMoreThanOneCategory()
    {
        using var fixture = CreateWorkspace();
        using (var connection = OpenInspectionConnection(fixture.Path, ValidPassphrase, readOnly: false))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO categories (id, name, position)
                VALUES ('second-category', 'Work', 1);
                """;
            command.ExecuteNonQuery();
        }

        var result = fixture.Store.Open(fixture.Path, UnlockPassphrase(ValidPassphrase));
        using var session = result.Session;

        Assert.Equal(WorkspaceOpenStatus.Opened, result.Status);
        Assert.NotNull(session);
        Assert.Equal("Home", session.FirstCategoryName);
    }

    [Fact]
    public void PublicationFailureLeavesNoWorkspaceOrCandidate()
    {
        using var fixture = new WorkspaceFixture();
        var store = new EncryptedWorkspaceStore(
            new SequenceIdentifierGenerator("candidate", "category"),
            new FailingPublishFileOperations());

        var result = store.Create(
            fixture.Path,
            CreatePassphrase(ValidPassphrase),
            CreateCategory("Home"));

        Assert.Equal(WorkspaceCreationStatus.Failed, result.Status);
        Assert.Null(result.Session);
        Assert.False(File.Exists(fixture.Path));
        Assert.Empty(Directory.GetFiles(fixture.Directory));
    }

    private static WorkspaceFixture CreateWorkspace()
    {
        var fixture = new WorkspaceFixture();
        var result = fixture.Store.Create(
            fixture.Path,
            CreatePassphrase(ValidPassphrase),
            CreateCategory("Home"));
        Assert.Equal(WorkspaceCreationStatus.Created, result.Status);
        result.Session?.Dispose();
        return fixture;
    }

    private static void AssertInvalidOpenLeavesBytesUnchanged(
        WorkspaceFixture fixture,
        WorkspacePassphrase passphrase)
    {
        var originalHash = Hash(fixture.Path);

        var result = fixture.Store.Open(fixture.Path, passphrase);

        Assert.Equal(WorkspaceOpenStatus.InvalidPassphraseOrStore, result.Status);
        Assert.Null(result.Session);
        Assert.Equal(originalHash, Hash(fixture.Path));
    }

    private static WorkspacePassphrase CreatePassphrase(string value) =>
        Assert.IsType<WorkspacePassphrase>(WorkspacePassphrase.Create(value, value).Passphrase);

    private static WorkspacePassphrase UnlockPassphrase(string value) =>
        Assert.IsType<WorkspacePassphrase>(WorkspacePassphrase.ForUnlock(value));

    private static CategoryName CreateCategory(string value) =>
        Assert.IsType<CategoryName>(CategoryName.Create(value).CategoryName);

    private static void CreateAndClose(WorkspaceFixture fixture, string categoryName)
    {
        var result = fixture.Store.Create(
            fixture.Path,
            CreatePassphrase(ValidPassphrase),
            CreateCategory(categoryName));
        Assert.Equal(WorkspaceCreationStatus.Created, result.Status);
        result.Session?.Dispose();
    }

    private static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static SqliteConnection OpenInspectionConnection(
        string path,
        string passphrase,
        bool readOnly)
    {
        var uri = new Uri(path).AbsoluteUri
            + "?cipher=chacha20&legacy=0&kdf_iter=64007&plaintext_header_size=0&hmac_check=1";
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = uri,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false,
            Password = passphrase,
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    private static T ExecuteScalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        Assert.NotNull(value);
        return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }

    private sealed class WorkspaceFixture : IDisposable
    {
        public WorkspaceFixture()
        {
            Directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"dot-orbit-tests-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Directory);
            Path = System.IO.Path.Combine(Directory, "workspace.db");
        }

        public string Directory { get; }

        public string Path { get; }

        public EncryptedWorkspaceStore Store { get; } = new();

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }

    private sealed class SequenceIdentifierGenerator(params string?[] values) : IIdentifierGenerator
    {
        private readonly Queue<string?> _values = new(values);

        public string NewIdentifier() => _values.Dequeue()!;
    }

    private sealed class FailingPublishFileOperations : IWorkspaceFileOperations
    {
        private readonly WorkspaceFileOperations _inner = new();

        public string ResolvePath(string path) => _inner.ResolvePath(path);

        public bool Exists(string path) => _inner.Exists(path);

        public void EnsureParentDirectory(string path) => _inner.EnsureParentDirectory(path);

        public void EnsureDirectory(string path) => _inner.EnsureDirectory(path);

        public string GetCandidatePath(string targetPath, string identifier) =>
            _inner.GetCandidatePath(targetPath, identifier);

        public void Publish(string candidatePath, string targetPath) =>
            throw new IOException("Injected publication failure.");

        public void Copy(string sourcePath, string candidatePath) =>
            _inner.Copy(sourcePath, candidatePath);

        public void Flush(string path) => _inner.Flush(path);

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

        public void DeleteFile(string path) => _inner.DeleteFile(path);
    }
}
