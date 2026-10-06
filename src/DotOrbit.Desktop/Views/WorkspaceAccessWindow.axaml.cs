using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.ViewModels;
using DotOrbit.Storage.Sqlite;

namespace DotOrbit.Desktop.Views;

public sealed partial class WorkspaceAccessWindow : Window
{
    private WorkspaceAccessViewModel _viewModel = null!;
    private IWorkspacePathProvider? _workspacePathProvider;

    public WorkspaceAccessWindow()
        : this(new DefaultWorkspaceStore(), new SystemWorkspacePathProvider())
    {
    }

    internal WorkspaceAccessWindow(
        IWorkspaceStore workspaceStore,
        IWorkspacePathProvider workspacePathProvider)
    {
        ArgumentNullException.ThrowIfNull(workspaceStore);
        ArgumentNullException.ThrowIfNull(workspacePathProvider);
        _workspacePathProvider = workspacePathProvider;
        Initialise(new WorkspaceAccessViewModel(
            workspaceStore,
            workspacePathProvider.ResolveDefaultWorkspace(),
            OpenDefaultShell));
    }

    public WorkspaceAccessWindow(WorkspaceAccessViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        Initialise(viewModel);
    }

    private void Initialise(WorkspaceAccessViewModel viewModel)
    {
        _viewModel = viewModel;
        AvaloniaXamlLoader.Load(this);
        DataContext = viewModel;
        Opened += OnOpened;
        Closed += OnClosed;
        _viewModel.PassphraseFocusRequested += OnPassphraseFocusRequested;
    }

    private void OpenDefaultShell(IWorkspaceSession session)
    {
        var mainWindow = _workspacePathProvider is null
            ? new MainWindow(session)
            : new MainWindow(session, null, null, ReturnToWorkspaceAccess);
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = mainWindow;
        }

        mainWindow.Show();
        Close();
    }

    private void ReturnToWorkspaceAccess()
    {
        var access = new WorkspaceAccessWindow(
            new DefaultWorkspaceStore(),
            _workspacePathProvider ?? new SystemWorkspacePathProvider());
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = access;
        }

        access.Show();
    }

    private void OnOpened(object? sender, EventArgs e) => FocusInitialField();

    private void OnClosed(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        Closed -= OnClosed;
        _viewModel.PassphraseFocusRequested -= OnPassphraseFocusRequested;
    }

    private void OnPassphraseFocusRequested(object? sender, EventArgs e) =>
        this.FindControl<TextBox>("PassphraseTextBox")?.Focus();

    private void FocusInitialField()
    {
        if (_viewModel.IsBlocked)
        {
            return;
        }

        var controlName = _viewModel.IsCreateMode
            ? "FirstCategoryTextBox"
            : "PassphraseTextBox";
        this.FindControl<TextBox>(controlName)?.Focus();
    }
}
