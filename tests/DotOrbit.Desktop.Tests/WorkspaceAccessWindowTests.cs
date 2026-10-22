using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.ViewModels;
using DotOrbit.Desktop.Views;
using DotOrbit.Storage.Sqlite;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class WorkspaceAccessWindowTests
{
    [AvaloniaFact]
    public async Task SuccessfulUnlockAppliesRememberedMainWindowGeometry()
    {
        var session = new StubWorkspaceSession();
        var store = new StubWorkspaceStore(
            exists: true,
            WorkspaceOpenResult.Opened(session));
        var preferences = new RecordingMainWindowSizePreferenceStore(new(1270, 710));
        MainWindow? mainWindow = null;
        var access = new WorkspaceAccessWindow(
            store,
            new StubWorkspacePathProvider("/platform-data/dot-orbit/workspace.orb"),
            preferences,
            new FixedMainWindowScreenProvider(new(1600, 1000)),
            opened => mainWindow = opened);
        access.Show();
        var viewModel = Assert.IsType<WorkspaceAccessViewModel>(access.DataContext);
        viewModel.Passphrase = "correct horse battery";

        viewModel.SubmitCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var openedMainWindow = Assert.IsType<MainWindow>(mainWindow);
        Assert.False(access.IsVisible);
        Assert.True(openedMainWindow.IsVisible);
        Assert.Equal(1270, openedMainWindow.Width);
        Assert.Equal(710, openedMainWindow.Height);
        Assert.Equal(1, preferences.LoadAttempts);
        Assert.Equal(0, preferences.SaveAttempts);

        openedMainWindow.Close();
        await DrainUiUntilAsync(() => !openedMainWindow.IsVisible);
        Assert.Equal(1, preferences.SaveAttempts);
    }

    [AvaloniaFact]
    public void StartupUsesThePlatformWorkspacePathProvider()
    {
        var store = new StubWorkspaceStore(exists: false);

        _ = new WorkspaceAccessWindow(
            store,
            new StubWorkspacePathProvider("/platform-data/dot-orbit/workspace.orb"));

        Assert.Equal("/platform-data/dot-orbit/workspace.orb", store.LastExistsPath);
    }

    [AvaloniaFact]
    public void FirstRunFieldsExposeAccessibleNamesHelpAndLogicalTabOrder()
    {
        var window = CreateWindow(exists: false);
        window.Show();

        var category = Assert.IsType<TextBox>(window.FindControl<TextBox>("FirstCategoryTextBox"));
        var passphrase = Assert.IsType<TextBox>(window.FindControl<TextBox>("PassphraseTextBox"));
        var confirmation = Assert.IsType<TextBox>(window.FindControl<TextBox>("ConfirmationTextBox"));
        var reveal = Assert.IsType<ToggleButton>(window.FindControl<ToggleButton>("PassphraseVisibilityToggle"));
        var submit = Assert.IsType<Button>(window.FindControl<Button>("SubmitButton"));

        Assert.Equal("First Category name", AutomationProperties.GetName(category));
        Assert.Equal("Passphrase", AutomationProperties.GetName(passphrase));
        Assert.Equal("Confirm passphrase", AutomationProperties.GetName(confirmation));
        Assert.Equal("Show passphrase", AutomationProperties.GetName(reveal));
        Assert.Equal("Create workspace", AutomationProperties.GetName(submit));
        Assert.Contains("paste", AutomationProperties.GetHelpText(passphrase), StringComparison.OrdinalIgnoreCase);
        Assert.True(category.Focusable);
        Assert.True(passphrase.Focusable);
        Assert.True(confirmation.Focusable);
        Assert.True(reveal.Focusable);
        Assert.True(submit.Focusable);
        Assert.Equal([0, 1, 2, 3, 5], new[]
        {
            category.TabIndex,
            passphrase.TabIndex,
            confirmation.TabIndex,
            reveal.TabIndex,
            submit.TabIndex,
        });
        Assert.Contains(
            "no recovery",
            window.FindControl<TextBlock>("NoRecoveryWarning")?.Text,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "16",
            window.FindControl<TextBlock>("PassphraseGuidance")?.Text,
            StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void ShowHideIsKeyboardOperableAndExposesItsChangedState()
    {
        var window = CreateWindow(exists: false);
        window.Show();
        var passphrase = Assert.IsType<TextBox>(window.FindControl<TextBox>("PassphraseTextBox"));
        var confirmation = Assert.IsType<TextBox>(window.FindControl<TextBox>("ConfirmationTextBox"));
        var reveal = Assert.IsType<ToggleButton>(window.FindControl<ToggleButton>("PassphraseVisibilityToggle"));
        var originalMask = passphrase.PasswordChar;

        Assert.True(reveal.Focus());
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);

        Assert.True(reveal.IsChecked);
        Assert.Equal('\0', passphrase.PasswordChar);
        Assert.Equal('\0', confirmation.PasswordChar);
        Assert.NotEqual(originalMask, passphrase.PasswordChar);
        Assert.Equal("Hide passphrase", AutomationProperties.GetName(reveal));
    }

    [AvaloniaFact]
    public void UnlockFailureIsAnnouncedAndReturnsFocusToPassphrase()
    {
        var store = new StubWorkspaceStore(exists: true);
        var viewModel = new WorkspaceAccessViewModel(store, "/data/workspace.db", _ => { });
        var window = new WorkspaceAccessWindow(viewModel);
        window.Show();
        var passphrase = Assert.IsType<TextBox>(window.FindControl<TextBox>("PassphraseTextBox"));
        var submit = Assert.IsType<Button>(window.FindControl<Button>("SubmitButton"));
        passphrase.Text = "a wrong passphrase";

        Assert.True(submit.Focus());
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);

        var validation = Assert.IsType<TextBlock>(window.FindControl<TextBlock>("ValidationMessage"));
        Assert.True(validation.IsVisible);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(validation));
        Assert.True(passphrase.IsFocused);
        Assert.DoesNotContain("a wrong passphrase", validation.Text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void MigrationFailureRecoveryPathIsWrappedAnnouncedAndReturnsFocus()
    {
        var recoveryPath = "/a/long/recovery/location/dot-orbit-pre-migration-v1-20260927T1200000000000Z-identifier.dotorbit-recovery";
        var store = new StubWorkspaceStore(
            exists: true,
            WorkspaceOpenResult.MigrationFailed(recoveryPath));
        var viewModel = new WorkspaceAccessViewModel(store, "/data/workspace.db", _ => { });
        var window = new WorkspaceAccessWindow(viewModel);
        window.Show();
        var passphrase = Assert.IsType<TextBox>(window.FindControl<TextBox>("PassphraseTextBox"));
        var submit = Assert.IsType<Button>(window.FindControl<Button>("SubmitButton"));
        passphrase.Text = "correct horse battery";

        Assert.True(submit.Focus());
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);

        var validation = Assert.IsType<TextBlock>(window.FindControl<TextBlock>("ValidationMessage"));
        var restore = Assert.IsType<Button>(
            window.FindControl<Button>("RestoreMigrationRecoveryButton"));
        Assert.True(validation.IsVisible);
        Assert.Equal(TextWrapping.Wrap, validation.TextWrapping);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(validation));
        Assert.Contains(recoveryPath, validation.Text, StringComparison.Ordinal);
        Assert.True(restore.IsVisible);
        Assert.Equal("Restore pre-upgrade workspace", AutomationProperties.GetName(restore));
        Assert.Equal(4, restore.TabIndex);
        Assert.True(passphrase.IsFocused);

        Assert.True(restore.Focus());
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);

        Assert.False(restore.IsVisible);
        Assert.Contains("was restored", validation.Text, StringComparison.Ordinal);
        Assert.True(passphrase.IsFocused);
    }

    [AvaloniaFact]
    public void BlockedStartupIsAssertivelyAnnouncedWithNoEnabledWorkspaceActions()
    {
        var store = new StubWorkspaceStore(exists: true);
        var viewModel = new WorkspaceAccessViewModel(
            store,
            new DefaultWorkspaceResolution(
                DefaultWorkspaceResolutionStatus.Conflict,
                "/private/user/workspace.orb"),
            _ => { });
        var window = new WorkspaceAccessWindow(viewModel);
        window.Show();

        var message = Assert.IsType<TextBlock>(
            window.FindControl<TextBlock>("WorkspaceResolutionMessage"));
        var passphrase = Assert.IsType<TextBox>(
            window.FindControl<TextBox>("PassphraseTextBox"));
        var reveal = Assert.IsType<ToggleButton>(
            window.FindControl<ToggleButton>("PassphraseVisibilityToggle"));
        var submit = Assert.IsType<Button>(window.FindControl<Button>("SubmitButton"));

        Assert.True(message.IsVisible);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(message));
        Assert.Equal("Workspace startup blocked", AutomationProperties.GetName(message));
        Assert.DoesNotContain("/private", message.Text, StringComparison.Ordinal);
        Assert.False(passphrase.IsVisible);
        Assert.False(reveal.IsVisible);
        Assert.False(submit.IsVisible);
        Assert.False(viewModel.SubmitCommand.CanExecute(null));
        Assert.False(viewModel.TogglePassphraseVisibilityCommand.CanExecute(null));
        Assert.False(viewModel.RestoreMigrationRecoveryCommand.CanExecute(null));
    }

    private static WorkspaceAccessWindow CreateWindow(bool exists) =>
        new(new WorkspaceAccessViewModel(
            new StubWorkspaceStore(exists),
            "/data/workspace.db",
            _ => { }));

    private static async Task DrainUiUntilAsync(Func<bool> condition)
    {
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private sealed class RecordingMainWindowSizePreferenceStore(MainWindowSize loadedSize)
        : IMainWindowSizePreferenceStore
    {
        public int LoadAttempts { get; private set; }

        public int SaveAttempts { get; private set; }

        public MainWindowSize? LoadMainWindowSize()
        {
            LoadAttempts++;
            return loadedSize;
        }

        public bool TrySaveMainWindowSize(MainWindowSize size)
        {
            SaveAttempts++;
            return true;
        }
    }

    private sealed class FixedMainWindowScreenProvider(MainWindowSize size)
        : IMainWindowScreenProvider
    {
        public MainWindowSize? GetUsableLogicalSize(Window window) => size;
    }

    private sealed class StubWorkspaceSession : IWorkspaceSession
    {
        public IWorkspaceWork Work { get; } = new MemoryWorkspaceWork();

        public int SchemaVersion => 1;

        public string FirstCategoryName => "Home";

        public IWorkspaceRecovery Recovery => throw new NotSupportedException();

        public DotOrbit.Core.Interchange.IProposalWorkspaceSource Proposals =>
            throw new NotSupportedException();

        public DotOrbit.Core.Interchange.IPlanningSnapshotSource Planning =>
            throw new NotSupportedException();

        public PassphraseRotationResult RotatePassphrase(
            WorkspacePassphrase currentPassphrase,
            WorkspacePassphrase newPassphrase) =>
            PassphraseRotationResult.Failed();

        public void Dispose()
        {
        }
    }

    private sealed class StubWorkspaceStore(
        bool exists,
        WorkspaceOpenResult? openResult = null) : IWorkspaceStore
    {
        public string? LastExistsPath { get; private set; }

        public bool Exists(string path)
        {
            LastExistsPath = path;
            return exists;
        }

        public WorkspaceCreationResult Create(
            string path,
            WorkspacePassphrase passphrase,
            CategoryName firstCategory) => WorkspaceCreationResult.Failed();

        public WorkspaceOpenResult Open(string path, WorkspacePassphrase passphrase) =>
            openResult ?? WorkspaceOpenResult.InvalidPassphraseOrStore();

        public MigrationRecoveryRestoreResult RestoreMigrationRecovery(
            string workspacePath,
            WorkspacePassphrase passphrase,
            string recoveryPointPath) => MigrationRecoveryRestoreResult.Restored();
    }

    private sealed class StubWorkspacePathProvider(string path) : IWorkspacePathProvider
    {
        public string GetDefaultWorkspacePath() => path;

        public DefaultWorkspaceResolution ResolveDefaultWorkspace() =>
            DefaultWorkspaceResolution.Ready(path);
    }
}
