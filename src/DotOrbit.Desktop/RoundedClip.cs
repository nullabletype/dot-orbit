using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DotOrbit.Desktop;

public sealed class RoundedClip : AvaloniaObject
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<RoundedClip, Border, bool>("IsEnabled");

    static RoundedClip()
    {
        IsEnabledProperty.Changed.AddClassHandler<Border>(OnIsEnabledChanged);
    }

    private RoundedClip()
    {
    }

    public static bool GetIsEnabled(Border border) => border.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(Border border, bool value) => border.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(Border border, AvaloniaPropertyChangedEventArgs _)
    {
        border.SizeChanged -= OnBorderSizeChanged;
        if (border.GetValue(IsEnabledProperty))
        {
            border.SizeChanged += OnBorderSizeChanged;
            UpdateClip(border);
        }
        else
        {
            ClearClip(border);
        }
    }

    private static void OnBorderSizeChanged(object? sender, SizeChangedEventArgs _)
    {
        UpdateClip((Border)sender!);
    }

    private static void UpdateClip(Border border)
    {
        if (border.Child is not Control child)
        {
            return;
        }

        const double antiAliasGuard = 0.5;
        var radius = border.CornerRadius.TopLeft + antiAliasGuard;
        child.Clip = new RectangleGeometry(new Rect(child.Bounds.Size), radius, radius);
    }

    private static void ClearClip(Border border)
    {
        if (border.Child is Control child)
        {
            child.Clip = null;
        }
    }
}
