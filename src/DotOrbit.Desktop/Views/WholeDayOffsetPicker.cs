using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using System.Globalization;

namespace DotOrbit.Desktop.Views;

/// <summary>Keeps the visible numeric draft authoritative, including rejected typing and paste.</summary>
public sealed class WholeDayOffsetPicker : NumericUpDown
{
    public static readonly StyledProperty<string> RawTextProperty = AvaloniaProperty.Register<WholeDayOffsetPicker, string>(
        nameof(RawText), "0", defaultBindingMode: BindingMode.TwoWay);
    private TextBox? _editor;
    private bool _preservingInvalidInput;
    protected override Type StyleKeyOverride => typeof(NumericUpDown);
    public string RawText { get => GetValue(RawTextProperty); set => SetValue(RawTextProperty, value); }
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (_editor is not null) _editor.TextChanging -= OnTextChanging;
        base.OnApplyTemplate(e);
        _editor = e.NameScope.Find<TextBox>("PART_TextBox");
        var raw = RawText;
        if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var days) && days >= Minimum && days <= Maximum)
            SetCurrentValue(ValueProperty, (decimal?)days);
        SetCurrentValue(TextProperty, raw);
        if (_editor is not null)
        {
            _editor.Text = raw;
            _editor.TextChanging += OnTextChanging;
        }
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !IsValid(_editor?.Text ?? string.Empty)) { e.Handled = true; return; }
        base.OnKeyDown(e);
    }
    private bool IsValid(string raw) => int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var days)
        && days >= Minimum && days <= Maximum;
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        var raw = _editor?.Text ?? string.Empty;
        _preservingInvalidInput = !IsValid(raw);
        try
        {
            base.OnLostFocus(e);
            if (_preservingInvalidInput)
            {
                SetCurrentValue(TextProperty, raw);
                if (_editor is not null) _editor.Text = raw;
                SetCurrentValue(RawTextProperty, raw);
            }
        }
        finally { _preservingInvalidInput = false; }
    }
    private void OnTextChanging(object? sender, TextChangingEventArgs e)
    {
        if (!_preservingInvalidInput) SetCurrentValue(RawTextProperty, _editor?.Text ?? string.Empty);
    }
}
