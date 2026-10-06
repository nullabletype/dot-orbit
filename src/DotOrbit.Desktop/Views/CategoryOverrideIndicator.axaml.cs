using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DotOrbit.Desktop.Views;

public sealed partial class CategoryOverrideIndicator : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<CategoryOverrideIndicator, string?>(nameof(Text));

    public CategoryOverrideIndicator() => AvaloniaXamlLoader.Load(this);

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
}
