using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Interactivity;
using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.ViewModels;

namespace DotOrbit.Desktop.Views;

public sealed partial class RecoveryWindow : Window
{
    private readonly RecoveryViewModel _viewModel;

    public RecoveryWindow() =>
        throw new NotSupportedException("Recovery requires an unlocked workspace session.");

    internal RecoveryWindow(
        IWorkspaceRecovery recovery,
        Action<IWorkspaceSession> sessionReplaced)
    {
        AvaloniaXamlLoader.Load(this);
        _viewModel = new RecoveryViewModel(
            recovery,
            new RecoveryPathPicker(this),
            sessionReplaced);
        Initialise();
    }

    public RecoveryWindow(RecoveryViewModel viewModel)
    {
        AvaloniaXamlLoader.Load(this);
        _viewModel = viewModel;
        Initialise();
    }

    private void Initialise()
    {
        DataContext = _viewModel;
        Opened += OnOpened;
        Closed += OnClosed;
        _viewModel.RestoreCancelled += OnRestoreCancelled;
        _viewModel.RestoreConfirmationRequested += OnRestoreConfirmationRequested;
    }

    private async void OnSelectRecoveryDirectory(object? sender, RoutedEventArgs e) =>
        await _viewModel.SelectRecoveryDirectoryAsync();

    private async void OnSelectRecoveryPoint(object? sender, RoutedEventArgs e) =>
        await _viewModel.SelectRecoveryPointAsync();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnOpened(object? sender, EventArgs e) =>
        this.FindControl<Button>("SelectRecoveryDirectoryButton")?.Focus();

    private void OnClosed(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        Closed -= OnClosed;
        _viewModel.RestoreCancelled -= OnRestoreCancelled;
        _viewModel.RestoreConfirmationRequested -= OnRestoreConfirmationRequested;
    }

    private void OnRestoreCancelled(object? sender, EventArgs e) =>
        this.FindControl<Button>("RequestRestoreButton")?.Focus();

    private void OnRestoreConfirmationRequested(object? sender, EventArgs e) =>
        this.FindControl<Button>("CancelRestoreButton")?.Focus();
}
