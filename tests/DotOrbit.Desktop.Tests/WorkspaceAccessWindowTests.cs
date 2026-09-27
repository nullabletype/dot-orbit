using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.VisualTree;
using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.ViewModels;
using DotOrbit.Desktop.Views;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class WorkspaceAccessWindowTests
{
    [AvaloniaFact]
    public void StartupUsesThePlatformWorkspacePathProvider()
    {
        var store = new StubWorkspaceStore(exists: false);

        _ = new WorkspaceAccessWindow(
            store,
            new StubWorkspacePathProvider("/platform-data/dot-orbit/workspace.db"));

        Assert.Equal("/platform-data/dot-orbit/workspace.db", store.LastExistsPath);
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

    private static WorkspaceAccessWindow CreateWindow(bool exists) =>
        new(new WorkspaceAccessViewModel(
            new StubWorkspaceStore(exists),
            "/data/workspace.db",
            _ => { }));

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
    }
}
