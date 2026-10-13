using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DotOrbit.Desktop.ViewModels;

namespace DotOrbit.Desktop.Views;

public sealed partial class PlanningSnapshotCopyWindow : Window
{
    private readonly PlanningSnapshotCopyViewModel _viewModel;

    public PlanningSnapshotCopyWindow() =>
        throw new NotSupportedException("Planning snapshot copy requires an unlocked workspace session.");

    internal PlanningSnapshotCopyWindow(PlanningSnapshotCopyViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        AvaloniaXamlLoader.Load(this);
        _viewModel = viewModel;
        DataContext = viewModel;
        Opened += OnOpened;
        Closed += OnClosed;
        _viewModel.CopyFinished += OnCopyFinished;
        _viewModel.CopyCancelled += OnCopyCancelled;
    }

    private async void OnConfirmCopy(object? sender, RoutedEventArgs e) =>
        await _viewModel.ConfirmCopyAsync();

    private void OnCancelCopy(object? sender, RoutedEventArgs e) => _viewModel.CancelCopy();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnOpened(object? sender, EventArgs e) =>
        this.FindControl<Button>(
            _viewModel.CanConfirmCopy
                ? "CancelPlanningSnapshotCopyButton"
                : "ClosePlanningSnapshotCopyButton")?.Focus();

    private void OnCopyFinished(object? sender, EventArgs e) =>
        this.FindControl<Button>("ClosePlanningSnapshotCopyButton")?.Focus();

    private void OnCopyCancelled(object? sender, EventArgs e) =>
        this.FindControl<Button>("ClosePlanningSnapshotCopyButton")?.Focus();

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.CancelCopy();
        Opened -= OnOpened;
        Closed -= OnClosed;
        _viewModel.CopyFinished -= OnCopyFinished;
        _viewModel.CopyCancelled -= OnCopyCancelled;
    }
}
