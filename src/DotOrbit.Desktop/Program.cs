using Avalonia;

namespace DotOrbit.Desktop;

internal static class Program
{
    internal static PerformanceTraceSession? TraceSession { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        using var trace = PerformanceTraceSession.TryStart(args, AppContext.BaseDirectory);
        TraceSession = trace;
        try { return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        finally { TraceSession = null; }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
