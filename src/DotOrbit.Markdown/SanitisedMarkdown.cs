using System.Globalization;
using System.Net;
using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace DotOrbit.Markdown;

public enum MarkdownBlockKind
{
    Paragraph,
    Heading,
    BulletItem,
    NumberedItem,
    Quote,
    Code,
    ThematicBreak,
}

public enum MarkdownListStyle
{
    None,
    Bullet,
    Numeric,
    LowerAlpha,
}

[Flags]
public enum MarkdownInlineStyle
{
    None = 0,
    Emphasis = 1,
    Strong = 2,
    Code = 4,
    ImagePlaceholder = 8,
}

public sealed record MarkdownInline(string Text, MarkdownInlineStyle Style = MarkdownInlineStyle.None, string? Link = null);

public sealed record MarkdownBlock
{
    public MarkdownBlock(
        MarkdownBlockKind kind,
        IReadOnlyList<MarkdownInline> inlines,
        int level = 0,
        int number = 0,
        IReadOnlyList<MarkdownBlock>? children = null,
        int listGroup = 0,
        bool isLooseListItem = false,
        MarkdownListStyle listStyle = MarkdownListStyle.None)
    {
        Kind = kind;
        Inlines = inlines;
        Level = level;
        Number = number;
        Children = children ?? [];
        ListGroup = listGroup;
        IsLooseListItem = isLooseListItem;
        ListStyle = listStyle;
    }

    public MarkdownBlockKind Kind { get; }
    public IReadOnlyList<MarkdownInline> Inlines { get; }
    public int Level { get; }
    public int Number { get; }
    public IReadOnlyList<MarkdownBlock> Children { get; }
    public int ListGroup { get; }
    public bool IsLooseListItem { get; }
    public MarkdownListStyle ListStyle { get; }
}

public sealed class SanitisedMarkdownDocument
{
    public static SanitisedMarkdownDocument Empty { get; } = new([]);

    internal SanitisedMarkdownDocument(IReadOnlyList<MarkdownBlock> blocks)
    {
        Blocks = blocks;
    }

    public IReadOnlyList<MarkdownBlock> Blocks { get; }

    public bool IsEmpty => Blocks.Count == 0;

    public string ToHtml()
    {
        var output = new StringBuilder();
        WriteBlocks(output, Blocks);
        return output.ToString();
    }

    public string ToPlainText()
    {
        var lines = new List<string>();
        AddPlainText(lines, Blocks, 0);
        return string.Join(Environment.NewLine, lines);
    }

    private static void WriteBlocks(StringBuilder output, IReadOnlyList<MarkdownBlock> blocks)
    {
        ListIdentity? openList = null;

        foreach (var block in blocks)
        {
            var list = block.Kind is MarkdownBlockKind.BulletItem or MarkdownBlockKind.NumberedItem
                ? new ListIdentity(block.Kind, block.ListGroup)
                : (ListIdentity?)null;
            if (openList != list)
            {
                CloseList(output, openList);
                openList = list;
                if (list is not null)
                {
                    if (list.Value.Kind == MarkdownBlockKind.BulletItem)
                    {
                        output.Append("<ul>");
                    }
                    else if (block.ListStyle == MarkdownListStyle.LowerAlpha && block.Number == 1)
                    {
                        output.Append("<ol type=\"a\">");
                    }
                    else if (block.ListStyle == MarkdownListStyle.LowerAlpha)
                    {
                        output.Append("<ol type=\"a\" start=\"")
                            .Append(block.Number.ToString(CultureInfo.InvariantCulture))
                            .Append("\">");
                    }
                    else if (block.Number == 1)
                    {
                        output.Append("<ol>");
                    }
                    else
                    {
                        output.Append("<ol start=\"")
                            .Append(block.Number.ToString(CultureInfo.InvariantCulture))
                            .Append("\">");
                    }
                }
            }

            switch (block.Kind)
            {
                case MarkdownBlockKind.Paragraph:
                    WriteElement(output, "p", block.Inlines);
                    break;
                case MarkdownBlockKind.Heading:
                    WriteElement(output, $"h{Math.Clamp(block.Level, 1, 6)}", block.Inlines);
                    break;
                case MarkdownBlockKind.BulletItem:
                case MarkdownBlockKind.NumberedItem:
                    output.Append("<li>");
                    WriteListItemContent(output, block);
                    output.Append("</li>");
                    break;
                case MarkdownBlockKind.Quote:
                    output.Append("<blockquote>");
                    WriteElement(output, "p", block.Inlines);
                    output.Append("</blockquote>");
                    break;
                case MarkdownBlockKind.Code:
                    output.Append("<pre><code>");
                    output.Append(WebUtility.HtmlEncode(InlineText(block.Inlines)));
                    output.Append("</code></pre>");
                    break;
                case MarkdownBlockKind.ThematicBreak:
                    output.Append("<hr />");
                    break;
            }
        }

        CloseList(output, openList);
    }

