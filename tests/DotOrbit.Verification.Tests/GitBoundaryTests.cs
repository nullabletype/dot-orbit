using System.Diagnostics;
using System.IO.Compression;
using DotOrbit.Verification;
using Xunit;

namespace DotOrbit.Verification.Tests;

public sealed class GitBoundaryTests
{
    [Fact]
    public async Task RepositoryInspectorDistinguishesIgnoredUntrackedStagedAndUnstagedFiles()
    {
        var repositoryPath = CreateRepository();
        try
        {
            var runner = new ProcessRunner(new NullOutput());
            var inspector = new GitRepositoryInspector(repositoryPath, runner);

            Assert.True((await inspector.ReadAsync(CancellationToken.None))!.IsClean);
            File.WriteAllText(Path.Combine(repositoryPath, "ignored.txt"), "ignored");
            Assert.True((await inspector.ReadAsync(CancellationToken.None))!.IsClean);

            var trackedPath = Path.Combine(repositoryPath, "work.txt");
            File.WriteAllText(trackedPath, "untracked");
            Assert.False((await inspector.ReadAsync(CancellationToken.None))!.IsClean);
            RunGit(repositoryPath, "add", "work.txt");
            Assert.False((await inspector.ReadAsync(CancellationToken.None))!.IsClean);
            RunGit(repositoryPath, "commit", "-m", "Add work file");
            File.AppendAllText(trackedPath, " changed");
            Assert.False((await inspector.ReadAsync(CancellationToken.None))!.IsClean);
        }
        finally
        {
            DeleteDirectory(repositoryPath);
        }
    }

    [Fact]
    public async Task VerificationWorkspaceUsesExactCommitSnapshotAndRemovesIt()
    {
        var repositoryPath = CreateRepository();
        string? snapshotPath = null;
        try
        {
            var runner = new ProcessRunner(new NullOutput());
            var inspector = new GitRepositoryInspector(repositoryPath, runner);
            var commit = (await inspector.ReadAsync(CancellationToken.None))!.Commit;
            var provider = new GitVerificationWorkspaceProvider(repositoryPath, runner);

            snapshotPath = await provider.CreateAsync(commit, CancellationToken.None);

            Assert.NotNull(snapshotPath);
            Assert.False(File.Exists(Path.Combine(snapshotPath, ".git")));
            Assert.False(Directory.Exists(Path.Combine(snapshotPath, ".git")));
            Assert.Equal("committed", File.ReadAllText(Path.Combine(snapshotPath, "tracked.txt")));
            File.WriteAllText(Path.Combine(repositoryPath, "tracked.txt"), "changed outside snapshot");
            Assert.Equal("committed", File.ReadAllText(Path.Combine(snapshotPath, "tracked.txt")));
            var readOnlyPath = Path.Combine(snapshotPath, "read-only-output.tmp");
            File.WriteAllText(readOnlyPath, "generated output");
            File.SetAttributes(readOnlyPath, FileAttributes.ReadOnly);
            Assert.True(await provider.RemoveAsync(snapshotPath, CancellationToken.None));
            Assert.False(Directory.Exists(snapshotPath));
            snapshotPath = null;
        }
        finally
        {
            if (snapshotPath is not null)
            {
                DeleteDirectory(snapshotPath);
            }

            DeleteDirectory(repositoryPath);
        }
    }

    [Fact]
    public async Task VerificationWorkspaceDisablesConfiguredWorkingTreeLineEndingConversion()
    {
        var repositoryPath = CreateRepository();
        string? snapshotPath = null;
        try
        {
            const string committedContent = "line one\nline two\n";
            File.WriteAllText(Path.Combine(repositoryPath, "tracked.txt"), committedContent);
            RunGit(repositoryPath, "add", "tracked.txt");
            RunGit(repositoryPath, "commit", "--quiet", "-m", "Add line endings fixture");
            RunGit(repositoryPath, "config", "core.autocrlf", "true");
            var runner = new ProcessRunner(new NullOutput());
            var inspector = new GitRepositoryInspector(repositoryPath, runner);
            var commit = (await inspector.ReadAsync(CancellationToken.None))!.Commit;
            var provider = new GitVerificationWorkspaceProvider(repositoryPath, runner, TimeSpan.Zero);

            snapshotPath = await provider.CreateAsync(commit, CancellationToken.None);

            Assert.NotNull(snapshotPath);
            Assert.Equal(committedContent, File.ReadAllText(Path.Combine(snapshotPath, "tracked.txt")));
            Assert.True(await provider.RemoveAsync(snapshotPath, CancellationToken.None));
            snapshotPath = null;
        }
        finally
        {
            if (snapshotPath is not null)
            {
                DeleteDirectory(snapshotPath);
            }

            DeleteDirectory(repositoryPath);
        }
    }

