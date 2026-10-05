using System.Globalization;
using System.Text;
using Avalonia.Input;
using Avalonia.Input.Platform;
using DotOrbit.Markdown;

namespace DotOrbit.Desktop.Clipboard;

public interface IMarkdownClipboard
{
    ValueTask WriteAsync(SanitisedMarkdownDocument document);
}

internal enum ClipboardPlatform
{
    Linux,
    MacOS,
    Windows,
}

internal static class MarkdownClipboardTransfer
{
    public static DataFormat<string> HtmlFormat(ClipboardPlatform platform) =>
        DataFormat.CreateStringPlatformFormat(platform switch
        {
            ClipboardPlatform.MacOS => "public.html",
            ClipboardPlatform.Windows => "HTML Format",
            _ => "text/html",
        });

    public static DataTransfer Create(SanitisedMarkdownDocument document, ClipboardPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(document);
        var item = new DataTransferItem();
        item.SetText(document.ToPlainText());
        item.Set(HtmlFormat(platform), HtmlPayload(document.ToHtml(), platform));
        var transfer = new DataTransfer();
        transfer.Add(item);
        return transfer;
    }

    internal static string HtmlPayload(string fragment, ClipboardPlatform platform)
    {
        if (platform != ClipboardPlatform.Windows) return fragment;

        const string headerTemplate = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string beforeFragment = "<html><body><!--StartFragment-->";
        const string afterFragment = "<!--EndFragment--></body></html>";
        var emptyHeader = string.Format(CultureInfo.InvariantCulture, headerTemplate, 0, 0, 0, 0);
        var startHtml = Encoding.UTF8.GetByteCount(emptyHeader);
        var startFragment = startHtml + Encoding.UTF8.GetByteCount(beforeFragment);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        var endHtml = endFragment + Encoding.UTF8.GetByteCount(afterFragment);
        var header = string.Format(CultureInfo.InvariantCulture, headerTemplate, startHtml, endHtml, startFragment, endFragment);
        return header + beforeFragment + fragment + afterFragment;
    }
}

public sealed class AvaloniaMarkdownClipboard(IClipboard clipboard) : IMarkdownClipboard
{
    public async ValueTask WriteAsync(SanitisedMarkdownDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var platform = OperatingSystem.IsWindows()
            ? ClipboardPlatform.Windows
            : OperatingSystem.IsMacOS()
                ? ClipboardPlatform.MacOS
                : ClipboardPlatform.Linux;
        await clipboard.SetDataAsync(MarkdownClipboardTransfer.Create(document, platform));
    }
}
