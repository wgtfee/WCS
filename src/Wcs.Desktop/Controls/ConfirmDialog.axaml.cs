using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Wcs.Desktop.Controls;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
    }

    private void OnYesClick(object? sender, RoutedEventArgs e)
        => Close(true);

    private void OnNoClick(object? sender, RoutedEventArgs e)
        => Close(false);
}
