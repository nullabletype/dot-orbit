using DotOrbit.Mcp.Ipc;

namespace DotOrbit.Mcp.Ipc.Tests;

public sealed class ChunkedPayloadProtocolTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(LocalMcpProtocol.ChunkBytes)]
    [InlineData(LocalMcpProtocol.ChunkBytes + 1)]
    [InlineData(256 * 1024)]
    public async Task PayloadRoundTripsAcrossBoundedFrames(int length)
    {
        var payload = Enumerable.Range(0, length).Select(index => (byte)(index % 251)).ToArray();
        await using var stream = new MemoryStream();

        await ChunkedPayloadProtocol.WriteAsync(
            stream,
            "request",
            "17",
            "submit.proposal",
            "key",
            null,
            null,
            null,
            false,
            payload,
            TestContext.Current.CancellationToken);
        stream.Position = 0;
        var begin = await JsonLineCodec.ReadAsync(
            stream,
            LocalMcpJsonContext.Default.SessionMessage,
            TestContext.Current.CancellationToken);
        using var actual = await ChunkedPayloadProtocol.ReadFollowingAsync(
            stream,
            Assert.IsType<SessionMessage>(begin),
            "request",
            LocalMcpProtocol.MaximumProposalBytes,
            TestContext.Current.CancellationToken);

        Assert.Equal(payload, actual.Bytes.ToArray());
        Assert.Equal("submit.proposal", actual.Header.Operation);
        Assert.Equal("key", actual.Header.IdempotencyKey);
        Assert.All(stream.ToArray().Chunk(LocalMcpProtocol.MaximumFrameBytes + 1), _ => { });
    }

    [Fact]
    public async Task AggregateLimitIsRejectedBeforeAllocatingPayload()
    {
        await using var stream = new MemoryStream();
        var begin = new SessionMessage(
            LocalMcpProtocol.Version,
            "request-begin",
            "1",
            TotalLength: LocalMcpProtocol.MaximumProposalBytes + 1,
            ChunkCount: 129);

        var exception = await Assert.ThrowsAsync<ProtocolException>(async () =>
            await ChunkedPayloadProtocol.ReadFollowingAsync(
                stream,
                begin,
                "request",
                LocalMcpProtocol.MaximumProposalBytes,
                TestContext.Current.CancellationToken));

        Assert.Equal("payload-begin-invalid", exception.Code);
    }

    [Fact]
    public async Task ReorderedChunkIsRejectedWithFixedError()
    {
        await using var stream = new MemoryStream();
        await JsonLineCodec.WriteAsync(
            stream,
            new SessionMessage(
                LocalMcpProtocol.Version,
                "request-chunk",
                "1",
                ChunkIndex: 1,
                ChunkCount: 1,
                Payload: [1]),
            LocalMcpJsonContext.Default.SessionMessage,
            TestContext.Current.CancellationToken);
        stream.Position = 0;
        var begin = new SessionMessage(
            LocalMcpProtocol.Version,
            "request-begin",
            "1",
            TotalLength: 1,
            ChunkCount: 1);

        var exception = await Assert.ThrowsAsync<ProtocolException>(async () =>
            await ChunkedPayloadProtocol.ReadFollowingAsync(
                stream,
                begin,
                "request",
                LocalMcpProtocol.MaximumProposalBytes,
                TestContext.Current.CancellationToken));

        Assert.Equal("payload-chunk-invalid", exception.Code);
    }

    [Fact]
    public async Task RequestBeginRejectsAResponseOnlyField()
    {
        await using var stream = new MemoryStream();
        var begin = new SessionMessage(
            LocalMcpProtocol.Version,
            "request-begin",
            "1",
            Operation: "read.instructions",
            Status: "ok");

        var exception = await Assert.ThrowsAsync<ProtocolException>(async () =>
            await ChunkedPayloadProtocol.ReadFollowingAsync(
                stream,
                begin,
                "request",
                LocalMcpProtocol.MaximumProposalBytes,
                TestContext.Current.CancellationToken));

        Assert.Equal("payload-begin-invalid", exception.Code);
    }

    [Fact]
    public async Task RequestChunkRejectsABeginOnlyField()
    {
        await using var stream = new MemoryStream();
        await JsonLineCodec.WriteAsync(
            stream,
            new SessionMessage(
                LocalMcpProtocol.Version,
                "request-chunk",
                "1",
                Operation: "read.instructions",
                ChunkIndex: 0,
                ChunkCount: 1,
                Payload: [1]),
            LocalMcpJsonContext.Default.SessionMessage,
            TestContext.Current.CancellationToken);
        await JsonLineCodec.WriteAsync(
            stream,
            new SessionMessage(LocalMcpProtocol.Version, "request-end", "1"),
            LocalMcpJsonContext.Default.SessionMessage,
            TestContext.Current.CancellationToken);
        stream.Position = 0;
        var begin = new SessionMessage(
            LocalMcpProtocol.Version,
            "request-begin",
            "1",
            Operation: "read.instructions",
            TotalLength: 1,
            ChunkCount: 1);

        var exception = await Assert.ThrowsAsync<ProtocolException>(async () =>
            await ChunkedPayloadProtocol.ReadFollowingAsync(
                stream,
                begin,
                "request",
                LocalMcpProtocol.MaximumProposalBytes,
                TestContext.Current.CancellationToken));

        Assert.Equal("payload-chunk-invalid", exception.Code);
    }

    [Fact]
    public async Task RequestEndRejectsAResponseOnlyField()
    {
        await using var stream = new MemoryStream();
        await JsonLineCodec.WriteAsync(
            stream,
            new SessionMessage(
                LocalMcpProtocol.Version,
                "request-end",
                "1",
                Duplicate: true),
            LocalMcpJsonContext.Default.SessionMessage,
            TestContext.Current.CancellationToken);
        stream.Position = 0;
        var begin = new SessionMessage(
            LocalMcpProtocol.Version,
            "request-begin",
            "1",
            Operation: "read.instructions");

        var exception = await Assert.ThrowsAsync<ProtocolException>(async () =>
            await ChunkedPayloadProtocol.ReadFollowingAsync(
                stream,
                begin,
                "request",
                LocalMcpProtocol.MaximumProposalBytes,
                TestContext.Current.CancellationToken));

        Assert.Equal("payload-end-invalid", exception.Code);
    }
}
