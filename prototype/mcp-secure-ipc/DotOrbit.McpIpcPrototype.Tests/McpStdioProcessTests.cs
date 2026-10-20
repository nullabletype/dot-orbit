using System.Collections.Concurrent;
using ModelContextProtocol.Client;

namespace DotOrbit.McpIpcPrototype.Tests;

[Collection(ProcessTestGroup.Name)]
public sealed class McpStdioProcessTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OfficialSdkListsAndCallsOnlySyntheticToolsForBothLaunchShapes(bool useWinExeSurrogate)
    {
        using var directory = new TemporaryDirectory();
        await using var host = PrototypeProcess.StartHost(directory.RuntimeRoot);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await host.WaitForErrorAsync("broker=ready", timeout.Token);

        var diagnostics = new ConcurrentQueue<string>();
        var arguments = useWinExeSurrogate
            ? new[] { "--mcp-stdio", "--runtime-root", directory.RuntimeRoot, "--client-name", "sdk-test", "--client-version", "1.0" }
            : new[] { "--stdio", "--runtime-root", directory.RuntimeRoot, "--client-name", "sdk-test", "--client-version", "1.0" };
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = useWinExeSurrogate ? "winexe-surrogate" : "adapter",
            Command = useWinExeSurrogate
                ? PrototypeProcess.PrototypeExecutablePath
                : PrototypeProcess.AdapterExecutablePath,
            Arguments = arguments,
            InheritEnvironmentVariables = false,
            EnvironmentVariables = StdioClientTransportOptions.GetDefaultEnvironmentVariables(),
            StandardErrorLines = diagnostics.Enqueue,
            ShutdownTimeout = TimeSpan.FromSeconds(5),
        });
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);

        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        var call = client.CallToolAsync(
            "orbit_status",
            new Dictionary<string, object?>(),
            cancellationToken: timeout.Token);
        await host.WaitForErrorAsync("consent=pending", timeout.Token);
        await host.SendControlAsync("approve");
        var result = await call;
        var submitted = await client.CallToolAsync(
            "orbit_submit_synthetic_proposal",
            new Dictionary<string, object?> { ["idempotencyKey"] = "sdk-proposal" },
            cancellationToken: timeout.Token);

        Assert.Equal(
            ["orbit_status", "orbit_submit_synthetic_proposal"],
            tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));
        Assert.NotEqual(true, result.IsError);
        Assert.NotEmpty(result.Content);
        Assert.NotEqual(true, submitted.IsError);
        Assert.NotEmpty(submitted.Content);
        Assert.All(diagnostics, line => Assert.StartsWith("mcp-prototype:", line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task OfficialSdkCancellationReachesBrokerAndLeavesSessionUsable()
    {
        using var directory = new TemporaryDirectory();
        await using var host = PrototypeProcess.StartHost(directory.RuntimeRoot);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await host.WaitForErrorAsync("broker=ready", timeout.Token);
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "cancellation",
            Command = PrototypeProcess.PrototypeExecutablePath,
            Arguments =
            [
                "--mcp-stdio", "--runtime-root", directory.RuntimeRoot,
                "--client-name", "sdk-test", "--client-version", "1.0",
            ],
            InheritEnvironmentVariables = false,
            EnvironmentVariables = StdioClientTransportOptions.GetDefaultEnvironmentVariables(),
            ShutdownTimeout = TimeSpan.FromSeconds(5),
        });
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        using var cancelled = new CancellationTokenSource();
        var delayed = client.CallToolAsync(
            "orbit_status",
            new Dictionary<string, object?> { ["delayMilliseconds"] = 5000 },
            cancellationToken: cancelled.Token);
        await host.WaitForErrorAsync("consent=pending", timeout.Token);
        await host.SendControlAsync("approve");
        cancelled.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await delayed);
        var next = await client.CallToolAsync(
            "orbit_status",
            new Dictionary<string, object?>(),
            cancellationToken: timeout.Token);

        Assert.NotEqual(true, next.IsError);
        Assert.NotEmpty(next.Content);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            var name = $"ms-{Guid.NewGuid():N}"[..11];
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), name);
            Directory.CreateDirectory(Path);
            RuntimeRoot = System.IO.Path.Combine(Path, "r");
        }

        public string Path { get; }
        public string RuntimeRoot { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
