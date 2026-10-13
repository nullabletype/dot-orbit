using System.Text;
using Avalonia.Input;
using Avalonia.Input.Platform;

namespace DotOrbit.Desktop.Clipboard;

public interface IPlainTextClipboard
{
    ValueTask WriteTextAsync(ReadOnlyMemory<byte> strictUtf8Text);
}

internal static class PlainTextClipboardTransfer
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static DataTransfer Create(ReadOnlySpan<byte> strictUtf8Text)
    {
        var item = new DataTransferItem();
        item.SetText(StrictUtf8.GetString(strictUtf8Text));
        var transfer = new DataTransfer();
        transfer.Add(item);
        return transfer;
    }
}

public sealed class AvaloniaPlainTextClipboard(IClipboard clipboard) : IPlainTextClipboard
{
    public async ValueTask WriteTextAsync(ReadOnlyMemory<byte> strictUtf8Text)
    {
        using var transfer = PlainTextClipboardTransfer.Create(strictUtf8Text.Span);
        await clipboard.SetDataAsync(transfer);
    }
}
