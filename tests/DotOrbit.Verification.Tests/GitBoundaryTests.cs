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
            Directory.Delete(repositoryPath, recursive: true);
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

            Directory.Delete(repositoryPath, recursive: true);
        }
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
}
