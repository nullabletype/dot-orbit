using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.ViewModels;

namespace DotOrbit.Desktop.Views;

public sealed partial class PassphraseRotationWindow : Window
{
    private readonly PassphraseRotationViewModel _viewModel;
    private readonly Action _workspaceUnavailable;
    private bool _workspaceUnavailableHandled;

    public event EventHandler? PassphraseChanged;

    public PassphraseRotationWindow() =>
        throw new NotSupportedException("Passphrase rotation requires an unlocked workspace session.");

    internal PassphraseRotationWindow(
        IWorkspaceSession session,
        Action<IWorkspaceSession> sessionReplaced,
        Action workspaceUnavailable)
        : this(new PassphraseRotationViewModel(session, sessionReplaced), workspaceUnavailable)
    {
    }

    public PassphraseRotationWindow(PassphraseRotationViewModel viewModel)
        : this(viewModel, () => { })
    {
    }

    internal PassphraseRotationWindow(
        PassphraseRotationViewModel viewModel,
        Action workspaceUnavailable)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(workspaceUnavailable);
        AvaloniaXamlLoader.Load(this);
        _viewModel = viewModel;
        _workspaceUnavailable = workspaceUnavailable;
        DataContext = _viewModel;
        Opened += OnOpened;
        Closed += OnClosed;
        _viewModel.FocusRequested += OnFocusRequested;
        _viewModel.CancelRequested += OnCancelRequested;
        _viewModel.RotationCompleted += OnRotationCompleted;
        _viewModel.WorkspaceUnavailableExitRequested += OnWorkspaceUnavailableExitRequested;
    }

    private void OnOpened(object? sender, EventArgs e) =>
        this.FindControl<TextBox>("CurrentPassphraseTextBox")?.Focus();

    private void OnClosed(object? sender, EventArgs e)
    {
        var returnToUnlock = _viewModel.IsWorkspaceUnavailable
            && !_workspaceUnavailableHandled;
        _workspaceUnavailableHandled |= returnToUnlock;
        _viewModel.ClearSecrets();
        Opened -= OnOpened;
        Closed -= OnClosed;
        _viewModel.FocusRequested -= OnFocusRequested;
        _viewModel.CancelRequested -= OnCancelRequested;
        _viewModel.RotationCompleted -= OnRotationCompleted;
        _viewModel.WorkspaceUnavailableExitRequested -= OnWorkspaceUnavailableExitRequested;
        if (returnToUnlock)
        {
            _workspaceUnavailable();
        }
    }

    private void OnFocusRequested(string controlName) =>
        this.FindControl<Control>(controlName)?.Focus();

    private void OnCancelRequested(object? sender, EventArgs e) => Close();

    private void OnRotationCompleted(object? sender, EventArgs e)
    {
        PassphraseChanged?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void OnWorkspaceUnavailableExitRequested(object? sender, EventArgs e)
    {
        _workspaceUnavailableHandled = true;
        Close();
        _workspaceUnavailable();
    }
}
