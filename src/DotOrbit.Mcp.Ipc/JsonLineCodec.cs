using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace DotOrbit.Mcp.Ipc;

internal sealed class ProtocolException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

internal static class JsonLineCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async ValueTask<T?> ReadAsync<T>(
        Stream stream,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
        where T : class
    {
        var buffer = ArrayPool<byte>.Shared.Rent(LocalMcpProtocol.MaximumFrameBytes);
        var length = 0;
        var oneByte = new byte[1];
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(oneByte, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    if (length == 0)
                    {
                        return null;
                    }

                    throw new ProtocolException("frame-incomplete");
                }

                if (oneByte[0] == (byte)'\n')
                {
                    break;
                }

                if (length >= LocalMcpProtocol.MaximumFrameBytes)
                {
                    throw new ProtocolException("frame-too-large");
                }

                buffer[length++] = oneByte[0];
            }

            _ = StrictUtf8.GetCharCount(buffer.AsSpan(0, length));
            return JsonSerializer.Deserialize(buffer.AsSpan(0, length), typeInfo)
                ?? throw new ProtocolException("frame-invalid");
        }
        catch (DecoderFallbackException)
        {
            throw new ProtocolException("frame-utf8-invalid");
        }
        catch (JsonException)
        {
            throw new ProtocolException("frame-json-invalid");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(oneByte);
            CryptographicOperations.ZeroMemory(buffer.AsSpan(0, length));
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    public static async ValueTask WriteAsync<T>(
        Stream stream,
        T value,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        try
        {
            if (payload.Length > LocalMcpProtocol.MaximumFrameBytes)
            {
                throw new ProtocolException("frame-too-large");
            }

            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }
}
