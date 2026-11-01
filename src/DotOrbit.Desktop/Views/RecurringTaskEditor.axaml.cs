using Avalonia.Controls;
using System.Globalization;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DotOrbit.Desktop.Markdown;
using DotOrbit.Desktop.ViewModels;

namespace DotOrbit.Desktop.Views;

public sealed partial class RecurringTaskEditor : UserControl
{
    private DateTime? _dateAtOpen;

    public RecurringTaskEditor()
    {
        InitializeComponent();
        RecurringMarkdownSource.AddHandler(KeyDownEvent, OnMarkdownKeyDown, RoutingStrategies.Tunnel);
    }
    private void OnDateValidationError(object? sender, CalendarDatePickerDateValidationErrorEventArgs e) => e.ThrowException = false;
    private void OnCalendarOpened(object? sender, EventArgs e)
    {
        if (sender is not InspectorDatePicker picker || DataContext is not RecurringTasksViewModel model) return;
        _dateAtOpen = picker.SelectedDate;
        picker.SetCurrentValue(CalendarDatePicker.DisplayDateProperty,
            picker.SelectedDate ?? model.CurrentDate.ToDateTime(TimeOnly.MinValue));
    }
    private void OnCalendarClosed(object? sender, EventArgs e)
    {
        if (sender is InspectorDatePicker { SelectedDate: { } selected } && selected != _dateAtOpen
            && DataContext is RecurringTasksViewModel model)
            model.FirstDate = selected.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        _dateAtOpen = null;
    }
    private void OnMarkdownActivated(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RecurringTasksViewModel model) return;
        model.IsEditingMarkdown = true;
        Dispatcher.UIThread.Post(() => RecurringMarkdownSource.Focus(), DispatcherPriority.ApplicationIdle);
    }
    private void OnMarkdownLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is RecurringTasksViewModel model) model.IsEditingMarkdown = false;
    }
    private void OnMarkdownKeyDown(object? sender, KeyEventArgs e) => MarkdownSourceEditor.TryHandleKeyDown(RecurringMarkdownSource, e);
}
