using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotOrbit.Desktop.Views;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class StyleGuideWindowTests
{
    [AvaloniaFact]
    public void ReferenceSurfaceUsesProductionRecipesAndSyntheticAccessibleStates()
    {
        var window = new StyleGuideWindow();
        window.Show();

        Assert.Equal("dot-orbit component reference", AutomationProperties.GetName(window));
        var selectedNavigation = window.FindControl<RadioButton>("ReferenceSelectedNavigation")!;
        var disabledNavigation = window.FindControl<RadioButton>("ReferenceDisabledNavigation")!;
        Assert.True(selectedNavigation.IsChecked);
        Assert.Equal("Reference Projects navigation selected", AutomationProperties.GetName(selectedNavigation));
        var navigationContent = Assert.Single(
            selectedNavigation.GetVisualDescendants().OfType<ContentPresenter>(),
            presenter => presenter.Name == "NavigationContent");
        Assert.Equal(new Thickness(7, 0, 0, 0), navigationContent.Margin);
        Assert.False(disabledNavigation.IsEnabled);
        Assert.Equal("Reference Completed navigation disabled", AutomationProperties.GetName(disabledNavigation));
        var panels = window.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("list-panel"))
            .ToArray();
        Assert.True(panels.Length >= 4);
        Assert.All(panels, panel => Assert.Equal(new Thickness(10), panel.Padding));

        var peer = window.FindControl<Border>("ReferencePeerRow")!;
        var last = window.FindControl<Border>("ReferenceLastRow")!;
        Assert.Contains("interactive-row", peer.Classes);
        Assert.Equal(new Thickness(0, 0, 0, 1), peer.BorderThickness);
        Assert.Contains("last", last.Classes);
        Assert.Equal(new Thickness(0, 0, 0, 2), last.BorderThickness);
        Assert.Equal(Color.Parse("#FF4FA3"), Solid(last.BorderBrush).Color);

        var handle = window.FindControl<Button>("ReferenceDragHandle")!;
        Assert.Equal("Move reference task", AutomationProperties.GetName(handle));
        Assert.Equal(30, handle.Bounds.Width);
        Assert.Equal(30, handle.Bounds.Height);
        var dots = handle.GetVisualDescendants().OfType<Ellipse>().ToArray();
        Assert.Equal(6, dots.Length);
        Assert.All(dots, dot =>
        {
            Assert.Equal(2, dot.Bounds.Width);
            Assert.Equal(2, dot.Bounds.Height);
        });

        var uncheckedCompletion = window.FindControl<ToggleButton>("ReferenceUncheckedCompletion")!;
        var checkedCompletion = window.FindControl<ToggleButton>("ReferenceCheckedCompletion")!;
        Assert.Equal("Complete reference task", AutomationProperties.GetName(uncheckedCompletion));
        Assert.Equal("Reopen reference task", AutomationProperties.GetName(checkedCompletion));
        Assert.False(uncheckedCompletion.IsChecked);
        Assert.True(checkedCompletion.IsChecked);
        AssertCompletionGeometry(uncheckedCompletion, tickVisible: false);
        AssertCompletionGeometry(checkedCompletion, tickVisible: true);
        uncheckedCompletion.IsChecked = true;
        checkedCompletion.IsChecked = false;
        Assert.Equal("Reopen reference task", AutomationProperties.GetName(uncheckedCompletion));
        Assert.Equal("Complete reference task", AutomationProperties.GetName(checkedCompletion));

        var todayUnchecked = window.FindControl<ToggleButton>("ReferenceTodayUnchecked")!;
        var todayChecked = window.FindControl<ToggleButton>("ReferenceTodayChecked")!;
        Assert.Equal("Add reference task to Today", AutomationProperties.GetName(todayUnchecked));
        Assert.Equal("Remove reference task from Today", AutomationProperties.GetName(todayChecked));
        Assert.False(todayUnchecked.IsChecked);
        Assert.True(todayChecked.IsChecked);
        Assert.Equal(30, todayUnchecked.Bounds.Width);
        Assert.Equal(30, todayUnchecked.Bounds.Height);
        AssertTodayStarGeometry(todayUnchecked, filled: false);
        AssertTodayStarGeometry(todayChecked, filled: true);

        var disclosure = window.FindControl<ToggleButton>("ReferenceDisclosure")!;
        Assert.True(disclosure.IsChecked);
        Assert.Equal("Collapse reference details", AutomationProperties.GetName(disclosure));
        var expandedGlyph = Assert.Single(disclosure.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>(),
            path => path.Name == "DisclosureExpanded");
        var collapsedGlyph = Assert.Single(disclosure.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>(),
            path => path.Name == "DisclosureCollapsed");
        Assert.True(expandedGlyph.IsVisible);
        Assert.False(collapsedGlyph.IsVisible);
        var disclosureCentre = CentreInWindow(disclosure, window);
        var glyphCentre = CentreInWindow(expandedGlyph, window);
        Assert.InRange(Math.Abs(disclosureCentre.X - glyphCentre.X), 0, 0.5);
        Assert.InRange(Math.Abs(disclosureCentre.Y - glyphCentre.Y), 0, 0.5);
        disclosure.IsChecked = false;
        Assert.Equal("Expand reference details", AutomationProperties.GetName(disclosure));
        Assert.False(expandedGlyph.IsVisible);
        Assert.True(collapsedGlyph.IsVisible);
        var invalidField = window.FindControl<TextBox>("ReferenceInvalidField")!;
        Assert.Equal("Reference invalid field", AutomationProperties.GetName(invalidField));
        Assert.Equal("Required value. Choose a category before saving.", AutomationProperties.GetHelpText(invalidField));
        Assert.Equal(Color.Parse("#FF7A90"), Solid(invalidField.BorderBrush).Color);
        var date = Assert.Single(window.GetVisualDescendants().OfType<CalendarDatePicker>(),
            picker => AutomationProperties.GetName(picker) == "Reference date");
        Assert.Equal(new DateTime(2026, 10, 2), date.SelectedDate?.Date);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Classes.Contains("validation-message") && text.Text!.StartsWith("Required value", StringComparison.Ordinal));
        var markdownPreview = window.FindControl<MarkdownPreviewSurface>("ReferenceMarkdownPreviewButton")!;
        var markdownSource = window.FindControl<TextBox>("ReferenceMarkdownSource")!;
        Assert.Contains("markdown-preview", markdownPreview.Classes);
        Assert.True(markdownPreview.IsEffectivelyVisible);
        Assert.False(markdownSource.IsEffectivelyVisible);
        markdownPreview.RaiseEvent(new RoutedEventArgs(MarkdownPreviewSurface.ActivatedEvent));
        Assert.True(markdownSource.IsEffectivelyVisible);
        Assert.True(markdownSource.IsFocused);
        var nextControl = window.FindControl<Button>("ReferenceRowTitle")!;
        Assert.True(nextControl.Focus());
        Dispatcher.UIThread.RunJobs();
        Assert.True(markdownPreview.IsEffectivelyVisible);
        Assert.False(markdownSource.IsEffectivelyVisible);
        Assert.True(nextControl.IsFocused);
        window.Close();
    }

    private static void AssertTodayStarGeometry(ToggleButton toggle, bool filled)
    {
        var outline = Assert.Single(toggle.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>(),
            path => path.Name == "TodayStarOutline");
        var fill = Assert.Single(toggle.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>(),
            path => path.Name == "TodayStarFill");
        Assert.Equal(!filled, outline.IsVisible);
        Assert.Equal(filled, fill.IsVisible);
        Assert.Equal(18, outline.Width);
        Assert.Equal(18, outline.Height);
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Center, outline.HorizontalAlignment);
        Assert.Equal(Avalonia.Layout.VerticalAlignment.Center, outline.VerticalAlignment);
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Center, fill.HorizontalAlignment);
        Assert.Equal(Avalonia.Layout.VerticalAlignment.Center, fill.VerticalAlignment);
        Assert.Equal(0, Solid(toggle.Background).Color.A);
    }

    [AvaloniaFact]
    public void ReferenceRowsKeepWholeRowHoverTransparentTitlesAndVisibleKeyboardFocus()
    {
        var window = new StyleGuideWindow();
        window.Show();
        var row = window.FindControl<Border>("ReferencePeerRow")!;
        var title = window.FindControl<Button>("ReferenceRowTitle")!;
        var handle = window.FindControl<Button>("ReferenceDragHandle")!;
        var completion = window.FindControl<ToggleButton>("ReferenceUncheckedCompletion")!;

        window.MouseMove(CentreInWindow(title, window), RawInputModifiers.None);
        Assert.Equal(Color.Parse("#151923"), Solid(row.Background).Color);
        Assert.Equal(Colors.Transparent, Solid(title.Background).Color);
        Assert.Equal(Colors.Transparent, Solid(title.BorderBrush).Color);

        window.MouseMove(CentreInWindow(handle, window), RawInputModifiers.None);
        Assert.Equal(Color.Parse("#151923"), Solid(row.Background).Color);
        Assert.Equal(Color.Parse("#414958"), Solid(handle.BorderBrush).Color);

        window.MouseMove(CentreInWindow(completion, window), RawInputModifiers.None);
        Assert.Equal(Color.Parse("#151923"), Solid(row.Background).Color);
        Assert.Equal(Color.Parse("#F2F4F7"), Solid(completion.BorderBrush).Color);

        window.MouseMove(new Point(2, 2), RawInputModifiers.None);
        Assert.True(title.Focus(NavigationMethod.Tab));
        Assert.Equal(Color.Parse("#FF4FA3"), Solid(title.BorderBrush).Color);
        Assert.Equal(Colors.Transparent, Solid(row.Background).Color);
        window.Close();
    }

    [AvaloniaFact]
    public void ClickingNonFocusableSpaceOutsideMarkdownEditorReturnsToPreview()
    {
        var window = new StyleGuideWindow();
        window.Show();
        var preview = window.FindControl<MarkdownPreviewSurface>("ReferenceMarkdownPreviewButton")!;
        var editor = window.FindControl<TextBox>("ReferenceMarkdownSource")!;
        preview.RaiseEvent(new RoutedEventArgs(MarkdownPreviewSurface.ActivatedEvent));
        Assert.True(editor.IsFocused);

        var heading = Assert.Single(
            window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "MARKDOWN PREVIEW");
        var point = CentreInWindow(heading, window);
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.True(preview.IsEffectivelyVisible);
        Assert.False(editor.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void MarkdownHeadingUsesAFontDerivedLineBoxThatPreservesDescenders()
    {
        var window = new StyleGuideWindow();
        window.Show();
        var preview = window.FindControl<MarkdownPreview>("ReferenceMarkdownPreview")!;
        var heading = Assert.Single(
            preview.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Inlines?.Text == "Prepare the garden");

        Assert.True(
            heading.Bounds.Height > heading.FontSize,
            $"Heading line box {heading.Bounds.Height} must exceed font size {heading.FontSize} to preserve descenders.");
        window.Close();
    }

    private static void AssertCompletionGeometry(ToggleButton toggle, bool tickVisible)
    {
        Assert.Equal(30, toggle.Bounds.Width);
        Assert.Equal(30, toggle.Bounds.Height);
        var box = Assert.Single(toggle.GetVisualDescendants().OfType<Border>(), border => border.Name == "CompletionBox");
        Assert.Equal(22, box.Bounds.Width);
        Assert.Equal(22, box.Bounds.Height);
        Assert.Equal(new CornerRadius(6), box.CornerRadius);
        var tick = Assert.Single(toggle.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>(), path => path.Name == "CompletionTick");
        Assert.Equal(tickVisible, tick.IsVisible);
    }

    private static Point CentreInWindow(Control control, Window window)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);
        return Assert.IsType<Point>(point);
    }

    private static ISolidColorBrush Solid(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush);
}
