using Avalonia.Controls;

namespace DotOrbit.Desktop;

internal readonly record struct MainWindowSize(double Width, double Height);

internal readonly record struct MainWindowSizeRestoration(
    MainWindowSize Size,
    MainWindowSize MinimumSize);

internal interface IMainWindowSizePreferenceStore
{
    MainWindowSize? LoadMainWindowSize();

    bool TrySaveMainWindowSize(MainWindowSize size);
}

internal interface IMainWindowScreenProvider
{
    MainWindowSize? GetUsableLogicalSize(Window window);
}

internal static class MainWindowSizePolicy
{
    internal static MainWindowSize DefaultSize { get; } = new(1180, 760);

    internal static MainWindowSize SupportedMinimumSize { get; } = new(1120, 600);

    internal static MainWindowSizeRestoration Restore(
        MainWindowSize? savedSize,
        MainWindowSize? usableScreenSize,
        MainWindowSize? fallbackSize = null,
        MainWindowSize? frameOverhead = null)
    {
        var fallback = IsValid(fallbackSize) ? fallbackSize!.Value : DefaultSize;
        if (!IsValid(usableScreenSize))
        {
            return new(DefaultSize, SupportedMinimumSize);
        }

        var usable = SubtractFrameOverhead(usableScreenSize!.Value, frameOverhead);
        var candidate = IsValid(savedSize) ? savedSize!.Value : fallback;
        var minimum = new MainWindowSize(
            Math.Min(SupportedMinimumSize.Width, usable.Width),
            Math.Min(SupportedMinimumSize.Height, usable.Height));
        return new(
            new MainWindowSize(
                Math.Clamp(candidate.Width, minimum.Width, usable.Width),
                Math.Clamp(candidate.Height, minimum.Height, usable.Height)),
            minimum);
    }

    internal static MainWindowSize CalculateFrameOverhead(
        MainWindowSize clientSize,
        MainWindowSize? frameSize)
    {
        if (!IsValid(clientSize) || !IsValid(frameSize))
        {
            return new(0, 0);
        }

        return new(
            Math.Max(0, frameSize!.Value.Width - clientSize.Width),
            Math.Max(0, frameSize.Value.Height - clientSize.Height));
    }

    internal static MainWindowSize RememberNormalSize(
        MainWindowSize rememberedSize,
        MainWindowSize reportedSize,
        WindowState windowState,
        WindowResizeReason reason) =>
        windowState == WindowState.Normal
        && reason != WindowResizeReason.DpiChange
        && IsValid(reportedSize)
            ? reportedSize
            : rememberedSize;

    internal static bool IsValid(MainWindowSize? size) =>
        size is { } value
        && double.IsFinite(value.Width)
        && double.IsFinite(value.Height)
        && value.Width > 0
        && value.Height > 0;

    private static MainWindowSize SubtractFrameOverhead(
        MainWindowSize usableScreenSize,
        MainWindowSize? frameOverhead)
    {
        var overhead = frameOverhead is { } value
                       && double.IsFinite(value.Width)
                       && double.IsFinite(value.Height)
                       && value.Width >= 0
                       && value.Height >= 0
            ? value
            : new MainWindowSize(0, 0);
        return new(
            Math.Max(1, usableScreenSize.Width - overhead.Width),
            Math.Max(1, usableScreenSize.Height - overhead.Height));
    }
}

internal sealed class AvaloniaMainWindowScreenProvider : IMainWindowScreenProvider
{
    public MainWindowSize? GetUsableLogicalSize(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        try
        {
            var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
            if (screen is null || !double.IsFinite(screen.Scaling) || screen.Scaling <= 0)
            {
                return null;
            }

            return ToLogicalSize(
                screen.WorkingArea.Width,
                screen.WorkingArea.Height,
                screen.Scaling);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    internal static MainWindowSize? ToLogicalSize(
        double pixelWidth,
        double pixelHeight,
        double scaling)
    {
        if (!double.IsFinite(pixelWidth)
            || !double.IsFinite(pixelHeight)
            || !double.IsFinite(scaling)
            || pixelWidth <= 0
            || pixelHeight <= 0
            || scaling <= 0)
        {
            return null;
        }

        return new(pixelWidth / scaling, pixelHeight / scaling);
    }
}

internal sealed class TransientMainWindowSizePreferenceStore(
    MainWindowSize? size = null) : IMainWindowSizePreferenceStore
{
    public MainWindowSize? LoadMainWindowSize() => size;

    public bool TrySaveMainWindowSize(MainWindowSize value) =>
        MainWindowSizePolicy.IsValid(value);
}
