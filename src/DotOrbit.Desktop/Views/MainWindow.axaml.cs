using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Interactivity;
using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.ViewModels;

namespace DotOrbit.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private IWorkspaceSession? _session;

    public MainWindow()
        : this(null)
    {
    }

    internal MainWindow(IWorkspaceSession? session)
    {
        _session = session;
        AvaloniaXamlLoader.Load(this);
        DataContext = new ShellViewModel();
        var recoveryButton = this.FindControl<Button>("OpenRecoveryButton");
        if (recoveryButton is not null)
        {
            recoveryButton.IsVisible = session is not null;
        }

        Closed += OnClosed;
    }

    private void OnOpenRecovery(object? sender, RoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        new RecoveryWindow(_session.Recovery, ReplaceSession).Show(this);
    }

    private void ReplaceSession(IWorkspaceSession session)
    {
        _session?.Dispose();
        _session = session;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        _session?.Dispose();
        _session = null;
    }
}
