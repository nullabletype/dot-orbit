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

    public WorkspaceAccessWindow()
        : this(new EncryptedWorkspaceStore(), new SystemWorkspacePathProvider())
    {
    }

    internal WorkspaceAccessWindow(
        IWorkspaceStore workspaceStore,
        IWorkspacePathProvider workspacePathProvider)
    {
        ArgumentNullException.ThrowIfNull(workspaceStore);
        ArgumentNullException.ThrowIfNull(workspacePathProvider);
        Initialise(new WorkspaceAccessViewModel(
            workspaceStore,
            workspacePathProvider.GetDefaultWorkspacePath(),
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
        var mainWindow = new MainWindow();
        mainWindow.Closed += (_, _) => session.Dispose();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = mainWindow;
        }

        mainWindow.Show();
        Close();
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
        var controlName = _viewModel.IsCreateMode
            ? "FirstCategoryTextBox"
            : "PassphraseTextBox";
        this.FindControl<TextBox>(controlName)?.Focus();
    }
}
