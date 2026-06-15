using Avalonia.Controls;

namespace Wcs.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>显示个人信息覆盖层</summary>
    public void ShowProfileOverlay()
    {
        ProfileOverlay.IsVisible = true;
    }

    /// <summary>关闭个人信息覆盖层</summary>
    private void OnProfileOverlayClosed()
    {
        ProfileOverlay.IsVisible = false;
    }

    /// <summary>显示修改密码覆盖层</summary>
    public void ShowChangePasswordOverlay()
    {
        ChangePasswordOverlay.IsVisible = true;
    }

    /// <summary>关闭修改密码覆盖层</summary>
    private void OnChangePasswordOverlayClosed()
    {
        ChangePasswordOverlay.IsVisible = false;
    }
}
