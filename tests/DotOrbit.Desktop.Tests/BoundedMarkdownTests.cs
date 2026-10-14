using DotOrbit.Markdown;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class BoundedMarkdownTests
{
    [Fact]
    public void CountsActualNodesBeforeSanitiserMergesInlines()
    {
        var result = SanitisedMarkdown.ParseBounded("a *b* c", 6);

        Assert.Equal(MarkdownBudgetError.None, result.Error);
        Assert.Equal(5, result.NodeCount);
        Assert.Equal(1, result.ContainerDepth);
        Assert.Equal("<p>a <em>b</em> c</p>", result.Document!.ToHtml());
        Assert.Equal(MarkdownBudgetError.NodeLimitExceeded, SanitisedMarkdown.ParseBounded("a *b* c", 4).Error);
    }

    [Fact]
    public void DefaultNodeBoundaryAccepts4096AndRejects4097()
    {
        var source = string.Join("\n\n", Enumerable.Repeat("text", 2048));
        var accepted = SanitisedMarkdown.ParseBounded(source);
        var rejected = SanitisedMarkdown.ParseBounded(source + "\n\n---");

        Assert.Equal(4096, accepted.NodeCount);
        Assert.NotNull(accepted.Document);
        Assert.Equal(MarkdownBudgetError.NodeLimitExceeded, rejected.Error);
        Assert.Equal(4097, rejected.NodeCount);
        Assert.Null(rejected.Document);
    }

    [Theory]
    [InlineData(32, MarkdownBudgetError.None)]
    [InlineData(33, MarkdownBudgetError.ContainerDepthExceeded)]
    public void QuoteContainerDepthHasExactDefaultBoundary(int depth, MarkdownBudgetError expected)
    {
        var result = SanitisedMarkdown.ParseBounded(new string('>', depth) + " text");

        Assert.Equal(expected, result.Error);
        Assert.Equal(depth, result.ContainerDepth);
        Assert.Equal(expected == MarkdownBudgetError.None, result.Document is not null);
    }

    [Fact]
    public void DiscardedHtmlAndImageChildrenStillConsumeBudget()
    {
        var html = SanitisedMarkdown.ParseBounded("<script>secret</script>", 0);
        var image = SanitisedMarkdown.ParseBounded("![*label*](https://example.test/image)", 3);
        var safe = SanitisedMarkdown.ParseBounded("![*label*](https://example.test/image)\n\n<script>secret</script>");

        Assert.Equal(MarkdownBudgetError.NodeLimitExceeded, html.Error);
        Assert.Null(html.Document);
        Assert.Equal(MarkdownBudgetError.NodeLimitExceeded, image.Error);
        Assert.Equal(5, safe.NodeCount);
        Assert.Equal(2, safe.ContainerDepth);
        Assert.Equal("<p>[Remote image not loaded: label]</p>", safe.Document!.ToHtml());
    }

    [Fact]
    public void CallerCanChooseBudgetsForExistingStateAndEmptyFields()
    {
        var source = string.Join("\n\n", Enumerable.Repeat("text", 2049));
        Assert.NotNull(SanitisedMarkdown.ParseBounded(source, 65536, 32).Document);
        var empty = SanitisedMarkdown.ParseBounded(string.Empty, 0, 0);
        Assert.Equal(0, empty.NodeCount);
        Assert.Equal(0, empty.ContainerDepth);
        Assert.True(empty.Document!.IsEmpty);
    }
}
