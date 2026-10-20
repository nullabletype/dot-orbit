using System.Diagnostics;
using System.Threading.Channels;

namespace DotOrbit.McpIpcPrototype.Tests;

internal sealed class PrototypeProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Channel<string> _standardError = Channel.CreateUnbounded<string>();
    private readonly Task _standardErrorPump;
    private readonly Task _standardOutputPump;

    private PrototypeProcess(Process process)
    {
        _process = process;
        _standardErrorPump = PumpAsync(process.StandardError, _standardError.Writer);
        _standardOutputPump = PumpAsync(process.StandardOutput, null);
    }

    public static PrototypeProcess StartHost(
        string runtimeRoot,
        bool aiEnabled = true,
        bool mcpEnabled = true)
    {
        var arguments = new List<string> { "host", "--runtime-root", runtimeRoot };
        if (aiEnabled)
        {
            arguments.Add("--ai-on");
        }

        if (mcpEnabled)
        {
            arguments.Add("--mcp-on");
        }

        return Start(PrototypeExecutablePath, arguments);
    }

    public static string PrototypeExecutablePath =>
        Path.Combine(AppContext.BaseDirectory, ExecutableName("dot-orbit-mcp-prototype"));

    public static string AdapterExecutablePath =>
        Path.Combine(AppContext.BaseDirectory, ExecutableName("dot-orbit-mcp-adapter-prototype"));

    public async Task WaitForErrorAsync(string expected, CancellationToken cancellationToken)
    {
        while (await _standardError.Reader.WaitToReadAsync(cancellationToken))
        {
            while (_standardError.Reader.TryRead(out var line))
            {
                if (line.Contains(expected, StringComparison.Ordinal))
                {
                    return;
                }
            }
        }

        throw new InvalidOperationException("process-ended-before-expected-status");
    }

    public Task SendControlAsync(string command) => _process.StandardInput.WriteLineAsync(command);

    public async Task CrashAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        await Task.WhenAll(_standardErrorPump, _standardOutputPump);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            try
            {
                await _process.StandardInput.WriteLineAsync("exit");
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync();
                }
            }
        }

        await Task.WhenAll(_standardErrorPump, _standardOutputPump);
        _process.Dispose();
    }

    private static PrototypeProcess Start(string executable, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        var process = Process.Start(start) ?? throw new InvalidOperationException("prototype-process-did-not-start");
        return new PrototypeProcess(process);
    }

    private static async Task PumpAsync(StreamReader reader, ChannelWriter<string>? writer)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (writer is not null)
                {
                    await writer.WriteAsync(line);
                }
            }
        }
        finally
        {
            writer?.TryComplete();
        }
    }

    private static string ExecutableName(string name) => OperatingSystem.IsWindows() ? $"{name}.exe" : name;
}
