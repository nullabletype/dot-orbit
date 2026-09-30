using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
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

    private TextBox? _editor;
    private string? _invalidTextBeingRestored;

    protected override Type StyleKeyOverride => typeof(CalendarDatePicker);

    public string RawText
    {
        get => GetValue(RawTextProperty);
        set => SetValue(RawTextProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (_editor is not null) _editor.TextChanging -= OnEditorTextChanging;
        base.OnApplyTemplate(e);
        _editor = e.NameScope.Find<TextBox>("PART_TextBox")
            ?? this.GetVisualDescendants().OfType<TextBox>().SingleOrDefault();
        if (_editor is not null) _editor.TextChanging += OnEditorTextChanging;
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

    private void OnEditorTextChanging(object? sender, TextChangingEventArgs e)
    {
        var text = _editor?.Text ?? string.Empty;
        if (_invalidTextBeingRestored is { } invalidText && text != invalidText) return;
        SetCurrentValue(RawTextProperty, text);
    }
}
