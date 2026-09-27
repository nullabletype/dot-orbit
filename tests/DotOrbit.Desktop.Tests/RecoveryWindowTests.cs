using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;
using DotOrbit.Desktop.ViewModels;
using DotOrbit.Desktop.Views;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class RecoveryWindowTests
{
    [AvaloniaFact]
    public void ControlsExposeRecoverySemanticsAccessibleNamesAndLogicalTabOrder()
    {
        var window = CreateWindow(out _);
        window.Show();

        var directory = Assert.IsType<Button>(
            window.FindControl<Button>("SelectRecoveryDirectoryButton"));
        var create = Assert.IsType<Button>(
            window.FindControl<Button>("CreateRecoveryPointButton"));
        var recoveryPoint = Assert.IsType<Button>(
            window.FindControl<Button>("SelectRecoveryPointButton"));
        var review = Assert.IsType<Button>(window.FindControl<Button>("RequestRestoreButton"));
        var confirm = Assert.IsType<Button>(window.FindControl<Button>("ConfirmRestoreButton"));
        var cancel = Assert.IsType<Button>(window.FindControl<Button>("CancelRestoreButton"));
        var close = Assert.IsType<Button>(window.FindControl<Button>("CloseRecoveryButton"));

        Assert.Equal("Choose recovery directory", AutomationProperties.GetName(directory));
        Assert.Equal("Create encrypted recovery point", AutomationProperties.GetName(create));
        Assert.Equal("Choose encrypted recovery point", AutomationProperties.GetName(recoveryPoint));
        Assert.Equal("Review restore", AutomationProperties.GetName(review));
        Assert.Equal("Confirm restore", AutomationProperties.GetName(confirm));
        Assert.Equal("Cancel restore", AutomationProperties.GetName(cancel));
        Assert.Equal("Close recovery", AutomationProperties.GetName(close));
        Assert.Equal([0, 1, 2, 3, 4, 5, 6], new[]
        {
            directory.TabIndex,
            create.TabIndex,
            recoveryPoint.TabIndex,
            review.TabIndex,
            confirm.TabIndex,
            cancel.TabIndex,
            close.TabIndex,
        });
        Assert.True(directory.IsFocused);
        Assert.All(
            new[] { directory, create, recoveryPoint, review, confirm, cancel, close },
            button => Assert.True(button.Focusable));
        Assert.Contains(
            "not live synchronisation",
            window.GetVisualDescendants()
                .OfType<TextBlock>()
                .Select(text => text.Text)
                .First(text => text?.Contains("external sync", StringComparison.Ordinal) is true),
            StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task KeyboardReviewAndCancellationDoNotCallRestoreAndReturnFocus()
    {
        var recovery = new RecoveryViewModelTests.StubWorkspaceRecovery();
        var picker = new RecoveryViewModelTests.StubRecoveryPathPicker
        {
            DirectoryResults = new Queue<string?>(["/sync-folder/recovery"]),
            RecoveryPointResults = new Queue<string?>(["/portable/selected.dotorbit-recovery"]),
        };
        var viewModel = new RecoveryViewModel(recovery, picker, _ => { });
        await viewModel.SelectRecoveryDirectoryAsync();
        await viewModel.SelectRecoveryPointAsync();
        var window = new RecoveryWindow(viewModel);
        window.Show();
        var review = Assert.IsType<Button>(window.FindControl<Button>("RequestRestoreButton"));
        var cancel = Assert.IsType<Button>(window.FindControl<Button>("CancelRestoreButton"));

        Assert.True(review.Focus());
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Assert.True(viewModel.IsRestoreConfirmationVisible);
        Assert.True(cancel.IsFocused);
        var confirmation = Assert.IsType<Border>(
            window.FindControl<Border>("RestoreConfirmationPanel"));
        Assert.Equal("Restore confirmation", AutomationProperties.GetName(confirmation));
        Assert.Equal(
            AutomationLiveSetting.Assertive,
            AutomationProperties.GetLiveSetting(confirmation));
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);

        Assert.Equal(0, recovery.RestoreCallCount);
        Assert.False(viewModel.IsRestoreConfirmationVisible);
        Assert.True(review.IsFocused);
        var status = Assert.IsType<TextBlock>(window.FindControl<TextBlock>("RecoveryStatusMessage"));
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(status));
        Assert.Equal(
            "Restore cancelled. The current workspace was not changed.",
            status.Text);
    }

    [AvaloniaFact]
    public void KeyboardActivationOfDirectoryPickerUpdatesTheVisibleSelection()
    {
        var picker = new RecoveryViewModelTests.StubRecoveryPathPicker
        {
            DirectoryResults = new Queue<string?>(["/sync-folder/recovery"]),
        };
        var viewModel = new RecoveryViewModel(
            new RecoveryViewModelTests.StubWorkspaceRecovery(),
            picker,
            _ => { });
        var window = new RecoveryWindow(viewModel);
        window.Show();
        var directory = Assert.IsType<Button>(
            window.FindControl<Button>("SelectRecoveryDirectoryButton"));

        Assert.True(directory.Focus());
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);

        Assert.Equal("/sync-folder/recovery", viewModel.RecoveryDirectoryPath);
        Assert.Equal(
            "/sync-folder/recovery",
            window.FindControl<TextBlock>("RecoveryDirectoryText")?.Text);
    }

    private static RecoveryWindow CreateWindow(out RecoveryViewModel viewModel)
    {
        viewModel = new RecoveryViewModel(
            new RecoveryViewModelTests.StubWorkspaceRecovery(),
            new RecoveryViewModelTests.StubRecoveryPathPicker(),
            _ => { });
        return new RecoveryWindow(viewModel);
    }
}
