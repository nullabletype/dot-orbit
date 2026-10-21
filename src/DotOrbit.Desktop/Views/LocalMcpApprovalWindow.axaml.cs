using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DotOrbit.Desktop.ViewModels;

namespace DotOrbit.Desktop.Views;

internal sealed partial class LocalMcpApprovalWindow : Window
{
    private readonly LocalMcpApprovalViewModel _viewModel;
    private bool _resolved;

    public LocalMcpApprovalWindow() =>
        throw new NotSupportedException("Local MCP approval requires a pending request.");

    public LocalMcpApprovalWindow(LocalMcpApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        AvaloniaXamlLoader.Load(this);
        _viewModel = new(request);
        DataContext = _viewModel;
        Opened += OnOpened;
        Closing += OnClosing;
        _ = CloseWhenRequestCompletesAsync();
    }

    private void OnOpened(object? sender, EventArgs e) =>
        this.FindControl<Button>("DenyButton")?.Focus();

    private void OnDeny(object? sender, RoutedEventArgs e) => Resolve(LocalMcpApprovalChoice.Deny);

    private void OnAllowOnce(object? sender, RoutedEventArgs e) => Resolve(LocalMcpApprovalChoice.AllowOnce);

    private void OnAlwaysAllow(object? sender, RoutedEventArgs e) => Resolve(LocalMcpApprovalChoice.AlwaysAllow);

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_resolved)
        {
            _ = _viewModel.Request.Resolve(LocalMcpApprovalChoice.Deny);
        }
    }

    private void Resolve(LocalMcpApprovalChoice choice)
    {
        _resolved = true;
        _ = _viewModel.Request.Resolve(choice);
        Close();
    }

    private async Task CloseWhenRequestCompletesAsync()
    {
        try
        {
            _ = await _viewModel.Request.Completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (IsVisible)
            {
                Close();
            }
        });
    }
}
