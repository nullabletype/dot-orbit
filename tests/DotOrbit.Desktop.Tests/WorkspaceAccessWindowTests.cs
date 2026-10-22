using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Layout;
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
            opened => mainWindow = opened,
            workspacePathAvailability: new StoreWorkspacePathAvailability(store));
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
            new StubWorkspacePathProvider("/platform-data/dot-orbit/workspace.orb"),
            workspacePathAvailability: new StoreWorkspacePathAvailability(store));

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
        Assert.Equal([4, 5, 6, 7, 9], new[]
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
        Assert.DoesNotContain(recoveryPath, validation.Text, StringComparison.Ordinal);
        Assert.Contains("validated recovery point", validation.Text, StringComparison.Ordinal);
        Assert.True(restore.IsVisible);
        Assert.Equal("Restore pre-upgrade workspace", AutomationProperties.GetName(restore));
        Assert.Equal(8, restore.TabIndex);
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

    [AvaloniaFact]
    public void MissingRememberedWorkspaceFocusesTheRetryAction()
    {
        var viewModel = new WorkspaceAccessViewModel(
            new StubWorkspaceStore(exists: false),
            WorkspacePathSelection.Existing(
                Path.GetFullPath("/missing/workspace.orb"),
                wasRemembered: true),
            new TransientWorkspacePathPreferenceStore(),
            _ => { });
        var window = new WorkspaceAccessWindow(viewModel);

        window.Show();

        var retry = Assert.IsType<Button>(window.FindControl<Button>("RetryWorkspaceButton"));
        Assert.True(retry.IsVisible);
        Assert.True(retry.IsFocused);
        Assert.Equal("Retry selected workspace", AutomationProperties.GetName(retry));
    }

    [AvaloniaFact]
    public void WorkspaceLocationControlsAreNamedKeyboardOperableAndAnnounceThePath()
    {
        var window = CreateWindow(exists: false);
        window.Show();

        var existing = Assert.IsType<Button>(
            window.FindControl<Button>("ChooseExistingWorkspaceButton"));
        var create = Assert.IsType<Button>(
            window.FindControl<Button>("ChooseNewWorkspaceButton"));
        var label = Assert.IsType<TextBlock>(window.FindControl<TextBlock>("WorkspaceFileLabel"));
        var path = Assert.IsType<TextBlock>(window.FindControl<TextBlock>("SelectedWorkspacePath"));

        Assert.Equal("Choose existing workspace", AutomationProperties.GetName(existing));
        Assert.Equal("Choose where to create a new workspace", AutomationProperties.GetName(create));
        Assert.Equal(HorizontalAlignment.Left, existing.HorizontalAlignment);
        Assert.Equal(HorizontalAlignment.Right, create.HorizontalAlignment);
        Assert.Equal("WORKSPACE FILE", label.Text);
        Assert.Equal("Default workspace location", AutomationProperties.GetHelpText(path));
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(path));
        Assert.Equal(TextWrapping.Wrap, path.TextWrapping);
        Assert.True(existing.Focusable);
        Assert.True(create.Focusable);
        Assert.Equal(0, existing.TabIndex);
        Assert.Equal(1, create.TabIndex);
        Assert.Contains("Selected workspace location", AutomationProperties.GetName(path), StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspacePickersDoNotUseTheFilenameExtensionAsAFileTypeGate()
    {
        var open = AvaloniaWorkspaceLocationPicker.CreateOpenOptions();
        var save = AvaloniaWorkspaceLocationPicker.CreateSaveOptions();

        Assert.False(open.AllowMultiple);
        Assert.Null(open.FileTypeFilter);
        Assert.Equal("workspace.orb", save.SuggestedFileName);
        Assert.Null(save.FileTypeChoices);
        Assert.Null(save.DefaultExtension);
        Assert.True(save.ShowOverwritePrompt);
    }

    [AvaloniaFact]
    public async Task PickerActionsApplyExistingAndNewIntentWithoutExtensionInference()
    {
        var existingPath = Path.GetFullPath("/chosen/existing.notes");
        var newPath = Path.GetFullPath("/chosen/new-workspace.no-extension");
        var store = new StubWorkspaceStore(exists: false);
        var access = new WorkspaceAccessWindow(
            store,
            new StubWorkspacePathProvider(Path.GetFullPath("/default/workspace.orb")),
            workspaceLocationPicker: new StaticWorkspaceLocationPicker(existingPath, newPath),
            workspacePathAvailability: new StoreWorkspacePathAvailability(store));
        access.Show();
        var viewModel = Assert.IsType<WorkspaceAccessViewModel>(access.DataContext);

        Assert.IsType<Button>(access.FindControl<Button>("ChooseExistingWorkspaceButton"))
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await DrainUiUntilAsync(() => viewModel.SelectedWorkspacePath == existingPath);

        Assert.True(viewModel.IsWorkspaceUnavailable);
        Assert.False(viewModel.IsCreateMode);

        Assert.IsType<Button>(access.FindControl<Button>("ChooseNewWorkspaceButton"))
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await DrainUiUntilAsync(() => viewModel.SelectedWorkspacePath == newPath);

        Assert.False(viewModel.IsWorkspaceUnavailable);
        Assert.True(viewModel.IsCreateMode);
        Assert.Equal("New workspace location", viewModel.WorkspaceLocationStatus);
    }

    [AvaloniaFact]
    public async Task CancelledPickerRestoresFocusWithoutChangingTheSelection()
    {
        var defaultPath = Path.GetFullPath("/default/workspace.orb");
        var store = new StubWorkspaceStore(exists: false);
        var access = new WorkspaceAccessWindow(
            store,
            new StubWorkspacePathProvider(defaultPath),
            workspaceLocationPicker: new StaticWorkspaceLocationPicker(null, null),
            workspacePathAvailability: new StoreWorkspacePathAvailability(store));
        access.Show();
        var viewModel = Assert.IsType<WorkspaceAccessViewModel>(access.DataContext);
        var launcher = Assert.IsType<Button>(
            access.FindControl<Button>("ChooseExistingWorkspaceButton"));

        launcher.Focus();
        launcher.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await DrainUiUntilAsync(() => launcher.IsEnabled && launcher.IsFocused);

        Assert.Equal(defaultPath, viewModel.SelectedWorkspacePath);
        Assert.False(viewModel.HasValidationMessage);
    }

    [AvaloniaFact]
    public async Task PickerFailureIsAnnouncedAndRestoresFocus()
    {
        var store = new StubWorkspaceStore(exists: false);
        var access = new WorkspaceAccessWindow(
            store,
            new StubWorkspacePathProvider(Path.GetFullPath("/default/workspace.orb")),
            workspaceLocationPicker: new ThrowingWorkspaceLocationPicker(),
            workspacePathAvailability: new StoreWorkspacePathAvailability(store));
        access.Show();
        var launcher = Assert.IsType<Button>(
            access.FindControl<Button>("ChooseNewWorkspaceButton"));
        var validation = Assert.IsType<TextBlock>(access.FindControl<TextBlock>("ValidationMessage"));

        launcher.Focus();
        launcher.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await DrainUiUntilAsync(() => launcher.IsEnabled && launcher.IsFocused && validation.IsVisible);

        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(validation));
        Assert.DoesNotContain("/default", validation.Text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task MissingRememberedWorkspaceCanReturnToTheDefaultLocation()
    {
        var rememberedPath = Path.GetFullPath("/missing/remembered.workspace");
        var defaultPath = Path.GetFullPath("/default/workspace.orb");
        var store = new StubWorkspaceStore(exists: false);
        var access = new WorkspaceAccessWindow(
            store,
            new StubWorkspacePathProvider(defaultPath, usesRememberedWorkspace: true),
            workspacePathPreferenceStore: new StaticWorkspacePathPreferenceStore(rememberedPath),
            workspacePathAvailability: new StoreWorkspacePathAvailability(store));
        access.Show();
        var viewModel = Assert.IsType<WorkspaceAccessViewModel>(access.DataContext);
        var useDefault = Assert.IsType<Button>(
            access.FindControl<Button>("UseDefaultWorkspaceButton"));
        var path = Assert.IsType<TextBlock>(access.FindControl<TextBlock>("SelectedWorkspacePath"));

        Assert.Equal("Remembered workspace location", AutomationProperties.GetHelpText(path));

        useDefault.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await DrainUiUntilAsync(() => viewModel.SelectedWorkspacePath == defaultPath);

        Assert.False(viewModel.IsWorkspaceUnavailable);
        Assert.True(viewModel.IsCreateMode);
        Assert.Equal("Default workspace location", viewModel.WorkspaceLocationStatus);
        Assert.Equal("Default workspace location", AutomationProperties.GetHelpText(path));
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

    private sealed class StubWorkspacePathProvider(
        string path,
        bool usesRememberedWorkspace = false) : IWorkspacePathProvider
    {
        public bool UsesRememberedWorkspace => usesRememberedWorkspace;

        public string GetDefaultWorkspacePath() => path;

        public DefaultWorkspaceResolution ResolveDefaultWorkspace() =>
            DefaultWorkspaceResolution.Ready(path);
    }

    private sealed class StaticWorkspaceLocationPicker(
        string? existingPath,
        string? newPath) : IWorkspaceLocationPicker
    {
        public Task<string?> PickExistingAsync(Window owner) => Task.FromResult<string?>(existingPath);

        public Task<string?> PickNewAsync(Window owner) => Task.FromResult<string?>(newPath);
    }

    private sealed class ThrowingWorkspaceLocationPicker : IWorkspaceLocationPicker
    {
        public Task<string?> PickExistingAsync(Window owner) => throw new IOException("Injected failure.");

        public Task<string?> PickNewAsync(Window owner) => throw new IOException("Injected failure.");
    }

    private sealed class StaticWorkspacePathPreferenceStore(string path)
        : IWorkspacePathPreferenceStore
    {
        public string? LoadWorkspacePath() => path;

        public bool TrySaveWorkspacePath(string value) => true;
    }
}