    private static void WriteListItemContent(StringBuilder output, MarkdownBlock item)
    {
        if (item.IsLooseListItem)
        {
            WriteBlocks(output, item.Children);
            return;
        }

        var hasContent = false;
        for (var index = 0; index < item.Children.Count; index++)
        {
            var child = item.Children[index];
            if (child.Kind == MarkdownBlockKind.Paragraph)
            {
                if (hasContent) output.Append("<br /><br />");
                WriteInlines(output, child.Inlines);
            }
            else if (child.Kind is MarkdownBlockKind.BulletItem or MarkdownBlockKind.NumberedItem)
            {
                var nestedItems = new List<MarkdownBlock> { child };
                while (index + 1 < item.Children.Count
                    && item.Children[index + 1].Kind == child.Kind
                    && item.Children[index + 1].ListGroup == child.ListGroup)
                    nestedItems.Add(item.Children[++index]);
                WriteBlocks(output, nestedItems);
            }
            else
            {
                WriteBlocks(output, [child]);
            }
            hasContent = true;
        }
    }

    private static void AddPlainText(List<string> lines, IReadOnlyList<MarkdownBlock> blocks, int listDepth)
    {
        MarkdownBlock? previous = null;
        foreach (var block in blocks)
        {
            if (block.Kind is MarkdownBlockKind.BulletItem or MarkdownBlockKind.NumberedItem)
            {
                if (previous is not null
                    && previous.Kind is MarkdownBlockKind.BulletItem or MarkdownBlockKind.NumberedItem
                    && (previous.Kind != block.Kind || previous.ListGroup != block.ListGroup))
                    lines.Add(string.Empty);
                AddListItemPlainText(lines, block, listDepth);
                previous = block;
                continue;
            }

            lines.Add(block.Kind switch
            {
                MarkdownBlockKind.Quote => $"› {InlineText(block.Inlines)}",
                MarkdownBlockKind.ThematicBreak => "────────",
                _ => InlineText(block.Inlines),
            });
            if (block.Children.Count > 0) AddPlainText(lines, block.Children, listDepth + 1);
            previous = block;
        }
    }

    private static void AddListItemPlainText(List<string> lines, MarkdownBlock item, int listDepth)
    {
        var indentation = new string(' ', listDepth * 2);
        var marker = ListMarker(item);
        var markerWritten = false;
        MarkdownBlock? previous = null;
        foreach (var child in item.Children)
        {
            if (child.Kind == MarkdownBlockKind.Paragraph)
            {
                if (!markerWritten)
                {
                    lines.Add($"{indentation}{marker}{InlineText(child.Inlines)}");
                    markerWritten = true;
                }
                else
                {
                    lines.Add(string.Empty);
                    lines.Add($"{indentation}  {InlineText(child.Inlines)}");
                }
            }
            else if (child.Kind is MarkdownBlockKind.BulletItem or MarkdownBlockKind.NumberedItem)
            {
                if (!markerWritten)
                {
                    lines.Add($"{indentation}{marker.TrimEnd()}");
                    markerWritten = true;
                }
                if (previous is not null
                    && previous.Kind is MarkdownBlockKind.BulletItem or MarkdownBlockKind.NumberedItem
                    && (previous.Kind != child.Kind || previous.ListGroup != child.ListGroup))
                    lines.Add(string.Empty);
                AddListItemPlainText(lines, child, listDepth + 1);
            }
            else
            {
                if (!markerWritten)
                {
                    lines.Add($"{indentation}{marker.TrimEnd()}");
                    markerWritten = true;
                }
                AddPlainText(lines, [child], listDepth + 1);
            }
            previous = child;
        }

        if (!markerWritten) lines.Add($"{indentation}{marker.TrimEnd()}");
    }

