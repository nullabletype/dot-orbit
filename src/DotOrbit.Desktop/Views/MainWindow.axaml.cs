using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.ComponentModel;
using Avalonia.Markup.Xaml;
using Avalonia.Interactivity;
using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.ViewModels;

namespace DotOrbit.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private IWorkspaceSession? _session;
    private bool _closingApproved;

    public MainWindow()
        : this(null)
    {
    }

    internal MainWindow(IWorkspaceSession? session)
    {
        _session = session;
        AvaloniaXamlLoader.Load(this);
        SetSessionContext();
        var recoveryButton = this.FindControl<Button>("OpenRecoveryButton");
        if (recoveryButton is not null)
        {
            recoveryButton.IsVisible = session is not null;
        }

        Closed += OnClosed;
        Closing += OnClosing;
    }

    private void OnOpenRecovery(object? sender, RoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        Navigate(() => new RecoveryWindow(_session!.Recovery, ReplaceSession).Show(this));
    }

    private void ReplaceSession(IWorkspaceSession session)
    {
        _session?.Dispose();
        _session = session;
        SetSessionContext();
    }

    private void SetSessionContext()
    {
        if (DataContext is ShellViewModel { Work: { } oldWork }) oldWork.PropertyChanged -= OnWorkChanged;
        DataContext = new ShellViewModel(_session?.Work);
        if (DataContext is ShellViewModel { Work: { } work }) work.PropertyChanged += OnWorkChanged;
    }

    private void OnWorkChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectCaptureViewModel.NeedsDecision)
            && DataContext is ShellViewModel { Work.NeedsDecision: true })
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var radio in this.GetVisualDescendants().OfType<RadioButton>())
                    if (radio.DataContext is NavigationItemViewModel navigation)
                        radio.SetCurrentValue(RadioButton.IsCheckedProperty, navigation.IsSelected);
                this.FindControl<Button>("GuardSave")?.Focus();
            });
    }

    private void Navigate(Action action)
    {
        if (DataContext is ShellViewModel { Work: { } work }) work.Navigate(action);
        else action();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingApproved || DataContext is not ShellViewModel { Work.IsDirty: true }) return;
        e.Cancel = true;
        Navigate(() => { _closingApproved = true; Close(); });
    }

    private void OnFocusInspector(object? sender, RoutedEventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (DataContext is ShellViewModel { Work.NeedsDecision: true }) this.FindControl<Button>("GuardSave")?.Focus();
        else if (DataContext is ShellViewModel { Work.HasInspector: true }) this.FindControl<TextBox>("DraftTitle")?.Focus();
        else this.FindControl<Button>("NewProjectButton")?.Focus();
    });

    private void OnQuickAddKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: ProjectRowViewModel project } field) return;
        if (e.Key == Key.Escape) { project.QuickTitle = string.Empty; e.Handled = true; return; }
        if (e.Key is not (Key.Enter or Key.Tab) || string.IsNullOrWhiteSpace(field.Text)) return;
        project.QuickTitle = field.Text;
        e.Handled = true;
        project.Submit();
        Dispatcher.UIThread.Post(() => this.GetVisualDescendants().OfType<TextBox>()
            .FirstOrDefault(box => box.Classes.Contains("quick-add") && box.DataContext is ProjectRowViewModel row && row.Id == project.Id)?.Focus());
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        _session?.Dispose();
        _session = null;
    }
}
