using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using DotOrbit.Mcp.Ipc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DotOrbit.Mcp.Adapter.Tests;

public sealed class McpServerTests
{
    [Fact]
    public async Task StdioInputRejectsAnOversizedJsonLineBeforeTheSdkReceivesIt()
    {
        await using var source = new MemoryStream("12345\n"u8.ToArray());
        await using var bounded = new BoundedLineInputStream(source, maximumLineBytes: 4);
        var buffer = new byte[16];

        var exception = await Assert.ThrowsAsync<IOException>(async () =>
            _ = await bounded.ReadAsync(buffer, TestContext.Current.CancellationToken));

        Assert.Equal("mcp-stdio-frame-too-large", exception.Message);
    }

    [Fact]
    public async Task StdioBoundAllowsWorstCaseEscapingForAMaximumSizeProposal()
    {
        const string prefix = "{\"format\":\"dot-orbit-new-work-proposal\",\"schemaVersion\":1,\"operations\":[{\"operation\":\"createTask\",\"operationId\":\"1\",\"localId\":\"task\",\"title\":\"Task\",\"description\":\"";
        const string suffix = "\",\"project\":null,\"category\":{\"mode\":\"explicit\",\"category\":{\"existingId\":\"home\"}},\"dueDate\":null,\"participants\":[]}]}";
        var proposal = string.Concat(
            prefix,
            new string('\u007f', LocalMcpProtocol.MaximumProposalBytes - prefix.Length - suffix.Length),
            suffix);
        var serialized = JsonSerializer.SerializeToUtf8Bytes(new { proposal });
        var line = new byte[serialized.Length + 1];
        serialized.CopyTo(line, 0);
        line[^1] = (byte)'\n';
        await using var source = new MemoryStream(line);
        await using var bounded = new BoundedLineInputStream(
            source,
            LocalMcpServer.MaximumStdioRequestBytes);
        var buffer = new byte[64 * 1024];
        var read = 0;

        while (await bounded.ReadAsync(buffer, TestContext.Current.CancellationToken) > 0)
        {
            read += buffer.Length;
        }

        Assert.True(serialized.Length > 10 * 1024 * 1024);
        Assert.True(serialized.Length <= LocalMcpServer.MaximumStdioRequestBytes);
        Assert.True(read >= serialized.Length);
        Assert.Equal(LocalMcpProtocol.MaximumProposalBytes, Encoding.UTF8.GetByteCount(proposal));
    }

    [Fact]
    public async Task CredentialHelperOutputIsDrainedButNeverCapturedPastItsBound()
    {
        await using var stream = new MemoryStream(Enumerable.Repeat((byte)'a', 257).ToArray());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AdapterProfileCredentialStore.ReadBoundedOutputAsync(
                stream,
                maximumBytes: 256,
                TestContext.Current.CancellationToken));

        Assert.Equal("adapter-credential-output-too-large", exception.Message);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public void PackageSmokeRequiresTheExactInformationalVersion()
    {
        var variable = "DOTORBIT_PACKAGE_SMOKE_VERSION";
        var original = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "wrong");
            Assert.Equal(1, LocalMcpServer.RunPackageSmoke());

            var actual = typeof(LocalMcpServer).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .Cast<System.Reflection.AssemblyInformationalVersionAttribute>()
                .Single()
                .InformationalVersion;
            Environment.SetEnvironmentVariable(variable, actual);
            Assert.Equal(0, LocalMcpServer.RunPackageSmoke());
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, original);
        }
    }

    [Fact]
    public async Task OfficialSdkInitializesListsAndCallsOnlyProductionTools()
    {
        await using var streams = new DuplexPipeStreams();
        await using var broker = new RecordingBroker();
        using var host = LocalMcpServer.BuildHost(broker, streams.ServerInput, streams.ServerOutput);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = host.RunAsync(timeout.Token);
        var transport = new StreamClientTransport(
            streams.ClientOutput,
            streams.ClientInput,
            NullLoggerFactory.Instance);
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);

        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        var snapshot = await client.CallToolAsync(
            "orbit_get_planning_snapshot",
            new Dictionary<string, object?>(),
            cancellationToken: timeout.Token);
        var submitted = await client.CallToolAsync(
            "orbit_submit_proposal",
            new Dictionary<string, object?>
            {
                ["idempotencyKey"] = "retry-1",
                ["proposal"] = "{}",
            },
            cancellationToken: timeout.Token);

        Assert.Equal(
            ["orbit_get_instructions", "orbit_get_planning_snapshot", "orbit_submit_proposal"],
            tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));
        Assert.NotEqual(true, snapshot.IsError);
        Assert.NotEqual(true, submitted.IsError);
        Assert.Equal("retry-1", broker.IdempotencyKey);
        Assert.Equal("{}", System.Text.Encoding.UTF8.GetString(broker.Proposal.Span));

        await streams.ClientOutput.DisposeAsync();
        timeout.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CompletedDuplicateDoesNotClaimThatReviewIsStillRequired()
    {
        await using var broker = new RecordingBroker("already-processed", duplicate: true);
        var tools = new OrbitTools(broker);

        var result = await tools.SubmitProposalAsync(
            "retry-1",
            "{}",
            TestContext.Current.CancellationToken);

        Assert.Equal("already-processed", result.Status);
        Assert.True(result.Duplicate);
        Assert.False(result.ReviewRequired);
    }

    private sealed class RecordingBroker(
        string submissionStatus = "pending-local-review",
        bool duplicate = false) : ILocalMcpBrokerClient
    {
        public string? IdempotencyKey { get; private set; }
        public ReadOnlyMemory<byte> Proposal { get; private set; }

        public Task<LocalMcpBrokerResponse> ReadReusableInstructionsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new LocalMcpBrokerResponse("ok", "instructions"u8.ToArray()));

        public Task<LocalMcpBrokerResponse> ReadPlanningSnapshotAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new LocalMcpBrokerResponse("ok", "{\"workspaceRevision\":1}"u8.ToArray()));

        public Task<LocalMcpBrokerResponse> SubmitProposalAsync(
            string idempotencyKey,
            ReadOnlyMemory<byte> strictUtf8,
            CancellationToken cancellationToken = default)
        {
            IdempotencyKey = idempotencyKey;
            Proposal = strictUtf8.ToArray();
            return Task.FromResult(new LocalMcpBrokerResponse(
                submissionStatus,
                ReadOnlyMemory<byte>.Empty,
                "receipt-1",
                duplicate));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DuplexPipeStreams : IAsyncDisposable
    {
        private readonly AnonymousPipeServerStream _serverInput = new(
            PipeDirection.In,
            HandleInheritability.None);
        private readonly AnonymousPipeServerStream _serverOutput = new(
            PipeDirection.Out,
            HandleInheritability.None);

        public DuplexPipeStreams()
        {
            ClientOutput = new AnonymousPipeClientStream(
                PipeDirection.Out,
                _serverInput.GetClientHandleAsString());
            ClientInput = new AnonymousPipeClientStream(
                PipeDirection.In,
                _serverOutput.GetClientHandleAsString());
        }

        public Stream ServerInput => _serverInput;
        public Stream ServerOutput => _serverOutput;
        public AnonymousPipeClientStream ClientInput { get; }
        public AnonymousPipeClientStream ClientOutput { get; }

        public async ValueTask DisposeAsync()
        {
            await ClientInput.DisposeAsync();
            await ClientOutput.DisposeAsync();
            await _serverInput.DisposeAsync();
            await _serverOutput.DisposeAsync();
        }
    }
}
