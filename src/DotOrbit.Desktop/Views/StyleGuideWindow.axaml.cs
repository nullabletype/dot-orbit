using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotOrbit.Markdown;
using DotOrbit.Desktop.Markdown;

namespace DotOrbit.Desktop.Views;

public sealed partial class StyleGuideWindow : Window
{
    private bool _markdownPointerStartedOutside;

    public StyleGuideWindow()
    {
        AvaloniaXamlLoader.Load(this);
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        var source = this.FindControl<TextBox>("ReferenceMarkdownSource")!;
        source.AddHandler(
            KeyDownEvent,
            OnReferenceMarkdownSourceKeyDown,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        var document = SanitisedMarkdownRenderer.Render(source.Text ?? string.Empty);
        this.FindControl<MarkdownPreview>("ReferenceMarkdownPreview")!.Document = document;
        AutomationProperties.SetName(
            this.FindControl<MarkdownPreviewSurface>("ReferenceMarkdownPreviewButton")!,
            $"Rendered description: {document.ToPlainText().ReplaceLineEndings(" ")}. Activate to edit Markdown.");
    }

    private static void OnReferenceCompletionStateChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string subject } toggle) return;
        AutomationProperties.SetName(toggle, $"{(toggle.IsChecked == true ? "Reopen" : "Complete")} {subject}");
    }

    private static void OnReferenceDisclosureStateChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string subject } toggle) return;
        AutomationProperties.SetName(toggle, $"{(toggle.IsChecked == true ? "Collapse" : "Expand")} {subject}");
    }

    private void OnReferenceMarkdownEdit(object? sender, RoutedEventArgs e)
    {
        this.FindControl<MarkdownPreviewSurface>("ReferenceMarkdownPreviewButton")!.IsVisible = false;
        var source = this.FindControl<TextBox>("ReferenceMarkdownSource")!;
        source.IsVisible = true;
        source.Focus();
    }

    private void OnReferenceMarkdownSourceLostFocus(object? sender, RoutedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (this.FindControl<TextBox>("ReferenceMarkdownSource") is { IsKeyboardFocusWithin: false, IsEffectivelyVisible: true })
                ShowReferenceMarkdownPreview();
        }, DispatcherPriority.ApplicationIdle);
    }

    private void OnReferenceMarkdownSourceKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is TextBox editor) MarkdownSourceEditor.TryHandleKeyDown(editor, e);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var source = this.FindControl<TextBox>("ReferenceMarkdownSource")!;
        var hit = this.InputHitTest(e.GetPosition(this)) as Control;
        _markdownPointerStartedOutside = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && source.IsEffectivelyVisible
            && hit != source
            && hit?.GetVisualAncestors().Contains(source) != true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var showPreview = _markdownPointerStartedOutside
            && e.InitialPressMouseButton == MouseButton.Left;
        _markdownPointerStartedOutside = false;
        if (showPreview && this.FindControl<TextBox>("ReferenceMarkdownSource")!.IsEffectivelyVisible)
            ShowReferenceMarkdownPreview();
    }

    private void ShowReferenceMarkdownPreview()
    {
        var source = this.FindControl<TextBox>("ReferenceMarkdownSource")!;
        var document = SanitisedMarkdownRenderer.Render(source.Text ?? string.Empty);
        this.FindControl<MarkdownPreview>("ReferenceMarkdownPreview")!.Document = document;
        AutomationProperties.SetName(
            this.FindControl<MarkdownPreviewSurface>("ReferenceMarkdownPreviewButton")!,
            $"Rendered description: {document.ToPlainText().ReplaceLineEndings(" ")}. Activate to edit Markdown.");
        source.IsVisible = false;
        var preview = this.FindControl<MarkdownPreviewSurface>("ReferenceMarkdownPreviewButton")!;
        preview.IsVisible = true;
    }
}
