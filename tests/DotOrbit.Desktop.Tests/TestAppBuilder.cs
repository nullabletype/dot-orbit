using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using DotOrbit.Desktop;

[assembly: AvaloniaTestApplication(typeof(DotOrbit.Desktop.Tests.TestAppBuilder))]

namespace DotOrbit.Desktop.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
