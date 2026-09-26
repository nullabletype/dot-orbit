using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DotOrbit.Verification;

internal sealed record VerificationOptions(bool Evidence, string? ExpectedSha)
{
    private static readonly Regex FullSha = new("^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant);

    public static VerificationOptions? Parse(IReadOnlyList<string> arguments)
    {
        var evidence = false;
        string? expectedSha = null;

        for (var index = 0; index < arguments.Count; index++)
        {
            switch (arguments[index])
            {
                case "--evidence":
                    evidence = true;
                    break;
                case "--expected-sha" when index + 1 < arguments.Count:
                    expectedSha = arguments[++index];
                    break;
                default:
                    Console.Error.WriteLine("verify: result=failed reason=invalid-arguments");
                    return null;
            }
        }

        if (expectedSha is not null && (!evidence || !FullSha.IsMatch(expectedSha)))
        {
            Console.Error.WriteLine("verify: result=failed reason=invalid-expected-sha");
            return null;
        }

        return new VerificationOptions(evidence, expectedSha?.ToLowerInvariant());
    }
}

internal sealed record RepositoryState(string Commit, bool IsClean);

internal interface IRepositoryInspector
{
    Task<RepositoryState?> ReadAsync(CancellationToken cancellationToken);
}

internal interface IVerificationWorkspaceProvider
{
    Task<string?> CreateAsync(string commit, CancellationToken cancellationToken);
    Task<bool> RemoveAsync(string path, CancellationToken cancellationToken);
}

internal sealed record ProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    TimeSpan? Timeout = null,
    bool EchoOutput = true,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null);

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut = false);

internal interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken);
}

internal interface IVerificationOutput
{
    void Write(string message);
    void WriteError(string message);
}

