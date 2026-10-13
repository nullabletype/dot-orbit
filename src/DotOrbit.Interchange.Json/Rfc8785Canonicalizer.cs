using System.Buffers;
using System.Buffers.Text;
using System.Text;
using System.Text.Json;

namespace DotOrbit.Interchange.Json;

public static class Rfc8785Canonicalizer
{
    private const long MaximumExactInteger = 9_007_199_254_740_991;

    public static byte[] Canonicalize(JsonElement value)
    {
        var output = new ArrayBufferWriter<byte>();
        WriteValue(output, value);
        return output.WrittenSpan.ToArray();
    }

    public static byte[] Canonicalize(ReadOnlySpan<byte> utf8Json)
    {
        using var document = JsonDocument.Parse(utf8Json.ToArray());
        return Canonicalize(document.RootElement);
    }

    private static void WriteValue(ArrayBufferWriter<byte> output, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(output, value);
                break;
            case JsonValueKind.Array:
                WriteArray(output, value);
                break;
            case JsonValueKind.String:
                WriteString(output, value.GetString() ?? string.Empty);
                break;
            case JsonValueKind.Number:
                WriteInteger(output, value.GetRawText());
                break;
            case JsonValueKind.True:
                WriteAscii(output, "true");
                break;
            case JsonValueKind.False:
                WriteAscii(output, "false");
                break;
            case JsonValueKind.Null:
                WriteAscii(output, "null");
                break;
            default:
                throw new InvalidDataException("Unsupported JSON token in canonical input.");
        }
    }

    private static void WriteObject(ArrayBufferWriter<byte> output, JsonElement value)
    {
        var properties = value.EnumerateObject().ToArray();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in properties)
            if (!names.Add(property.Name))
                throw new InvalidDataException("Duplicate object member names are not canonical JSON.");

        Array.Sort(properties, static (left, right) =>
            StringComparer.Ordinal.Compare(left.Name, right.Name));
        WriteByte(output, (byte)'{');
        for (var index = 0; index < properties.Length; index++)
        {
            if (index != 0) WriteByte(output, (byte)',');
            WriteString(output, properties[index].Name);
            WriteByte(output, (byte)':');
            WriteValue(output, properties[index].Value);
        }
        WriteByte(output, (byte)'}');
    }

    private static void WriteArray(ArrayBufferWriter<byte> output, JsonElement value)
    {
        WriteByte(output, (byte)'[');
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (index++ != 0) WriteByte(output, (byte)',');
            WriteValue(output, item);
        }
        WriteByte(output, (byte)']');
    }

    private static void WriteInteger(ArrayBufferWriter<byte> output, string rawText)
    {
        if (rawText.Contains('.', StringComparison.Ordinal)
            || rawText.Contains('e', StringComparison.OrdinalIgnoreCase)
            || rawText.StartsWith('+')
            || (rawText.Length > 1 && rawText[0] == '0')
            || (rawText.Length > 2 && rawText[0] == '-' && rawText[1] == '0')
            || !long.TryParse(rawText, System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out var number)
            || number is < -MaximumExactInteger or > MaximumExactInteger)
        {
            throw new InvalidDataException("Only canonical I-JSON integers are supported.");
        }

        var span = output.GetSpan(20);
        if (!Utf8Formatter.TryFormat(number, span, out var written))
            throw new InvalidOperationException("Could not format a JSON integer.");
        output.Advance(written);
    }

    private static void WriteString(ArrayBufferWriter<byte> output, string value)
    {
        WriteByte(output, (byte)'"');
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed)
                != OperationStatus.Done)
            {
                throw new InvalidDataException("Invalid Unicode scalar value.");
            }
            remaining = remaining[consumed..];
            if (rune.Value is '"' or '\\')
            {
                WriteByte(output, (byte)'\\');
                WriteByte(output, (byte)rune.Value);
            }
            else if (rune.Value <= 0x1f)
            {
                switch (rune.Value)
                {
                    case '\b': WriteAscii(output, "\\b"); break;
                    case '\t': WriteAscii(output, "\\t"); break;
                    case '\n': WriteAscii(output, "\\n"); break;
                    case '\f': WriteAscii(output, "\\f"); break;
                    case '\r': WriteAscii(output, "\\r"); break;
                    default:
                        WriteAscii(output, "\\u00");
                        WriteHexByte(output, (byte)rune.Value);
                        break;
                }
            }
            else
            {
                var span = output.GetSpan(4);
                if (!rune.TryEncodeToUtf8(span, out var written))
                    throw new InvalidDataException("Invalid Unicode scalar value.");
                output.Advance(written);
            }
        }
        WriteByte(output, (byte)'"');
    }

    private static void WriteHexByte(ArrayBufferWriter<byte> output, byte value)
    {
        const string digits = "0123456789abcdef";
        WriteByte(output, (byte)digits[value >> 4]);
        WriteByte(output, (byte)digits[value & 0x0f]);
    }

    private static void WriteAscii(ArrayBufferWriter<byte> output, string text)
    {
        var destination = output.GetSpan(text.Length);
        for (var index = 0; index < text.Length; index++) destination[index] = (byte)text[index];
        output.Advance(text.Length);
    }

    private static void WriteByte(ArrayBufferWriter<byte> output, byte value)
    {
        output.GetSpan(1)[0] = value;
        output.Advance(1);
    }
}
