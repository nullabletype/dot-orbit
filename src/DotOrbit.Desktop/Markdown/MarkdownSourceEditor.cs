using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;

namespace DotOrbit.Desktop.Markdown;

internal static class MarkdownSourceEditor
{
    private const string Indentation = "    ";

    public static bool TryHandleKeyDown(TextBox editor, KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(e);

        MarkdownTextEdit? edit = null;
        if (e.Key == Key.Tab && !HasTraversalModifier(e.KeyModifiers))
        {
            edit = CreateIndentEdit(editor.Text ?? string.Empty, editor.CaretIndex, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        }
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            edit = CreateListContinuationEdit(
                editor.Text ?? string.Empty,
                editor.SelectionStart,
                editor.SelectionEnd,
                editor.CaretIndex);
        }

        if (edit is null) return false;
        Apply(editor, edit.Value);
        e.Handled = true;
        return true;
    }

    private static bool HasTraversalModifier(KeyModifiers modifiers) =>
        modifiers.HasFlag(KeyModifiers.Control)
        || modifiers.HasFlag(KeyModifiers.Meta)
        || modifiers.HasFlag(KeyModifiers.Alt);

    private static MarkdownTextEdit CreateIndentEdit(string text, int caretIndex, bool outdent)
    {
        var caret = Math.Clamp(caretIndex, 0, text.Length);
        var lineStart = FindLineStart(text, caret);
        if (!outdent)
        {
            var lineEnd = text.IndexOf('\n', lineStart);
            if (lineEnd < 0) lineEnd = text.Length;
            var line = text.AsSpan(lineStart, lineEnd - lineStart).TrimEnd('\r');
            if (!IsInsideCodeBlock(text, lineStart)
                && TryParseListPrefix(line, out var indentationLength, out var prefixLength, out var marker)
                && marker.Kind is ListMarkerKind.Ordered or ListMarkerKind.LowerAlpha)
            {
                var replacement = $"{line[..indentationLength]}{Indentation}- ";
                var prefixEnd = lineStart + prefixLength;
                var updatedCaret = caret <= prefixEnd
                    ? lineStart + replacement.Length
                    : caret + replacement.Length - prefixLength;
                return new MarkdownTextEdit(lineStart, prefixLength, replacement, updatedCaret);
            }

            return new MarkdownTextEdit(lineStart, 0, Indentation, caret + Indentation.Length);
        }

        var removalLength = 0;
        if (lineStart < text.Length && text[lineStart] == '\t')
        {
            removalLength = 1;
        }
        else
        {
            while (removalLength < Indentation.Length
                && lineStart + removalLength < text.Length
                && text[lineStart + removalLength] == ' ')
                removalLength++;
        }

        return new MarkdownTextEdit(
            lineStart,
            removalLength,
            string.Empty,
            Math.Max(lineStart, caret - removalLength));
    }

    private static MarkdownTextEdit? CreateListContinuationEdit(
        string text,
        int selectionStart,
        int selectionEnd,
        int caretIndex)
    {
        if (selectionStart != selectionEnd) return null;
        var caret = Math.Clamp(caretIndex, 0, text.Length);
        var lineStart = FindLineStart(text, caret);
        if (IsInsideCodeBlock(text, lineStart)) return null;
        var beforeCaret = text.AsSpan(lineStart, caret - lineStart);
        if (!TryParseListPrefix(beforeCaret, out var indentationLength, out var prefixLength, out var marker)) return null;

        var lineEnd = text.IndexOf('\n', caret);
        if (lineEnd < 0) lineEnd = text.Length;
        var currentContent = beforeCaret[prefixLength..];
        var remainingContent = text.AsSpan(caret, lineEnd - caret);
        if (IsWhitespace(currentContent) && IsWhitespace(remainingContent))
        {
            if (indentationLength > 0)
            {
                var indentationRemoval = text[lineStart] == '\t'
                    ? 1
                    : Math.Min(Indentation.Length, indentationLength);
                return new MarkdownTextEdit(
                    lineStart,
                    indentationRemoval,
                    string.Empty,
                    caret - indentationRemoval);
            }

            return new MarkdownTextEdit(lineStart, prefixLength, string.Empty, lineStart);
        }

        var indentation = beforeCaret[..indentationLength].ToString();
        var nextMarker = marker.Kind switch
        {
            ListMarkerKind.Ordered => $"{(marker.Number + 1).ToString(CultureInfo.InvariantCulture)}{marker.Delimiter} ",
            ListMarkerKind.LowerAlpha => $"{ToLowerAlpha(marker.Number + 1)}{marker.Delimiter} ",
            _ => $"{marker.Delimiter} ",
        };
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var insertion = $"{newline}{indentation}{nextMarker}";
        return new MarkdownTextEdit(caret, 0, insertion, caret + insertion.Length);
    }

