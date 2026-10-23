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
            UpdateClip(border, border.Bounds.Size);
        }
        else
        {
            border.Clip = null;
        }
    }

    private static void OnBorderSizeChanged(object? sender, SizeChangedEventArgs args)
    {
        UpdateClip((Border)sender!, args.NewSize);
    }

    private static void UpdateClip(Border border, Size size)
    {
        var thickness = border.BorderThickness;
        var borderInset = Math.Max(
            Math.Max(thickness.Left, thickness.Top),
            Math.Max(thickness.Right, thickness.Bottom));
        var radius = border.CornerRadius.TopLeft + borderInset;
        border.Clip = new RectangleGeometry(new Rect(size), radius, radius);
    }
}
