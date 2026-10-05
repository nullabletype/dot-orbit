using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.ViewModels;
using DotOrbit.Desktop.Views;
using DotOrbit.Storage.Sqlite;

namespace DotOrbit.Desktop;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.Args?.Contains("--markdown-link-smoke", StringComparer.Ordinal) == true)
            {
                ConfigureMarkdownLinkSmoke(desktop);
            }
            else if (desktop.Args?.Contains("--style-guide", StringComparer.Ordinal) == true)
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
                    desktop.MainWindow = new WorkspaceAccessWindow(
                        new EncryptedWorkspaceStore(),
                        SystemWorkspacePathProvider.FromArguments(
                            desktop.Args,
                            Environment.CurrentDirectory,
                            AppContext.BaseDirectory));
                }
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ConfigureMarkdownLinkSmoke(IClassicDesktopStyleApplicationLifetime desktop)
    {
        const int failureExitCode = 23;
        const string syntheticPassphrase = "synthetic markdown smoke passphrase";
        var directory = Path.Combine(Path.GetTempPath(), $"dot-orbit-markdown-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var passphrase = WorkspacePassphrase.Create(syntheticPassphrase, syntheticPassphrase).Passphrase;
        var category = CategoryName.Create("Markdown smoke").CategoryName;
        var created = passphrase is null || category is null
            ? WorkspaceCreationResult.Failed()
            : new EncryptedWorkspaceStore().Create(Path.Combine(directory, "workspace.db"), passphrase, category);
        if (created is not { Status: WorkspaceCreationStatus.Created, Session: { } session })
        {
            Console.Error.WriteLine($"markdown-link-smoke: result=failed code={failureExitCode}");
            desktop.Shutdown(failureExitCode);
            return;
        }

        var categoryId = session.Work.Read().Categories.Single().Id;
        var project = session.Work.CreateProject("Markdown smoke", "Read the plan", categoryId, null);
        var window = new MainWindow(session);
        window.Closed += (_, _) =>
        {
            session.Dispose();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        };
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        window.Opened += async (_, _) =>
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var shell = (ShellViewModel)window.DataContext!;
            shell.PrimaryNavigation.Single(item => item.Title == "Projects").SelectCommand.Execute(null);
            shell.Work!.SelectProject(project.Id);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var preview = window.FindControl<MarkdownPreviewSurface>("MarkdownPreviewButton")!;
            var editor = window.FindControl<TextBox>("MarkdownSource")!;
            for (var iteration = 0; iteration < 1; iteration++)
            {
                preview.Activate();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                editor.Text = "Read [the plan](https://example.test/plan";
                editor.CaretIndex = editor.Text.Length;
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                editor.Text += ")";
                editor.CaretIndex = editor.Text.Length;
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                shell.Work.FinishMarkdownEditing();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
            var renderedLink = preview.GetVisualDescendants().OfType<HyperlinkButton>().SingleOrDefault();
            var exitCode = renderedLink is null ? failureExitCode : 0;
            Console.WriteLine($"markdown-link-smoke: result={(exitCode == 0 ? "passed" : "failed")} code={exitCode}");
            desktop.Shutdown(exitCode);
        };
        desktop.MainWindow = window;
    }
}
