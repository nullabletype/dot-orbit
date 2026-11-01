using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.ViewModels;
using DotOrbit.Desktop.Views;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class RecurringTaskPresentationTests
{
    private static FixedTimeProvider Clock() => new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
    private static RecurringTasksViewModel Begin(ProjectCaptureViewModel work)
    {
        work.Recurring.NewCommand.Execute(null);
        work.Recurring.Title = "Recurring task";
        work.Recurring.Category = work.Recurring.Categories.Single(item => item.Id == "home");
        return work.Recurring;
    }
    private static void Stage(RecurringTasksViewModel model, string label)
    {
        model.ParticipantToAdd = ParticipantChoice.New;
        model.NewParticipantLabel = label;
        model.AddNewParticipantCommand.Execute(null);
    }

    [AvaloniaTheory]
    [InlineData("Potato", false)]
    [InlineData("1.5", false)]
    [InlineData("-1", false)]
    [InlineData("999999999", false)]
    [InlineData("Potato", true)]
    [InlineData("1.5", true)]
    [InlineData("-1", true)]
    [InlineData("999999999", true)]
    public void InvalidTypedOrPastedOffsetNeverSavesPreviousValueAfterFocusLeaves(string invalid, bool replacement)
    {
        var store = new RecurringMemoryWorkspace();
        var shell = new ShellViewModel(store, Clock());
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            shell.PrimaryNavigation.Single(item => item.Title == "Recurring").SelectCommand.Execute(null);
            var model = Begin(shell.Work!);
            model.HasDueDate = true;
            model.DueOffset = "5";
            Dispatcher.UIThread.RunJobs();
            var picker = Find<WholeDayOffsetPicker>(window, "recurring-due-offset");
            var editor = Assert.Single(picker.GetVisualDescendants().OfType<TextBox>());
            editor.Focus(); editor.SelectAll();
            if (replacement) editor.Text = invalid; // Text replacement is the same input boundary used by paste.
            else window.KeyTextInput(invalid);
            Dispatcher.UIThread.RunJobs();
            Find<TextBox>(window, "recurring-title").Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(invalid, editor.Text);
            Assert.Equal(invalid, model.DueOffset);
            editor.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(invalid, editor.Text);
            Assert.Equal(invalid, model.DueOffset);
            Assert.False(model.CanSave);
            Assert.False(Find<Button>(window, "recurring-save").IsEnabled);
            Assert.True(model.HasOffsetError);
            Assert.NotEmpty(model.DueOffsetValidation);
            model.SaveCommand.Execute(null);
            Assert.Empty(store.ReadRecurringTasks());
            Assert.Empty(store.Read().Tasks);
            var left = false;
            shell.Work!.Navigate(() => left = true);
            Assert.True(model.NeedsDecision);
            model.SaveAndLeaveCommand.Execute(null);
            Assert.False(left);
            Assert.Empty(store.ReadRecurringTasks());
            model.StayCommand.Execute(null);
            editor.Text = "0";
            Dispatcher.UIThread.RunJobs();
            Assert.True(model.CanSave);
            Assert.True(Find<Button>(window, "recurring-save").IsEnabled);
            model.SaveCommand.Execute(null);
            Assert.Equal(0, Assert.Single(store.ReadRecurringTasks()).DueDateOffsetDays);
        }
        finally { shell.Work!.Recurring.CancelCommand.Execute(null); window.Close(); }
    }

    [AvaloniaFact]
    public void OffsetSpinnerUsesWholeDaysAndRepresentableDateBounds()
    {
        var shell = new ShellViewModel(new RecurringMemoryWorkspace(), Clock());
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            shell.PrimaryNavigation.Single(item => item.Title == "Recurring").SelectCommand.Execute(null);
            var model = Begin(shell.Work!);
            model.FirstDate = "9999-12-30";
            model.HasDueDate = true;
            model.DueOffset = "0";
            Dispatcher.UIThread.RunJobs();
            var picker = Find<WholeDayOffsetPicker>(window, "recurring-due-offset");
            Assert.Equal(0, picker.Minimum);
            Assert.Equal(1, picker.Maximum);
            Assert.Equal(1, picker.Increment);
            Assert.True(picker.ShowButtonSpinner);
            var editor = Assert.Single(picker.GetVisualDescendants().OfType<TextBox>());
            editor.Focus();
            window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("1", model.DueOffset);
            window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("1", model.DueOffset);
            editor.Text = "2";
            Dispatcher.UIThread.RunJobs();
            Assert.False(model.CanSave);
            editor.Text = "1";
            Dispatcher.UIThread.RunJobs();
            Assert.True(model.CanSave);
            model.FirstDate = "9999-12-31";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, model.MaximumDueOffset);
            Assert.Equal("1", model.DueOffset);
            Assert.Equal("1", editor.Text);
            Assert.False(model.CanSave);
        }
        finally { shell.Work!.Recurring.CancelCommand.Execute(null); window.Close(); }
    }

    [Fact]
    public void NewParticipantsRemainDraftOnlyUntilDefinitionSaveAndCancelOrDiscardCreatesNothing()
    {
        var store = new RecurringMemoryWorkspace();
        var work = new ProjectCaptureViewModel(store, Clock());
        var model = Begin(work);
        Stage(model, "New Person");
        Assert.Equal("New Person", Assert.Single(model.SelectedParticipants).Label);
        Assert.True(Assert.Single(model.SelectedParticipants).IsDraft);
        Assert.Empty(store.Read().Participants);
        Assert.Empty(store.ReadRecurringTasks());
        model.CancelCommand.Execute(null);
        Assert.Empty(store.Read().Participants);
        model = Begin(work);
        Stage(model, "Discarded Person");
        var left = false;
        work.Navigate(() => left = true);
        Assert.True(model.NeedsDecision);
        model.DiscardAndLeaveCommand.Execute(null);
        Assert.True(left);
        Assert.Empty(store.Read().Participants);
        model = Begin(work);
        Assert.Empty(model.SelectedParticipants);
        Stage(model, "Saved Person");
        model.SaveCommand.Execute(null);
        var person = Assert.Single(store.Read().Participants);
        Assert.Equal("Saved Person", person.Label);
        Assert.Equal([person.Id], Assert.Single(store.ReadRecurringTasks()).ParticipantIds);
    }

    [Fact]
    public void ParticipantEntryReusesMatchesRejectsDuplicateAndRetainsFailedSaveDraft()
    {
        var store = new RecurringMemoryWorkspace();
        var existing = store.CreateParticipant("Alex");
        var work = new ProjectCaptureViewModel(store, Clock());
        var model = Begin(work);
        Stage(model, " alex ");
        Assert.Equal(existing.Id, Assert.Single(model.SelectedParticipants).Id);
        Assert.False(Assert.Single(model.SelectedParticipants).IsDraft);
        Stage(model, "ALEX");
        Assert.Single(model.SelectedParticipants);
        Assert.Contains("already selected", model.ParticipantValidation, StringComparison.Ordinal);
        Stage(model, "  ");
        Assert.Contains("Enter a Participant label", model.ParticipantValidation, StringComparison.Ordinal);
        Stage(model, "Draft Person");
        store.FailCreate = true;
        model.SaveCommand.Execute(null);
        Assert.Single(store.Read().Participants);
        Assert.Equal(2, model.SelectedParticipants.Count());
        Assert.True(model.HasEditor);
        store.FailCreate = false;
        model.SaveCommand.Execute(null);
        Assert.Equal(2, store.Read().Participants.Count);
        Assert.Equal(2, Assert.Single(store.ReadRecurringTasks()).ParticipantIds.Count);
    }

    [Fact]
    public void PendingParticipantTextGuardsNavigationAndSaveAndRemovedDraftCanBeReadded()
    {
        var store = new RecurringMemoryWorkspace();
        var work = new ProjectCaptureViewModel(store, Clock());
        var model = Begin(work);
        model.SaveCommand.Execute(null);
        model.Definitions.Single().EditCommand.Execute(null);
        model.ParticipantToAdd = ParticipantChoice.New;
        model.NewParticipantLabel = "Unadded Person";
        Assert.True(model.IsDirty);
        var left = false;
        work.Navigate(() => left = true);
        Assert.True(model.NeedsDecision);
        model.SaveAndLeaveCommand.Execute(null);
        Assert.False(left);
        Assert.Contains("Add or cancel", model.ParticipantValidation, StringComparison.Ordinal);
        Assert.Empty(store.Read().Participants);
        model.StayCommand.Execute(null);
        model.CancelNewParticipantCommand.Execute(null);
        Assert.Empty(model.NewParticipantLabel);
        Assert.Empty(model.ParticipantValidation);
        Assert.False(model.IsDirty);
        Stage(model, "Staged Person");
        var staged = Assert.Single(model.SelectedParticipants);
        staged.RemoveCommand.Execute(null);
        Assert.Empty(model.SelectedParticipants);
        model.ParticipantToAdd = model.AvailableParticipants.Single(item => item.Id == staged.Id);
        model.AddParticipantCommand.Execute(null);
        Assert.Equal(staged.Id, Assert.Single(model.SelectedParticipants).Id);
        Assert.True(Assert.Single(model.SelectedParticipants).IsDraft);
        model.SaveCommand.Execute(null);
        Assert.Equal("Staged Person", Assert.Single(store.Read().Participants).Label);
    }

    [Fact]
    public void ChangingParticipantChoiceKeepsPendingNewLabelVisibleAndResolvable()
    {
        var store = new RecurringMemoryWorkspace();
        var existing = store.CreateParticipant("Alex");
        var model = Begin(new(store, Clock()));
        model.ParticipantToAdd = ParticipantChoice.New;
        model.NewParticipantLabel = "Taylor";
        model.ParticipantToAdd = model.AvailableParticipants.Single(item => item.Id == existing.Id);
        Assert.True(model.ShowNewParticipantEntry);
        model.AddParticipantCommand.Execute(null);
        Assert.True(model.ShowNewParticipantEntry);
        Assert.Equal("Taylor", model.NewParticipantLabel);
        model.SaveCommand.Execute(null);
        Assert.Contains("Add or cancel", model.ParticipantValidation, StringComparison.Ordinal);
        Assert.Empty(store.ReadRecurringTasks());
        model.AddNewParticipantCommand.Execute(null);
        Assert.Equal(2, model.SelectedParticipants.Count());
        Assert.False(model.ShowNewParticipantEntry);
        model.SaveCommand.Execute(null);
        Assert.Equal(2, store.Read().Participants.Count);
        Assert.Equal(2, Assert.Single(store.ReadRecurringTasks()).ParticipantIds.Count);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompactRecurringRowsUseSharedInteractionsAndInspectorScrollClearance(bool light)
    {
        var application = Assert.IsType<App>(Application.Current);
        application.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        var store = new RecurringMemoryWorkspace();
        var project = store.CreateProject("Household", "", "home", null);
        store.CreateParticipant("Alex");
        foreach (var title in new[] { "Review the week", "Water the plants", "Prepare the monthly household budget and review upcoming commitments" })
            store.CreateRecurringTask(new(title, "A short **Markdown** description.", project.Id, null, [], title.StartsWith("Prepare", StringComparison.Ordinal) ? RecurrenceCadence.Monthly : RecurrenceCadence.Weekly, new(2026, 11, 2), 5, true));
        var shell = new ShellViewModel(store, Clock());
        var window = new MainWindow { DataContext = shell, Width = 1120, Height = 600 };
        window.Show();
        try
        {
            shell.PrimaryNavigation.Single(item => item.Title == "Recurring").SelectCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var rows = window.GetVisualDescendants().OfType<Border>().Where(item => item.Classes.Contains("recurring-row")).ToArray();
            Assert.Equal(3, rows.Length);
            Assert.All(rows, row => Assert.InRange(row.Bounds.Height, 60, 90));
            Assert.Equal(rows[0].TranslatePoint(new Point(0, rows[0].Bounds.Height), window)!.Value.Y,
                rows[1].TranslatePoint(default, window)!.Value.Y);
            var first = Assert.Single(shell.Work!.Recurring.Definitions, item => item.Title == "Review the week");
            var titleButton = Find<Button>(window, first.EditAutomationId);
            Assert.Contains("task-title", titleButton.Classes);
            Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(titleButton.Background).Color);
            var rowPoint = rows[0].TranslatePoint(new Point(rows[0].Bounds.Width / 2, rows[0].Bounds.Height - 2), window)!.Value;
            Click(window, rowPoint);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Review the week", shell.Work.Recurring.Title);
            Assert.True(first.IsSelected);
            Assert.Contains("editing", AutomationProperties.GetItemStatus(titleButton), StringComparison.Ordinal);
            shell.Work.Recurring.HasDueDate = true;
            Dispatcher.UIThread.RunJobs();
            var editor = Assert.Single(window.GetVisualDescendants().OfType<RecurringTaskEditor>());
            var scroll = Assert.IsType<ScrollViewer>(editor.FindControl<ScrollViewer>("RecurringEditorScroll"));
            var content = Assert.IsType<StackPanel>(scroll.Content);
            Assert.Equal(new Thickness(0, 0, 32, 16), content.Margin);
            var participantPicker = Find<ComboBox>(window, "recurring-participant-picker");
            shell.Work.Recurring.ParticipantToAdd = ParticipantChoice.New;
            Dispatcher.UIThread.RunJobs();
            var newLabel = Find<TextBox>(window, "recurring-new-participant-label");
            Assert.True(newLabel.IsFocused);
            newLabel.Text = "Sam";
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Sam", Assert.Single(shell.Work.Recurring.SelectedParticipants).Label);
            Assert.True(participantPicker.IsKeyboardFocusWithin);
            shell.Work.Recurring.CancelCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var pause = Assert.Single(window.GetVisualDescendants().OfType<Button>(), button => button.IsEffectivelyVisible && AutomationProperties.GetName(button) == "Pause recurring Task Review the week");
            Click(window, pause.TranslatePoint(new Point(pause.Bounds.Width / 2, pause.Bounds.Height / 2), window)!.Value);
            Dispatcher.UIThread.RunJobs();
            Assert.False(shell.Work.Recurring.HasEditor);
            Assert.False(store.ReadRecurringTasks().Single(item => item.Title == "Review the week").IsEnabled);
            var delete = Find<Button>(window, shell.Work.Recurring.Definitions.First().DeleteAutomationId);
            Click(window, delete.TranslatePoint(new Point(delete.Bounds.Width / 2, delete.Bounds.Height / 2), window)!.Value);
            Dispatcher.UIThread.RunJobs();
            Assert.True(shell.Work.Recurring.NeedsDeleteConfirmation);
            Assert.False(shell.Work.Recurring.HasEditor);
            shell.Work.Recurring.CancelDeleteCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var selected = shell.Work.Recurring.Definitions.First();
            var keyboardTitle = Find<Button>(window, selected.EditAutomationId);
            Assert.True(keyboardTitle.Focus());
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
            Dispatcher.UIThread.RunJobs();
            Assert.True(shell.Work.Recurring.HasEditor);
            shell.Work.Recurring.CancelCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            keyboardTitle = Find<Button>(window, selected.EditAutomationId);
            Assert.True(keyboardTitle.Focus());
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Dispatcher.UIThread.RunJobs();
            Assert.True(shell.Work.Recurring.HasEditor);
            Assert.Equal("5", shell.Work.Recurring.DueOffset);
            Assert.Equal("5", Find<WholeDayOffsetPicker>(window, "recurring-due-offset").RawText);
            using var bitmap = new RenderTargetBitmap(new PixelSize(1120, 600), new Vector(96, 96));
            bitmap.Render(window);
            Stage(shell.Work.Recurring, "Morgan");
            shell.Work.Recurring.ParticipantToAdd = ParticipantChoice.New;
            shell.Work.Recurring.NewParticipantLabel = "Taylor";
            Dispatcher.UIThread.RunJobs();
            var offsetPicker = Find<WholeDayOffsetPicker>(window, "recurring-due-offset");
            Assert.Equal("5", shell.Work.Recurring.DueOffset);
            Assert.Equal("5", Assert.Single(offsetPicker.GetVisualDescendants().OfType<TextBox>()).Text);
            scroll.Offset = new Vector(0, scroll.Extent.Height);
            Dispatcher.UIThread.RunJobs();
            using var lower = new RenderTargetBitmap(new PixelSize(1120, 600), new Vector(96, 96));
            lower.Render(window);
            var numeric = Find<WholeDayOffsetPicker>(window, "recurring-due-offset");
            scroll.Offset = new Vector(0, Math.Max(0, numeric.TranslatePoint(default, content)!.Value.Y - 70));
            Dispatcher.UIThread.RunJobs();
            using var middle = new RenderTargetBitmap(new PixelSize(1120, 600), new Vector(96, 96));
            middle.Render(window);
        }
        finally { shell.Work!.Recurring.CancelCommand.Execute(null); window.Close(); application.RequestedThemeVariant = ThemeVariant.Dark; }
    }

    private static void Click(Window window, Point point)
    {
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
    }
    private static T Find<T>(Window window, string id) where T : Control => Assert.Single(window.GetVisualDescendants().OfType<T>(),
        item => item.IsEffectivelyVisible && AutomationProperties.GetAutomationId(item) == id);
}