    [Fact]
    public void GitBlobObjectIdMatchesKnownGitVectors()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dot-orbit-blob-{Guid.NewGuid():N}");
        try
        {
            var vectors = new (byte[] Content, string ObjectId)[]
            {
                ([], "e69de29bb2d1d6434b8b29ae775ad8c2e48c5391"),
                ("hello\n"u8.ToArray(), "ce013625030ba8dba906f756967f9e9ca394464a"),
                ("hello\r\n"u8.ToArray(), "ef0493b275aa2080237f676d2ef6559246f56636"),
                ([0x00, 0x01, 0xff], "494b1410a95b9ef0a980c33411fbf7d564472741"),
            };

            foreach (var (content, objectId) in vectors)
            {
                File.WriteAllBytes(path, content);
                Assert.Equal(objectId, GitVerificationWorkspaceProvider.ComputeGitBlobObjectId(path));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task VerificationWorkspaceSupportsNestedPathsContainingSpaces()
    {
        const string relativePath = "nested folder/tracked file.txt";
        var runner = new ArchiveProcessRunner(
            treeOutput: $"100644 blob da2bdc1b30ad8d1a827b1f81f14f674716f00927\t{relativePath}\0",
            archiveEntryPath: relativePath);
        var provider = new GitVerificationWorkspaceProvider(Path.GetTempPath(), runner, TimeSpan.Zero);
        var snapshotPath = await provider.CreateAsync(new string('a', 40), CancellationToken.None);

        Assert.NotNull(snapshotPath);
        Assert.Equal("committed", File.ReadAllText(Path.Combine(snapshotPath, "nested folder", "tracked file.txt")));
        Assert.True(await provider.RemoveAsync(snapshotPath, CancellationToken.None));
    }

    [Fact]
    public async Task VerificationWorkspaceRejectsSamePathWithDifferentBlobContent()
    {
        var directoryDeletion = new ControlledDeletion(true);
        var fileDeletion = new ControlledFileDeletion(true);
        var diagnostics = new List<string>();
        var provider = new GitVerificationWorkspaceProvider(
            Path.GetTempPath(),
            new ArchiveProcessRunner(archiveContent: "altered"),
            TimeSpan.Zero,
            directoryDeletion.TryDelete,
            fileDeletion.TryDelete,
            diagnostics.Add);

        Assert.Null(await provider.CreateAsync(new string('a', 40), CancellationToken.None));
        Assert.Equal(1, directoryDeletion.Calls);
        Assert.Equal(1, fileDeletion.Calls);
        Assert.Equal(["verify: snapshot result=failed reason=blob-mismatch"], diagnostics);
    }

    [Theory]
    [InlineData("100755")]
    [InlineData("120000")]
    [InlineData("160000")]
    public async Task VerificationWorkspaceRejectsTreesThatArchiveCannotRepresentExactly(string mode)
    {
        var runner = new ArchiveProcessRunner(treeOutput: $"{mode} blob synthetic\ttracked.txt\0");
        var provider = new GitVerificationWorkspaceProvider(Path.GetTempPath(), runner, TimeSpan.Zero);

        Assert.Null(await provider.CreateAsync(new string('a', 40), CancellationToken.None));
        Assert.Equal(0, runner.ArchiveCalls);
    }

    [Fact]
    public async Task VerificationWorkspaceRejectsLocalExportAttributesThatChangeTheCommitTree()
    {
        var repositoryPath = CreateRepository();
        try
        {
            var runner = new ProcessRunner(new NullOutput());
            var inspector = new GitRepositoryInspector(repositoryPath, runner);
            var commit = (await inspector.ReadAsync(CancellationToken.None))!.Commit;
            File.WriteAllText(Path.Combine(repositoryPath, ".git", "info", "attributes"), "tracked.txt export-ignore\n");
            var provider = new GitVerificationWorkspaceProvider(repositoryPath, runner, TimeSpan.Zero);

            Assert.Null(await provider.CreateAsync(commit, CancellationToken.None));
        }
        finally
        {
            DeleteDirectory(repositoryPath);
        }
    }

    [Fact]
    public async Task VerificationWorkspaceRejectsLocalExportSubstitutionThatChangesBlobContent()
    {
        var repositoryPath = CreateRepository();
        try
        {
            File.WriteAllText(Path.Combine(repositoryPath, "tracked.txt"), "$Format:%H$");
            RunGit(repositoryPath, "add", "tracked.txt");
            RunGit(repositoryPath, "commit", "--quiet", "-m", "Add archive placeholder");
            File.WriteAllText(Path.Combine(repositoryPath, ".git", "info", "attributes"), "tracked.txt export-subst\n");
            var runner = new ProcessRunner(new NullOutput());
            var inspector = new GitRepositoryInspector(repositoryPath, runner);
            var commit = (await inspector.ReadAsync(CancellationToken.None))!.Commit;
            var directoryDeletion = new ControlledDeletion(true);
            var fileDeletion = new ControlledFileDeletion(true);
            var provider = new GitVerificationWorkspaceProvider(
                repositoryPath,
                runner,
                TimeSpan.Zero,
                directoryDeletion.TryDelete,
                fileDeletion.TryDelete);

            Assert.Null(await provider.CreateAsync(commit, CancellationToken.None));
            Assert.Equal(1, directoryDeletion.Calls);
            Assert.Equal(1, fileDeletion.Calls);
        }
        finally
        {
            DeleteDirectory(repositoryPath);
        }
    }

    [Fact]
    public async Task VerificationWorkspaceIgnoresReplacementRefsWhenReadingExactCommit()
    {
        var repositoryPath = CreateRepository();
        string? snapshotPath = null;
        try
        {
            var runner = new ProcessRunner(new NullOutput());
            var inspector = new GitRepositoryInspector(repositoryPath, runner);
            var originalCommit = (await inspector.ReadAsync(CancellationToken.None))!.Commit;
            File.WriteAllText(Path.Combine(repositoryPath, "tracked.txt"), "replacement");
            RunGit(repositoryPath, "add", "tracked.txt");
            RunGit(repositoryPath, "commit", "--quiet", "-m", "Replacement commit");
            var replacementCommit = (await inspector.ReadAsync(CancellationToken.None))!.Commit;
            RunGit(repositoryPath, "replace", originalCommit, replacementCommit);
            var provider = new GitVerificationWorkspaceProvider(repositoryPath, runner, TimeSpan.Zero);

            snapshotPath = await provider.CreateAsync(originalCommit, CancellationToken.None);

            Assert.NotNull(snapshotPath);
            Assert.Equal("committed", File.ReadAllText(Path.Combine(snapshotPath, "tracked.txt")));
            Assert.True(await provider.RemoveAsync(snapshotPath, CancellationToken.None));
            snapshotPath = null;
        }
        finally
        {
            if (snapshotPath is not null)
            {
                DeleteDirectory(snapshotPath);
            }

            DeleteDirectory(repositoryPath);
        }
    }

    [Fact]
    public async Task VerificationWorkspaceRemovesPartialArchiveWhenGitArchiveFails()
    {
        var runner = new ArchiveProcessRunner(archiveExitCode: 1, writeInvalidArchive: true);
        var fileDeletion = new ControlledFileDeletion(true);
        var provider = new GitVerificationWorkspaceProvider(
            Path.GetTempPath(), runner, TimeSpan.Zero, deleteFile: fileDeletion.TryDelete);

        Assert.Null(await provider.CreateAsync(new string('a', 40), CancellationToken.None));
        Assert.Equal(1, fileDeletion.Calls);
        Assert.False(File.Exists(runner.LastArchivePath));
    }

    [Fact]
    public async Task VerificationWorkspaceRemovesPartialArchiveWhenArchiveRunnerThrows()
    {
        var runner = new ArchiveProcessRunner(
            writeInvalidArchive: true,
            archiveException: new OperationCanceledException("synthetic cancellation"));
        var fileDeletion = new ControlledFileDeletion(true);
        var provider = new GitVerificationWorkspaceProvider(
            Path.GetTempPath(), runner, TimeSpan.Zero, deleteFile: fileDeletion.TryDelete);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => provider.CreateAsync(new string('a', 40), CancellationToken.None));
        Assert.Equal(1, fileDeletion.Calls);
        Assert.False(File.Exists(runner.LastArchivePath));
    }

    [Fact]
    public async Task VerificationWorkspaceCleansDirectoryAndArchiveIndependentlyWhenExtractionFails()
    {
        var runner = new ArchiveProcessRunner(writeInvalidArchive: true);
        var directoryDeletion = new ControlledDeletion(Enumerable.Repeat(false, 60).ToArray());
        var fileDeletion = new ControlledFileDeletion(true);
        var provider = new GitVerificationWorkspaceProvider(
            Path.GetTempPath(),
            runner,
            TimeSpan.Zero,
            directoryDeletion.TryDelete,
            fileDeletion.TryDelete);

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => provider.CreateAsync(new string('a', 40), CancellationToken.None));
            Assert.Equal(60, directoryDeletion.Calls);
            Assert.Equal(1, fileDeletion.Calls);
            Assert.False(File.Exists(runner.LastArchivePath));
        }
        finally
        {
            if (runner.LastArchivePath is not null)
            {
                DeleteDirectory(Path.ChangeExtension(runner.LastArchivePath, null));
            }
        }
    }

    [Fact]
    public async Task VerificationWorkspaceReportsArchiveCleanupExhaustionAndRemovesExtractedDirectory()
    {
        var runner = new ArchiveProcessRunner();
        var directoryDeletion = new ControlledDeletion(true);
        var fileDeletion = new ControlledFileDeletion(Enumerable.Repeat(false, 60).ToArray());
        var provider = new GitVerificationWorkspaceProvider(
            Path.GetTempPath(),
            runner,
            TimeSpan.Zero,
            directoryDeletion.TryDelete,
            fileDeletion.TryDelete);

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => provider.CreateAsync(new string('a', 40), CancellationToken.None));
            Assert.Equal(60, fileDeletion.Calls);
            Assert.Equal(1, directoryDeletion.Calls);
        }
        finally
        {
            if (runner.LastArchivePath is not null)
            {
                File.Delete(runner.LastArchivePath);
            }
        }
    }

    [Fact]
    public async Task VerificationWorkspaceRetriesFailedCleanup()
    {
        var runner = new ArchiveProcessRunner();
        var deletion = new ControlledDeletion(false, true);
        var provider = new GitVerificationWorkspaceProvider(
            Path.GetTempPath(), runner, TimeSpan.Zero, deletion.TryDelete);
        var snapshotPath = await provider.CreateAsync(new string('a', 40), CancellationToken.None);

        Assert.NotNull(snapshotPath);
        Assert.True(await provider.RemoveAsync(snapshotPath, CancellationToken.None));
        Assert.Equal(2, deletion.Calls);
    }

    [Fact]
    public async Task VerificationWorkspaceFailsAfterBoundedCleanupAttempts()
    {
        var deletion = new ControlledDeletion(Enumerable.Repeat(false, 60).ToArray());
        var provider = new GitVerificationWorkspaceProvider(
            Path.GetTempPath(), new ArchiveProcessRunner(), TimeSpan.Zero, deletion.TryDelete);
        var snapshotPath = await provider.CreateAsync(new string('a', 40), CancellationToken.None);

        Assert.NotNull(snapshotPath);
        try
        {
            Assert.False(await provider.RemoveAsync(snapshotPath, CancellationToken.None));
            Assert.Equal(60, deletion.Calls);
        }
        finally
        {
            DeleteDirectory(snapshotPath);
        }
    }

    [Fact]
    public async Task VerificationWorkspaceRefusesUnownedCleanupPath()
    {
        var deletion = new ControlledDeletion(true);
        var provider = new GitVerificationWorkspaceProvider(
            Path.GetTempPath(), new ArchiveProcessRunner(), TimeSpan.Zero, deletion.TryDelete);

        Assert.False(await provider.RemoveAsync(Path.GetTempPath(), CancellationToken.None));
        Assert.Equal(0, deletion.Calls);
    }

    [Fact]
    public async Task VerificationWorkspaceCleanupHonoursCancellationBetweenAttempts()
    {
        var deletion = new ControlledDeletion(false, true);
        var provider = new GitVerificationWorkspaceProvider(
            Path.GetTempPath(), new ArchiveProcessRunner(), TimeSpan.Zero, deletion.TryDelete);
        var snapshotPath = await provider.CreateAsync(new string('a', 40), CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.NotNull(snapshotPath);
        try
        {
            await Assert.ThrowsAsync<TaskCanceledException>(
                () => provider.RemoveAsync(snapshotPath, cancellation.Token));
            Assert.Equal(1, deletion.Calls);
        }
        finally
        {
            DeleteDirectory(snapshotPath);
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(entry, FileAttributes.Normal);
        }

        File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }

    private static string CreateRepository()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dot-orbit-git-boundary-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        RunGit(path, "init", "--quiet");
        RunGit(path, "config", "user.name", "Dot Orbit Tests");
        RunGit(path, "config", "user.email", "dot-orbit-tests@example.invalid");
        File.WriteAllText(Path.Combine(path, ".gitignore"), "ignored.txt\n");
        File.WriteAllText(Path.Combine(path, "tracked.txt"), "committed");
        RunGit(path, "add", ".");
        RunGit(path, "commit", "--quiet", "-m", "Initial commit");
        return path;
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, standardError);
    }

    private sealed class NullOutput : IVerificationOutput
    {
        public void Write(string message)
        {
        }

        public void WriteError(string message)
        {
        }
    }

    private sealed class ArchiveProcessRunner(
        string treeOutput = "100644 blob da2bdc1b30ad8d1a827b1f81f14f674716f00927\ttracked.txt\0",
        int archiveExitCode = 0,
        bool writeInvalidArchive = false,
        Exception? archiveException = null,
        string archiveEntryPath = "tracked.txt",
        string archiveContent = "committed") : IProcessRunner
    {
        public int ArchiveCalls { get; private set; }
        public string? LastArchivePath { get; private set; }

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            if (request.Arguments.Contains("ls-tree", StringComparer.Ordinal))
            {
                return Task.FromResult(new ProcessResult(0, treeOutput, ""));
            }

            ArchiveCalls++;
            var outputIndex = request.Arguments.ToList().IndexOf("--output");
            var archivePath = request.Arguments[outputIndex + 1];
            LastArchivePath = archivePath;
            if (writeInvalidArchive)
            {
                File.WriteAllText(archivePath, "not a zip archive");
                if (archiveException is not null)
                {
                    throw archiveException;
                }

                return Task.FromResult(new ProcessResult(archiveExitCode, "", "synthetic archive failure"));
            }

            using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
            var entry = archive.CreateEntry(archiveEntryPath);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(archiveContent);
            return Task.FromResult(new ProcessResult(archiveExitCode, "", ""));
        }
    }

    private sealed class ControlledDeletion(params bool[] results)
    {
        private readonly Queue<bool> results = new(results);

        public int Calls { get; private set; }

        public bool TryDelete(string path)
        {
            Calls++;
            var result = results.Dequeue();
            if (result)
            {
                DeleteDirectory(path);
            }

            return result;
        }
    }

    private sealed class ControlledFileDeletion(params bool[] results)
    {
        private readonly Queue<bool> results = new(results);

        public int Calls { get; private set; }

        public bool TryDelete(string path)
        {
            Calls++;
            var result = results.Dequeue();
            if (result && File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }

            return result;
        }
    }
}
