using System.Text;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using DotOrbit.Interchange.Json;

namespace DotOrbit.Desktop;

internal static class ProposalTextRules
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static bool TryMeasure(string text, out int byteCount)
    {
        try
        {
            byteCount = StrictUtf8.GetByteCount(text);
            return byteCount <= ChangeProposalJsonParser.MaximumInputBytes;
        }
        catch (EncoderFallbackException)
        {
            byteCount = 0;
            return false;
        }
    }

    public static byte[] Encode(string text) => StrictUtf8.GetBytes(text);
}

internal sealed class ProposalTextAttemptedEventArgs(bool accepted, int byteCount) : EventArgs
{
    public bool Accepted { get; } = accepted;
    public int ByteCount { get; } = byteCount;
}

internal sealed class BoundedProposalTextBox : TextBox
{
    protected override Type StyleKeyOverride => typeof(TextBox);

    public event EventHandler<ProposalTextAttemptedEventArgs>? ProposalTextAttempted;

    public bool TryReplaceSelection(string insertion)
    {
        ArgumentNullException.ThrowIfNull(insertion);
        var text = Text ?? string.Empty;
        var start = Math.Min(SelectionStart, SelectionEnd);
        var end = Math.Max(SelectionStart, SelectionEnd);
        var candidate = string.Concat(text.AsSpan(0, start), insertion, text.AsSpan(end));
        if (!ProposalTextRules.TryMeasure(candidate, out var byteCount))
        {
            ProposalTextAttempted?.Invoke(this, new(false, 0));
            return false;
        }

        Text = candidate;
        CaretIndex = start + insertion.Length;
        SelectionStart = CaretIndex;
        SelectionEnd = CaretIndex;
        return true;
    }

    public bool TryReplaceAll(string replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (!ProposalTextRules.TryMeasure(replacement, out _))
        {
            ProposalTextAttempted?.Invoke(this, new(false, 0));
            return false;
        }

        Text = replacement;
        CaretIndex = replacement.Length;
        SelectionStart = CaretIndex;
        SelectionEnd = CaretIndex;
        return true;
    }

    protected override string? CoerceText(string? value)
    {
        var candidate = value ?? string.Empty;
        if (string.Equals(candidate, Text ?? string.Empty, StringComparison.Ordinal))
        {
            return base.CoerceText(value);
        }

        if (!ProposalTextRules.TryMeasure(candidate, out var byteCount))
        {
            ProposalTextAttempted?.Invoke(this, new(false, 0));
            return base.CoerceText(Text);
        }

        var accepted = base.CoerceText(value);
        ProposalTextAttempted?.Invoke(this, new(true, byteCount));
        return accepted;
    }
}

internal interface IProposalClipboardReader
{
    Task<string?> ReadTextAsync();
}

internal sealed class AvaloniaProposalClipboardReader(IClipboard clipboard) : IProposalClipboardReader
{
    public Task<string?> ReadTextAsync() => clipboard.TryGetTextAsync();
}

internal interface IProposalFilePicker
{
    Task<string?> PickAsync(Window owner);
}

internal sealed class AvaloniaProposalFilePicker : IProposalFilePicker
{
    public async Task<string?> PickAsync(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var files = await owner.StorageProvider.OpenFilePickerAsync(new()
        {
            Title = "Load a dot-orbit proposal",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("JSON proposal")
                {
                    Patterns = ["*.json"],
                    MimeTypes = ["application/json"],
                },
            ],
        });
        if (files.Count != 1)
        {
            return null;
        }

        return files[0].TryGetLocalPath();
    }
}
