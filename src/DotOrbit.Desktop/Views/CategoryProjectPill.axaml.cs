using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DotOrbit.Core.Workspaces;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace DotOrbit.Desktop.Views;

public sealed partial class CategoryProjectPill : UserControl
{
    public static readonly StyledProperty<string?> CategoryNameProperty =
        AvaloniaProperty.Register<CategoryProjectPill, string?>(nameof(CategoryName));

    public static readonly StyledProperty<string> CategoryColourKeyProperty =
        AvaloniaProperty.Register<CategoryProjectPill, string>(nameof(CategoryColourKey), IdentityColourPalette.DefaultKey);

    public static readonly StyledProperty<string?> ProjectNameProperty =
        AvaloniaProperty.Register<CategoryProjectPill, string?>(nameof(ProjectName));

    public static readonly StyledProperty<string> ProjectColourKeyProperty =
        AvaloniaProperty.Register<CategoryProjectPill, string>(nameof(ProjectColourKey), IdentityColourPalette.DefaultKey);

    public static readonly StyledProperty<bool> HasOverrideProperty =
        AvaloniaProperty.Register<CategoryProjectPill, bool>(nameof(HasOverride));

    public CategoryProjectPill()
    {
        AvaloniaXamlLoader.Load(this);
        UpdateProjectVisibility();
    }

    public string? CategoryName
    {
        get => GetValue(CategoryNameProperty);
        set => SetValue(CategoryNameProperty, value);
    }

    public string CategoryColourKey
    {
        get => GetValue(CategoryColourKeyProperty);
        set => SetValue(CategoryColourKeyProperty, value);
    }

    public string? ProjectName
    {
        get => GetValue(ProjectNameProperty);
        set => SetValue(ProjectNameProperty, value);
    }

    public string ProjectColourKey
    {
        get => GetValue(ProjectColourKeyProperty);
        set => SetValue(ProjectColourKeyProperty, value);
    }

    public bool HasOverride
    {
        get => GetValue(HasOverrideProperty);
        set => SetValue(HasOverrideProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ProjectNameProperty) UpdateProjectVisibility();
    }

    private void UpdateProjectVisibility()
    {
        var hasProject = !string.IsNullOrWhiteSpace(ProjectName);
        if (this.FindControl<ShapePath>("Join") is { } join) join.IsVisible = hasProject;
        if (this.FindControl<StackPanel>("ProjectSegment") is { } segment) segment.IsVisible = hasProject;
    }
}
