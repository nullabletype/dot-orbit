using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace DotOrbit.Desktop.Views;

public enum WorkType
{
    Task,
    Project,
    Category,
}

public sealed partial class WorkTypeIcon : UserControl
{
    public static readonly StyledProperty<WorkType> WorkTypeProperty =
        AvaloniaProperty.Register<WorkTypeIcon, WorkType>(nameof(WorkType));

    public WorkTypeIcon()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<ShapePath>("TaskIcon")!.Data = WorkTypeIconGeometry.Task;
        this.FindControl<ShapePath>("ProjectIcon")!.Data = WorkTypeIconGeometry.Project;
        this.FindControl<ShapePath>("CategoryIcon")!.Data = WorkTypeIconGeometry.Category;
        UpdateVisibleIcon();
    }

    public WorkType WorkType
    {
        get => GetValue(WorkTypeProperty);
        set => SetValue(WorkTypeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WorkTypeProperty)
        {
            UpdateVisibleIcon();
        }
    }

    private void UpdateVisibleIcon()
    {
        if (this.FindControl<ShapePath>("TaskIcon") is not { } task
            || this.FindControl<ShapePath>("ProjectIcon") is not { } project
            || this.FindControl<ShapePath>("CategoryIcon") is not { } category)
        {
            return;
        }

        task.IsVisible = WorkType == WorkType.Task;
        project.IsVisible = WorkType == WorkType.Project;
        category.IsVisible = WorkType == WorkType.Category;
    }
}
