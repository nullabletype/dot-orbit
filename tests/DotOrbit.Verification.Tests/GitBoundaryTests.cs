using System.Diagnostics;
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
    public async Task VerificationWorkspaceUsesDetachedCommitSnapshotAndRemovesIt()
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
            Assert.True(File.Exists(Path.Combine(snapshotPath, "tracked.txt")));
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
            if (snapshotPath is not null && Directory.Exists(snapshotPath))
            {
                RunGit(repositoryPath, "worktree", "remove", "--force", snapshotPath);
            }

            DeleteDirectory(repositoryPath);
        }
    }

    [Fact]
    public async Task VerificationWorkspaceRetriesFailedCleanup()
    {
        var runner = new WorkspaceProcessRunner(1, 0);
        var provider = new GitVerificationWorkspaceProvider(Path.GetTempPath(), runner, TimeSpan.Zero);
        var snapshotPath = await provider.CreateAsync(new string('a', 40), CancellationToken.None);

        Assert.NotNull(snapshotPath);
        Assert.True(await provider.RemoveAsync(snapshotPath, CancellationToken.None));
        Assert.Equal(2, runner.RemoveCalls);
    }

    [Fact]
    public async Task VerificationWorkspaceFailsAfterBoundedCleanupAttempts()
    {
        var runner = new WorkspaceProcessRunner(Enumerable.Repeat(1, 10).ToArray());
        var provider = new GitVerificationWorkspaceProvider(Path.GetTempPath(), runner, TimeSpan.Zero);
        var snapshotPath = await provider.CreateAsync(new string('a', 40), CancellationToken.None);

        Assert.NotNull(snapshotPath);
        try
        {
            Assert.False(await provider.RemoveAsync(snapshotPath, CancellationToken.None));
            Assert.Equal(10, runner.RemoveCalls);
        }
        finally
        {
            DeleteDirectory(snapshotPath);
        }
    }

    [Fact]
    public async Task VerificationWorkspaceDoesNotTreatPartialRemovalAsSuccess()
    {
        var runner = new WorkspaceProcessRunner(
            Enumerable.Repeat(1, 10).ToArray(),
            deleteOnFirstFailedRemoval: true);
        var provider = new GitVerificationWorkspaceProvider(Path.GetTempPath(), runner, TimeSpan.Zero);
        var snapshotPath = await provider.CreateAsync(new string('a', 40), CancellationToken.None);

        Assert.NotNull(snapshotPath);
        Assert.False(await provider.RemoveAsync(snapshotPath, CancellationToken.None));
        Assert.Equal(10, runner.RemoveCalls);
    }

    [Fact]
    public async Task VerificationWorkspaceRefusesUnownedCleanupPath()
    {
        var runner = new WorkspaceProcessRunner(0);
        var provider = new GitVerificationWorkspaceProvider(Path.GetTempPath(), runner, TimeSpan.Zero);

        Assert.False(await provider.RemoveAsync(Path.GetTempPath(), CancellationToken.None));
        Assert.Equal(0, runner.RemoveCalls);
    }

    [Fact]
    public async Task VerificationWorkspaceCleanupHonoursCancellationBetweenAttempts()
    {
        var runner = new WorkspaceProcessRunner(1, 0);
        var provider = new GitVerificationWorkspaceProvider(Path.GetTempPath(), runner, TimeSpan.Zero);
        var snapshotPath = await provider.CreateAsync(new string('a', 40), CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.NotNull(snapshotPath);
        try
        {
            await Assert.ThrowsAsync<TaskCanceledException>(
                () => provider.RemoveAsync(snapshotPath, cancellation.Token));
            Assert.Equal(1, runner.RemoveCalls);
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

    private sealed class WorkspaceProcessRunner : IProcessRunner
    {
        private readonly Queue<int> removalExitCodes;
        private readonly bool deleteOnFirstFailedRemoval;

        public WorkspaceProcessRunner(params int[] removalExitCodes)
            : this(removalExitCodes, deleteOnFirstFailedRemoval: false)
        {
        }

        public WorkspaceProcessRunner(int[] removalExitCodes, bool deleteOnFirstFailedRemoval)
        {
            this.removalExitCodes = new Queue<int>(removalExitCodes);
            this.deleteOnFirstFailedRemoval = deleteOnFirstFailedRemoval;
        }

        public int RemoveCalls { get; private set; }

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            if (request.Arguments.Contains("add", StringComparer.Ordinal))
            {
                Directory.CreateDirectory(request.Arguments[^2]);
                return Task.FromResult(new ProcessResult(0, "", ""));
            }

            RemoveCalls++;
            var path = request.Arguments[^1];
            var exitCode = removalExitCodes.Dequeue();
            if (exitCode == 0 || (deleteOnFirstFailedRemoval && RemoveCalls == 1))
            {
                DeleteDirectory(path);
            }

            return Task.FromResult(new ProcessResult(exitCode, "", ""));
        }
    }
}
