using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace DotOrbit.Desktop.Views;

public sealed class InspectorDatePicker : CalendarDatePicker
{
    public static readonly StyledProperty<string> RawTextProperty =
        AvaloniaProperty.Register<InspectorDatePicker, string>(
            nameof(RawText),
            string.Empty,
            defaultBindingMode: BindingMode.TwoWay);

    private Calendar? _calendar;
    private DateTime? _dateAtOpen;
    private DateTime? _confirmedDay;
    private TextBox? _editor;
    private string? _invalidTextBeingRestored;

    public InspectorDatePicker()
    {
        AddHandler(KeyDownEvent, OnPickerKeyDown, RoutingStrategies.Tunnel);
        CalendarOpened += (_, _) =>
        {
            _dateAtOpen = SelectedDate;
            _confirmedDay = null;
        };
        CalendarClosed += (_, _) =>
        {
            if (DateChosen is null) return;
            _calendar?.ClearValue(FocusableProperty);
            SetCurrentValue(SelectedDateProperty, _confirmedDay ?? _dateAtOpen);
            if (_confirmedDay.HasValue) DateChosen.Invoke(this, EventArgs.Empty);
        };
    }

    public event EventHandler? DateChosen;

    protected override Type StyleKeyOverride => typeof(CalendarDatePicker);

    public string RawText
    {
        get => GetValue(RawTextProperty);
        set => SetValue(RawTextProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (_calendar is not null)
        {
            _calendar.RemoveHandler(KeyDownEvent, OnCalendarKeyDown);
            _calendar.RemoveHandler(PointerReleasedEvent, OnCalendarPointerReleased);
        }
        if (_editor is not null)
        {
            _editor.TextChanging -= OnEditorTextChanging;
            _editor.RemoveHandler(KeyDownEvent, OnEditorKeyDown);
        }
        base.OnApplyTemplate(e);
        _editor = e.NameScope.Find<TextBox>("PART_TextBox")
            ?? this.GetVisualDescendants().OfType<TextBox>().SingleOrDefault();
        if (_editor is not null)
        {
            _editor.TextChanging += OnEditorTextChanging;
            _editor.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
        }
        _calendar = e.NameScope.Find<Calendar>("PART_Calendar");
        if (_calendar is not null)
        {
            // The native calendar closes during its bubble handlers. Capture confirmation first.
            _calendar.AddHandler(KeyDownEvent, OnCalendarKeyDown, RoutingStrategies.Tunnel);
            _calendar.AddHandler(PointerReleasedEvent, OnCalendarPointerReleased, RoutingStrategies.Tunnel);
        }
    }

    protected override void OnDateValidationError(CalendarDatePickerDateValidationErrorEventArgs e)
    {
        _invalidTextBeingRestored = e.Text;
        SetCurrentValue(RawTextProperty, e.Text);
        base.OnDateValidationError(e);
        Dispatcher.UIThread.Post(() =>
        {
            if (_editor is not null && _invalidTextBeingRestored is { } invalidText)
                _editor.Text = invalidText;
            _invalidTextBeingRestored = null;
        }, DispatcherPriority.Input);
    }

    private void OnPickerKeyDown(object? sender, KeyEventArgs e)
    {
        // The native popup calendar is not focusable in the Fluent picker template.
        if (!IsDropDownOpen && e.Key == Key.Down && e.KeyModifiers is KeyModifiers.Alt or KeyModifiers.Control
            && _calendar is not null && DateChosen is not null)
            _calendar.Focusable = true;
    }

    private void OnCalendarKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is Calendar { DisplayMode: CalendarMode.Month } && e.Key is Key.Enter or Key.Space)
            _confirmedDay = SelectedDate ?? DisplayDate;
    }

    private void OnCalendarPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || e.Source is not Visual source) return;
        var day = source as CalendarDayButton ?? source.FindAncestorOfType<CalendarDayButton>();
        if (day is { IsEffectivelyEnabled: true, DataContext: DateTime date }) _confirmedDay = date;
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !IsDropDownOpen && _invalidTextBeingRestored is null)
            DateChosen?.Invoke(this, EventArgs.Empty);
    }

    private void OnEditorTextChanging(object? sender, TextChangingEventArgs e)
    {
        // Arrow keys preview dates in the native popup before Enter, Space or a day click.
        if (IsDropDownOpen && DateChosen is not null) return;
        var text = _editor?.Text ?? string.Empty;
        if (_invalidTextBeingRestored is { } invalidText && text != invalidText) return;
        SetCurrentValue(RawTextProperty, text);
    }
}
