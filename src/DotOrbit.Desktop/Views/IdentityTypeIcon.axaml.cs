using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DotOrbit.Core.Workspaces;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace DotOrbit.Desktop.Views;

public sealed partial class IdentityTypeIcon : UserControl
{
    public static readonly StyledProperty<WorkType> WorkTypeProperty =
        AvaloniaProperty.Register<IdentityTypeIcon, WorkType>(nameof(WorkType), WorkType.Category);

    public static readonly StyledProperty<string> ColourKeyProperty =
        AvaloniaProperty.Register<IdentityTypeIcon, string>(nameof(ColourKey), IdentityColourPalette.DefaultKey);

    public IdentityTypeIcon()
    {
        AvaloniaXamlLoader.Load(this);
        UpdateIcon();
    }

    public WorkType WorkType
    {
        get => GetValue(WorkTypeProperty);
        set => SetValue(WorkTypeProperty, value);
    }

    public string ColourKey
    {
        get => GetValue(ColourKeyProperty);
        set => SetValue(ColourKeyProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WorkTypeProperty || change.Property == ColourKeyProperty) UpdateIcon();
    }

    private void UpdateIcon()
    {
        if (this.FindControl<ShapePath>("Icon") is not { } icon) return;
        var isProject = WorkType == WorkType.Project;
        icon.Data = isProject ? WorkTypeIconGeometry.ProjectIdentity : WorkTypeIconGeometry.Category;
        icon.Width = isProject ? 11 : 8;
        icon.Height = isProject ? 10 : 8;
        foreach (var key in IdentityColourPalette.Keys) icon.Classes.Remove(key);
        if (IdentityColourPalette.IsSupported(ColourKey)) icon.Classes.Add(ColourKey);
    }
}