    private static void CloseList(StringBuilder output, ListIdentity? list)
    {
        if (list?.Kind == MarkdownBlockKind.BulletItem) output.Append("</ul>");
        if (list?.Kind == MarkdownBlockKind.NumberedItem) output.Append("</ol>");
    }

    private static void WriteElement(StringBuilder output, string element, IReadOnlyList<MarkdownInline> inlines)
    {
        output.Append('<').Append(element).Append('>');
        WriteInlines(output, inlines);
        output.Append("</").Append(element).Append('>');
    }

    private static void WriteInlines(StringBuilder output, IReadOnlyList<MarkdownInline> inlines)
    {
        foreach (var inline in inlines) WriteInline(output, inline);
    }

    private static void WriteInline(StringBuilder output, MarkdownInline inline)
    {
        var style = inline.Style;
        if (inline.Link is not null)
        {
            output.Append("<a href=\"").Append(WebUtility.HtmlEncode(inline.Link)).Append("\" rel=\"noreferrer noopener\">");
        }
        if (style.HasFlag(MarkdownInlineStyle.Strong)) output.Append("<strong>");
        if (style.HasFlag(MarkdownInlineStyle.Emphasis)) output.Append("<em>");
        if (style.HasFlag(MarkdownInlineStyle.Code)) output.Append("<code>");

        var segments = inline.Text.Split('\n');
        for (var index = 0; index < segments.Length; index++)
        {
            if (index > 0) output.Append("<br />");
            output.Append(WebUtility.HtmlEncode(segments[index]));
        }

        if (style.HasFlag(MarkdownInlineStyle.Code)) output.Append("</code>");
        if (style.HasFlag(MarkdownInlineStyle.Emphasis)) output.Append("</em>");
        if (style.HasFlag(MarkdownInlineStyle.Strong)) output.Append("</strong>");
        if (inline.Link is not null) output.Append("</a>");
    }

    private static string InlineText(IEnumerable<MarkdownInline> inlines) => string.Concat(inlines.Select(inline => inline.Text));

    public static string ListMarker(MarkdownBlock item) => item.ListStyle switch
    {
        MarkdownListStyle.Bullet => "• ",
        MarkdownListStyle.LowerAlpha => $"{ToLowerAlpha(item.Number)}. ",
        _ => $"{item.Number.ToString(CultureInfo.InvariantCulture)}. ",
    };

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

    private readonly record struct ListIdentity(MarkdownBlockKind Kind, int Group);
}

