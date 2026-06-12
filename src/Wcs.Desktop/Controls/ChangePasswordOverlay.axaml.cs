using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Wcs.Desktop.Controls;

public partial class ChangePasswordOverlay : UserControl
{
    public event Action? CloseRequested;

    public ChangePasswordOverlay()
    {
        InitializeComponent();
    }

    private void CloseClick(object? sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke();
    }
}
