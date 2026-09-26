using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using DotOrbit.Desktop.ViewModels;
using DotOrbit.Desktop.Views;

namespace DotOrbit.Desktop;

internal readonly record struct NativeSmokeScenario(bool IsEnabled, NativeSmokeFailure Failure)
{
    private const string SmokeArgument = "--native-smoke";
    private const string StartupFailureArgument = "--native-smoke-failure=startup";
    private const string NavigationFailureArgument = "--native-smoke-failure=navigation";

    public static NativeSmokeScenario FromArguments(string[]? arguments)
    {
        if (arguments?.Contains(StartupFailureArgument, StringComparer.Ordinal) is true)
        {
            return new(true, NativeSmokeFailure.Startup);
        }

        if (arguments?.Contains(NavigationFailureArgument, StringComparer.Ordinal) is true)
        {
            return new(true, NativeSmokeFailure.NavigationAssertion);
        }

        return new(
            arguments?.Contains(SmokeArgument, StringComparer.Ordinal) is true,
            NativeSmokeFailure.None);
    }
}

internal enum NativeSmokeFailure
{
    None,
    Startup,
    NavigationAssertion,
}

internal static class NativeSmokeRunner
{
    internal const int StartupFailureExitCode = 20;
    internal const int NavigationFailureExitCode = 21;

    public static int Run(MainWindow window, NativeSmokeScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (scenario.Failure == NativeSmokeFailure.Startup)
        {
            return Fail(StartupFailureExitCode, "startup");
        }

        try
        {
            VerifyInitialView(window);
            VerifyKeyboardNavigation(window, scenario);
            Console.WriteLine("native-smoke: phase=complete result=passed shutdown=requested");
            return 0;
        }
        catch (NativeSmokeException exception)
        {
            return Fail(exception.ExitCode, exception.Phase);
        }
    }

    private static void VerifyInitialView(MainWindow window)
    {
        var shell = GetShell(window);
        Ensure(
            shell.ViewTitle == "Today" &&
            shell.SelectedItem.AutomationId == "navigation-today" &&
            GetDisplayedView(window) == "Today",
            StartupFailureExitCode,
            "startup");
        Console.WriteLine("native-smoke: phase=startup result=passed view=Today");
    }

    private static void VerifyKeyboardNavigation(MainWindow window, NativeSmokeScenario scenario)
    {
        var archive = window.GetVisualDescendants()
            .OfType<RadioButton>()
            .SingleOrDefault(control =>
                AutomationProperties.GetAutomationId(control) == "navigation-archive")
            ?? throw new NativeSmokeException(NavigationFailureExitCode, "navigation-control");

        var peer = ControlAutomationPeer.CreatePeerForElement(archive);
        Ensure(
            peer.GetAutomationId() == "navigation-archive" && peer.IsKeyboardFocusable(),
            NavigationFailureExitCode,
            "navigation-accessibility");

        window.Activate();
        peer.SetFocus();
        Ensure(peer.HasKeyboardFocus() && archive.IsFocused, NavigationFailureExitCode, "navigation-focus");

        archive.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Space,
            PhysicalKey = PhysicalKey.Space,
        });
        archive.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyUpEvent,
            Key = Key.Space,
            PhysicalKey = PhysicalKey.Space,
        });

        var shell = GetShell(window);
        var expectedView = scenario.Failure == NativeSmokeFailure.NavigationAssertion
            ? "Intentional assertion failure"
            : "Archive";
        Ensure(
            shell.ViewTitle == expectedView &&
            GetDisplayedView(window) == expectedView &&
            archive.IsChecked is true &&
            archive.IsFocused,
            NavigationFailureExitCode,
            "navigation-assertion");
        Console.WriteLine("native-smoke: phase=navigation result=passed view=Archive focus=usable");
    }

    private static string? GetDisplayedView(MainWindow window) =>
        window.FindControl<TextBlock>("CurrentViewTitleText")?.Text;

    private static ShellViewModel GetShell(MainWindow window)
    {
        Ensure(window.DataContext is ShellViewModel, StartupFailureExitCode, "startup-view-model");
        return (ShellViewModel)window.DataContext!;
    }

    private static int Fail(int exitCode, string phase)
    {
        Console.Error.WriteLine($"native-smoke: phase={phase} result=failed code={exitCode}");
        return exitCode;
    }

    private static void Ensure(bool condition, int exitCode, string phase)
    {
        if (!condition)
        {
            throw new NativeSmokeException(exitCode, phase);
        }
    }

    private sealed class NativeSmokeException(int exitCode, string phase) : Exception
    {
        public int ExitCode { get; } = exitCode;

        public string Phase { get; } = phase;
    }
}
