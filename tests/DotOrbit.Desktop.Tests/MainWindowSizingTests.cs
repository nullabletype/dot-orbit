using Avalonia.Controls;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class MainWindowSizingTests
{
    [Theory]
    [InlineData(2880, 1800, 2, 1440, 900)]
    [InlineData(2400, 1350, 1.25, 1920, 1080)]
    [InlineData(1920, 1080, 1.5, 1280, 720)]
    [InlineData(1280, 720, 1, 1280, 720)]
    public void LogicalWorkingAreaConvertsPixelsWithoutCompoundingScaling(
        double pixelWidth,
        double pixelHeight,
        double scaling,
        double expectedWidth,
        double expectedHeight)
    {
        var logical = AvaloniaMainWindowScreenProvider.ToLogicalSize(
            pixelWidth,
            pixelHeight,
            scaling);

        Assert.Equal(new MainWindowSize(expectedWidth, expectedHeight), logical);
    }

    [Theory]
    [InlineData(0, 720, 1)]
    [InlineData(1280, 0, 1)]
    [InlineData(1280, 720, 0)]
    [InlineData(1280, 720, double.NaN)]
    [InlineData(double.PositiveInfinity, 720, 1)]
    public void LogicalWorkingAreaRejectsInvalidPlatformValues(
        double pixelWidth,
        double pixelHeight,
        double scaling)
    {
        Assert.Null(AvaloniaMainWindowScreenProvider.ToLogicalSize(
            pixelWidth,
            pixelHeight,
            scaling));
    }

    [Fact]
    public void WindowSizeRestoreClampsOversizedValuesToUsableArea()
    {
        var restoration = MainWindowSizePolicy.Restore(
            new MainWindowSize(4000, 3000),
            new MainWindowSize(1280, 720));

        Assert.Equal(new MainWindowSize(1280, 720), restoration.Size);
        Assert.Equal(new MainWindowSize(1120, 600), restoration.MinimumSize);
    }

    [Fact]
    public void WindowSizeRestoreRaisesUndersizedValuesToSupportedMinimum()
    {
        var restoration = MainWindowSizePolicy.Restore(
            new MainWindowSize(640, 480),
            new MainWindowSize(1600, 1000));

        Assert.Equal(new MainWindowSize(1120, 600), restoration.Size);
        Assert.Equal(new MainWindowSize(1120, 600), restoration.MinimumSize);
    }

    [Fact]
    public void WindowSizeRestoreLowersOnlyTheConstrainedMinimumAxis()
    {
        var restoration = MainWindowSizePolicy.Restore(
            new MainWindowSize(1180, 760),
            new MainWindowSize(1000, 900));

        Assert.Equal(new MainWindowSize(1000, 760), restoration.Size);
        Assert.Equal(new MainWindowSize(1000, 600), restoration.MinimumSize);
    }

    [Fact]
    public void WindowSizeRestoreUsesEntireWorkingAreaWhenBothAxesAreConstrained()
    {
        var restoration = MainWindowSizePolicy.Restore(
            new MainWindowSize(1180, 760),
            new MainWindowSize(960, 540));

        Assert.Equal(new MainWindowSize(960, 540), restoration.Size);
        Assert.Equal(new MainWindowSize(960, 540), restoration.MinimumSize);
    }

    [Fact]
    public void WindowSizeRestoreKeepsTheDecoratedFrameInsideTheWorkingArea()
    {
        var restoration = MainWindowSizePolicy.Restore(
            new MainWindowSize(1400, 900),
            new MainWindowSize(1000, 700),
            frameOverhead: new MainWindowSize(16, 38));

        Assert.Equal(new MainWindowSize(984, 662), restoration.Size);
        Assert.Equal(new MainWindowSize(984, 600), restoration.MinimumSize);
        Assert.Equal(
            new MainWindowSize(1000, 700),
            new MainWindowSize(
                restoration.Size.Width + 16,
                restoration.Size.Height + 38));
    }

    [Fact]
    public void WindowFrameOverheadUsesOnlyPositiveDecorationDimensions()
    {
        Assert.Equal(
            new MainWindowSize(16, 38),
            MainWindowSizePolicy.CalculateFrameOverhead(
                new MainWindowSize(1180, 760),
                new MainWindowSize(1196, 798)));
        Assert.Equal(
            new MainWindowSize(0, 0),
            MainWindowSizePolicy.CalculateFrameOverhead(
                new MainWindowSize(1180, 760),
                new MainWindowSize(1170, 750)));
    }

    [Fact]
    public void WindowSizeRestoreUsesDefaultWhenScreenInformationIsUnavailable()
    {
        var restoration = MainWindowSizePolicy.Restore(
            new MainWindowSize(1400, 900),
            usableScreenSize: null,
            fallbackSize: new MainWindowSize(1500, 950));

        Assert.Equal(MainWindowSizePolicy.DefaultSize, restoration.Size);
        Assert.Equal(MainWindowSizePolicy.SupportedMinimumSize, restoration.MinimumSize);
    }

    [Fact]
    public void WindowSizeRestoreUsesTheConfiguredFallbackWhenThePreferenceIsInvalid()
    {
        var restoration = MainWindowSizePolicy.Restore(
            new MainWindowSize(double.NaN, 700),
            new MainWindowSize(1600, 1000),
            new MainWindowSize(1240, 780));

        Assert.Equal(new MainWindowSize(1240, 780), restoration.Size);
    }

    [Fact]
    public void WindowSizeCaptureAcceptsAValidNormalResize()
    {
        var remembered = MainWindowSizePolicy.RememberNormalSize(
            new MainWindowSize(1180, 760),
            new MainWindowSize(1320, 740),
            WindowState.Normal,
            WindowResizeReason.User);

        Assert.Equal(new MainWindowSize(1320, 740), remembered);
    }

    [Theory]
    [InlineData(0, 740)]
    [InlineData(1320, -1)]
    [InlineData(double.NaN, 740)]
    public void WindowSizeCaptureIgnoresInvalidNormalResize(
        double width,
        double height)
    {
        var original = new MainWindowSize(1180, 760);

        var remembered = MainWindowSizePolicy.RememberNormalSize(
            original,
            new MainWindowSize(width, height),
            WindowState.Normal,
            WindowResizeReason.User);

        Assert.Equal(original, remembered);
    }

    [Theory]
    [InlineData(WindowState.Maximized, WindowResizeReason.User)]
    [InlineData(WindowState.Minimized, WindowResizeReason.User)]
    [InlineData(WindowState.FullScreen, WindowResizeReason.User)]
    [InlineData(WindowState.Normal, WindowResizeReason.DpiChange)]
    public void WindowSizeCaptureIgnoresTransientWindowStatesAndDpiChanges(
        WindowState state,
        WindowResizeReason reason)
    {
        var original = new MainWindowSize(1180, 760);

        var remembered = MainWindowSizePolicy.RememberNormalSize(
            original,
            new MainWindowSize(1920, 1080),
            state,
            reason);

        Assert.Equal(original, remembered);
    }
}
