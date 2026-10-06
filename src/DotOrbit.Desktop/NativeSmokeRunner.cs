using System.Reflection;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.ViewModels;
using DotOrbit.Desktop.Views;
using DotOrbit.Storage.Sqlite;

namespace DotOrbit.Desktop;

internal readonly record struct NativeSmokeScenario(
    bool IsEnabled,
    bool ExercisesPackagedWorkspace,
    NativeSmokeFailure Failure)
{
    private const string SmokeArgument = "--native-smoke";
    private const string PackageSmokeArgument = "--package-smoke";
    private const string StartupFailureArgument = "--native-smoke-failure=startup";
    private const string NavigationFailureArgument = "--native-smoke-failure=navigation";

    public static NativeSmokeScenario FromArguments(string[]? arguments)
    {
        if (arguments?.Contains(StartupFailureArgument, StringComparer.Ordinal) is true)
        {
            return new(true, false, NativeSmokeFailure.Startup);
        }

        if (arguments?.Contains(NavigationFailureArgument, StringComparer.Ordinal) is true)
        {
            return new(true, false, NativeSmokeFailure.NavigationAssertion);
        }

        var exercisesPackagedWorkspace =
            arguments?.Contains(PackageSmokeArgument, StringComparer.Ordinal) is true;

        return new(
            exercisesPackagedWorkspace
            || arguments?.Contains(SmokeArgument, StringComparer.Ordinal) is true,
            exercisesPackagedWorkspace,
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

        if (scenario.ExercisesPackagedWorkspace)
        {
            var workspaceExitCode = PackagedWorkspaceSmoke.Run();
            if (workspaceExitCode != 0)
            {
                return workspaceExitCode;
            }
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

internal static class PackagedWorkspaceSmoke
{
    internal const int WorkspaceFailureExitCode = 22;

    private const string PortableRecoveryEnvironmentVariable = "DOTORBIT_PACKAGE_SMOKE_RECOVERY";
    private const string VersionEnvironmentVariable = "DOTORBIT_PACKAGE_SMOKE_VERSION";
    private const string SyntheticPassphrase = "dot-orbit sample only";
    private const string SyntheticCategory = "Package smoke";
    private const string PortableRecoveryCategory = "Work";

    public static int Run()
    {
        var expectedVersion = Environment.GetEnvironmentVariable(VersionEnvironmentVariable);
        var actualVersion = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!MatchesExpectedVersion(expectedVersion, actualVersion))
        {
            return Fail("version");
        }

        return Run(
            Path.Combine(
                Path.GetTempPath(),
                $"dot-orbit-package-smoke-{Guid.NewGuid():N}"),
            Environment.GetEnvironmentVariable(PortableRecoveryEnvironmentVariable));
    }

    internal static bool MatchesExpectedVersion(string? expectedVersion, string? actualVersion) =>
        !string.IsNullOrWhiteSpace(expectedVersion)
        && string.Equals(expectedVersion, actualVersion, StringComparison.Ordinal);

    internal static int Run(string directory) => Run(directory, null);

    internal static int Run(string directory, string? portableRecoveryFixturePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        int exitCode;
        try
        {
            exitCode = ExerciseWorkspace(directory, portableRecoveryFixturePath);
        }
        catch (IOException)
        {
            exitCode = Fail();
        }
        catch (UnauthorizedAccessException)
        {
            exitCode = Fail();
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // A failed setup may not have created the temporary directory.
        }
        catch (IOException)
        {
            return Fail();
        }
        catch (UnauthorizedAccessException)
        {
            return Fail();
        }

        return exitCode;
    }

    private static int ExerciseWorkspace(
        string directory,
        string? portableRecoveryFixturePath)
    {
        Directory.CreateDirectory(directory);
        var workspacePath = Path.Combine(directory, "workspace.orb");
        var store = new EncryptedWorkspaceStore();
        var passphrase = WorkspacePassphrase.Create(
            SyntheticPassphrase,
            SyntheticPassphrase).Passphrase;
        var category = CategoryName.Create(SyntheticCategory).CategoryName;
        if (passphrase is null || category is null)
        {
            return Fail();
        }

        var created = store.Create(workspacePath, passphrase, category);
        using (created.Session)
        {
            if (created.Status != WorkspaceCreationStatus.Created || created.Session is null)
            {
                return Fail();
            }
        }

        var opened = store.Open(
            workspacePath,
            WorkspacePassphrase.ForUnlock(SyntheticPassphrase)!);
        using (opened.Session)
        {
            if (opened.Status != WorkspaceOpenStatus.Opened
                || opened.Session is null
                || opened.Session.SchemaVersion != EncryptedWorkspaceStore.CurrentSchemaVersion
                || !string.Equals(
                    opened.Session.FirstCategoryName,
                    SyntheticCategory,
                    StringComparison.Ordinal))
            {
                return Fail();
            }
        }

        if (!string.IsNullOrWhiteSpace(portableRecoveryFixturePath)
            && !RestorePortableRecovery(
                store,
                workspacePath,
                portableRecoveryFixturePath,
                directory))
        {
            return Fail("portable-recovery");
        }

        Console.WriteLine("package-smoke: phase=encrypted-workspace result=passed");
        return 0;
    }

    private static bool RestorePortableRecovery(
        EncryptedWorkspaceStore store,
        string workspacePath,
        string portableRecoveryFixturePath,
        string directory)
    {
        var current = store.Open(
            workspacePath,
            WorkspacePassphrase.ForUnlock(SyntheticPassphrase)!);
        using var currentSession = current.Session;
        if (current.Status != WorkspaceOpenStatus.Opened || currentSession is null)
        {
            return false;
        }

        var restored = currentSession.Recovery.Restore(
            portableRecoveryFixturePath,
            Path.Combine(directory, "pre-restore-recovery"));
        using var restoredSession = restored.Session;
        if (restored.Status != WorkspaceRestoreStatus.Restored
            || restoredSession is null
            || restoredSession.SchemaVersion != EncryptedWorkspaceStore.CurrentSchemaVersion
            || !string.Equals(
                restoredSession.FirstCategoryName,
                PortableRecoveryCategory,
                StringComparison.Ordinal))
        {
            return false;
        }

        Console.WriteLine("package-smoke: phase=portable-recovery result=passed");
        return true;
    }

    private static int Fail(string phase = "encrypted-workspace")
    {
        Console.Error.WriteLine(
            $"package-smoke: phase={phase} result=failed code={WorkspaceFailureExitCode}");
        return WorkspaceFailureExitCode;
    }
}
