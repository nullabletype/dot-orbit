using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotOrbit.Desktop.Clipboard;
using DotOrbit.Desktop.Markdown;
using DotOrbit.Desktop.ViewModels;
using DotOrbit.Desktop.Views;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class MarkdownTests
{
    [Fact]
    public void CommonMarkSubsetProducesMatchingPreviewHtmlAndPlainText()
    {
        const string source = "# Plan\n\nUse **bold**, *care*, and `code` with [docs](https://example.test/docs).\n\n- First\n- Second\n\n> Keep context\n\n```text\nsafe <value>\n```";

        var document = SanitisedMarkdownRenderer.Render(source);

        Assert.Collection(
            document.Blocks,
            block => Assert.Equal(MarkdownBlockKind.Heading, block.Kind),
            block =>
            {
                Assert.Equal(MarkdownBlockKind.Paragraph, block.Kind);
                Assert.Contains(block.Inlines, inline => inline.Style.HasFlag(MarkdownInlineStyle.Strong) && inline.Text == "bold");
                Assert.Contains(block.Inlines, inline => inline.Style.HasFlag(MarkdownInlineStyle.Emphasis) && inline.Text == "care");
                Assert.Contains(block.Inlines, inline => inline.Style.HasFlag(MarkdownInlineStyle.Code) && inline.Text == "code");
                Assert.Contains(block.Inlines, inline => inline.Link == "https://example.test/docs");
            },
            block => Assert.Equal(MarkdownBlockKind.BulletItem, block.Kind),
            block => Assert.Equal(MarkdownBlockKind.BulletItem, block.Kind),
            block => Assert.Equal(MarkdownBlockKind.Quote, block.Kind),
            block => Assert.Equal(MarkdownBlockKind.Code, block.Kind));
        Assert.Equal("Plan\nUse bold, care, and code with docs.\n• First\n• Second\n› Keep context\nsafe <value>", document.ToPlainText().ReplaceLineEndings("\n"));
        Assert.Equal(
            "<h1>Plan</h1><p>Use <strong>bold</strong>, <em>care</em>, and <code>code</code> with <a href=\"https://example.test/docs\" rel=\"noreferrer noopener\">docs</a>.</p><ul><li>First</li><li>Second</li></ul><blockquote><p>Keep context</p></blockquote><pre><code>safe &lt;value&gt;</code></pre>",
            document.ToHtml());
    }

    [Fact]
    public void OrderedListStartMatchesPreviewPlainTextAndRichHtml()
    {
        var document = SanitisedMarkdownRenderer.Render("3. Third\n4. Fourth");

        Assert.Collection(
            document.Blocks,
            block => Assert.Equal((MarkdownBlockKind.NumberedItem, 3), (block.Kind, block.Number)),
            block => Assert.Equal((MarkdownBlockKind.NumberedItem, 4), (block.Kind, block.Number)));
        Assert.Equal("3. Third\n4. Fourth", document.ToPlainText().ReplaceLineEndings("\n"));
        Assert.Equal("<ol start=\"3\"><li>Third</li><li>Fourth</li></ol>", document.ToHtml());
    }

    [Fact]
    public void NestedAndLooseListContentIsPreservedInEveryOutput()
    {
        var nested = SanitisedMarkdownRenderer.Render("- Parent\n  - Child one\n  - Child two\n- Sibling");
        var loose = SanitisedMarkdownRenderer.Render("- First paragraph\n\n  second paragraph\n- Next");

        Assert.Equal("• Parent\n  • Child one\n  • Child two\n• Sibling", nested.ToPlainText().ReplaceLineEndings("\n"));
        Assert.Equal("<ul><li>Parent<ul><li>Child one</li><li>Child two</li></ul></li><li>Sibling</li></ul>", nested.ToHtml());
        Assert.Equal("• First paragraph\n\n  second paragraph\n• Next", loose.ToPlainText().ReplaceLineEndings("\n"));
        Assert.Equal("<ul><li><p>First paragraph</p><p>second paragraph</p></li><li><p>Next</p></li></ul>", loose.ToHtml());
    }

    [Fact]
    public void NestedListsPreserveInterleavedContentAndSeparateListStarts()
    {
        var interleaved = SanitisedMarkdownRenderer.Render("- Before\n\n  - Child\n\n  After");
        var separated = SanitisedMarkdownRenderer.Render("3. Third\n4. Fourth\n\n<div>discarded</div>\n\n7. Seventh\n8. Eighth");
        var separatedNested = SanitisedMarkdownRenderer.Render(
            "- Parent\n\n    - First\n\n    <div>discarded</div>\n\n    - Second");

        Assert.Equal("• Before\n  • Child\n\n  After", interleaved.ToPlainText().ReplaceLineEndings("\n"));
        Assert.Equal(
            "<ul><li><p>Before</p><ul><li>Child</li></ul><p>After</p></li></ul>",
            interleaved.ToHtml());
        Assert.Equal(
            "<ol start=\"3\"><li>Third</li><li>Fourth</li></ol><ol start=\"7\"><li>Seventh</li><li>Eighth</li></ol>",
            separated.ToHtml());
        Assert.Equal("3. Third\n4. Fourth\n\n7. Seventh\n8. Eighth", separated.ToPlainText().ReplaceLineEndings("\n"));
        Assert.Equal("• Parent\n  • First\n\n  • Second", separatedNested.ToPlainText().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void NestedOrderedListsKeepHierarchyAndVisibleNumbersInEveryOutput()
    {
        var document = SanitisedMarkdownRenderer.Render("3. Parent\n   1. Child\n4. Sibling");

        Assert.Equal("3. Parent\n  1. Child\n4. Sibling", document.ToPlainText().ReplaceLineEndings("\n"));
        Assert.Equal(
            "<ol start=\"3\"><li>Parent<ol><li>Child</li></ol></li><li>Sibling</li></ol>",
            document.ToHtml());
    }

    [Fact]
    public void FourLevelOrderedUnorderedAndMixedListsKeepTheirHierarchy()
    {
        var unordered = SanitisedMarkdownRenderer.Render("- One\n    - Two\n        - Three\n            - Four");
        var ordered = SanitisedMarkdownRenderer.Render("1. One\n    1. Two\n        1. Three\n            1. Four");
        var mixed = SanitisedMarkdownRenderer.Render("- One\n    1. Two\n        - Three\n            a. Four\n            b. Four again");

        Assert.Equal("• One\n  • Two\n    • Three\n      • Four", unordered.ToPlainText().ReplaceLineEndings("\n"));
        Assert.Equal("1. One\n  1. Two\n    1. Three\n      1. Four", ordered.ToPlainText().ReplaceLineEndings("\n"));
        Assert.Equal(
            "• One\n  1. Two\n    • Three\n      a. Four\n      b. Four again",
            mixed.ToPlainText().ReplaceLineEndings("\n"));
        Assert.Equal(
            "<ul><li>One<ol><li>Two<ul><li>Three<ol type=\"a\"><li>Four</li><li>Four again</li></ol></li></ul></li></ol></li></ul>",
            mixed.ToHtml());
    }

    [Fact]
    public void LowerAlphaListConvenienceDoesNotRewriteCode()
    {
        var fenced = SanitisedMarkdownRenderer.Render("```text\na. remains code\n```");
        var falseFenceCloser = SanitisedMarkdownRenderer.Render(
            "~~~~\na. first code line\n~~~~ trailing text\nb. second code line\n~~~~");
        var indented = SanitisedMarkdownRenderer.Render("    b. also remains code");
        var tabIndented = SanitisedMarkdownRenderer.Render("\tc. tab-indented code");
        var mixedTabIndented = SanitisedMarkdownRenderer.Render(" \td. mixed-tab-indented code");

        Assert.Equal("a. remains code", fenced.ToPlainText());
        Assert.Equal("<pre><code>a. remains code</code></pre>", fenced.ToHtml());
        Assert.Equal(
            "a. first code line\n~~~~ trailing text\nb. second code line",
            falseFenceCloser.ToPlainText().ReplaceLineEndings("\n"));
        Assert.Equal(
            "<pre><code>a. first code line\n~~~~ trailing text\nb. second code line</code></pre>",
            falseFenceCloser.ToHtml().ReplaceLineEndings("\n"));
        Assert.Equal("b. also remains code", indented.ToPlainText());
        Assert.Equal("<pre><code>b. also remains code</code></pre>", indented.ToHtml());
        Assert.Equal("c. tab-indented code", tabIndented.ToPlainText());
        Assert.Equal("<pre><code>c. tab-indented code</code></pre>", tabIndented.ToHtml());
        Assert.Equal("d. mixed-tab-indented code", mixedTabIndented.ToPlainText());
        Assert.Equal("<pre><code>d. mixed-tab-indented code</code></pre>", mixedTabIndented.ToHtml());
    }

    [AvaloniaFact]
    public void NativePreviewIndentsNestedListItems()
    {
        var preview = new MarkdownPreview
        {
            Document = SanitisedMarkdownRenderer.Render("- Parent\n    1. Child\n        - Grandchild\n            a. Great-grandchild"),
        };
        var window = new Window { Content = preview };
        window.Show();
        var parent = Assert.Single(preview.GetVisualDescendants().OfType<TextBlock>(), text => text.Inlines?.Text == "• Parent");
        var child = Assert.Single(preview.GetVisualDescendants().OfType<TextBlock>(), text => text.Inlines?.Text == "1. Child");
        var grandchild = Assert.Single(preview.GetVisualDescendants().OfType<TextBlock>(), text => text.Inlines?.Text == "• Grandchild");
        var greatGrandchild = Assert.Single(
            preview.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Inlines?.Text == "a. Great-grandchild");

        Assert.True(child.Margin.Left > parent.Margin.Left);
        Assert.True(grandchild.Margin.Left > child.Margin.Left);
        Assert.True(greatGrandchild.Margin.Left > grandchild.Margin.Left);
        window.Close();
    }

    [AvaloniaFact]
    public void NativePreviewRendersThematicBreakInsideAListItem()
    {
        var preview = new MarkdownPreview { Document = SanitisedMarkdownRenderer.Render("- Before\n\n    ***\n\n    After") };
        var window = new Window { Content = preview };
        window.Show();
        var content = preview.GetVisualDescendants().ToArray();

        Assert.Contains(content.OfType<TextBlock>(), text => text.Inlines?.Text == "• Before");
        Assert.Contains(content.OfType<Border>(), border => border.Height == 1 && border.Margin.Left > 0);
        Assert.Contains(content.OfType<TextBlock>(), text => text.Inlines?.Text == "After");
        window.Close();
    }

    [AvaloniaFact]
    public void NativePreviewGivesMarkdownBlocksMoreSpaceThanConsecutiveListItems()
    {
        var source = "# Heading\n\nParagraph\n\n- First\n- Second\n\n3. Third\n4. Fourth\n\n![Sketch](https://example.test/sketch.png)";
        var preview = new MarkdownPreview { Document = SanitisedMarkdownRenderer.Render(source) };
        var window = new Window { Content = preview };
        window.Show();
        var text = preview.GetVisualDescendants().OfType<TextBlock>().ToArray();
        var paragraph = Assert.Single(text, item => item.Inlines?.Text == "Paragraph");
        var first = Assert.Single(text, item => item.Inlines?.Text == "• First");
        var second = Assert.Single(text, item => item.Inlines?.Text == "• Second");
        var third = Assert.Single(text, item => item.Inlines?.Text == "3. Third");
        var fourth = Assert.Single(text, item => item.Inlines?.Text == "4. Fourth");
        var image = Assert.Single(text, item => item.Inlines?.Text == "[Remote image not loaded: Sketch]");

        Assert.True(paragraph.Margin.Top > second.Margin.Top);
        Assert.True(first.Margin.Top > second.Margin.Top);
        Assert.True(third.Margin.Top > fourth.Margin.Top);
        Assert.True(image.Margin.Top > fourth.Margin.Top);
        window.Close();
    }

    [AvaloniaFact]
    public void NativePreviewPreservesSoftLineBreaksInsideAParagraph()
    {
        var document = SanitisedMarkdownRenderer.Render("First line\nSecond line\n\nThird paragraph");
        var preview = new MarkdownPreview { Document = document };
        var window = new Window { Content = preview };
        window.Show();
        var text = preview.GetVisualDescendants().OfType<TextBlock>().ToArray();

        Assert.Equal("<p>First line<br />Second line</p><p>Third paragraph</p>", document.ToHtml());
        Assert.Contains(text, item => item.Inlines?.Text == "First line\nSecond line");
        Assert.True(Assert.Single(text, item => item.Inlines?.Text == "Third paragraph").Margin.Top > 0);
        window.Close();
    }

    [AvaloniaFact]
    public void NativePreviewExposesOnlyApprovedExternalLinksAsKeyboardFocusableHyperlinks()
    {
        var document = SanitisedMarkdownRenderer.Render(
            "[Docs](https://example.test/docs) [Email](mailto:person@example.test) [Section](#details) [Unsafe](javascript:alert(1))");
        var preview = new MarkdownPreview { Document = document };
        var window = new Window { Content = preview };
        window.Show();
        var links = preview.GetVisualDescendants().OfType<HyperlinkButton>().ToArray();

        Assert.Collection(
            links,
            link => Assert.Equal("https://example.test/docs", link.NavigateUri?.AbsoluteUri),
            link => Assert.Equal("mailto:person@example.test", link.NavigateUri?.AbsoluteUri));
        Assert.All(links, link =>
        {
            Assert.True(link.Focusable);
            Assert.StartsWith("Open ", AutomationProperties.GetName(link), StringComparison.Ordinal);
        });
        var inertText = string.Concat(
            preview.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text ?? text.Inlines?.Text));
        Assert.Contains("Section", inertText, StringComparison.Ordinal);
        Assert.Contains("Unsafe", inertText, StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void CompletingALinkWhilePreviewIsAttachedCanRenderWithoutInvalidatingTheVisualTree()
    {
        var preview = new MarkdownPreview
        {
            Width = 520,
            Document = SanitisedMarkdownRenderer.Render("Read [the plan](https://example.test/plan"),
        };
        var window = new Window { Width = 600, Height = 240, Content = preview };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        preview.Document = SanitisedMarkdownRenderer.Render("Read [the plan](https://example.test/plan)");
        using var bitmap = new RenderTargetBitmap(new PixelSize(600, 240));
        bitmap.Render(preview);

        Assert.Single(preview.GetVisualDescendants().OfType<HyperlinkButton>());
        Assert.DoesNotContain(
            preview.GetVisualDescendants().OfType<TextBlock>().SelectMany(text => text.Inlines ?? []),
            inline => inline is InlineUIContainer);
        window.Close();
    }

    [AvaloniaFact]
    public void NonBreakingSpaceEntityCreatesAnIntentionalSpacerParagraph()
    {
        var document = SanitisedMarkdownRenderer.Render("First\n\n&nbsp;\n\nSecond");
        var preview = new MarkdownPreview { Document = document };
        var window = new Window { Content = preview };
        window.Show();

        Assert.Equal("\u00a0", document.Blocks[1].Inlines.Single().Text);
        Assert.Equal("First\n\u00a0\nSecond", document.ToPlainText().ReplaceLineEndings("\n"));
        Assert.Contains(preview.GetVisualDescendants().OfType<TextBlock>(), text => text.Inlines?.Text == "\u00a0");
        window.Close();
    }

    [Fact]
    public void ExecutableHtmlUnsafeUrisAndRemoteImagesBecomeInertContent()
    {
        const string source = "[safe](https://example.test) [unsafe](javascript:alert(1)) ![tracker](https://tracker.test/pixel.png) ![network path](//tracker.test/pixel.png)\n\n<script>alert('x')</script>\n\n<object data=\"https://example.test\"></object>\n\n<div onclick=\"run()\">hidden</div>";

        var document = SanitisedMarkdownRenderer.Render(source);
        var html = document.ToHtml();

        Assert.Contains("href=\"https://example.test/\"", html, StringComparison.Ordinal);
        Assert.Contains("unsafe", html, StringComparison.Ordinal);
        Assert.Contains("[Remote image not loaded: tracker]", html, StringComparison.Ordinal);
        Assert.Contains("[Remote image not loaded: network path]", html, StringComparison.Ordinal);
        Assert.DoesNotContain("javascript", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<object", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tracker.test", html, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("safe unsafe [Remote image not loaded: tracker] [Remote image not loaded: network path]", document.ToPlainText());
    }

    [Theory]
    [InlineData("https://example.test/path", true)]
    [InlineData("http://example.test/path", true)]
    [InlineData("mailto:person@example.test", true)]
    [InlineData("#details", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:text/html,payload", false)]
    [InlineData("file:///private/value", false)]
    [InlineData("custom:payload", false)]
    public void OnlyApprovedLinkSchemesRemainLinks(string destination, bool expectedLink)
    {
        var document = SanitisedMarkdownRenderer.Render($"[label]({destination})");
        var inline = Assert.Single(Assert.Single(document.Blocks).Inlines);

        Assert.Equal(expectedLink, inline.Link is not null);
        Assert.Equal("label", document.ToPlainText());
        Assert.Equal(expectedLink, document.ToHtml().Contains("<a href=", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("manual or imported text")]
    public void EverySourceUsesTheSameDeterministicSanitisationPath(string source)
    {
        var first = SanitisedMarkdownRenderer.Render(source);
        var second = SanitisedMarkdownRenderer.Render(source);

        Assert.Equal(first.ToHtml(), second.ToHtml());
        Assert.Equal(first.ToPlainText(), second.ToPlainText());
    }

    [Theory]
    [InlineData((int)ClipboardPlatform.Linux)]
    [InlineData((int)ClipboardPlatform.MacOS)]
    [InlineData((int)ClipboardPlatform.Windows)]
    public void ClipboardTransferPublishesMatchingHtmlAndPlainText(int platformValue)
    {
        var platform = (ClipboardPlatform)platformValue;
        var document = SanitisedMarkdownRenderer.Render("**Safe** text");

        using var transfer = MarkdownClipboardTransfer.Create(document, platform);

        Assert.Equal("Safe text", transfer.TryGetText());
        var html = Assert.IsType<string>(transfer.TryGetValue(MarkdownClipboardTransfer.HtmlFormat(platform)));
        Assert.Contains("<strong>Safe</strong> text", html, StringComparison.Ordinal);
        if (platform == ClipboardPlatform.Windows)
        {
            Assert.StartsWith("Version:0.9\r\n", html, StringComparison.Ordinal);
            Assert.Contains("<!--StartFragment-->", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WindowsClipboardOffsetsIdentifyUtf8HtmlAndUnicodeFragmentExactly()
    {
        const string fragment = "<p><strong>Café 🌍</strong></p>";
        var payload = MarkdownClipboardTransfer.HtmlPayload(fragment, ClipboardPlatform.Windows);
        var bytes = Encoding.UTF8.GetBytes(payload);
        var offsets = payload.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Take(4)
            .Select(line => int.Parse(line.AsSpan(line.IndexOf(':') + 1), System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();

        Assert.Equal(
            $"<html><body><!--StartFragment-->{fragment}<!--EndFragment--></body></html>",
            Encoding.UTF8.GetString(bytes[offsets[0]..offsets[1]]));
        Assert.Equal(fragment, Encoding.UTF8.GetString(bytes[offsets[2]..offsets[3]]));
        Assert.Equal(bytes.Length, offsets[1]);
    }

    [Fact]
    public void ProjectAndTaskDraftsShareTheLiveRenderModelAndPreviewState()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Project", "**Project**", "home", null);
        var task = work.CreateTask(project.Id, "Task");
        work.UpdateTask(task.Id, task.Title, "*Task*", null, null);
        var model = new ProjectCaptureViewModel(work);

        model.SelectProject(project.Id);
        Assert.True(model.ShowMarkdownPreview);
        Assert.Equal("<p><strong>Project</strong></p>", model.RenderedDescription.ToHtml());
        model.EditMarkdownCommand.Execute(null);
        Assert.True(model.ShowMarkdownEditor);
        model.Description = "[Updated](https://example.test)";
        Assert.Equal("Updated", model.RenderedDescription.ToPlainText());
        model.FinishMarkdownEditing();
        Assert.True(model.ShowMarkdownPreview);

        model.Cancel();
        model.SelectTask(task.Id);
        Assert.Equal("<p><em>Task</em></p>", model.RenderedDescription.ToHtml());
        Assert.True(model.ShowMarkdownPreview);
    }

    [AvaloniaFact]
    public void PreviewActivatesEditorAndLosingFocusRendersBeforeCopyingBothRepresentations()
    {
        using var session = new RecoveryViewModelTests.StubWorkspaceSession(new RecoveryViewModelTests.StubWorkspaceRecovery());
        var work = Assert.IsType<MemoryWorkspaceWork>(session.Work);
        var project = work.CreateProject("Project", "**Rendered**", "home", null);
        var clipboard = new RecordingMarkdownClipboard();
        var window = new MainWindow(session, clipboard);
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.PrimaryNavigation.Single(item => item.Title == "Projects").SelectCommand.Execute(null);
        shell.Work!.SelectProject(project.Id);
        Dispatcher.UIThread.RunJobs();

        var preview = window.FindControl<MarkdownPreviewSurface>("MarkdownPreviewButton")!;
        var editor = window.FindControl<TextBox>("MarkdownSource")!;
        Assert.True(preview.IsEffectivelyVisible);
        Assert.False(editor.IsEffectivelyVisible);
        Assert.Equal("Rendered description: Rendered. Activate to edit Markdown.", AutomationProperties.GetName(preview));
        var previewPeer = ControlAutomationPeer.CreatePeerForElement(preview);
        Assert.Equal(AutomationControlType.Button, previewPeer.GetAutomationControlType());
        Assert.Equal("Rendered description: Rendered. Activate to edit Markdown.", previewPeer.GetName());
        Assert.True(previewPeer.IsEnabled());
        Assert.True(previewPeer.IsKeyboardFocusable());
        Assert.IsAssignableFrom<IInvokeProvider>(previewPeer).Invoke();
        Dispatcher.UIThread.RunJobs();
        Assert.True(editor.IsEffectivelyVisible);
        Assert.True(editor.IsFocused);

        editor.Text = "## Changed";
        var copy = NamedButton(window, "Copy rendered description");
        PressTab(window, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.True(preview.IsEffectivelyVisible);
        Assert.False(editor.IsEffectivelyVisible);
        Assert.True(copy.IsFocused);
        Assert.Contains(preview.GetVisualDescendants().OfType<TextBlock>(), text => text.Inlines?.Text == "Changed");
        Assert.Equal("Rendered description: Changed. Activate to edit Markdown.", AutomationProperties.GetName(preview));

        Activate(window, copy);
        Assert.NotNull(clipboard.Document);
        Assert.Equal("<h2>Changed</h2>", clipboard.Document.ToHtml());
        Assert.Equal("Changed", clipboard.Document.ToPlainText());
        Assert.Equal("Rendered description copied as rich text and plain text.", shell.Work.Message);
        var status = Assert.IsType<TextBlock>(window.FindControl<TextBlock>("TopBarStatusMessage"));
        Assert.Equal(shell.Work.Message, status.Text);
        Assert.True(status.IsEffectivelyVisible);
        Assert.Contains(window.FindControl<Border>("TopBar"), status.GetVisualAncestors());
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));
        window.Close();
    }

    [AvaloniaFact]
    public void LinkActivationStaysInPreviewInsteadOfTriggeringTheEditSurface()
    {
        using var session = new RecoveryViewModelTests.StubWorkspaceSession(new RecoveryViewModelTests.StubWorkspaceRecovery());
        var work = Assert.IsType<MemoryWorkspaceWork>(session.Work);
        var project = work.CreateProject("Project", "Read [the plan](https://example.test/plan).", "home", null);
        var window = new MainWindow(session);
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.PrimaryNavigation.Single(item => item.Title == "Projects").SelectCommand.Execute(null);
        shell.Work!.SelectProject(project.Id);
        Dispatcher.UIThread.RunJobs();

        var preview = window.FindControl<MarkdownPreviewSurface>("MarkdownPreviewButton")!;
        var editor = window.FindControl<TextBox>("MarkdownSource")!;
        var link = Assert.Single(preview.GetVisualDescendants().OfType<HyperlinkButton>());
        var linkActivations = 0;
        link.Click += (_, _) => linkActivations++;
        var linkPeer = ControlAutomationPeer.CreatePeerForElement(link);
        Assert.Equal(AutomationControlType.Hyperlink, linkPeer.GetAutomationControlType());
        Assert.Equal("Open the plan, https://example.test/plan", linkPeer.GetName());

        Assert.True(link.Focus());
        PressKey(window, PhysicalKey.Enter, RawInputModifiers.None);
        Assert.Equal(1, linkActivations);
        Assert.True(preview.IsEffectivelyVisible);
        Assert.False(editor.IsEffectivelyVisible);

        var point = CentreInWindow(link, window);
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, linkActivations);
        Assert.True(preview.IsEffectivelyVisible);
        Assert.False(editor.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void PreviewPointerActivationRequiresAPressAndReleaseInsideTheSurface()
    {
        var preview = new MarkdownPreviewSurface
        {
            Width = 120,
            Height = 80,
            Margin = new Thickness(50, 40, 0, 0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Background = Brushes.Transparent,
            Content = new TextBlock { Text = "Preview" },
        };
        var activations = 0;
        preview.Activated += (_, _) => activations++;
        var window = new Window
        {
            Width = 320,
            Height = 240,
            Content = new Grid { Children = { preview } },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var inside = CentreInWindow(preview, window);
        var outside = new Point(260, 180);

        window.MouseDown(outside, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseUp(inside, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, activations);

        window.MouseDown(inside, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseUp(outside, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, activations);

        window.MouseDown(inside, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseUp(inside, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, activations);
        window.Close();
    }

    [AvaloniaFact]
    public void PreviewPointerActivationWorksAtTheTopOfAnOffsetSurface()
    {
        var preview = new MarkdownPreviewSurface
        {
            Width = 120,
            Height = 80,
            Margin = new Thickness(50, 40, 0, 0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Background = Brushes.Transparent,
            Content = new TextBlock { Text = "Preview" },
        };
        var activations = 0;
        preview.Activated += (_, _) => activations++;
        var window = new Window
        {
            Width = 320,
            Height = 240,
            Content = new Grid { Children = { preview } },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var upperInside = preview.TranslatePoint(new Point(10, 10), window);
        Assert.True(upperInside.HasValue);

        window.MouseDown(upperInside.Value, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseUp(upperInside.Value, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, activations);
        window.Close();
    }

    [AvaloniaFact]
    public void LosingMarkdownEditorFocusWithControlTabOrPointerKeepsTheIntendedDateControl()
    {
        using var session = new RecoveryViewModelTests.StubWorkspaceSession(new RecoveryViewModelTests.StubWorkspaceRecovery());
        var work = Assert.IsType<MemoryWorkspaceWork>(session.Work);
        var project = work.CreateProject("Project", "Description", "home", new DateOnly(2026, 10, 12));
        var window = new MainWindow(session);
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.PrimaryNavigation.Single(item => item.Title == "Projects").SelectCommand.Execute(null);
        shell.Work!.SelectProject(project.Id);
        Dispatcher.UIThread.RunJobs();

        var preview = window.FindControl<MarkdownPreviewSurface>("MarkdownPreviewButton")!;
        var editor = window.FindControl<TextBox>("MarkdownSource")!;
        var date = window.FindControl<CalendarDatePicker>("DraftDate")!;
        ActivatePreview(window, preview);
        PressTab(window, RawInputModifiers.Control);
        Assert.True(preview.IsEffectivelyVisible);
        Assert.False(editor.IsEffectivelyVisible);
        Assert.True(date.IsKeyboardFocusWithin);
        window.Close();
    }

    [AvaloniaFact]
    public void ClickingDateWhileEditingMarkdownOpensCalendarAndReturnsPreview()
    {
        using var session = new RecoveryViewModelTests.StubWorkspaceSession(new RecoveryViewModelTests.StubWorkspaceRecovery());
        var work = Assert.IsType<MemoryWorkspaceWork>(session.Work);
        var project = work.CreateProject("Project", "Description", "home", new DateOnly(2026, 10, 12));
        var window = new MainWindow(session);
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.PrimaryNavigation.Single(item => item.Title == "Projects").SelectCommand.Execute(null);
        shell.Work!.SelectProject(project.Id);
        Dispatcher.UIThread.RunJobs();

        var preview = window.FindControl<MarkdownPreviewSurface>("MarkdownPreviewButton")!;
        var editor = window.FindControl<TextBox>("MarkdownSource")!;
        var date = window.FindControl<CalendarDatePicker>("DraftDate")!;
        ActivatePreview(window, preview);
        var calendarButton = Assert.Single(date.GetVisualDescendants().OfType<Button>());
        var clickPoint = CentreInWindow(calendarButton, window);
        window.MouseDown(clickPoint, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseUp(clickPoint, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.True(preview.IsEffectivelyVisible);
        Assert.False(editor.IsEffectivelyVisible);
        Assert.True(date.IsKeyboardFocusWithin);
        Assert.True(
            date.IsDropDownOpen,
            $"editor={editor.Bounds}; preview={preview.Bounds}; date={date.Bounds}; button={calendarButton.Bounds}");
        window.Close();
    }

    [AvaloniaFact]
    public void MarkdownEditorIndentsWithTabAndContinuesBulletAndOrderedMarkersWithEnter()
    {
        using var session = new RecoveryViewModelTests.StubWorkspaceSession(new RecoveryViewModelTests.StubWorkspaceRecovery());
        var work = Assert.IsType<MemoryWorkspaceWork>(session.Work);
        var project = work.CreateProject("Project", "- Parent", "home", null);
        var window = new MainWindow(session);
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.PrimaryNavigation.Single(item => item.Title == "Projects").SelectCommand.Execute(null);
        shell.Work!.SelectProject(project.Id);
        Dispatcher.UIThread.RunJobs();

        var preview = window.FindControl<MarkdownPreviewSurface>("MarkdownPreviewButton")!;
        var editor = window.FindControl<TextBox>("MarkdownSource")!;
        ActivatePreview(window, preview);

        editor.Text = "- Parent";
        editor.CaretIndex = editor.Text.Length;
        PressKey(window, PhysicalKey.Enter, RawInputModifiers.None);
        Assert.Equal("- Parent\n- ", editor.Text);
        PressKey(window, PhysicalKey.Z, RawInputModifiers.Control);
        Assert.Equal("- Parent", editor.Text);

        editor.Text = "- Parent\n- Child";
        editor.CaretIndex = editor.Text.Length;
        PressTab(window, RawInputModifiers.None);
        Assert.Equal("- Parent\n    - Child", editor.Text);
        Assert.True(editor.IsEffectivelyVisible);
        Assert.True(editor.IsFocused);
        PressKey(window, PhysicalKey.Z, RawInputModifiers.Control);
        Assert.Equal("- Parent\n- Child", editor.Text);
        editor.CaretIndex = editor.Text.Length;
        PressTab(window, RawInputModifiers.None);
        PressTab(window, RawInputModifiers.Shift);
        Assert.Equal("- Parent\n- Child", editor.Text);

        editor.Text = "- Parent\n    * Nested";
        editor.CaretIndex = editor.Text.Length;
        PressKey(window, PhysicalKey.Enter, RawInputModifiers.None);
        Assert.Equal("- Parent\n    * Nested\n    * ", editor.Text);

        editor.Text = "   3. Third";
        editor.CaretIndex = editor.Text.Length;
        PressKey(window, PhysicalKey.Enter, RawInputModifiers.None);
        Assert.Equal("   3. Third\n   4. ", editor.Text);

        editor.Text = "7. Parent\n    8) Eighth";
        editor.CaretIndex = editor.Text.Length;
        PressKey(window, PhysicalKey.Enter, RawInputModifiers.None);
        Assert.Equal("7. Parent\n    8) Eighth\n    9) ", editor.Text);

        editor.Text = "1. Parent\n    a. Alpha";
        editor.CaretIndex = editor.Text.Length;
        PressKey(window, PhysicalKey.Enter, RawInputModifiers.None);
        Assert.Equal("1. Parent\n    a. Alpha\n    b. ", editor.Text);

        editor.Text = "````\n```\n- code";
        editor.CaretIndex = editor.Text.Length;
        PressKey(window, PhysicalKey.Enter, RawInputModifiers.None);
        Assert.Equal($"````\n```\n- code{Environment.NewLine}", editor.Text);

        editor.Text = "    - indented code";
        editor.CaretIndex = editor.Text.Length;
        PressKey(window, PhysicalKey.Enter, RawInputModifiers.None);
        Assert.Equal($"    - indented code{Environment.NewLine}", editor.Text);

        editor.Text = "1. Item\n\n        - nested code";
        editor.CaretIndex = editor.Text.Length;
        PressKey(window, PhysicalKey.Enter, RawInputModifiers.None);
        Assert.Equal($"1. Item\n\n        - nested code{Environment.NewLine}", editor.Text);

        editor.Text = "1. Item\n   continuation\n\n    - Child";
        editor.CaretIndex = editor.Text.Length;
        PressKey(window, PhysicalKey.Enter, RawInputModifiers.None);
        Assert.Equal("1. Item\n   continuation\n\n    - Child\n    - ", editor.Text);

        editor.Text = "```bad`info\n- Item";
        editor.CaretIndex = editor.Text.Length;
        PressKey(window, PhysicalKey.Enter, RawInputModifiers.None);
        Assert.Equal("```bad`info\n- Item\n- ", editor.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void MarkdownEditorTurnsIndentedOrderedItemsIntoUnorderedSubpoints()
    {
        using var session = new RecoveryViewModelTests.StubWorkspaceSession(new RecoveryViewModelTests.StubWorkspaceRecovery());
        var work = Assert.IsType<MemoryWorkspaceWork>(session.Work);
        var project = work.CreateProject("Project", "1. Parent\n2. Child", "home", null);
        var window = new MainWindow(session);
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.PrimaryNavigation.Single(item => item.Title == "Projects").SelectCommand.Execute(null);
        shell.Work!.SelectProject(project.Id);
        Dispatcher.UIThread.RunJobs();

        var preview = window.FindControl<MarkdownPreviewSurface>("MarkdownPreviewButton")!;
        var editor = window.FindControl<TextBox>("MarkdownSource")!;
        ActivatePreview(window, preview);

        editor.CaretIndex = editor.Text!.Length;
        PressTab(window, RawInputModifiers.None);
        Assert.Equal("1. Parent\n    - Child", editor.Text);
        PressKey(window, PhysicalKey.Enter, RawInputModifiers.None);
        Assert.Equal("1. Parent\n    - Child\n    - ", editor.Text);

        editor.Text = "1. Parent\n2) Child";
        editor.CaretIndex = editor.Text.Length;
        PressTab(window, RawInputModifiers.None);
        Assert.Equal("1. Parent\n    - Child", editor.Text);

        editor.Text = "1. Parent\n    a. Detail";
        editor.CaretIndex = editor.Text.Length;
        PressTab(window, RawInputModifiers.None);
        Assert.Equal("1. Parent\n        - Detail", editor.Text);

        editor.Text = "1. Parent\n    - Child";
        editor.CaretIndex = editor.Text.Length;
        PressTab(window, RawInputModifiers.Shift);
        Assert.Equal("1. Parent\n- Child", editor.Text);

        editor.Text = "1. Parent\nDescription";
        editor.CaretIndex = editor.Text.Length;
        PressTab(window, RawInputModifiers.None);
        Assert.Equal("1. Parent\n    Description", editor.Text);

        editor.Text = "```text\n2. remains code\n```";
        editor.CaretIndex = editor.Text.IndexOf(" code", StringComparison.Ordinal);
        PressTab(window, RawInputModifiers.None);
        Assert.Equal("```text\n    2. remains code\n```", editor.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void ClickingNonFocusableInspectorSpaceOutsideMarkdownEditorReturnsToPreview()
    {
        using var session = new RecoveryViewModelTests.StubWorkspaceSession(new RecoveryViewModelTests.StubWorkspaceRecovery());
        var work = Assert.IsType<MemoryWorkspaceWork>(session.Work);
        var project = work.CreateProject("Project", "Description", "home", null);
        var window = new MainWindow(session);
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.PrimaryNavigation.Single(item => item.Title == "Projects").SelectCommand.Execute(null);
        shell.Work!.SelectProject(project.Id);
        Dispatcher.UIThread.RunJobs();

        var preview = window.FindControl<MarkdownPreviewSurface>("MarkdownPreviewButton")!;
        var editor = window.FindControl<TextBox>("MarkdownSource")!;
        ActivatePreview(window, preview);
        var label = Assert.Single(
            window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "DESCRIPTION · MARKDOWN");
        var point = CentreInWindow(label, window);
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.True(preview.IsEffectivelyVisible);
        Assert.False(editor.IsEffectivelyVisible);
        window.Close();
    }

    private static Button NamedButton(Window window, string name) => Assert.Single(
        window.GetVisualDescendants().OfType<Button>(), button => AutomationProperties.GetName(button) == name);

    private static void Activate(Window window, Button button)
    {
        Assert.True(button.Focus());
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    private static void ActivatePreview(Window window, MarkdownPreviewSurface preview)
    {
        Assert.True(preview.Focus());
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    private static void PressTab(Window window, RawInputModifiers modifiers)
    {
        PressKey(window, PhysicalKey.Tab, modifiers);
    }

    private static void PressKey(Window window, PhysicalKey key, RawInputModifiers modifiers)
    {
        window.KeyPressQwerty(key, modifiers);
        window.KeyReleaseQwerty(key, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    private static Point CentreInWindow(Control control, Window window)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);
        Assert.True(point.HasValue);
        return point.Value;
    }

    private sealed class RecordingMarkdownClipboard : IMarkdownClipboard
    {
        public SanitisedMarkdownDocument? Document { get; private set; }

        public ValueTask WriteAsync(SanitisedMarkdownDocument document)
        {
            Document = document;
            return ValueTask.CompletedTask;
        }
    }
}
