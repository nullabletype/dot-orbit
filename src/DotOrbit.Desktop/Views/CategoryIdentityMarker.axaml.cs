using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
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
        UpdateColourClass();
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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ColourKeyProperty) UpdateColourClass();
    }

    private void UpdateColourClass()
    {
        if (this.FindControl<Ellipse>("ColourMarker") is not { } marker) return;
        foreach (var key in IdentityColourPalette.Keys) marker.Classes.Remove(key);
        if (IdentityColourPalette.IsSupported(ColourKey)) marker.Classes.Add(ColourKey);
    }
}
