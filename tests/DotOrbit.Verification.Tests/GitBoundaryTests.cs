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
        var runner = new WorkspaceProcessRunner([1, 0], canonicalPathOverride: "/canonical/temp/worktree");
        var provider = new GitVerificationWorkspaceProvider(Path.GetTempPath(), runner, TimeSpan.Zero);
        var snapshotPath = await provider.CreateAsync(new string('a', 40), CancellationToken.None);

        Assert.NotNull(snapshotPath);
        Assert.True(await provider.RemoveAsync(snapshotPath, CancellationToken.None));
        Assert.Equal(2, runner.RemoveCalls);
    }

    [Fact]
    public void WorktreeRegistrationParserHandlesRawNonAsciiPaths()
    {
        const string path = "C:/Témp/工作/dov-123";
        var output = $"worktree {path}\0HEAD synthetic\0branch refs/heads/example\0\0";

        Assert.True(GitVerificationWorkspaceProvider.ContainsRegisteredWorktree(output, path));
        Assert.False(GitVerificationWorkspaceProvider.ContainsRegisteredWorktree(output, "C:/Temp/other"));
    }

    [Fact]
    public async Task VerificationWorkspaceCleansUpWhenCanonicalPathLookupFails()
    {
        var runner = new WorkspaceProcessRunner([0], canonicalLookupFails: true);
        var provider = new GitVerificationWorkspaceProvider(Path.GetTempPath(), runner, TimeSpan.Zero);

        Assert.Null(await provider.CreateAsync(new string('a', 40), CancellationToken.None));
        Assert.Equal(1, runner.RemoveCalls);
    }

    [Fact]
    public async Task VerificationWorkspaceRetriesCleanupWhenCanonicalPathLookupFails()
    {
        var runner = new WorkspaceProcessRunner([1, 0], canonicalLookupFails: true);
        var provider = new GitVerificationWorkspaceProvider(Path.GetTempPath(), runner, TimeSpan.Zero);

        Assert.Null(await provider.CreateAsync(new string('a', 40), CancellationToken.None));
        Assert.Equal(2, runner.RemoveCalls);
    }

    [Fact]
    public async Task VerificationWorkspacePropagatesExhaustedCleanupAfterCanonicalPathLookupFails()
    {
        var runner = new WorkspaceProcessRunner(
            Enumerable.Repeat(1, 10).ToArray(),
            canonicalLookupFails: true);
        var provider = new GitVerificationWorkspaceProvider(Path.GetTempPath(), runner, TimeSpan.Zero);

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => provider.CreateAsync(new string('a', 40), CancellationToken.None));
            Assert.Equal(10, runner.RemoveCalls);
        }
        finally
        {
            DeleteDirectory(runner.CreatedPath!);
        }
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
    public async Task VerificationWorkspaceFinishesDeletionAfterGitUnregistersIt()
    {
        var runner = new WorkspaceProcessRunner(
            [1],
            unregisterOnFirstFailedRemoval: true);
        var provider = new GitVerificationWorkspaceProvider(Path.GetTempPath(), runner, TimeSpan.Zero);
        var snapshotPath = await provider.CreateAsync(new string('a', 40), CancellationToken.None);

        Assert.NotNull(snapshotPath);
        Assert.True(await provider.RemoveAsync(snapshotPath, CancellationToken.None));
        Assert.Equal(1, runner.RemoveCalls);
        Assert.False(Directory.Exists(snapshotPath));
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
        private readonly bool unregisterOnFirstFailedRemoval;
        private readonly string? canonicalPathOverride;
        private readonly bool canonicalLookupFails;
        private string? worktreePath;
        private string? canonicalPath;
        private bool registered;

        public WorkspaceProcessRunner(params int[] removalExitCodes)
            : this(removalExitCodes, unregisterOnFirstFailedRemoval: false, canonicalPathOverride: null)
        {
        }

        public WorkspaceProcessRunner(
            int[] removalExitCodes,
            bool unregisterOnFirstFailedRemoval = false,
            string? canonicalPathOverride = null,
            bool canonicalLookupFails = false)
        {
            this.removalExitCodes = new Queue<int>(removalExitCodes);
            this.unregisterOnFirstFailedRemoval = unregisterOnFirstFailedRemoval;
            this.canonicalPathOverride = canonicalPathOverride;
            this.canonicalLookupFails = canonicalLookupFails;
        }

        public int RemoveCalls { get; private set; }
        public string? CreatedPath => worktreePath;

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            if (request.Arguments.Contains("add", StringComparer.Ordinal))
            {
                worktreePath = request.Arguments[^2];
                canonicalPath = canonicalPathOverride ?? worktreePath;
                registered = true;
                Directory.CreateDirectory(worktreePath);
                return Task.FromResult(new ProcessResult(0, "", ""));
            }

            if (request.Arguments.Contains("rev-parse", StringComparer.Ordinal))
            {
                return Task.FromResult(canonicalLookupFails
                    ? new ProcessResult(1, "", "synthetic canonical path failure")
                    : new ProcessResult(0, $"{canonicalPath}\n", ""));
            }

            if (request.Arguments.Contains("list", StringComparer.Ordinal))
            {
                var output = registered ? $"worktree {canonicalPath}\0HEAD synthetic\0\0" : "";
                return Task.FromResult(new ProcessResult(0, output, ""));
            }

            RemoveCalls++;
            var path = request.Arguments[^1];
            var exitCode = removalExitCodes.Dequeue();
            if (exitCode == 0)
            {
                registered = false;
                DeleteDirectory(path);
            }
            else if (unregisterOnFirstFailedRemoval && RemoveCalls == 1)
            {
                registered = false;
            }

            return Task.FromResult(new ProcessResult(exitCode, "", ""));
        }
    }
}
