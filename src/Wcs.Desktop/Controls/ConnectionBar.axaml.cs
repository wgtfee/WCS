using Avalonia.Controls;
using Avalonia.Media;
using Wcs.Desktop.Models;

namespace Wcs.Desktop.Controls;

/// <summary>
/// 底部连接状态栏
/// </summary>
public partial class ConnectionBar : UserControl
{
    public ConnectionBar()
    {
        InitializeComponent();
    }

    public void UpdateState(ConnectionState state)
    {
        var (color, text) = state switch
        {
            Models.ConnectionState.Connected => (Brushes.LimeGreen, "Connected"),
            Models.ConnectionState.Connecting => (Brushes.Orange, "Connecting..."),
            _ => (Brushes.Red, "Disconnected")
        };

        StatusDot.Fill = color;
        StatusText.Text = text;
        StatusText.Foreground = color;
    }
}
