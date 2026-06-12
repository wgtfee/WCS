using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Wcs.Desktop.ViewModels;
using Wcs.Desktop.Views;

namespace Wcs.Desktop.Controls;

public partial class ProfileMenuView : UserControl
{
    private bool _isDarkTheme = true;

    public ProfileMenuView()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? GetViewModel()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.MainWindow is MainWindow mainWin)
                return mainWin.DataContext as MainWindowViewModel;
        }
        return null;
    }

    private MainWindow? GetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow as MainWindow;
        return null;
    }

    private void OnPersonalInfoClick(object? sender, RoutedEventArgs e)
    {
        // 先关闭当前所在 Flyout，避免覆盖层点击被 Flyout 拦截
        var btn = this.FindAncestorOfType<FlyoutPresenter>()?.Parent as Button;
        btn?.Flyout?.Hide();

        GetMainWindow()?.ShowProfileOverlay();
    }

    private void OnChangePasswordClick(object? sender, RoutedEventArgs e)
    {
        var btn = this.FindAncestorOfType<FlyoutPresenter>()?.Parent as Button;
        btn?.Flyout?.Hide();

        GetMainWindow()?.ShowChangePasswordOverlay();
    }

    private void OnThemeToggleClick(object? sender, RoutedEventArgs e)
    {
        _isDarkTheme = !_isDarkTheme;
        ThemeButton.Content = _isDarkTheme ? "🎨 浅色主题" : "🎨 深色主题";

        if (_isDarkTheme)
            SetDarkTheme();
        else
            SetLightTheme();
    }

    private static void SetLightTheme()
    {
        var resources = Application.Current?.Resources;
        if (resources == null) return;

        resources["BgPrimary"] = Avalonia.Media.Color.Parse("#F5F5F5");
        resources["BgSecondary"] = Avalonia.Media.Color.Parse("#FFFFFF");
        resources["BgSidebar"] = Avalonia.Media.Color.Parse("#FAFAFA");
        resources["BgStatusBar"] = Avalonia.Media.Color.Parse("#E8E8E8");
        resources["BgCard"] = Avalonia.Media.Color.Parse("#FFFFFF");
        resources["TabBg"] = Avalonia.Media.Color.Parse("#F0F0F0");
        resources["BorderColor"] = Avalonia.Media.Color.Parse("#E0E0E0");
        resources["TextPrimary"] = Avalonia.Media.Color.Parse("#1E1E1E");
        resources["TextSecondary"] = Avalonia.Media.Color.Parse("#666666");
    }

    private static void SetDarkTheme()
    {
        var resources = Application.Current?.Resources;
        if (resources == null) return;

        resources["BgPrimary"] = Avalonia.Media.Color.Parse("#1E1E1E");
        resources["BgSecondary"] = Avalonia.Media.Color.Parse("#2A2A2A");
        resources["BgSidebar"] = Avalonia.Media.Color.Parse("#252525");
        resources["BgStatusBar"] = Avalonia.Media.Color.Parse("#1A1A1A");
        resources["TabBg"] = Avalonia.Media.Color.Parse("#3A3A3A");
        resources["BgCard"] = Avalonia.Media.Color.Parse("#333333");
        resources["BorderColor"] = Avalonia.Media.Color.Parse("#404040");
        resources["TextPrimary"] = Avalonia.Media.Color.Parse("#FFFFFF");
        resources["TextSecondary"] = Avalonia.Media.Color.Parse("#9E9E9E");
    }

    private async void OnLogoutClick(object? sender, RoutedEventArgs e)
    {
        var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        if (desktop?.MainWindow is not Window window) return;

        var confirm = new ConfirmDialog("确认注销", "确定要注销登录吗？");
        var result = await confirm.ShowDialog<bool>(window);
        if (result)
            Environment.Exit(0);
    }
}
