using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.ViewModels;
using DotOrbit.Desktop.Views;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class PassphraseRotationWindowTests
{
    [AvaloniaFact]
    public void FieldsExposePasteGuidanceAccessibleNamesLiveErrorAndLogicalTabOrder()
    {
        var window = CreateWindow(out _);
        window.Show();

        var current = Assert.IsType<TextBox>(window.FindControl<TextBox>("CurrentPassphraseTextBox"));
        var next = Assert.IsType<TextBox>(window.FindControl<TextBox>("NewPassphraseTextBox"));
        var confirmation = Assert.IsType<TextBox>(window.FindControl<TextBox>("ConfirmationTextBox"));
        var visibility = Assert.IsType<ToggleButton>(window.FindControl<ToggleButton>("PassphraseVisibilityToggle"));
        var submit = Assert.IsType<Button>(window.FindControl<Button>("ChangePassphraseButton"));
        var cancel = Assert.IsType<Button>(window.FindControl<Button>("CancelPassphraseButton"));
        var error = Assert.IsType<TextBlock>(window.FindControl<TextBlock>("ValidationMessage"));

        Assert.Equal("Current passphrase", AutomationProperties.GetName(current));
        Assert.Equal("New passphrase", AutomationProperties.GetName(next));
        Assert.Equal("Confirm new passphrase", AutomationProperties.GetName(confirmation));
        Assert.Contains("Paste", AutomationProperties.GetHelpText(current), StringComparison.Ordinal);
        Assert.Contains("Password-manager", AutomationProperties.GetHelpText(next), StringComparison.Ordinal);
        Assert.Equal("Show passphrases", AutomationProperties.GetName(visibility));
        Assert.Equal("Change workspace passphrase", AutomationProperties.GetName(submit));
        Assert.Equal("Cancel", AutomationProperties.GetName(cancel));
        Assert.Equal("Passphrase rotation error", AutomationProperties.GetName(error));
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(error));
        Assert.Equal([0, 1, 2, 3, 4, 5], new[]
        {
            current.TabIndex,
            next.TabIndex,
            confirmation.TabIndex,
            visibility.TabIndex,
            submit.TabIndex,
            cancel.TabIndex,
        });
        Assert.True(current.IsFocused);
        Assert.Contains("16 or more", window.FindControl<TextBlock>("PassphraseGuidance")?.Text);
        Assert.Contains("no recovery mechanism", window.FindControl<TextBlock>("NoRecoveryWarning")?.Text);
    }

    [AvaloniaFact]
    public async Task KeyboardPasteVisibilityAndCancellationWorkWithoutSubmitting()
    {
        var window = CreateWindow(out var viewModel);
        window.Show();
        var current = Assert.IsType<TextBox>(window.FindControl<TextBox>("CurrentPassphraseTextBox"));
        var visibility = Assert.IsType<ToggleButton>(window.FindControl<ToggleButton>("PassphraseVisibilityToggle"));
        var cancel = Assert.IsType<Button>(window.FindControl<Button>("CancelPassphraseButton"));
        Assert.NotNull(window.Clipboard);
        var clipboardItem = new DataTransferItem();
        clipboardItem.SetText("pasted current passphrase");
        var clipboardData = new DataTransfer();
        clipboardData.Add(clipboardItem);
        await window.Clipboard.SetDataAsync(clipboardData);

        Assert.True(current.Focus());
        window.KeyPressQwerty(PhysicalKey.V, RawInputModifiers.Control);
        window.KeyReleaseQwerty(PhysicalKey.V, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("pasted current passphrase", current.Text);

        Assert.True(visibility.Focus());
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Assert.Equal('\0', current.PasswordChar);
        Assert.Equal("Hide passphrases", AutomationProperties.GetName(visibility));

        viewModel.NewPassphrase = "new portable passphrase";
        viewModel.Confirmation = "new portable passphrase";
        Assert.True(cancel.Focus());
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Assert.False(window.IsVisible);
        Assert.Empty(viewModel.CurrentPassphrase);
        Assert.Empty(viewModel.NewPassphrase);
        Assert.Empty(viewModel.Confirmation);
    }

    [AvaloniaFact]
    public void ValidationIsAnnouncedAndMovesFocusToConfirmation()
    {
        var window = CreateWindow(out var viewModel);
        window.Show();
        viewModel.CurrentPassphrase = "correct horse battery";
        viewModel.NewPassphrase = "new portable passphrase";
        viewModel.Confirmation = "different confirmation";
        var submit = Assert.IsType<Button>(window.FindControl<Button>("ChangePassphraseButton"));

        Assert.True(submit.Focus());
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);

        var confirmation = Assert.IsType<TextBox>(window.FindControl<TextBox>("ConfirmationTextBox"));
        Assert.True(confirmation.IsFocused);
        Assert.Equal(
            "New passphrase confirmation does not match.",
            window.FindControl<TextBlock>("ValidationMessage")?.Text);
    }

    [AvaloniaFact]
    public void SuccessfulKeyboardSubmissionReplacesTheSessionAndClosesTheWindow()
    {
        var replacement = new PassphraseRotationViewModelTests.StubWorkspaceSession();
        var session = new PassphraseRotationViewModelTests.StubWorkspaceSession
        {
            RotationResult = PassphraseRotationResult.Rotated(
                replacement,
                "/safe/pre-rotation.dotorbit-recovery"),
        };
        IWorkspaceSession? receivedSession = null;
        var viewModel = new PassphraseRotationViewModel(
            session,
            result => receivedSession = result)
        {
            CurrentPassphrase = "correct horse battery",
            NewPassphrase = "new portable passphrase",
            Confirmation = "new portable passphrase",
        };
        var window = new PassphraseRotationWindow(viewModel);
        window.Show();
        var submit = Assert.IsType<Button>(window.FindControl<Button>("ChangePassphraseButton"));

        Assert.True(submit.Focus());
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);

        Assert.Same(replacement, receivedSession);
        Assert.False(window.IsVisible);
        Assert.Empty(viewModel.CurrentPassphrase);
        Assert.Empty(viewModel.NewPassphrase);
        Assert.Empty(viewModel.Confirmation);
    }

    [AvaloniaFact]
    public void UnavailableWorkspaceKeepsRecoveryMessageVisibleUntilReturnToUnlock()
    {
        var session = new PassphraseRotationViewModelTests.StubWorkspaceSession
        {
            RotationResult = PassphraseRotationResult.WorkspaceUnavailable(
                "/safe/pre-rotation.dotorbit-recovery"),
        };
        var viewModel = new PassphraseRotationViewModel(session, _ => { })
        {
            CurrentPassphrase = "correct horse battery",
            NewPassphrase = "new portable passphrase",
            Confirmation = "new portable passphrase",
        };
        var returnRequests = 0;
        var window = new PassphraseRotationWindow(viewModel, () => returnRequests++);
        window.Show();
        var submit = Assert.IsType<Button>(window.FindControl<Button>("ChangePassphraseButton"));

        Assert.True(submit.Focus());
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);

        var current = Assert.IsType<TextBox>(window.FindControl<TextBox>("CurrentPassphraseTextBox"));
        var cancel = Assert.IsType<Button>(window.FindControl<Button>("CancelPassphraseButton"));
        Assert.False(current.IsEnabled);
        Assert.False(submit.IsEnabled);
        Assert.Equal("Return to unlock", cancel.Content);
        Assert.Equal("Return to unlock", AutomationProperties.GetName(cancel));
        Assert.Contains(
            "validated recovery point",
            window.FindControl<TextBlock>("ValidationMessage")?.Text,
            StringComparison.Ordinal);
        Assert.True(cancel.IsFocused);
        window.Close();

        Assert.Equal(1, returnRequests);
        Assert.False(window.IsVisible);
    }

    private static PassphraseRotationWindow CreateWindow(out PassphraseRotationViewModel viewModel)
    {
        viewModel = new PassphraseRotationViewModel(
            new PassphraseRotationViewModelTests.StubWorkspaceSession(),
            _ => { });
        return new PassphraseRotationWindow(viewModel);
    }
}