internal sealed class VerificationGate(
    string repositoryRoot,
    IRepositoryInspector repository,
    IVerificationWorkspaceProvider workspaces,
    IProcessRunner runner,
    IVerificationOutput output)
{
    private static readonly TimeSpan SmokeTimeout = TimeSpan.FromMinutes(2);

    public async Task<int> RunAsync(VerificationOptions options, CancellationToken cancellationToken)
    {
        RepositoryState? initialState = null;
        if (options.Evidence)
        {
            initialState = await repository.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (initialState is not null)
            {
                var state = initialState.IsClean ? "clean" : "dirty";
                output.Write($"verify: repository commit={initialState.Commit} state={state}");
            }

            output.Write($"verify: environment os={RuntimeInformation.OSDescription.Trim()} architecture={RuntimeInformation.OSArchitecture} runtime={Environment.Version}");
            if (!ValidateInitialState(initialState, options.ExpectedSha))
            {
                return 1;
            }
        }

        var verificationRoot = repositoryRoot;
        if (options.Evidence)
        {
            verificationRoot = await workspaces.CreateAsync(initialState!.Commit, cancellationToken).ConfigureAwait(false);
            if (verificationRoot is null)
            {
                output.WriteError("verify: result=failed evidence=refused reason=snapshot-unavailable");
                return 1;
            }
        }

        var phasesPassed = true;
        var cleanupSucceeded = true;
        try
        {
            foreach (var phase in CreatePhases(verificationRoot))
            {
                output.Write($"verify: phase={phase.Name} result=started");
                var result = await runner.RunAsync(phase.Request, cancellationToken).ConfigureAwait(false);
                if (!phase.Accepts(result))
                {
                    var reason = result.TimedOut ? "timeout" : "unexpected-result";
                    output.WriteError($"verify: phase={phase.Name} result=failed reason={reason} exit-code={result.ExitCode}");
                    output.WriteError("verify: result=failed");
                    phasesPassed = false;
                    break;
                }

                output.Write($"verify: phase={phase.Name} result=passed");
            }
        }
        catch (Exception)
        {
            output.WriteError("verify: result=failed reason=unexpected-error");
            phasesPassed = false;
        }
        finally
        {
            if (options.Evidence)
            {
                try
                {
                    cleanupSucceeded = await workspaces.RemoveAsync(
                        verificationRoot,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException)
                {
                    cleanupSucceeded = false;
                }
            }
        }

        if (!cleanupSucceeded)
        {
            output.WriteError("verify: result=failed evidence=refused reason=snapshot-cleanup-failed");
            return 1;
        }

        if (!phasesPassed)
        {
            return 1;
        }

        if (options.Evidence)
        {
            var finalState = await repository.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (finalState is null)
            {
                output.WriteError("verify: result=failed evidence=refused reason=repository-state-unavailable");
                return 1;
            }

            if (!finalState.IsClean)
            {
                output.WriteError("verify: result=failed evidence=refused reason=dirty-worktree");
                return 1;
            }

            if (!string.Equals(initialState!.Commit, finalState.Commit, StringComparison.OrdinalIgnoreCase))
            {
                output.WriteError("verify: result=failed evidence=refused reason=head-changed");
                return 1;
            }

            output.Write($"verify: result=verified commit={finalState.Commit}");
            return 0;
        }

        output.Write("verify: result=passed");
        return 0;
    }

    private bool ValidateInitialState(RepositoryState? state, string? expectedSha)
    {
        if (state is null)
        {
            output.WriteError("verify: result=failed evidence=refused reason=repository-state-unavailable");
            return false;
        }

        if (!state.IsClean)
        {
            output.WriteError("verify: result=failed evidence=refused reason=dirty-worktree");
            return false;
        }

        if (expectedSha is not null
            && !string.Equals(state.Commit, expectedSha, StringComparison.OrdinalIgnoreCase))
        {
            output.WriteError("verify: result=failed evidence=refused reason=expected-sha-mismatch");
            return false;
        }

        return true;
    }

    private static IEnumerable<VerificationPhase> CreatePhases(string workingDirectory)
    {
        yield return Positive("restore", workingDirectory, "dotnet", "restore", "DotOrbit.slnx", "--locked-mode");
        yield return Positive("format", workingDirectory, "dotnet", "format", "DotOrbit.slnx", "--no-restore", "--verify-no-changes");
        yield return Positive("build", workingDirectory, "dotnet", "build", "DotOrbit.slnx", "--configuration", "Release", "--no-restore");
        yield return Positive("tests", workingDirectory, "dotnet", "test", "--solution", "DotOrbit.slnx", "--configuration", "Release", "--no-build");
        yield return Smoke("native-smoke", workingDirectory, "--native-smoke", 0, "native-smoke: phase=complete result=passed shutdown=requested");
        yield return Smoke("native-smoke-startup-control", workingDirectory, "--native-smoke-failure=startup", 20, "native-smoke: phase=startup result=failed code=20");
        yield return Smoke("native-smoke-navigation-control", workingDirectory, "--native-smoke-failure=navigation", 21, "native-smoke: phase=navigation-assertion result=failed code=21");
    }

    private static VerificationPhase Positive(
        string name,
        string workingDirectory,
        string fileName,
        params string[] arguments) =>
        new(
            name,
            new ProcessRequest(fileName, arguments, WorkingDirectory: workingDirectory),
            result => !result.TimedOut && result.ExitCode == 0);

    private static VerificationPhase Smoke(
        string name,
        string workingDirectory,
        string argument,
        int expectedExitCode,
        string expectedDiagnostic)
    {
        string[] dotnetArguments =
        [
            "run",
            "--project",
            "src/DotOrbit.Desktop/DotOrbit.Desktop.csproj",
            "--configuration",
            "Release",
            "--no-build",
            "--",
            argument,
        ];
        ProcessRequest request;
        if (OperatingSystem.IsLinux())
        {
            request = new ProcessRequest(
                "xvfb-run",
                ["--auto-servernum", "dotnet", .. dotnetArguments],
                SmokeTimeout,
                WorkingDirectory: workingDirectory);
        }
        else
        {
            request = new ProcessRequest("dotnet", dotnetArguments, SmokeTimeout, WorkingDirectory: workingDirectory);
        }

        return new VerificationPhase(
            name,
            request,
            result => !result.TimedOut
                && result.ExitCode == expectedExitCode
                && string.Concat(result.StandardOutput, result.StandardError)
                    .Contains(expectedDiagnostic, StringComparison.Ordinal));
    }

    private sealed record VerificationPhase(
        string Name,
        ProcessRequest Request,
        Func<ProcessResult, bool> Accepts);
}

internal sealed class ProcessRunner(IVerificationOutput output) : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = request.FileName,
                WorkingDirectory = request.WorkingDirectory ?? RepositoryRoot.Find(AppContext.BaseDirectory),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (var argument in request.Arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        if (request.EnvironmentVariables is not null)
        {
            foreach (var variable in request.EnvironmentVariables)
            {
                process.StartInfo.Environment[variable.Key] = variable.Value;
            }
        }

        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return new ProcessResult(-1, "", "");
        }
        var standardOutput = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var standardError = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeout = request.Timeout is null
            ? null
            : new CancellationTokenSource(request.Timeout.Value);
        using var linked = timeout is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout?.IsCancellationRequested is true)
        {
            TryKillProcessTree(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            return new ProcessResult(-1, await standardOutput.ConfigureAwait(false), await standardError.ConfigureAwait(false), true);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await standardOutput.ConfigureAwait(false);
            await standardError.ConfigureAwait(false);
            throw;
        }

        var stdout = await standardOutput.ConfigureAwait(false);
        var stderr = await standardError.ConfigureAwait(false);
        if (request.EchoOutput && !string.IsNullOrEmpty(stdout))
        {
            output.Write(stdout.TrimEnd());
        }

        if (request.EchoOutput && !string.IsNullOrEmpty(stderr))
        {
            output.WriteError(stderr.TrimEnd());
        }

        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // The process exited between cancellation and the termination request.
        }
    }
}

