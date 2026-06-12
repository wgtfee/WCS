using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Wcs.Desktop.Controls;

public partial class UserProfileOverlay : UserControl
{
    public event Action? CloseRequested;

    public UserProfileOverlay()
    {
        InitializeComponent();
    }

    private void CloseClick(object? sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke();
    }
}
