using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Interactivity;
using DotOrbit.Core.Workspaces;
using DotOrbit.Export.Json;
using DotOrbit.Desktop.ViewModels;

namespace DotOrbit.Desktop.Views;

public sealed partial class PlaintextExportWindow : Window
{
    private readonly PlaintextExportViewModel _viewModel;

    public PlaintextExportWindow() =>
        throw new NotSupportedException("Plaintext export requires an unlocked workspace session.");

    internal PlaintextExportWindow(IWorkspaceWork work)
    {
        AvaloniaXamlLoader.Load(this);
        _viewModel = new PlaintextExportViewModel(
            work,
            new PlaintextExportDestinationPicker(this),
            new PlaintextWorkspaceExporter());
        Initialise();
    }

    internal PlaintextExportWindow(PlaintextExportViewModel viewModel)
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
        _viewModel.ConfirmationRequested += OnConfirmationRequested;
        _viewModel.ConfirmationCancelled += OnConfirmationCancelled;
        _viewModel.ExportFinished += OnExportFinished;
    }

    private async void OnSelectDestination(object? sender, RoutedEventArgs e) =>
        await _viewModel.SelectDestinationAsync();

    private async void OnConfirmExport(object? sender, RoutedEventArgs e) =>
        await _viewModel.ConfirmExportAsync();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnOpened(object? sender, EventArgs e) =>
        this.FindControl<Button>("SelectPlaintextExportDestinationButton")?.Focus();

    private void OnConfirmationRequested(object? sender, EventArgs e) =>
        this.FindControl<Button>("CancelPlaintextExportButton")?.Focus();

    private void OnConfirmationCancelled(object? sender, EventArgs e) =>
        this.FindControl<Button>("RequestPlaintextExportButton")?.Focus();

    private void OnExportFinished(object? sender, EventArgs e) =>
        this.FindControl<Button>("ClosePlaintextExportButton")?.Focus();

    private void OnClosed(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        Closed -= OnClosed;
        _viewModel.ConfirmationRequested -= OnConfirmationRequested;
        _viewModel.ConfirmationCancelled -= OnConfirmationCancelled;
        _viewModel.ExportFinished -= OnExportFinished;
    }
}
