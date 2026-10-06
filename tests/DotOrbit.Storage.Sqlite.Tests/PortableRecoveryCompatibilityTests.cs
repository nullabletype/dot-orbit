using System.Security.Cryptography;
using DotOrbit.Core.Workspaces;
using DotOrbit.Storage.Sqlite;
using Xunit;

namespace DotOrbit.Storage.Sqlite.Tests;

public sealed class PortableRecoveryCompatibilityTests
{
    private const string FixtureFileName = "portable-current-v12.dotorbit-recovery";
    private const string FixturePassphrase = "dot-orbit sample only";
    private const string FixtureSha256 = "d02eecfec5a6e756dfc2529ce9b42ec2c723847e7b8eb6442f90f63d1e581d07";

    [Fact]
    public void CheckedInRecoveryFixtureRestoresOnEverySupportedRuntime()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", FixtureFileName);
        Assert.True(File.Exists(fixturePath));
        Assert.Equal(FixtureSha256, Hash(fixturePath));

        var directory = Path.Combine(
            Path.GetTempPath(),
            $"dot-orbit-portable-recovery-{Guid.NewGuid():N}");
        var workspacePath = Path.Combine(directory, "workspace.db");
        var recoveryDirectory = Path.Combine(directory, "recovery");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new EncryptedWorkspaceStore();
            var passphrase = Assert.IsType<WorkspacePassphrase>(
                WorkspacePassphrase.Create(FixturePassphrase, FixturePassphrase).Passphrase);
            var category = Assert.IsType<CategoryName>(
                CategoryName.Create("Before portable restore").CategoryName);
            var created = store.Create(workspacePath, passphrase, category);
            using var session = Assert.IsAssignableFrom<IWorkspaceSession>(created.Session);
            Assert.Equal(WorkspaceCreationStatus.Created, created.Status);

            var restored = session.Recovery.Restore(fixturePath, recoveryDirectory);
            using var restoredSession = Assert.IsAssignableFrom<IWorkspaceSession>(restored.Session);

            Assert.Equal(WorkspaceRestoreStatus.Restored, restored.Status);
            Assert.Equal(EncryptedWorkspaceStore.CurrentSchemaVersion, restoredSession.SchemaVersion);
            Assert.Equal("Work", restoredSession.FirstCategoryName);
            Assert.Single(
                Directory.GetFiles(
                    recoveryDirectory,
                    $"*{EncryptedWorkspaceRecovery.RecoveryPointExtension}"));
            Assert.Equal(FixtureSha256, Hash(fixturePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
