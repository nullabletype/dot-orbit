using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DotOrbit.Desktop.Views;

namespace DotOrbit.Desktop;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.Args?.Contains("--style-guide", StringComparer.Ordinal) == true)
            {
                desktop.MainWindow = new StyleGuideWindow();
            }
            else
            {
                var smokeScenario = NativeSmokeScenario.FromArguments(desktop.Args);
                if (smokeScenario.IsEnabled)
                {
                    desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    var mainWindow = new MainWindow();
                    mainWindow.Opened += (_, _) =>
                    {
                        Dispatcher.UIThread.Post(
                            () =>
                            {
                                var exitCode = NativeSmokeRunner.Run(mainWindow, smokeScenario);
                                desktop.Shutdown(exitCode);
                            },
                            DispatcherPriority.ApplicationIdle);
                    };
                    desktop.MainWindow = mainWindow;
                }
                else
                {
                    desktop.MainWindow = new WorkspaceAccessWindow();
                }
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
