using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DotOrbit.Core.Workspaces;

namespace DotOrbit.Desktop.Views;

public sealed partial class CategoryPill : UserControl
{
    public static readonly StyledProperty<string?> CategoryNameProperty =
        AvaloniaProperty.Register<CategoryPill, string?>(nameof(CategoryName));

    public static readonly StyledProperty<string> ColourKeyProperty =
        AvaloniaProperty.Register<CategoryPill, string>(nameof(ColourKey), IdentityColourPalette.DefaultKey);

    public static readonly StyledProperty<bool> HasOverrideProperty =
        AvaloniaProperty.Register<CategoryPill, bool>(nameof(HasOverride));

    public CategoryPill() => AvaloniaXamlLoader.Load(this);

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

    public bool HasOverride
    {
        get => GetValue(HasOverrideProperty);
        set => SetValue(HasOverrideProperty, value);
    }
}