internal sealed class GitVerificationWorkspaceProvider(
    string repositoryRoot,
    IProcessRunner runner,
    TimeSpan? cleanupRetryDelay = null,
    Func<string, bool>? deleteTree = null,
    Func<string, bool>? deleteFile = null,
    Action<string>? reportDiagnostic = null)
    : IVerificationWorkspaceProvider
{
    private static readonly TimeSpan DefaultCleanupRetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly IReadOnlyDictionary<string, string> GitIdentityEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_NO_REPLACE_OBJECTS"] = "1",
        };
    private const int CleanupAttempts = 10;
    private readonly HashSet<string> ownedPaths = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly TimeSpan cleanupRetryDelay = cleanupRetryDelay ?? DefaultCleanupRetryDelay;
    private readonly Func<string, bool> deleteTree = deleteTree ?? TryDeleteTree;
    private readonly Func<string, bool> deleteFile = deleteFile ?? TryDeleteFile;

    public async Task<string?> CreateAsync(string commit, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dov-{Guid.NewGuid():N}");
        var archivePath = $"{path}.zip";
        var expectedTree = await ReadExpectedTreeAsync(commit, cancellationToken).ConfigureAwait(false);
        if (expectedTree is null)
        {
            return null;
        }

        ProcessResult result;
        try
        {
            result = await runner.RunAsync(
                new ProcessRequest(
                    "git",
                    ["-C", repositoryRoot, "archive", "--format=zip", "--output", archivePath, commit],
                    EchoOutput: false,
                    WorkingDirectory: repositoryRoot,
                    EnvironmentVariables: GitIdentityEnvironment),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (!await DeleteWithRetriesAsync(archivePath, deleteFile, CancellationToken.None).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The interrupted verification snapshot archive could not be cleaned up.");
            }

            throw;
        }

        if (result.ExitCode != 0)
        {
            if (!await DeleteWithRetriesAsync(archivePath, deleteFile, CancellationToken.None).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The failed verification snapshot archive could not be cleaned up.");
            }

            return null;
        }

        try
        {
            Directory.CreateDirectory(path);
            ZipFile.ExtractToDirectory(archivePath, path);
            var mismatchReason = FindSnapshotMismatch(path, expectedTree, cancellationToken);
            if (mismatchReason is not null)
            {
                reportDiagnostic?.Invoke($"verify: snapshot result=failed reason={mismatchReason}");
                throw new InvalidDataException("The verification snapshot does not match the requested commit tree.");
            }
        }
        catch (Exception exception)
        {
            var directoryRemoved = await DeleteWithRetriesAsync(
                path,
                deleteTree,
                CancellationToken.None).ConfigureAwait(false);
            var archiveRemoved = await DeleteWithRetriesAsync(
                archivePath,
                deleteFile,
                CancellationToken.None).ConfigureAwait(false);
            if (!directoryRemoved || !archiveRemoved)
            {
                throw new InvalidOperationException(
                    "The verification snapshot could not be cleaned up after setup failed.",
                    exception);
            }

            if (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return null;
            }

            throw;
        }

        if (!await DeleteWithRetriesAsync(archivePath, deleteFile, CancellationToken.None).ConfigureAwait(false))
        {
            if (!await DeleteWithRetriesAsync(path, deleteTree, CancellationToken.None).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The verification snapshot archive and directory could not be cleaned up.");
            }

            throw new InvalidOperationException("The verification snapshot archive could not be cleaned up.");
        }

        ownedPaths.Add(Path.GetFullPath(path));
        return path;
    }

    private async Task<IReadOnlyDictionary<string, string>?> ReadExpectedTreeAsync(
        string commit,
        CancellationToken cancellationToken)
    {
        var tree = await runner.RunAsync(
            new ProcessRequest(
                "git",
                ["-C", repositoryRoot, "ls-tree", "-r", "-z", commit],
                EchoOutput: false,
                WorkingDirectory: repositoryRoot,
                EnvironmentVariables: GitIdentityEnvironment),
            cancellationToken).ConfigureAwait(false);
        if (tree.ExitCode != 0)
        {
            return null;
        }

        var expectedTree = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in tree.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tabIndex = entry.IndexOf('\t', StringComparison.Ordinal);
            if (tabIndex < 0)
            {
                return null;
            }

            var metadata = entry[..tabIndex].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (metadata.Length != 3 || metadata[0] != "100644")
            {
                return null;
            }

            var entryPath = entry[(tabIndex + 1)..];
            if (!expectedTree.TryAdd(entryPath, metadata[2]))
            {
                return null;
            }
        }

        return expectedTree;
    }

    private static string? FindSnapshotMismatch(
        string path,
        IReadOnlyDictionary<string, string> expectedTree,
        CancellationToken cancellationToken)
    {
        var actualPaths = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Select(file => (File: file, RelativePath: Path.GetRelativePath(path, file).Replace('\\', '/')))
            .ToArray();
        if (actualPaths.Length != expectedTree.Count)
        {
            return "path-count-mismatch";
        }

        foreach (var (file, relativePath) in actualPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!expectedTree.TryGetValue(relativePath, out var expectedObjectId))
            {
                return "unexpected-path";
            }

            if (!string.Equals(ComputeGitBlobObjectId(file), expectedObjectId, StringComparison.Ordinal))
            {
                return "blob-mismatch";
            }
        }

        return null;
    }

    [SuppressMessage(
        "Security",
        "CA5350:Do not use weak cryptographic algorithms",
        Justification = "SHA-1 is required here only to reproduce this SHA-1 Git repository's blob object identifiers, not for security.")]
    internal static string ComputeGitBlobObjectId(string path)
    {
        using var stream = File.OpenRead(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(Encoding.ASCII.GetBytes($"blob {stream.Length}\0"));
        var buffer = new byte[81920];
        int bytesRead;
        while ((bytesRead = stream.Read(buffer)) > 0)
        {
            hash.AppendData(buffer, 0, bytesRead);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public async Task<bool> RemoveAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (!ownedPaths.Contains(fullPath))
        {
            return false;
        }

        var removed = await DeleteWithRetriesAsync(fullPath, deleteTree, cancellationToken).ConfigureAwait(false);
        if (removed)
        {
            ownedPaths.Remove(fullPath);
        }

        return removed;
    }

    private async Task<bool> DeleteWithRetriesAsync(
        string path,
        Func<string, bool> delete,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= CleanupAttempts; attempt++)
        {
            if (delete(path))
            {
                return true;
            }

            if (attempt < CleanupAttempts)
            {
                await Task.Delay(cleanupRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }

    private static bool TryDeleteTree(string path)
    {
        if (!Directory.Exists(path))
        {
            return true;
        }

        try
        {
            DeleteEntry(path);
            return !Directory.Exists(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryDeleteFile(string path)
    {
        if (!File.Exists(path))
        {
            return true;
        }

        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void DeleteEntry(string path)
    {
        var attributes = File.GetAttributes(path);
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            if (isDirectory)
            {
                Directory.Delete(path, recursive: false);
            }
            else
            {
                File.Delete(path);
            }

            return;
        }

        File.SetAttributes(path, FileAttributes.Normal);
        if (!isDirectory)
        {
            File.Delete(path);
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            DeleteEntry(entry);
        }

        Directory.Delete(path, recursive: false);
    }

}

internal sealed class GitRepositoryInspector(string repositoryRoot, IProcessRunner runner) : IRepositoryInspector
{
    public async Task<RepositoryState?> ReadAsync(CancellationToken cancellationToken)
    {
        var head = await runner.RunAsync(
            new ProcessRequest("git", ["-C", repositoryRoot, "rev-parse", "HEAD"], EchoOutput: false),
            cancellationToken).ConfigureAwait(false);
        var status = await runner.RunAsync(
            new ProcessRequest(
                "git",
                ["-C", repositoryRoot, "status", "--porcelain=v1", "--untracked-files=all"],
                EchoOutput: false),
            cancellationToken).ConfigureAwait(false);
        if (head.ExitCode != 0 || status.ExitCode != 0)
        {
            return null;
        }

        return new RepositoryState(head.StandardOutput.Trim().ToLowerInvariant(), string.IsNullOrWhiteSpace(status.StandardOutput));
    }
}

internal sealed class ConsoleVerificationOutput : IVerificationOutput
{
    public void Write(string message) => Console.WriteLine(message);
    public void WriteError(string message) => Console.Error.WriteLine(message);
}

internal static class RepositoryRoot
{
    public static string Find(string startPath)
    {
        var directory = new DirectoryInfo(startPath);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DotOrbit.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("The dot-orbit repository root could not be located.");
    }
}
