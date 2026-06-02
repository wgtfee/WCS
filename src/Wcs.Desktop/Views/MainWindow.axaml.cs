using Avalonia.Controls;
using Avalonia.Interactivity;
using Wcs.Desktop.ViewModels;

namespace Wcs.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            await vm.InitializeAsync();
        }
    }
}
