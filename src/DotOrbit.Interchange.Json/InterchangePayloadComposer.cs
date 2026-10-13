using System.Text;
using DotOrbit.Core.Interchange;

namespace DotOrbit.Interchange.Json;

public static class InterchangePayloadComposer
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static InterchangeGenerationResult Compose(
        params ReadOnlyMemory<byte>[] components)
    {
        ArgumentNullException.ThrowIfNull(components);
        long total = 0;
        foreach (var component in components)
        {
            try
            {
                _ = StrictUtf8.GetCharCount(component.Span);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("Interchange components must be strict UTF-8.", exception);
            }
            total += component.Length;
            if (total > AgentInterchangeV1.MaximumOutputBytes)
                return InterchangeGenerationResult.TooLarge();
        }

        var payload = GC.AllocateUninitializedArray<byte>((int)total);
        var offset = 0;
        foreach (var component in components)
        {
            component.Span.CopyTo(payload.AsSpan(offset));
            offset += component.Length;
        }

        return InterchangeGenerationResult.Success(payload);
    }
}
