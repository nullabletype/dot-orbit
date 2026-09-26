using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace DotOrbit.Desktop.ViewModels;

public sealed class NavigationItemViewModel : INotifyPropertyChanged
{
    private bool _isSelected;

    internal NavigationItemViewModel(
        string title,
        string iconData,
        string? countText,
        string automationId,
        string viewSubtitle,
        string emptyStateHeading,
        string emptyStateBody,
        Action<NavigationItemViewModel> select)
    {
        Title = title;
        IconData = iconData;
        CountText = countText;
        AutomationId = automationId;
        ViewSubtitle = viewSubtitle;
        EmptyStateHeading = emptyStateHeading;
        EmptyStateBody = emptyStateBody;
        SelectCommand = new RelayCommand(() => select(this));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title { get; }

    public string IconData { get; }

    public string? CountText { get; }

    public bool HasCount => CountText is not null;

    public string AccessibleName => $"Open {Title}";

    public string AccessibleHelpText => $"Show the {Title} view";

    public string AutomationId { get; }

    public string ViewSubtitle { get; }

    public string EmptyStateHeading { get; }

    public string EmptyStateBody { get; }

    public ICommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
