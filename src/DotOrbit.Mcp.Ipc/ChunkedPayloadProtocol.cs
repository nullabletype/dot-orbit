using System.Security.Cryptography;

namespace DotOrbit.Mcp.Ipc;

internal static class ChunkedPayloadProtocol
{
    public static async ValueTask WriteAsync(
        Stream stream,
        string kind,
        string id,
        string? operation,
        string? idempotencyKey,
        string? error,
        string? status,
        string? receipt,
        bool duplicate,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var chunkCount = payload.IsEmpty
            ? 0
            : checked((payload.Length + LocalMcpProtocol.ChunkBytes - 1) / LocalMcpProtocol.ChunkBytes);
        await JsonLineCodec.WriteAsync(
            stream,
            new SessionMessage(
                LocalMcpProtocol.Version,
                $"{kind}-begin",
                id,
                operation,
                idempotencyKey,
                payload.Length,
                ChunkCount: chunkCount,
                Error: error,
                Status: status,
                Receipt: receipt,
                Duplicate: duplicate),
            LocalMcpJsonContext.Default.SessionMessage,
            cancellationToken).ConfigureAwait(false);

        for (var index = 0; index < chunkCount; index++)
        {
            var offset = index * LocalMcpProtocol.ChunkBytes;
            var length = Math.Min(LocalMcpProtocol.ChunkBytes, payload.Length - offset);
            var chunk = payload.Slice(offset, length).ToArray();
            try
            {
                await JsonLineCodec.WriteAsync(
                    stream,
                    new SessionMessage(
                        LocalMcpProtocol.Version,
                        $"{kind}-chunk",
                        id,
                        ChunkIndex: index,
                        ChunkCount: chunkCount,
                        Payload: chunk),
                    LocalMcpJsonContext.Default.SessionMessage,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(chunk);
            }
        }

        await JsonLineCodec.WriteAsync(
            stream,
            new SessionMessage(LocalMcpProtocol.Version, $"{kind}-end", id),
            LocalMcpJsonContext.Default.SessionMessage,
            cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<ChunkedPayload> ReadFollowingAsync(
        Stream stream,
        SessionMessage begin,
        string kind,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (!SessionMessageValidation.IsPayloadBegin(begin, kind)
            || begin.TotalLength is < 0
            || begin.TotalLength > maximumBytes)
        {
            throw new ProtocolException("payload-begin-invalid");
        }

        var expectedChunks = begin.TotalLength == 0
            ? 0
            : checked((begin.TotalLength + LocalMcpProtocol.ChunkBytes - 1) / LocalMcpProtocol.ChunkBytes);
        if (begin.ChunkCount != expectedChunks)
        {
            throw new ProtocolException("payload-count-invalid");
        }

        var payload = new byte[begin.TotalLength];
        try
        {
            var written = 0;
            for (var index = 0; index < expectedChunks; index++)
            {
                var message = await JsonLineCodec.ReadAsync(
                    stream,
                    LocalMcpJsonContext.Default.SessionMessage,
                    cancellationToken).ConfigureAwait(false)
                    ?? throw new ProtocolException("payload-incomplete");
                var chunk = message.Payload;
                try
                {
                    var expectedLength = Math.Min(LocalMcpProtocol.ChunkBytes, begin.TotalLength - written);
                    if (!SessionMessageValidation.IsPayloadChunk(message, kind)
                        || message.Id != begin.Id
                        || message.ChunkIndex != index
                        || message.ChunkCount != expectedChunks
                        || chunk is null
                        || chunk.Length != expectedLength)
                    {
                        throw new ProtocolException("payload-chunk-invalid");
                    }

                    chunk.CopyTo(payload, written);
                    written += chunk.Length;
                }
                finally
                {
                    if (chunk is not null)
                    {
                        CryptographicOperations.ZeroMemory(chunk);
                    }
                }
            }

            var end = await JsonLineCodec.ReadAsync(
                stream,
                LocalMcpJsonContext.Default.SessionMessage,
                cancellationToken).ConfigureAwait(false)
                ?? throw new ProtocolException("payload-incomplete");
            if (!SessionMessageValidation.IsPayloadEnd(end, kind)
                || end.Id != begin.Id
                || written != begin.TotalLength)
            {
                throw new ProtocolException("payload-end-invalid");
            }

            return new ChunkedPayload(begin, payload);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(payload);
            throw;
        }
    }
}

internal sealed class ChunkedPayload(SessionMessage header, byte[] bytes) : IDisposable
{
    public SessionMessage Header { get; } = header;
    public ReadOnlyMemory<byte> Bytes => bytes;

    public void Dispose() => CryptographicOperations.ZeroMemory(bytes);
}