    private static bool TryParseListPrefix(
        ReadOnlySpan<char> line,
        out int indentationLength,
        out int prefixLength,
        out ListMarker marker)
    {
        indentationLength = 0;
        prefixLength = 0;
        marker = default;
        while (indentationLength < line.Length && line[indentationLength] is ' ' or '\t') indentationLength++;
        if (indentationLength >= line.Length) return false;

        var markerIndex = indentationLength;
        if (line[markerIndex] is '-' or '+' or '*')
        {
            if (!HasFollowingWhitespace(line, markerIndex + 1, out prefixLength)) return false;
            marker = new ListMarker(ListMarkerKind.Unordered, line[markerIndex], 0);
            return true;
        }

        var alphaEnd = markerIndex;
        while (alphaEnd < line.Length && line[alphaEnd] is >= 'a' and <= 'z') alphaEnd++;
        if (alphaEnd > markerIndex
            && alphaEnd < line.Length
            && line[alphaEnd] is '.' or ')'
            && HasFollowingWhitespace(line, alphaEnd + 1, out prefixLength)
            && TryParseLowerAlpha(line[markerIndex..alphaEnd], out var alphaNumber))
        {
            marker = new ListMarker(ListMarkerKind.LowerAlpha, line[alphaEnd], alphaNumber);
            return true;
        }

        var digitEnd = markerIndex;
        while (digitEnd < line.Length && char.IsAsciiDigit(line[digitEnd]) && digitEnd - markerIndex < 9) digitEnd++;
        if (digitEnd == markerIndex || digitEnd >= line.Length || line[digitEnd] is not ('.' or ')')) return false;
        if (!HasFollowingWhitespace(line, digitEnd + 1, out prefixLength)) return false;
        if (!int.TryParse(line[markerIndex..digitEnd], CultureInfo.InvariantCulture, out var number)
            || number >= 999_999_999)
            return false;
        marker = new ListMarker(ListMarkerKind.Ordered, line[digitEnd], number);
        return true;
    }

    private static bool TryParseLowerAlpha(ReadOnlySpan<char> value, out int number)
    {
        number = 0;
        foreach (var character in value)
        {
            if (number > (999_999_999 - 26) / 26) return false;
            number = (number * 26) + character - 'a' + 1;
        }
        return number is > 0 and < 999_999_999;
    }

    private static string ToLowerAlpha(int number)
    {
        var value = Math.Max(1, number);
        var output = new StringBuilder();
        while (value > 0)
        {
            value--;
            output.Insert(0, (char)('a' + (value % 26)));
            value /= 26;
        }
        return output.ToString();
    }

    private static bool HasFollowingWhitespace(ReadOnlySpan<char> line, int index, out int prefixLength)
    {
        prefixLength = 0;
        if (index >= line.Length || line[index] is not (' ' or '\t')) return false;
        while (index < line.Length && line[index] is ' ' or '\t') index++;
        prefixLength = index;
        return true;
    }

    private static bool IsInsideCodeBlock(string text, int lineStart)
    {
        var fenced = false;
        var fenceCharacter = '\0';
        var fenceLength = 0;
        foreach (var rawLine in text.AsSpan(0, lineStart).ToString().Split('\n'))
        {
            var line = rawLine.AsSpan().TrimEnd('\r');
            var indentation = 0;
            while (indentation < line.Length && indentation < 4 && line[indentation] == ' ') indentation++;
            if (indentation == 4) continue;
            line = line[indentation..];
            if (line.Length < 3 || line[0] is not ('`' or '~')) continue;
            var delimiterLength = 1;
            while (delimiterLength < line.Length && line[delimiterLength] == line[0]) delimiterLength++;
            if (delimiterLength < 3) continue;
            if (!fenced)
            {
                if (line[0] == '`' && line[delimiterLength..].Contains('`')) continue;
                fenced = true;
                fenceCharacter = line[0];
                fenceLength = delimiterLength;
            }
            else if (line[0] == fenceCharacter
                && delimiterLength >= fenceLength
                && IsWhitespace(line[delimiterLength..]))
            {
                fenced = false;
                fenceCharacter = '\0';
                fenceLength = 0;
            }
        }
        return fenced || IsStandaloneIndentedCode(text, lineStart);
    }

    private static bool IsStandaloneIndentedCode(string text, int lineStart)
    {
        var lineEnd = text.IndexOf('\n', lineStart);
        if (lineEnd < 0) lineEnd = text.Length;
        var line = text.AsSpan(lineStart, lineEnd - lineStart);
        var indentation = 0;
        while (indentation < line.Length && line[indentation] == ' ') indentation++;
        if (line.Length > 0 && line[0] == '\t') indentation = Indentation.Length;
        if (indentation < Indentation.Length) return false;

        var previousEnd = lineStart > 0 ? lineStart - 1 : 0;
        while (previousEnd > 0)
        {
            var previousStart = FindLineStart(text, previousEnd);
            var previous = text.AsSpan(previousStart, previousEnd - previousStart).TrimEnd('\r');
            if (!IsWhitespace(previous))
            {
                if (TryParseListPrefix(previous, out var previousIndentation, out var previousPrefixLength, out _)
                    && previousIndentation < indentation)
                    return indentation >= previousPrefixLength + Indentation.Length;

                var previousContentIndentation = 0;
                while (previousContentIndentation < previous.Length && previous[previousContentIndentation] == ' ')
                    previousContentIndentation++;
                if (previousContentIndentation == 0) return true;
            }
            previousEnd = previousStart > 0 ? previousStart - 1 : 0;
        }

        return true;
    }

    private static bool IsWhitespace(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
            if (!char.IsWhiteSpace(character)) return false;
        return true;
    }

    private static int FindLineStart(string text, int caret) =>
        caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1;

    private static void Apply(TextBox editor, MarkdownTextEdit edit)
    {
        editor.SelectionStart = edit.Start;
        editor.SelectionEnd = edit.Start + edit.Length;
        editor.SelectedText = edit.Replacement;
        editor.CaretIndex = edit.CaretIndex;
        editor.SelectionStart = edit.CaretIndex;
        editor.SelectionEnd = edit.CaretIndex;
    }

    private enum ListMarkerKind
    {
        Unordered,
        Ordered,
        LowerAlpha,
    }

    private readonly record struct ListMarker(ListMarkerKind Kind, char Delimiter, int Number);
    private readonly record struct MarkdownTextEdit(int Start, int Length, string Replacement, int CaretIndex);
}
