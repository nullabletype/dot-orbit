using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DotOrbit.Core.Workspaces;

namespace DotOrbit.Desktop.Views;

public sealed partial class CategoryIdentityMarker : UserControl
{
    public static readonly StyledProperty<string?> CategoryNameProperty =
        AvaloniaProperty.Register<CategoryIdentityMarker, string?>(nameof(CategoryName));

    public static readonly StyledProperty<string> ColourKeyProperty =
        AvaloniaProperty.Register<CategoryIdentityMarker, string>(nameof(ColourKey), IdentityColourPalette.DefaultKey);

    public CategoryIdentityMarker()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public string? CategoryName
    {
        get => GetValue(CategoryNameProperty);
        set => SetValue(CategoryNameProperty, value);
    }

    public string ColourKey
    {
        get => GetValue(ColourKeyProperty);
        set => SetValue(ColourKeyProperty, value);
    }

}
