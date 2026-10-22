using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace DotOrbit.Desktop.ViewModels;

public sealed class NavigationItemViewModel : INotifyPropertyChanged
{
    private bool _isSelected;
    private string? _countText;
    private string? _attentionText;

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

    public string? CountText
    {
        get => _countText;
        internal set
        {
            _countText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasCount));
            OnPropertyChanged(nameof(AccessibleCount));
        }
    }

    public bool HasCount => CountText is not null;

    public string? AttentionText
    {
        get => _attentionText;
        internal set
        {
            if (string.Equals(value, _attentionText, StringComparison.Ordinal))
            {
                return;
            }

            _attentionText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasAttention));
            OnPropertyChanged(nameof(AccessibleCount));
        }
    }

    public bool HasAttention => AttentionText is not null;

    public string AccessibleCount => CountText switch
    {
        _ when string.Equals(AttentionText, "Review 1", StringComparison.Ordinal) =>
            "1 proposal awaiting review",
        _ when AttentionText is not null =>
            AttentionText.Replace("Review ", string.Empty, StringComparison.Ordinal)
            + " proposals awaiting review",
        null => string.Empty,
        "1" => "1 item",
        _ => $"{CountText} items",
    };

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
