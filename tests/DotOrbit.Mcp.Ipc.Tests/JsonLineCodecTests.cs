using System.Text;

using DotOrbit.Mcp.Ipc;

namespace DotOrbit.Mcp.Ipc.Tests;

public sealed class JsonLineCodecTests
{
    [Fact]
    public async Task ValidCompactUtf8FrameRoundTrips()
    {
        var expected = new SessionMessage(
            LocalMcpProtocol.Version,
            "request",
            "42",
            "read");
        await using var stream = new MemoryStream();

        await JsonLineCodec.WriteAsync(
            stream,
            expected,
            LocalMcpJsonContext.Default.SessionMessage,
            TestContext.Current.CancellationToken);
        stream.Position = 0;
        var actual = await JsonLineCodec.ReadAsync(
            stream,
            LocalMcpJsonContext.Default.SessionMessage,
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task FrameLargerThanPublishedLimitIsRejectedBeforeParsing()
    {
        var bytes = new byte[LocalMcpProtocol.MaximumFrameBytes + 2];
        Array.Fill(bytes, (byte)'x');
        bytes[^1] = (byte)'\n';
        await using var stream = new MemoryStream(bytes);

        var exception = await Assert.ThrowsAsync<ProtocolException>(async () =>
            await JsonLineCodec.ReadAsync(
                stream,
                LocalMcpJsonContext.Default.SessionMessage,
                TestContext.Current.CancellationToken));

        Assert.Equal("frame-too-large", exception.Code);
    }

    [Fact]
    public async Task InvalidUtf8IsRejectedWithFixedError()
    {
        await using var stream = new MemoryStream([0xff, (byte)'\n']);

        var exception = await Assert.ThrowsAsync<ProtocolException>(async () =>
            await JsonLineCodec.ReadAsync(
                stream,
                LocalMcpJsonContext.Default.SessionMessage,
                TestContext.Current.CancellationToken));

        Assert.Equal("frame-utf8-invalid", exception.Code);
    }

    [Fact]
    public async Task MalformedJsonIsRejectedWithFixedErrorWithoutEchoingPayload()
    {
        const string payload = "private-task-text{";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload + "\n"));

        var exception = await Assert.ThrowsAsync<ProtocolException>(async () =>
            await JsonLineCodec.ReadAsync(
                stream,
                LocalMcpJsonContext.Default.SessionMessage,
                TestContext.Current.CancellationToken));

        Assert.Equal("frame-json-invalid", exception.Code);
        Assert.DoesNotContain(payload, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownPropertiesAreRejected()
    {
        const string payload = "{\"version\":1,\"type\":\"request\",\"unexpected\":true}\n";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));

        var exception = await Assert.ThrowsAsync<ProtocolException>(async () =>
            await JsonLineCodec.ReadAsync(
                stream,
                LocalMcpJsonContext.Default.SessionMessage,
                TestContext.Current.CancellationToken));

        Assert.Equal("frame-json-invalid", exception.Code);
    }

    [Fact]
    public async Task ExcessiveJsonDepthIsRejected()
    {
        var payload = new string('[', 17) + "null" + new string(']', 17) + "\n";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));

        var exception = await Assert.ThrowsAsync<ProtocolException>(async () =>
            await JsonLineCodec.ReadAsync(
                stream,
                LocalMcpJsonContext.Default.SessionMessage,
                TestContext.Current.CancellationToken));

        Assert.Equal("frame-json-invalid", exception.Code);
    }
}
