using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotOrbit.Desktop.ViewModels;
using DotOrbit.Desktop.Views;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class PlaintextExportWindowTests
{
    [Fact]
    public void DestinationPickerRequiresAnExplicitJsonLocationWithoutSuggestingRecoveryStorage()
    {
        var options = PlaintextExportDestinationPicker.CreateOptions();

        Assert.Equal("Export unencrypted workspace JSON", options.Title);
        Assert.Equal("dot-orbit-workspace-export.json", options.SuggestedFileName);
        Assert.Equal("json", options.DefaultExtension);
        Assert.True(options.ShowOverwritePrompt);
        Assert.Null(options.SuggestedStartLocation);
        Assert.Equal(["*.json"], Assert.Single(options.FileTypeChoices!).Patterns);
    }

    [AvaloniaFact]
    public async Task WindowPreviewsScopeAndRequiresAnAccessibleUnencryptedConfirmation()
    {
        var destination = new PlaintextExportDestination("workspace-export.json", "/exports/workspace-export.json");
        var exporter = new PlaintextExportViewModelTests.RecordingExporter();
        var viewModel = new PlaintextExportViewModel(
            new MemoryWorkspaceWork(),
            new PlaintextExportViewModelTests.StubDestinationPicker(destination),
            exporter);
        var window = new PlaintextExportWindow(viewModel);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var warning = Assert.IsType<Border>(window.FindControl<Border>("PlaintextWarning"));
        var choose = Assert.IsType<Button>(window.FindControl<Button>("SelectPlaintextExportDestinationButton"));
        var review = Assert.IsType<Button>(window.FindControl<Button>("RequestPlaintextExportButton"));
        var confirmation = Assert.IsType<Border>(window.FindControl<Border>("PlaintextExportConfirmationPanel"));
        var cancel = Assert.IsType<Button>(window.FindControl<Button>("CancelPlaintextExportButton"));
        var status = Assert.IsType<TextBlock>(window.FindControl<TextBlock>("PlaintextExportStatusMessage"));
        var destinationText = Assert.IsType<TextBlock>(window.FindControl<TextBlock>("PlaintextExportDestinationText"));

        Assert.Equal("Unencrypted export warning", AutomationProperties.GetName(warning));
        Assert.Equal("Choose plaintext export destination", AutomationProperties.GetName(choose));
        Assert.Equal("Export to selected location, confirmation required", AutomationProperties.GetName(review));
        Assert.Equal("Export to selected location…", review.Content);
        Assert.False(confirmation.IsEffectivelyVisible);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(confirmation));
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(status));
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text =>
            text.Text?.Contains("Bin contents are excluded", StringComparison.Ordinal) is true);
        Assert.True(choose.IsKeyboardFocusWithin);
        Assert.Equal(0, choose.TabIndex);
        Assert.Equal(1, review.TabIndex);

        await viewModel.SelectDestinationAsync();
        viewModel.RequestExportCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            "Selected plaintext export destination: workspace-export.json",
            AutomationProperties.GetName(destinationText));
        Assert.True(confirmation.IsEffectivelyVisible);
        Assert.Equal("Unencrypted export confirmation", AutomationProperties.GetName(confirmation));
        Assert.Equal("Confirm unencrypted workspace export", AutomationProperties.GetName(
            Assert.IsType<Button>(window.FindControl<Button>("ConfirmPlaintextExportButton"))));
        Assert.Equal("Cancel plaintext export", AutomationProperties.GetName(cancel));
        Assert.True(cancel.IsKeyboardFocusWithin);

        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.False(confirmation.IsEffectivelyVisible);
        Assert.True(review.IsKeyboardFocusWithin);
        Assert.Equal("Export cancelled. No plaintext file was written.", viewModel.StatusMessage);
        Assert.Equal(0, exporter.CallCount);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ConfirmedExportWritesOnceAndMovesFocusToTheCloseAction()
    {
        var destination = new PlaintextExportDestination("workspace-export.json", "/exports/workspace-export.json");
        var exporter = new PlaintextExportViewModelTests.RecordingExporter();
        var viewModel = new PlaintextExportViewModel(
            new MemoryWorkspaceWork(),
            new PlaintextExportViewModelTests.StubDestinationPicker(destination),
            exporter);
        var window = new PlaintextExportWindow(viewModel);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        await viewModel.SelectDestinationAsync();
        viewModel.RequestExportCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        await viewModel.ConfirmExportAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, exporter.CallCount);
        Assert.Equal("Unencrypted workspace JSON exported.", viewModel.StatusMessage);
        Assert.True(window.FindControl<Button>("ClosePlaintextExportButton")!.IsKeyboardFocusWithin);
        window.Close();
    }

    [AvaloniaFact]
    public void SettingsLaunchesExportOnlyForAnUnlockedSessionAndRestoresLauncherFocus()
    {
        using var session = new RecoveryViewModelTests.StubWorkspaceSession(
            new RecoveryViewModelTests.StubWorkspaceRecovery());
        var window = new MainWindow(session);
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.SettingsNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var launcher = Assert.IsType<Button>(window.FindControl<Button>("SettingsPlaintextExportButton"));

        Assert.True(launcher.IsVisible);
        Assert.Equal("Export unencrypted workspace JSON", AutomationProperties.GetName(launcher));
        Assert.True(launcher.Focus());
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        var exportWindow = Assert.IsType<PlaintextExportWindow>(Assert.Single(window.OwnedWindows));

        exportWindow.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(window.OwnedWindows);
        Assert.True(launcher.IsKeyboardFocusWithin);
        window.Close();
    }
}
