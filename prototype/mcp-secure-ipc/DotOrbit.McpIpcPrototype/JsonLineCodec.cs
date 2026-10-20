using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace DotOrbit.McpIpcPrototype;

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
        var buffer = new ArrayBufferWriter<byte>();
        var oneByte = new byte[1];

        while (true)
        {
            var count = await stream.ReadAsync(oneByte, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                if (buffer.WrittenCount == 0)
                {
                    return null;
                }

                throw new ProtocolException("frame-incomplete");
            }

            if (oneByte[0] == (byte)'\n')
            {
                break;
            }

            if (buffer.WrittenCount >= PrototypeProtocol.MaximumFrameBytes)
            {
                throw new ProtocolException("frame-too-large");
            }

            buffer.GetSpan(1)[0] = oneByte[0];
            buffer.Advance(1);
        }

        try
        {
            _ = StrictUtf8.GetCharCount(buffer.WrittenSpan);
            return JsonSerializer.Deserialize(buffer.WrittenSpan, typeInfo)
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
    }

    public static async ValueTask WriteAsync<T>(
        Stream stream,
        T value,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        if (payload.Length > PrototypeProtocol.MaximumFrameBytes)
        {
            throw new ProtocolException("frame-too-large");
        }

        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
