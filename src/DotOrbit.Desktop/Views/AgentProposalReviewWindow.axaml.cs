using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DotOrbit.Core.Interchange;
using DotOrbit.Desktop.ViewModels;

namespace DotOrbit.Desktop.Views;

public sealed partial class AgentProposalReviewWindow : Window
{
    private readonly AgentProposalReviewViewModel _viewModel;
    private readonly IProposalApplicationHost _host;

    public AgentProposalReviewWindow() =>
        throw new NotSupportedException("Proposal review requires a validated in-memory plan.");

    internal AgentProposalReviewWindow(
        AgentProposalReviewViewModel viewModel,
        IProposalApplicationHost host)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(host);
        AvaloniaXamlLoader.Load(this);
        _viewModel = viewModel;
        _host = host;
        DataContext = viewModel;
        Opened += OnOpened;
        Closing += OnClosing;
        Closed += OnClosed;
        _viewModel.ConfirmationOpened += OnConfirmationOpened;
        _viewModel.ConfirmationClosed += OnConfirmationClosed;
        _viewModel.ApplyFinished += OnApplyFinished;
    }

    private void OnRequestApply(object? sender, RoutedEventArgs e) => _viewModel.RequestApply();

    private async void OnConfirmApply(object? sender, RoutedEventArgs e) =>
        await _viewModel.ConfirmApplyAsync(_host);

    private void OnCancelApply(object? sender, RoutedEventArgs e) => _viewModel.CancelConfirmation();

    private async void OnRetryMaintenance(object? sender, RoutedEventArgs e) =>
        await _viewModel.RetryMaintenanceAsync(_host);

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.CanClose)
        {
            Close();
        }
    }

    private void OnOpened(object? sender, EventArgs e) =>
        this.FindControl<Button>("CloseProposalReviewButton")?.Focus();

    private void OnConfirmationOpened(object? sender, EventArgs e) =>
        this.FindControl<Button>("CancelProposalApplyButton")?.Focus();

    private void OnConfirmationClosed(object? sender, EventArgs e) =>
        this.FindControl<Button>(
            _viewModel.CanRequestApply
                ? "RequestProposalApplyButton"
                : "CloseProposalReviewButton")?.Focus();

    private void OnApplyFinished(object? sender, EventArgs e) =>
        this.FindControl<Button>(
            _viewModel.CanRetryMaintenance
                ? "RetryProposalMaintenanceButton"
                : "CloseProposalReviewButton")?.Focus();

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_viewModel.CanClose)
        {
            e.Cancel = true;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.Discard();
        Opened -= OnOpened;
        Closing -= OnClosing;
        Closed -= OnClosed;
        _viewModel.ConfirmationOpened -= OnConfirmationOpened;
        _viewModel.ConfirmationClosed -= OnConfirmationClosed;
        _viewModel.ApplyFinished -= OnApplyFinished;
    }
}
