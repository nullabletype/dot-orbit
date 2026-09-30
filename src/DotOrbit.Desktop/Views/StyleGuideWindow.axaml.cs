using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace DotOrbit.Desktop.Views;

public sealed partial class StyleGuideWindow : Window
{
    public StyleGuideWindow() => AvaloniaXamlLoader.Load(this);

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
}
