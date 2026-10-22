using System.Collections.Concurrent;
using System.Diagnostics;
using ModelContextProtocol.Client;

return await PackageCheck.RunAsync(args).ConfigureAwait(false);

internal static class PackageCheck
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(5);
    private static readonly string[] ExpectedTools =
        ["orbit_status", "orbit_submit_synthetic_proposal"];

    public static async Task<int> RunAsync(string[] args)
    {
        var options = Options.Parse(args);
        if (options is null)
        {
            Console.Error.WriteLine("package-check: result=failed reason=invalid-arguments");
            return 2;
        }

        try
        {
            var outputRoot = Path.Combine(
                options.RepositoryRoot,
                "artifacts",
                "mcp-prototype-package-check",
                options.RuntimeIdentifier);
            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }

            var winExeSurrogateRoot = Path.Combine(outputRoot, "winexe-surrogate");
            var adapterRoot = Path.Combine(outputRoot, "adapter");
            await PublishAsync(
                options,
                "prototype/mcp-secure-ipc/DotOrbit.McpIpcPrototype/DotOrbit.McpIpcPrototype.csproj",
                winExeSurrogateRoot).ConfigureAwait(false);
            await PublishAsync(
                options,
                "prototype/mcp-secure-ipc/DotOrbit.McpIpcPrototype.Adapter/DotOrbit.McpIpcPrototype.Adapter.csproj",
                adapterRoot).ConfigureAwait(false);

            var suffix = options.RuntimeIdentifier.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : string.Empty;
            var winExeSurrogate = ValidateSingleFile(winExeSurrogateRoot, $"dot-orbit-mcp-prototype{suffix}");
            var adapter = ValidateSingleFile(adapterRoot, $"dot-orbit-mcp-adapter-prototype{suffix}");
            await SmokeEofAsync(
                winExeSurrogate,
                ["--mcp-stdio", "--runtime-root", Path.Combine(outputRoot, "runtime")]).ConfigureAwait(false);
            await SmokeEofAsync(
                adapter,
                ["--stdio", "--runtime-root", Path.Combine(outputRoot, "runtime")]).ConfigureAwait(false);
            await SmokeMcpAsync(
                winExeSurrogate,
                ["--mcp-stdio", "--runtime-root", Path.Combine(outputRoot, "runtime")]).ConfigureAwait(false);
            await SmokeMcpAsync(
                adapter,
                ["--stdio", "--runtime-root", Path.Combine(outputRoot, "runtime")]).ConfigureAwait(false);

            Console.WriteLine(
                $"package-check: runtime={options.RuntimeIdentifier} winexe-surrogate-files=1 adapter-files=1 "
                + $"winexe-surrogate-bytes={new FileInfo(winExeSurrogate).Length} "
                + $"adapter-bytes={new FileInfo(adapter).Length} result=passed");
            return 0;
        }
        catch (PackageCheckException exception)
        {
            Console.Error.WriteLine($"package-check: result=failed reason={exception.Message}");
            return 1;
        }
    }

    private static async Task PublishAsync(Options options, string project, string outputRoot)
    {
        var result = await RunProcessAsync(
            "dotnet",
            [
                "publish",
                Path.Combine(options.RepositoryRoot, project),
                "--configuration", "Release",
                "--runtime", options.RuntimeIdentifier,
                "--self-contained", "true",
                "--output", outputRoot,
                "--no-restore",
                "-p:DotOrbitMcpPrototypePackage=true",
                "-p:DebugSymbols=false",
                "-p:DebugType=None",
            ],
            options.RepositoryRoot,
            closeStandardInput: false).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new PackageCheckException("publish-failed");
        }
    }

    private static string ValidateSingleFile(string outputRoot, string executableName)
    {
        var files = Directory.EnumerateFiles(outputRoot, "*", SearchOption.TopDirectoryOnly).ToArray();
        if (files.Length != 1 || !string.Equals(Path.GetFileName(files[0]), executableName, StringComparison.Ordinal))
        {
            throw new PackageCheckException("publish-output-not-single-file");
        }

        return files[0];
    }

    private static async Task SmokeEofAsync(string executable, IReadOnlyList<string> arguments)
    {
        var result = await RunProcessAsync(
            executable,
            arguments,
            Path.GetDirectoryName(executable)!,
            closeStandardInput: true).ConfigureAwait(false);
        if (result.ExitCode != 0
            || result.StandardOutput.Length != 0
            || result.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Any(line => !line.StartsWith("mcp-prototype:", StringComparison.Ordinal)))
        {
            throw new PackageCheckException("redirected-stdio-smoke-failed");
        }
    }

    private static async Task SmokeMcpAsync(string executable, IReadOnlyList<string> arguments)
    {
        var diagnostics = new ConcurrentQueue<string>();
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = Path.GetFileNameWithoutExtension(executable),
            Command = executable,
            Arguments = arguments.ToArray(),
            InheritEnvironmentVariables = false,
            EnvironmentVariables = StdioClientTransportOptions.GetDefaultEnvironmentVariables(),
            StandardErrorLines = diagnostics.Enqueue,
            ShutdownTimeout = TimeSpan.FromSeconds(5),
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var client = await McpClient.CreateAsync(
            transport,
            cancellationToken: timeout.Token).ConfigureAwait(false);
        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
        if (!tools.Select(tool => tool.Name).Order(StringComparer.Ordinal).SequenceEqual(
                ExpectedTools,
                StringComparer.Ordinal)
            || diagnostics.Any(line => !line.StartsWith("mcp-prototype:", StringComparison.Ordinal)))
        {
            throw new PackageCheckException("packaged-mcp-smoke-failed");
        }
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        bool closeStandardInput)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = closeStandardInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new PackageCheckException("process-start-failed");
        if (closeStandardInput)
        {
            process.StandardInput.Close();
        }

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(ProcessTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
            throw new PackageCheckException("process-timeout");
        }

        return new ProcessResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }

    private sealed record Options(string RepositoryRoot, string RuntimeIdentifier)
    {
        public static Options? Parse(string[] args)
        {
            string? root = null;
            string? runtime = null;
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--repository-root" when index + 1 < args.Length:
                        root = Path.GetFullPath(args[++index]);
                        break;
                    case "--runtime" when index + 1 < args.Length:
                        runtime = args[++index];
                        break;
                    default:
                        return null;
                }
            }

            return root is not null && runtime is "win-x64" or "linux-x64" or "osx-x64" or "osx-arm64"
                ? new Options(root, runtime)
                : null;
        }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class PackageCheckException(string message) : Exception(message);
}
