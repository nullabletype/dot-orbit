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
    private readonly IWorkspaceStore? _workspaceStore;
    private IWorkspacePathProvider? _workspacePathProvider;
    private readonly IWorkspacePathPreferenceStore? _workspacePathPreferenceStore;
    private readonly IWorkspacePathAvailability? _workspacePathAvailability;
    private readonly IWorkspaceLocationPicker? _workspaceLocationPicker;
    private readonly IMainWindowSizePreferenceStore? _mainWindowSizePreferenceStore;
    private readonly IMainWindowScreenProvider? _mainWindowScreenProvider;
    private readonly Action<MainWindow>? _mainWindowOpened;

    public WorkspaceAccessWindow()
        : this(new DefaultWorkspaceStore(), new SystemWorkspacePathProvider())
    {
    }

    internal WorkspaceAccessWindow(
        IWorkspaceStore workspaceStore,
        IWorkspacePathProvider workspacePathProvider,
        IMainWindowSizePreferenceStore? mainWindowSizePreferenceStore = null,
        IMainWindowScreenProvider? mainWindowScreenProvider = null,
        Action<MainWindow>? mainWindowOpened = null,
        IWorkspacePathPreferenceStore? workspacePathPreferenceStore = null,
        IWorkspaceLocationPicker? workspaceLocationPicker = null,
        IWorkspacePathAvailability? workspacePathAvailability = null)
    {
        ArgumentNullException.ThrowIfNull(workspaceStore);
        ArgumentNullException.ThrowIfNull(workspacePathProvider);
        _workspaceStore = workspaceStore;
        _workspacePathProvider = workspacePathProvider;
        _workspacePathPreferenceStore = workspacePathPreferenceStore ?? new TransientWorkspacePathPreferenceStore();
        _workspaceLocationPicker = workspaceLocationPicker ?? new AvaloniaWorkspaceLocationPicker();
        _workspacePathAvailability = workspacePathAvailability ?? new SystemWorkspacePathAvailability();
        _mainWindowSizePreferenceStore = mainWindowSizePreferenceStore;
        _mainWindowScreenProvider = mainWindowScreenProvider;
        _mainWindowOpened = mainWindowOpened;
        Initialise(new WorkspaceAccessViewModel(
            workspaceStore,
            WorkspacePathSelectionFactory.CreateInitial(
                workspacePathProvider,
                _workspacePathPreferenceStore),
            _workspacePathPreferenceStore,
            OpenDefaultShell,
            _workspacePathAvailability));
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
            : new MainWindow(
                session,
                workspaceUnavailable: ReturnToWorkspaceAccess,
                mainWindowSizePreferenceStore: _mainWindowSizePreferenceStore,
                mainWindowScreenProvider: _mainWindowScreenProvider);
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = mainWindow;
        }

        mainWindow.Show();
        _mainWindowOpened?.Invoke(mainWindow);
        Close();
    }

    private void ReturnToWorkspaceAccess()
    {
        var access = new WorkspaceAccessWindow(
            _workspaceStore ?? new DefaultWorkspaceStore(),
            _workspacePathProvider ?? new SystemWorkspacePathProvider(),
            _mainWindowSizePreferenceStore,
            _mainWindowScreenProvider,
            _mainWindowOpened,
            _workspacePathPreferenceStore,
            _workspaceLocationPicker,
            _workspacePathAvailability);
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

    private async void ChooseExistingWorkspaceClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await ChooseWorkspaceAsync(isNew: false, sender as Control);

    private async void ChooseNewWorkspaceClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await ChooseWorkspaceAsync(isNew: true, sender as Control);

    private async Task ChooseWorkspaceAsync(bool isNew, Control? launcher)
    {
        var existingButton = this.FindControl<Button>("ChooseExistingWorkspaceButton");
        var newButton = this.FindControl<Button>("ChooseNewWorkspaceButton");
        if (existingButton is null || newButton is null || !existingButton.IsEnabled || !newButton.IsEnabled)
        {
            return;
        }

        existingButton.IsEnabled = false;
        newButton.IsEnabled = false;
        var selectionApplied = false;
        try
        {
            var path = isNew
                ? await (_workspaceLocationPicker ?? new AvaloniaWorkspaceLocationPicker()).PickNewAsync(this)
                : await (_workspaceLocationPicker ?? new AvaloniaWorkspaceLocationPicker()).PickExistingAsync(this);
            if (path is null || !IsVisible)
            {
                return;
            }

            if (!WorkspacePathPolicy.TryCanonicalize(path, out var canonicalPath))
            {
                _viewModel.ReportWorkspaceLocationError();
                return;
            }

            if (isNew)
            {
                _viewModel.SelectNewWorkspace(canonicalPath);
            }
            else
            {
                _viewModel.SelectExistingWorkspace(canonicalPath);
            }

            selectionApplied = true;
            FocusInitialField();
        }
        catch (Exception exception) when (exception is NotSupportedException
                                           or IOException
                                           or UnauthorizedAccessException)
        {
            _viewModel.ReportWorkspaceLocationError();
        }
        finally
        {
            if (IsVisible)
            {
                existingButton.IsEnabled = true;
                newButton.IsEnabled = true;
                if (!selectionApplied)
                {
                    launcher?.Focus();
                }
            }
        }
    }

    private void RetryWorkspaceClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _viewModel.RetrySelectedWorkspace();
        FocusInitialField();
    }

    private void UseDefaultWorkspaceClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_workspacePathProvider is null)
        {
            return;
        }

        _viewModel.SelectDefaultWorkspace(
            WorkspacePathSelectionFactory.CreateDefault(_workspacePathProvider));
        FocusInitialField();
    }

    private void OnPassphraseFocusRequested(object? sender, EventArgs e) =>
        this.FindControl<TextBox>("PassphraseTextBox")?.Focus();

    private void FocusInitialField()
    {
        if (_viewModel.IsBlocked)
        {
            var recoveryControlName = _viewModel.IsWorkspaceUnavailable
                ? "RetryWorkspaceButton"
                : "ChooseExistingWorkspaceButton";
            this.FindControl<Button>(recoveryControlName)?.Focus();
            return;
        }

        var controlName = _viewModel.IsCreateMode
            ? "FirstCategoryTextBox"
            : "PassphraseTextBox";
        this.FindControl<TextBox>(controlName)?.Focus();
    }
}
