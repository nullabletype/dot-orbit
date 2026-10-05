using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using DotOrbit.Markdown;

namespace DotOrbit.Desktop.Views;

public sealed class MarkdownPreview : StackPanel
{
    public static readonly StyledProperty<SanitisedMarkdownDocument?> DocumentProperty =
        AvaloniaProperty.Register<MarkdownPreview, SanitisedMarkdownDocument?>(nameof(Document));

    public MarkdownPreview()
    {
        Spacing = 0;
    }

    public SanitisedMarkdownDocument? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DocumentProperty) Rebuild();
    }

    private void Rebuild()
    {
        Children.Clear();
        if (Document is not { IsEmpty: false } document)
        {
            Children.Add(new TextBlock
            {
                Text = "Nothing to preview yet. Activate to write a description.",
                FontStyle = FontStyle.Italic,
                Opacity = 0.72,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        AddBlocks(document.Blocks, 0, 0);
    }

    private void AddBlocks(IReadOnlyList<MarkdownBlock> blocks, int listDepth, double firstBlockSpacing)
    {
        MarkdownBlock? previous = null;
        foreach (var block in blocks)
        {
            var spacing = previous is null
                ? firstBlockSpacing
                : SameList(previous, block) ? 3 : 12;
            if (block.Kind is MarkdownBlockKind.BulletItem or MarkdownBlockKind.NumberedItem)
            {
                AddListItem(block, listDepth, spacing);
                previous = block;
                continue;
            }

            if (block.Kind == MarkdownBlockKind.ThematicBreak)
            {
                AddThematicBreak(0, spacing);
                previous = block;
                continue;
            }

            AddTextBlock(block, string.Empty, 0, spacing);
            previous = block;
        }
    }

    private void AddListItem(MarkdownBlock item, int listDepth, double spacing)
    {
        var marker = SanitisedMarkdownDocument.ListMarker(item);
        var markerWritten = false;
        MarkdownBlock? previous = null;
        foreach (var child in item.Children)
        {
            if (child.Kind == MarkdownBlockKind.Paragraph)
            {
                if (!markerWritten)
                {
                    AddTextBlock(child, marker, listDepth * 16, spacing);
                    markerWritten = true;
                }
                else
                {
                    AddTextBlock(child, string.Empty, (listDepth + 1) * 16, item.IsLooseListItem ? 12 : 4);
                }
            }
            else if (child.Kind is MarkdownBlockKind.BulletItem or MarkdownBlockKind.NumberedItem)
            {
                if (!markerWritten)
                {
                    AddTextBlock(item, marker.TrimEnd(), listDepth * 16, spacing);
                    markerWritten = true;
                }
                var childSpacing = previous is null || previous.Kind == MarkdownBlockKind.Paragraph
                    ? 4
                    : SameList(previous, child) ? 3 : 12;
                AddListItem(child, listDepth + 1, childSpacing);
            }
            else
            {
                if (!markerWritten)
                {
                    AddTextBlock(item, marker.TrimEnd(), listDepth * 16, spacing);
                    markerWritten = true;
                }
                if (child.Kind == MarkdownBlockKind.ThematicBreak)
                    AddThematicBreak((listDepth + 1) * 16, 12);
                else
                    AddTextBlock(child, string.Empty, (listDepth + 1) * 16, 12);
            }
            previous = child;
        }

        if (!markerWritten) AddTextBlock(item, marker.TrimEnd(), listDepth * 16, spacing);
    }

    private void AddTextBlock(MarkdownBlock block, string prefix, double leftIndent, double topSpacing)
    {
        if (block.Inlines.Any(IsApprovedExternalLink))
        {
            AddLinkedTextBlock(block, prefix, leftIndent, topSpacing);
            return;
        }

        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(leftIndent, topSpacing, 0, 0),
        };
        if (block.Kind == MarkdownBlockKind.Heading)
        {
            text.FontWeight = FontWeight.Bold;
            text.FontSize = block.Level switch { 1 => 20, 2 => 18, 3 => 16, _ => 14 };
        }
        if (block.Kind == MarkdownBlockKind.Code)
        {
            text.FontFamily = new FontFamily("monospace");
            text.FontSize = 12;
            text.LineHeight = 20;
        }
        if (block.Kind == MarkdownBlockKind.Quote)
        {
            text.FontStyle = FontStyle.Italic;
            text.Opacity = 0.82;
            prefix = "› ";
        }

        if (prefix.Length > 0) text.Inlines!.Add(new Run(prefix) { FontWeight = FontWeight.Bold });
        foreach (var inline in block.Inlines) text.Inlines!.Add(CreateInline(inline));
        Children.Add(text);
    }

    private void AddLinkedTextBlock(MarkdownBlock block, string prefix, double leftIndent, double topSpacing)
    {
        if (block.Kind == MarkdownBlockKind.Quote) prefix = "› ";
        var lines = new StackPanel
        {
            Spacing = 0,
            Margin = new Thickness(leftIndent, topSpacing, 0, 0),
        };
        var line = NewFlowLine();
        lines.Children.Add(line);

        if (prefix.Length > 0)
        {
            AddTextFragments(line, prefix, block, MarkdownInlineStyle.Strong);
        }

        foreach (var inline in block.Inlines)
        {
            var segments = inline.Text.Split('\n');
            for (var index = 0; index < segments.Length; index++)
            {
                if (index > 0)
                {
                    line = NewFlowLine();
                    lines.Children.Add(line);
                }

                if (segments[index].Length == 0) continue;
                if (IsApprovedExternalLink(inline))
                    AddLink(line, segments[index], inline, block);
                else
                    AddTextFragments(line, segments[index], block, inline.Style);
            }
        }

        Children.Add(lines);
    }

    private static WrapPanel NewFlowLine() => new()
    {
        Orientation = Avalonia.Layout.Orientation.Horizontal,
    };

    private static void AddTextFragments(
        WrapPanel line,
        string value,
        MarkdownBlock block,
        MarkdownInlineStyle style)
    {
        foreach (var fragment in WordFragments(value))
        {
            var text = new TextBlock { Text = fragment };
            ApplyBlockStyle(text, block);
            ApplyInlineStyle(text, style);
            line.Children.Add(text);
        }
    }

    private static void AddLink(WrapPanel line, string value, MarkdownInline inline, MarkdownBlock block)
    {
        var destination = new Uri(inline.Link!, UriKind.Absolute);
        var content = new TextBlock
        {
            Text = value,
            TextWrapping = TextWrapping.Wrap,
        };
        ApplyBlockStyle(content, block);
        ApplyInlineStyle(content, inline.Style);
        var link = new HyperlinkButton
        {
            Content = content,
            NavigateUri = destination,
            Focusable = true,
        };
        link.Classes.Add("markdown-link");
        AutomationProperties.SetControlTypeOverride(link, AutomationControlType.Hyperlink);
        AutomationProperties.SetName(link, $"Open {value}, {destination.AbsoluteUri}");
        line.Children.Add(link);
    }

    private static IEnumerable<string> WordFragments(string value)
    {
        var start = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsWhiteSpace(value[index])) continue;
            while (index + 1 < value.Length && char.IsWhiteSpace(value[index + 1])) index++;
            yield return value[start..(index + 1)];
            start = index + 1;
        }
        if (start < value.Length) yield return value[start..];
    }

    private static void ApplyBlockStyle(TextBlock text, MarkdownBlock block)
    {
        if (block.Kind == MarkdownBlockKind.Heading)
        {
            text.FontWeight = FontWeight.Bold;
            text.FontSize = block.Level switch { 1 => 20, 2 => 18, 3 => 16, _ => 14 };
        }
        if (block.Kind == MarkdownBlockKind.Code)
        {
            text.FontFamily = new FontFamily("monospace");
            text.FontSize = 12;
            text.LineHeight = 20;
        }
        if (block.Kind == MarkdownBlockKind.Quote)
        {
            text.FontStyle = FontStyle.Italic;
            text.Opacity = 0.82;
        }
    }

    private static void ApplyInlineStyle(TextBlock text, MarkdownInlineStyle style)
    {
        if (style.HasFlag(MarkdownInlineStyle.Strong)) text.FontWeight = FontWeight.Bold;
        if (style.HasFlag(MarkdownInlineStyle.Emphasis)
            || style.HasFlag(MarkdownInlineStyle.ImagePlaceholder)) text.FontStyle = FontStyle.Italic;
        if (style.HasFlag(MarkdownInlineStyle.Code)) text.FontFamily = new FontFamily("monospace");
    }

    private static bool SameList(MarkdownBlock left, MarkdownBlock right) =>
        left.Kind is MarkdownBlockKind.BulletItem or MarkdownBlockKind.NumberedItem
        && right.Kind == left.Kind
        && right.ListGroup == left.ListGroup;

    private void AddThematicBreak(double leftIndent, double topSpacing)
    {
        Children.Add(new Border
        {
            Height = 1,
            Background = Brushes.Gray,
            Opacity = 0.45,
            Margin = new Thickness(leftIndent, topSpacing + 4, 0, 4),
        });
    }

    private static Run CreateInline(MarkdownInline inline)
    {
        var run = new Run(inline.Text);
        if (inline.Style.HasFlag(MarkdownInlineStyle.Strong)) run.FontWeight = FontWeight.Bold;
        if (inline.Style.HasFlag(MarkdownInlineStyle.Emphasis)
            || inline.Style.HasFlag(MarkdownInlineStyle.ImagePlaceholder)) run.FontStyle = FontStyle.Italic;
        if (inline.Style.HasFlag(MarkdownInlineStyle.Code)) run.FontFamily = new FontFamily("monospace");
        if (inline.Link is not null) run.TextDecorations = TextDecorations.Underline;
        return run;
    }

    private static bool IsApprovedExternalLink(MarkdownInline inline) =>
        inline.Link is not null
        && !inline.Link.StartsWith('#')
        && Uri.TryCreate(inline.Link, UriKind.Absolute, out var destination)
        && destination.Scheme is "https" or "http" or "mailto";
}
