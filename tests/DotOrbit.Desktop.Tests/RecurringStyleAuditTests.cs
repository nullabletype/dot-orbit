using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using DotOrbit.Desktop.Clipboard;
using DotOrbit.Markdown;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.ViewModels;
using DotOrbit.Desktop.Views;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class RecurringStyleAuditTests
{
    private static FixedTimeProvider Clock() => new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
    [AvaloniaFact]
    public void RecurringIdentityPillsExposeLiveCategoryAndProjectActions()
    {
        var store = new RecurringMemoryWorkspace();
        var project = store.CreateProject("Project", "", "home", null);
        store.CreateRecurringTask(new("Definition", "", project.Id, null, [], RecurrenceCadence.Daily, new(2026, 11, 2), null, true));
        var shell = new ShellViewModel(store, Clock());
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            shell.PrimaryNavigation.Single(item => item.Title == "Recurring").SelectCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var pill = Assert.Single(window.GetVisualDescendants().OfType<CategoryProjectPill>(), item => item.IsEffectivelyVisible);
            var category = Assert.IsType<Button>(pill.FindControl<Button>("CategoryButton"));
            var projectButton = Assert.IsType<Button>(pill.FindControl<Button>("ProjectButton"));
            Assert.True(category.IsEnabled);
            Assert.True(category.Focusable);
            Assert.Equal("Open Category Home", AutomationProperties.GetName(category));
            Assert.True(projectButton.IsEnabled);
            Assert.True(projectButton.Focusable);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverridePillExplainsRelationshipWithoutAddingKeyboardStops(bool light)
    {
        var app = Assert.IsType<App>(Application.Current);
        app.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        var store = new RecurringMemoryWorkspace();
        var project = store.CreateProject("Household", "", "work", null);
        store.CreateRecurringTask(new("Monthly household review", "", project.Id, "home", [], RecurrenceCadence.Monthly, new(2026, 11, 2), null, false));
        var shell = new ShellViewModel(store, Clock());
        var window = new MainWindow { DataContext = shell, Width = 1120, Height = 600 };
        window.Show();
        try
        {
            shell.PrimaryNavigation.Single(item => item.Title == "Recurring").SelectCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var pill = Assert.Single(window.GetVisualDescendants().OfType<CategoryProjectPill>(), item => item.IsEffectivelyVisible);
            var category = pill.FindControl<Button>("CategoryButton")!;
            var parent = pill.FindControl<Button>("ProjectButton")!;
            var icon = Assert.Single(pill.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>(), item => item.Classes.Contains("category-pill-override-icon"));
            const string explanation = "Category overrides the Project's Category.";
            Assert.Equal(explanation, ToolTip.GetTip(category));
            Assert.Equal(explanation, AutomationProperties.GetHelpText(category));
            Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(icon));
            Assert.False(icon.Focusable);
            Assert.Equal(new Size(11, 11), icon.Bounds.Size);
            using var rendered = new RenderTargetBitmap(new PixelSize(1120, 600), new Vector(96, 96));
            rendered.Render(window);
            Assert.True(category.Focus(NavigationMethod.Tab));
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
            window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
            Dispatcher.UIThread.RunJobs();
            Assert.True(parent.IsFocused);
            pill.HasOverride = false;
            Assert.Null(ToolTip.GetTip(category));
            Assert.Equal(string.Empty, AutomationProperties.GetHelpText(category));
            pill.HasOverride = true;
            Assert.Equal(explanation, ToolTip.GetTip(category));
            Click(window, category);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Categories", shell.ViewTitle);
            Assert.Equal("category-selection-home", shell.MainViewTargetAutomationId);
            Assert.False(shell.Work!.Recurring.HasEditor);
        }
        finally { window.Close(); app.RequestedThemeVariant = ThemeVariant.Dark; }
    }

    [AvaloniaFact]
    public void NumericSpinnerOwnsOneRoundedBoundaryIncludingArrowButtons()
    {
        var shell = new ShellViewModel(new RecurringMemoryWorkspace(), Clock());
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            shell.PrimaryNavigation.Single(item => item.Title == "Recurring").SelectCommand.Execute(null);
            shell.Work!.Recurring.NewCommand.Execute(null);
            shell.Work.Recurring.HasDueDate = true;
            Dispatcher.UIThread.RunJobs();
            var picker = Assert.Single(window.GetVisualDescendants().OfType<WholeDayOffsetPicker>());
            var spinner = Assert.Single(picker.GetVisualDescendants().OfType<ButtonSpinner>());
            var boundary = Assert.Single(spinner.GetVisualDescendants().OfType<Border>(), border => border.TemplatedParent == spinner && border.Child is DockPanel);
            Assert.Equal(new CornerRadius(8), boundary.CornerRadius);
            Assert.NotNull(boundary.Child!.Clip);
        }
        finally { shell.Work!.Recurring.CancelCommand.Execute(null); window.Close(); }
    }
    [AvaloniaTheory]
    [InlineData("Active", "Projects")]
    [InlineData("Archived", "Archive")]
    [InlineData("Binned", "Categories")]
    public void PillSegmentsUseContextRoutesAndBinnedRestoreGuidance(string lifecycle, string destination)
    {
        var store = new RecurringMemoryWorkspace();
        var project = store.CreateProject("Project", "", "home", null);
        var definition = store.CreateRecurringTask(new("Definition", "", project.Id, null, [], RecurrenceCadence.Daily, new(2026, 11, 2), null, false));
        if (lifecycle == "Archived") store.ArchiveProject(project.Id);
        if (lifecycle == "Binned") store.MoveProjectToBin(project.Id);
        var shell = new ShellViewModel(store, Clock());
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            shell.PrimaryNavigation.Single(item => item.Title == "Recurring").SelectCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var category = Find<Button>(window, $"recurring-category-{definition.Id}");
            var projectButton = Find<Button>(window, $"recurring-project-{definition.Id}");
            Assert.True(category.Focusable);
            Assert.Equal("Open Category Home", AutomationProperties.GetName(category));
            if (lifecycle == "Binned")
            {
                Assert.False(projectButton.IsEnabled);
                Assert.False(projectButton.Focus());
                Assert.Contains("Restore the parent Project from Bin", AutomationProperties.GetHelpText(projectButton), StringComparison.Ordinal);
                Click(window, category);
            }
            else
            {
                Assert.True(category.Focus(NavigationMethod.Tab));
                window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
                window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
                Dispatcher.UIThread.RunJobs();
                Assert.True(projectButton.IsFocused);
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
            }
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(destination, shell.ViewTitle);
            Assert.False(shell.Work!.Recurring.HasEditor);
            Assert.Equal(lifecycle == "Active" ? $"project-selection-{project.Id}" : lifecycle == "Archived" ? $"archive-project-{project.Id}" : "category-selection-home", shell.MainViewTargetAutomationId);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void PillNavigationUsesDirtyGuardAndFreshCommittedProjectAfterSaveOrRefresh()
    {
        var store = new RecurringMemoryWorkspace();
        var first = store.CreateProject("First", "", "home", null);
        var second = store.CreateProject("Second", "", "work", null);
        var definition = store.CreateRecurringTask(new("Definition", "", first.Id, null, [], RecurrenceCadence.Daily, new(2026, 11, 2), null, true));
        var shell = new ShellViewModel(store, Clock());
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            var navigation = shell.PrimaryNavigation.Single(item => item.Title == "Recurring");
            navigation.SelectCommand.Execute(null);
            shell.Work!.Recurring.Definitions.Single().EditCommand.Execute(null);
            var model = shell.Work.Recurring;
            model.Title = string.Empty;
            model.Project = model.Projects.Single(item => item.ProjectId == second.Id);
            Dispatcher.UIThread.RunJobs();
            var projectButton = Find<Button>(window, $"recurring-project-{definition.Id}");
            Click(window, projectButton);
            Dispatcher.UIThread.RunJobs();
            Assert.True(model.NeedsDecision);
            Assert.Equal("Recurring", shell.ViewTitle);
            model.StayCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(second.Id, model.Project!.ProjectId);
            Click(window, projectButton);
            Dispatcher.UIThread.RunJobs();
            model.DiscardAndLeaveCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Projects", shell.ViewTitle);
            Assert.Equal($"project-selection-{first.Id}", shell.MainViewTargetAutomationId);
            navigation.SelectCommand.Execute(null);
            model.Definitions.Single().EditCommand.Execute(null);
            store.FailReadAfterMutation = true;
            model.Project = model.Projects.Single(item => item.ProjectId == second.Id);
            model.SaveCommand.Execute(null);
            projectButton = Find<Button>(window, $"recurring-project-{definition.Id}");
            Click(window, projectButton);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Recurring", shell.ViewTitle);
            Assert.True(shell.Work.CanRetryWorkspaceRefresh);
            store.FailReads = false; store.FailReadAfterMutation = false;
            shell.Work.RetryWorkspaceRefreshCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Projects", shell.ViewTitle);
            Assert.Equal($"project-selection-{second.Id}", shell.MainViewTargetAutomationId);
        }
        finally { shell.Work!.Recurring.CancelCommand.Execute(null); window.Close(); }
    }

    [Theory]
    [InlineData(false, false, "Standalone. Category Home")]
    [InlineData(true, false, "Project Project. Inherited Category Home")]
    [InlineData(true, true, "Project Project. Category override Work")]
    public void RowAccessibleIdentityDistinguishesCategoryRelationships(bool attached, bool overridden, string expected)
    {
        var store = new RecurringMemoryWorkspace();
        var project = attached ? store.CreateProject("Project", "", "home", null) : null;
        store.CreateRecurringTask(new("Definition", "", project?.Id, overridden ? "work" : attached ? null : "home", [], RecurrenceCadence.Daily, new(2026, 11, 2), null, true));
        var work = new ProjectCaptureViewModel(store, Clock());
        Assert.Contains(expected, work.Recurring.Definitions.Single().AccessibleName, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void MarkdownPreviewNamesCopyAndOutsideCanvasClickUseSharedSanitisedBehavior()
    {
        var clipboard = new RecordingClipboard();
        var store = new RecurringMemoryWorkspace();
        var shell = new ShellViewModel(store, Clock());
        var window = new MainWindow(null, clipboard) { DataContext = shell };
        window.Show();
        try
        {
            shell.PrimaryNavigation.Single(item => item.Title == "Recurring").SelectCommand.Execute(null);
            shell.Work!.Recurring.NewCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var model = shell.Work.Recurring;
            var preview = Assert.Single(window.GetVisualDescendants().OfType<MarkdownPreviewSurface>(), item => item.IsEffectivelyVisible);
            var copy = Assert.Single(window.GetVisualDescendants().OfType<Button>(), button => AutomationProperties.GetName(button) == "Copy rendered recurring Task description");
            Assert.Contains("Empty rendered description", AutomationProperties.GetName(preview), StringComparison.Ordinal);
            Assert.False(copy.IsEnabled);
            model.Description = "**Rendered** [unsafe](javascript:alert(1))";
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("Rendered", AutomationProperties.GetName(preview), StringComparison.Ordinal);
            Assert.DoesNotContain("**", AutomationProperties.GetName(preview), StringComparison.Ordinal);
            Assert.True(copy.IsEnabled);
            copy.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(clipboard.Document);
            Assert.Equal(model.RenderedDescription.ToPlainText(), clipboard.Document.ToPlainText());
            Assert.DoesNotContain("javascript:", clipboard.Document.ToHtml(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("copied", shell.Work.Message, StringComparison.Ordinal);
            Assert.Empty(store.ReadRecurringTasks());
            preview.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
            Dispatcher.UIThread.RunJobs();
            Assert.True(model.IsEditingMarkdown);
            var blank = new Point(400, 550);
            window.MouseDown(blank, MouseButton.Left, RawInputModifiers.LeftMouseButton);
            window.MouseUp(blank, MouseButton.Left, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.False(model.IsEditingMarkdown);
            Assert.True(preview.IsEffectivelyVisible);
            Assert.Empty(store.ReadRecurringTasks());
        }
        finally { shell.Work!.Recurring.CancelCommand.Execute(null); window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void SpinnerAndLivePillsRenderSharedHoverFocusErrorAndDisabledStates(bool light)
    {
        var app = Assert.IsType<App>(Application.Current);
        app.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        var store = new RecurringMemoryWorkspace();
        var project = store.CreateProject("Household", "", "home", null);
        var definition = store.CreateRecurringTask(new("Review the week", "A **Markdown** description.", project.Id, null, [], RecurrenceCadence.Weekly, new(2026, 11, 2), 5, true));
        var shell = new ShellViewModel(store, Clock());
        var window = new MainWindow { DataContext = shell, Width = 1120, Height = 600 };
        window.Show();
        try
        {
            shell.PrimaryNavigation.Single(item => item.Title == "Recurring").SelectCommand.Execute(null);
            shell.Work!.Recurring.Definitions.Single().EditCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var picker = Assert.Single(window.GetVisualDescendants().OfType<WholeDayOffsetPicker>());
            var input = Assert.Single(picker.GetVisualDescendants().OfType<TextBox>());
            var spinner = Assert.Single(picker.GetVisualDescendants().OfType<ButtonSpinner>());
            var boundary = Assert.Single(spinner.GetVisualDescendants().OfType<Border>(), border => border.TemplatedParent == spinner && border.Child is DockPanel);
            var editor = Assert.Single(window.GetVisualDescendants().OfType<RecurringTaskEditor>());
            var scroll = Assert.IsType<ScrollViewer>(editor.FindControl<ScrollViewer>("RecurringEditorScroll"));
            var content = Assert.IsType<StackPanel>(scroll.Content);
            scroll.Offset = new Vector(0, Math.Max(0, picker.TranslatePoint(default, content)!.Value.Y - 80));
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(boundary.Child!.Clip);
            Assert.Equal(new CornerRadius(8), boundary.CornerRadius);
            void RenderState()
            {
                using var rendered = new RenderTargetBitmap(new PixelSize(1120, 600), new Vector(96, 96));
                rendered.Render(window);
            }
            RenderState();
            input.Focus(NavigationMethod.Tab);
            Dispatcher.UIThread.RunJobs();
            RenderState();
            var increase = Assert.Single(spinner.GetVisualDescendants().OfType<RepeatButton>(), button => button.Name == "PART_IncreaseButton");
            var increasePoint = increase.TranslatePoint(new Point(increase.Bounds.Width / 2, increase.Bounds.Height / 2), window)!.Value;
            window.MouseMove(increasePoint, RawInputModifiers.None);
            RenderState();
            window.MouseDown(increasePoint, MouseButton.Left, RawInputModifiers.LeftMouseButton);
            Assert.True(increase.IsPressed);
            RenderState();
            window.MouseUp(increasePoint, MouseButton.Left, RawInputModifiers.None);
            input.Focus(NavigationMethod.Tab);
            input.Text = "Potato";
            Dispatcher.UIThread.RunJobs();
            Assert.True(shell.Work.Recurring.HasOffsetError);
            Assert.Equal(new Thickness(2), boundary.BorderThickness);
            Assert.Equal(0, input.BorderThickness.Top);
            var error = Assert.IsAssignableFrom<ISolidColorBrush>(window.FindResource(app.ActualThemeVariant, "ErrorBrush")).Color;
            Assert.Equal(error, Assert.IsAssignableFrom<ISolidColorBrush>(boundary.BorderBrush).Color);
            var category = Find<Button>(window, $"recurring-category-{definition.Id}");
            window.MouseMove(category.TranslatePoint(new Point(category.Bounds.Width / 2, category.Bounds.Height / 2), window)!.Value, RawInputModifiers.None);
            using var bitmap = new RenderTargetBitmap(new PixelSize(1120, 600), new Vector(96, 96));
            bitmap.Render(window);
            window.MouseDown(category.TranslatePoint(new Point(category.Bounds.Width / 2, category.Bounds.Height / 2), window)!.Value, MouseButton.Left, RawInputModifiers.LeftMouseButton);
            RenderState();
            window.MouseUp(category.TranslatePoint(new Point(category.Bounds.Width / 2, category.Bounds.Height / 2), window)!.Value, MouseButton.Left, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.True(shell.Work.Recurring.NeedsDecision);
            shell.Work.Recurring.StayCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            scroll.Offset = new Vector(0, Math.Max(0, picker.TranslatePoint(default, content)!.Value.Y - 80));
            Assert.True(category.Focus(NavigationMethod.Tab));
            Dispatcher.UIThread.RunJobs();
            RenderState();
            var focusIndicator = Assert.Single(category.GetVisualDescendants().OfType<Border>(), border => border.Name == "FocusIndicator");
            Assert.True(focusIndicator.IsVisible);
            Assert.Null(category.FocusAdorner);
            var accent = Assert.IsAssignableFrom<ISolidColorBrush>(window.FindResource(app.ActualThemeVariant, "AccentBrush")).Color;
            Assert.Equal(accent, Assert.IsAssignableFrom<ISolidColorBrush>(focusIndicator.BorderBrush).Color);
            using var focused = new RenderTargetBitmap(new PixelSize(1120, 600), new Vector(96, 96));
            focused.Render(window);
            picker.IsEnabled = false;
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(boundary.Child.Clip);
            Assert.Equal(new CornerRadius(8), boundary.CornerRadius);
            Assert.False(input.IsEffectivelyEnabled);
            RenderState();
        }
        finally { shell.Work!.Recurring.CancelCommand.Execute(null); window.Close(); app.RequestedThemeVariant = ThemeVariant.Dark; }
    }

    [AvaloniaTheory]
    [InlineData("Title", "recurring-title")]
    [InlineData("Date", "recurring-first-date")]
    [InlineData("Category", "recurring-category")]
    public void InvalidFieldsShowCorrectionBesideFocusedControl(string kind, string id)
    {
        var shell = new ShellViewModel(new RecurringMemoryWorkspace(), Clock());
        var window = new MainWindow { DataContext = shell, Width = 1120, Height = 600 };
        window.Show();
        try
        {
            shell.PrimaryNavigation.Single(item => item.Title == "Recurring").SelectCommand.Execute(null);
            var model = shell.Work!.Recurring;
            model.NewCommand.Execute(null);
            model.Title = kind == "Title" ? "" : "Valid";
            model.FirstDate = kind == "Date" ? "invalid" : "2026-11-02";
            model.Category = kind == "Category" ? null : model.Categories.Single(item => item.Id == "home");
            model.SaveCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var field = Find<Control>(window, id);
            var bottom = field.TranslatePoint(new Point(0, field.Bounds.Height), window)!.Value.Y;
            var nearby = window.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible && text.Classes.Contains("validation-message") && text.Text == model.Validation)
                .Select(text => text.TranslatePoint(default, window)!.Value.Y - bottom).Where(gap => gap >= 0).DefaultIfEmpty(double.MaxValue).Min();
            Assert.InRange(nearby, 0, 14);
            Assert.Contains("error", field.Classes);
            Assert.Equal(model.Validation, AutomationProperties.GetHelpText(field));
        }
        finally { shell.Work!.Recurring.CancelCommand.Execute(null); window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void CategoryChoicesReuseNamedColourMarkerAndLongNamesRemainBounded(bool attached)
    {
        var store = new RecurringMemoryWorkspace();
        store.RenameCategory("home", "A long Category name that must remain legible without covering the scrollbar");
        var project = attached ? store.CreateProject("Project", "", "home", null) : null;
        store.CreateRecurringTask(new("Definition", "", project?.Id, attached ? null : "home", [], RecurrenceCadence.Daily, new(2026, 11, 2), null, true));
        var shell = new ShellViewModel(store, Clock());
        var window = new MainWindow { DataContext = shell, Width = 1120, Height = 600 };
        window.Show();
        try
        {
            shell.PrimaryNavigation.Single(item => item.Title == "Recurring").SelectCommand.Execute(null);
            shell.Work!.Recurring.Definitions.Single().EditCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var category = Find<ComboBox>(window, "recurring-category");
            Assert.NotNull(category.ItemContainerTheme);
            Assert.Contains(category.GetVisualDescendants(), control => control is CategoryIdentityMarker);
            Assert.InRange(category.Bounds.Width, 100, 260);
            Assert.Equal(attached ? "Inherit Project Category" : store.Read().Categories.Single(item => item.Id == "home").Name, shell.Work.Recurring.Category!.Name);
        }
        finally { shell.Work!.Recurring.CancelCommand.Execute(null); window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void LongIdentitySegmentsStayInsideTheirRowColumnAndRemainIndependent(bool light)
    {
        var app = Assert.IsType<App>(Application.Current);
        app.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        var store = new RecurringMemoryWorkspace();
        store.RenameCategory("home", "A very long household and garden Category name");
        var project = store.CreateProject("A very long Project title for household planning", "", "home", null);
        var definition = store.CreateRecurringTask(new("Long inherited definition", "", project.Id, null, [], RecurrenceCadence.Monthly, new(2026, 11, 2), 5, false));
        var shell = new ShellViewModel(store, Clock());
        var window = new MainWindow { DataContext = shell, Width = 1120, Height = 600 };
        window.Show();
        try
        {
            shell.PrimaryNavigation.Single(item => item.Title == "Recurring").SelectCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var row = window.GetVisualDescendants().OfType<Border>().Single(border => border.Classes.Contains("recurring-row"));
            var pill = row.GetVisualDescendants().OfType<CategoryProjectPill>().Single();
            var grid = row.GetVisualDescendants().OfType<Grid>().First(candidate => candidate.ColumnDefinitions.Count == 3 && candidate.ColumnDefinitions[1].Width.Value == 130);
            var right = pill.TranslatePoint(new Point(pill.Bounds.Width, 0), grid)!.Value.X;
            Assert.True(right <= grid.ColumnDefinitions[0].ActualWidth, $"Pill right {right} exceeds title column {grid.ColumnDefinitions[0].ActualWidth}.");
            var category = Find<Button>(window, $"recurring-category-{definition.Id}");
            var parent = Find<Button>(window, $"recurring-project-{definition.Id}");
            Assert.True(category.Bounds.Width > 20 && parent.Bounds.Width > 20);
            Assert.Equal("Open Project A very long Project title for household planning", AutomationProperties.GetName(parent));
            using var rendered = new RenderTargetBitmap(new PixelSize(1120, 600), new Vector(96, 96));
            rendered.Render(window);
            Click(window, parent);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Projects", shell.ViewTitle);
            Assert.False(shell.Work!.Recurring.HasEditor);
        }
        finally { window.Close(); app.RequestedThemeVariant = ThemeVariant.Dark; }
    }

    private sealed class RecordingClipboard : IMarkdownClipboard
    {
        public SanitisedMarkdownDocument? Document { get; private set; }
        public ValueTask WriteAsync(SanitisedMarkdownDocument document) { Document = document; return ValueTask.CompletedTask; }
    }
    private static T Find<T>(Window window, string id) where T : Control => Assert.Single(window.GetVisualDescendants().OfType<T>(), item => item.IsEffectivelyVisible && AutomationProperties.GetAutomationId(item) == id);
    private static void Click(Window window, Control control)
    {
        Dispatcher.UIThread.RunJobs();
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
    }

}
