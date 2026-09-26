using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DotOrbit.Desktop.ViewModels;

namespace DotOrbit.Desktop.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = new ShellViewModel();
    }
}
