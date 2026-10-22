using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DotOrbit.Core.Workspaces;
using System.Windows.Input;

namespace DotOrbit.Desktop.Views;

public sealed partial class CategoryProjectPill : UserControl
{
    private readonly Grid _segments;
    private readonly Button _categoryButton;
    private readonly Button _projectButton;

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

    public static readonly StyledProperty<ICommand?> CategoryCommandProperty =
        AvaloniaProperty.Register<CategoryProjectPill, ICommand?>(nameof(CategoryCommand));

    public static readonly StyledProperty<ICommand?> ProjectCommandProperty =
        AvaloniaProperty.Register<CategoryProjectPill, ICommand?>(nameof(ProjectCommand));

    public static readonly StyledProperty<string?> CategoryAutomationIdProperty =
        AvaloniaProperty.Register<CategoryProjectPill, string?>(nameof(CategoryAutomationId));

    public static readonly StyledProperty<string?> ProjectAutomationIdProperty =
        AvaloniaProperty.Register<CategoryProjectPill, string?>(nameof(ProjectAutomationId));

    public static readonly StyledProperty<string?> ProjectHelpTextProperty =
        AvaloniaProperty.Register<CategoryProjectPill, string?>(nameof(ProjectHelpText));

    public CategoryProjectPill()
    {
        AvaloniaXamlLoader.Load(this);
        _segments = this.FindControl<Grid>("Segments")!;
        _categoryButton = this.FindControl<Button>("CategoryButton")!;
        _projectButton = this.FindControl<Button>("ProjectButton")!;
        UpdatePresentation();
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

    public ICommand? CategoryCommand
    {
        get => GetValue(CategoryCommandProperty);
        set => SetValue(CategoryCommandProperty, value);
    }

    public ICommand? ProjectCommand
    {
        get => GetValue(ProjectCommandProperty);
        set => SetValue(ProjectCommandProperty, value);
    }

    public string? CategoryAutomationId
    {
        get => GetValue(CategoryAutomationIdProperty);
        set => SetValue(CategoryAutomationIdProperty, value);
    }

    public string? ProjectAutomationId
    {
        get => GetValue(ProjectAutomationIdProperty);
        set => SetValue(ProjectAutomationIdProperty, value);
    }

    public string? ProjectHelpText
    {
        get => GetValue(ProjectHelpTextProperty);
        set => SetValue(ProjectHelpTextProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CategoryNameProperty
            || change.Property == ProjectNameProperty
            || change.Property == CategoryCommandProperty
            || change.Property == ProjectCommandProperty
            || change.Property == CategoryAutomationIdProperty
            || change.Property == ProjectAutomationIdProperty
            || change.Property == ProjectHelpTextProperty)
            UpdatePresentation();
    }

    private void UpdatePresentation()
    {
        var hasProject = !string.IsNullOrWhiteSpace(ProjectName);
        if (hasProject && !_segments.Children.Contains(_projectButton))
            _segments.Children.Add(_projectButton);
        else if (!hasProject && _segments.Children.Contains(_projectButton))
            _segments.Children.Remove(_projectButton);

        var interactive = CategoryCommand is not null || (hasProject && ProjectCommand is not null);
        IsHitTestVisible = interactive;
        AutomationProperties.SetAccessibilityView(this, interactive ? AccessibilityView.Content : AccessibilityView.Raw);

        _categoryButton.Focusable = CategoryCommand is not null;
        _categoryButton.IsEnabled = CategoryCommand?.CanExecute(null) == true;
        AutomationProperties.SetAccessibilityView(
            _categoryButton,
            CategoryCommand is null ? AccessibilityView.Raw : AccessibilityView.Control);
        AutomationProperties.SetName(
            _categoryButton,
            CategoryCommand is null ? string.Empty : $"Open Category {CategoryName}");
        AutomationProperties.SetAutomationId(_categoryButton, CategoryAutomationId ?? string.Empty);

        _projectButton.Focusable = hasProject && ProjectCommand is not null;
        _projectButton.IsEnabled = hasProject && ProjectCommand?.CanExecute(null) == true;
        AutomationProperties.SetAccessibilityView(
            _projectButton,
            hasProject && ProjectCommand is not null ? AccessibilityView.Control : AccessibilityView.Raw);
        AutomationProperties.SetName(
            _projectButton,
            hasProject && ProjectCommand is not null ? $"Open Project {ProjectName}" : string.Empty);
        AutomationProperties.SetAutomationId(
            _projectButton,
            hasProject ? ProjectAutomationId ?? string.Empty : string.Empty);
        AutomationProperties.SetHelpText(
            _projectButton,
            hasProject ? ProjectHelpText ?? string.Empty : string.Empty);
    }
}