public static class SanitisedMarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().Build();

    public static SanitisedMarkdownDocument Render(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(source)) return SanitisedMarkdownDocument.Empty;

        var normalised = NormaliseLowerAlphaLists(source);
        var blocks = new List<MarkdownBlock>();
        var nextListGroup = 0;
        foreach (var block in Markdig.Markdown.Parse(normalised.Source, Pipeline))
            AddBlock(blocks, block, ref nextListGroup, normalised.AlphaStarts);
        return blocks.Count == 0 ? SanitisedMarkdownDocument.Empty : new SanitisedMarkdownDocument(blocks);
    }

    private static void AddBlock(
        List<MarkdownBlock> output,
        Block block,
        ref int nextListGroup,
        IReadOnlyDictionary<int, int> alphaStarts,
        MarkdownBlockKind? containerKind = null)
    {
        switch (block)
        {
            case HtmlBlock:
                return;
            case HeadingBlock heading:
                AddLeaf(output, heading, MarkdownBlockKind.Heading, heading.Level);
                return;
            case ParagraphBlock paragraph:
                AddLeaf(output, paragraph, containerKind ?? MarkdownBlockKind.Paragraph);
                return;
            case QuoteBlock quote:
                foreach (var child in quote) AddBlock(output, child, ref nextListGroup, alphaStarts, MarkdownBlockKind.Quote);
                return;
            case ListBlock list:
                AddList(output, list, ref nextListGroup, alphaStarts);
                return;
            case CodeBlock code:
                var codeText = code.Lines.ToString();
                output.Add(new MarkdownBlock(MarkdownBlockKind.Code, [new MarkdownInline(codeText, MarkdownInlineStyle.Code)]));
                return;
            case ThematicBreakBlock:
                output.Add(new MarkdownBlock(MarkdownBlockKind.ThematicBreak, []));
                return;
            case ContainerBlock container:
                foreach (var child in container) AddBlock(output, child, ref nextListGroup, alphaStarts, containerKind);
                return;
        }
    }

    private static void AddList(
        List<MarkdownBlock> output,
        ListBlock list,
        ref int nextListGroup,
        IReadOnlyDictionary<int, int> alphaStarts)
    {
        var listGroup = ++nextListGroup;
        var alphaStart = 0;
        var isLowerAlpha = list.IsOrdered && alphaStarts.TryGetValue(list.Line, out alphaStart);
        var number = isLowerAlpha
            ? alphaStart
            : int.TryParse(list.OrderedStart, CultureInfo.InvariantCulture, out var parsedStart) ? parsedStart : 1;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var children = new List<MarkdownBlock>();
            foreach (var child in item)
                AddBlock(children, child, ref nextListGroup, alphaStarts);
            if (children.Count > 0)
            {
                output.Add(new MarkdownBlock(
                    list.IsOrdered ? MarkdownBlockKind.NumberedItem : MarkdownBlockKind.BulletItem,
                    [],
                    number: list.IsOrdered ? number : 0,
                    children: children,
                    listGroup: listGroup,
                    isLooseListItem: list.IsLoose,
                    listStyle: list.IsOrdered
                        ? isLowerAlpha ? MarkdownListStyle.LowerAlpha : MarkdownListStyle.Numeric
                        : MarkdownListStyle.Bullet));
            }
            if (list.IsOrdered) number++;
        }
    }

    private static NormalisedMarkdown NormaliseLowerAlphaLists(string source)
    {
        var output = new StringBuilder(source.Length);
        var alphaStarts = new Dictionary<int, int>();
        var lineNumber = 0;
        var inFence = false;
        var fenceCharacter = '\0';
        var fenceLength = 0;
        var activeListIndentations = new List<int>();

        for (var offset = 0; offset < source.Length;)
        {
            var newline = source.IndexOf('\n', offset);
            var end = newline < 0 ? source.Length : newline;
            var contentEnd = end > offset && source[end - 1] == '\r' ? end - 1 : end;
            var line = source.AsSpan(offset, contentEnd - offset);
            var indentationLength = 0;
            var indentationColumns = 0;
            while (indentationLength < line.Length && line[indentationLength] is ' ' or '\t')
            {
                indentationColumns = line[indentationLength] == '\t'
                    ? indentationColumns + 4 - (indentationColumns % 4)
                    : indentationColumns + 1;
                indentationLength++;
            }
            var candidate = line[indentationLength..];

            var isNestedBlockIndent = activeListIndentations.Count > 0
                && indentationColumns - activeListIndentations[^1] <= 4;
            var isFenceCandidate = TryReadFence(candidate, out var candidateFenceCharacter, out var candidateFenceLength);
            var isFenceLine = (indentationColumns <= 3 || isNestedBlockIndent) && isFenceCandidate;
            var wasInFence = inFence;
            if (isFenceLine)
            {
                if (!inFence)
                {
                    if (candidateFenceCharacter != '`' || !candidate[candidateFenceLength..].Contains('`'))
                    {
                        inFence = true;
                        fenceCharacter = candidateFenceCharacter;
                        fenceLength = candidateFenceLength;
                    }
                }
                else if (candidateFenceCharacter == fenceCharacter
                    && candidateFenceLength >= fenceLength
                    && IsWhitespace(candidate[candidateFenceLength..]))
                {
                    inFence = false;
                }
            }

            var alphaLength = 0;
            while (alphaLength < candidate.Length && candidate[alphaLength] is >= 'a' and <= 'z') alphaLength++;
            var alphaStart = 0;
            var isLowerAlphaMarker = alphaLength > 0
                && alphaLength < candidate.Length - 1
                && candidate[alphaLength] is '.' or ')'
                && candidate[alphaLength + 1] is ' ' or '\t'
                && TryParseLowerAlpha(candidate[..alphaLength], out alphaStart);
            if (!wasInFence && !isFenceLine)
            {
                while (activeListIndentations.Count > 0 && indentationColumns <= activeListIndentations[^1])
                    activeListIndentations.RemoveAt(activeListIndentations.Count - 1);
            }
            var isNestedListIndent = activeListIndentations.Count > 0
                && indentationColumns - activeListIndentations[^1] <= 4;
            var normaliseLowerAlpha = !wasInFence
                && !isFenceLine
                && isLowerAlphaMarker
                && (indentationColumns <= 3 || isNestedListIndent);
            if (normaliseLowerAlpha)
            {
                alphaStarts[lineNumber] = alphaStart;
                output.Append(line[..indentationLength])
                    .Append(alphaStart.ToString(CultureInfo.InvariantCulture))
                    .Append(candidate[alphaLength..]);
            }
            else
            {
                output.Append(line);
            }

            if (!wasInFence && !isFenceLine && !candidate.IsEmpty)
            {
                if (normaliseLowerAlpha || IsCommonMarkListMarker(candidate))
                {
                    activeListIndentations.Add(indentationColumns);
                }
                else if (indentationColumns == 0)
                {
                    activeListIndentations.Clear();
                }
            }

            output.Append(source.AsSpan(contentEnd, end - contentEnd));
            if (newline >= 0) output.Append('\n');
            offset = newline < 0 ? source.Length : newline + 1;
            lineNumber++;
        }

        return new NormalisedMarkdown(output.ToString(), alphaStarts);
    }

    private static bool IsCommonMarkListMarker(ReadOnlySpan<char> value)
    {
        if (value.Length >= 2 && value[0] is '-' or '+' or '*' && value[1] is ' ' or '\t') return true;
        var index = 0;
        while (index < value.Length && index < 9 && char.IsAsciiDigit(value[index])) index++;
        return index > 0
            && index < value.Length - 1
            && value[index] is '.' or ')'
            && value[index + 1] is ' ' or '\t';
    }

    private static bool TryParseLowerAlpha(ReadOnlySpan<char> value, out int number)
    {
        number = 0;
        foreach (var character in value)
        {
            if (number > (999_999_999 - 26) / 26) return false;
            number = (number * 26) + character - 'a' + 1;
        }
        return number is > 0 and <= 999_999_999;
    }

    private static bool TryReadFence(ReadOnlySpan<char> line, out char character, out int length)
    {
        character = '\0';
        length = 0;
        if (line.Length < 3 || line[0] is not ('`' or '~')) return false;
        character = line[0];
        while (length < line.Length && line[length] == character) length++;
        return length >= 3;
    }

    private static bool IsWhitespace(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
            if (!char.IsWhiteSpace(character)) return false;
        return true;
    }

    private static void AddLeaf(List<MarkdownBlock> output, LeafBlock leaf, MarkdownBlockKind kind, int level = 0)
    {
        if (leaf.Inline is null) return;
        var inlines = new List<MarkdownInline>();
        AddInlines(inlines, leaf.Inline.FirstChild, MarkdownInlineStyle.None);
        if (inlines.Count > 0) output.Add(new MarkdownBlock(kind, inlines, level));
    }

    private static void AddInlines(List<MarkdownInline> output, Inline? current, MarkdownInlineStyle inheritedStyle)
    {
        while (current is not null)
        {
            switch (current)
            {
                case LiteralInline literal:
                    AddText(output, literal.Content.ToString(), inheritedStyle);
                    break;
                case CodeInline code:
                    AddText(output, code.Content, inheritedStyle | MarkdownInlineStyle.Code);
                    break;
                case HtmlEntityInline entity:
                    AddText(output, entity.Transcoded.ToString(), inheritedStyle);
                    break;
                case LineBreakInline:
                    AddText(output, "\n", inheritedStyle);
                    break;
                case HtmlInline:
                    break;
                case AutolinkInline autolink:
                    var autolinkText = autolink.IsEmail ? autolink.Url : autolink.Url;
                    AddText(output, autolinkText, inheritedStyle, SafeUrl(autolink.IsEmail ? $"mailto:{autolink.Url}" : autolink.Url));
                    break;
                case LinkInline { IsImage: true } image:
                    var alternative = CollectText(image.FirstChild);
                    var remote = image.Url?.StartsWith("//", StringComparison.Ordinal) == true
                        || (Uri.TryCreate(image.Url, UriKind.Absolute, out var imageUri)
                            && imageUri.Scheme is "https" or "http");
                    var description = remote ? "Remote image not loaded" : "Image not loaded";
                    AddText(
                        output,
                        string.IsNullOrWhiteSpace(alternative)
                            ? $"[{description}]"
                            : $"[{description}: {alternative}]",
                        inheritedStyle | MarkdownInlineStyle.ImagePlaceholder);
                    break;
                case LinkInline link:
                    AddContainer(output, link, inheritedStyle, SafeUrl(link.Url));
                    break;
                case EmphasisInline emphasis:
                    var emphasisStyle = emphasis.DelimiterCount >= 2
                        ? MarkdownInlineStyle.Strong
                        : MarkdownInlineStyle.Emphasis;
                    AddContainer(output, emphasis, inheritedStyle | emphasisStyle, null);
                    break;
                case ContainerInline container:
                    AddContainer(output, container, inheritedStyle, null);
                    break;
            }
            current = current.NextSibling;
        }
    }

    private static void AddContainer(
        List<MarkdownInline> output,
        ContainerInline container,
        MarkdownInlineStyle style,
        string? link)
    {
        var nested = new List<MarkdownInline>();
        AddInlines(nested, container.FirstChild, style);
        if (link is not null)
            for (var index = 0; index < nested.Count; index++) nested[index] = nested[index] with { Link = link };
        output.AddRange(nested);
    }

    private static string CollectText(Inline? current)
    {
        var output = new List<MarkdownInline>();
        AddInlines(output, current, MarkdownInlineStyle.None);
        return string.Concat(output.Select(inline => inline.Text));
    }

    private static void AddText(
        List<MarkdownInline> output,
        string text,
        MarkdownInlineStyle style,
        string? link = null)
    {
        if (text.Length == 0) return;
        if (output.Count > 0 && output[^1].Style == style && output[^1].Link == link)
        {
            output[^1] = output[^1] with { Text = output[^1].Text + text };
            return;
        }
        output.Add(new MarkdownInline(text, style, link));
    }

    private static string? SafeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)) return null;
        if (value[0] == '#') return value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        return uri.Scheme is "https" or "http" or "mailto" ? uri.AbsoluteUri : null;
    }

    private sealed record NormalisedMarkdown(string Source, IReadOnlyDictionary<int, int> AlphaStarts);
}
