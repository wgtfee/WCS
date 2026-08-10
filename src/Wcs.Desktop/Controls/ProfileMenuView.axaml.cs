using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Wcs.Desktop.Services;
using Wcs.Desktop.Views;

namespace Wcs.Desktop.Controls;

public partial class ProfileMenuView : UserControl
{
    private bool _themeToggleable;

    public ProfileMenuView()
    {
        InitializeComponent();

        // 尝试获取 IThemeManager（仅在 AtomUI 可用时才能切换主题）
        _themeToggleable = ThemeManagerAccessor.GetService<AtomUI.Theme.IThemeManager>() is not null;

        try { UpdateThemeButtonText(); } catch { }
    }

    private MainWindow? GetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow as MainWindow;
        return null;
    }

    private void UpdateThemeButtonText()
    {
        var theme = ThemeManagerAccessor.GetService<AtomUI.Theme.IThemeManager>();
        if (theme is not null)
        {
            ThemeButton.Content = theme.IsDarkThemeMode ? "🎨 浅色主题" : "🎨 深色主题";
        }
        else
        {
            // fallback: 使用标准 Avalonia 主题
            var isDark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
            ThemeButton.Content = isDark ? "🎨 浅色主题" : "🎨 深色主题";
        }
    }

    private void OnPersonalInfoClick(object? sender, RoutedEventArgs e)
    {
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
        try
        {
            var theme = ThemeManagerAccessor.GetService<AtomUI.Theme.IThemeManager>();
            if (theme is not null)
            {
                theme.IsDarkThemeMode = !theme.IsDarkThemeMode;
                UpdateThemeButtonText();
                return;
            }
        }
        catch { }

        // 降级：写入自定义资源切换颜色（不触碰 AtomUI 的 RequestedThemeVariant）
        try
        {
            var app = Application.Current;
            if (app is null) return;
            var resources = app.Resources;
            var isDark = resources.ContainsKey("BgPrimary") &&
                         resources["BgPrimary"] is Avalonia.Media.Color c &&
                         c == Avalonia.Media.Color.Parse("#1E1E1E");

            if (isDark)
            {
                resources["BgPrimary"] = Avalonia.Media.Color.Parse("#F5F5F5");
                resources["BgSecondary"] = Avalonia.Media.Color.Parse("#FFFFFF");
                resources["BgSidebar"] = Avalonia.Media.Color.Parse("#FAFAFA");
                resources["BgStatusBar"] = Avalonia.Media.Color.Parse("#E8E8E8");
                resources["BgCard"] = Avalonia.Media.Color.Parse("#FFFFFF");
                resources["TabBg"] = Avalonia.Media.Color.Parse("#F0F0F0");
                resources["BorderColor"] = Avalonia.Media.Color.Parse("#E0E0E0");
                resources["TextPrimary"] = Avalonia.Media.Color.Parse("#1E1E1E");
                resources["TextSecondary"] = Avalonia.Media.Color.Parse("#666666");
                ThemeButton.Content = "🎨 深色主题";
            }
            else
            {
                resources["BgPrimary"] = Avalonia.Media.Color.Parse("#1E1E1E");
                resources["BgSecondary"] = Avalonia.Media.Color.Parse("#2A2A2A");
                resources["BgSidebar"] = Avalonia.Media.Color.Parse("#252525");
                resources["BgStatusBar"] = Avalonia.Media.Color.Parse("#1A1A1A");
                resources["TabBg"] = Avalonia.Media.Color.Parse("#3A3A3A");
                resources["BorderColor"] = Avalonia.Media.Color.Parse("#404040");
                resources["BgCard"] = Avalonia.Media.Color.Parse("#333333");
                resources["TextPrimary"] = Avalonia.Media.Color.Parse("#FFFFFF");
                resources["TextSecondary"] = Avalonia.Media.Color.Parse("#9E9E9E");
                ThemeButton.Content = "🎨 浅色主题";
            }
        }
        catch { }
    }

    private async void OnLogoutClick(object? sender, RoutedEventArgs e)
    {
        var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        if (desktop?.MainWindow is not Window window) return;

        var confirm = new ConfirmDialog("确认注销", "确定要注销登录吗？");
        var result = await confirm.ShowDialog<bool>(window);
        if (result)
        {
            var auth = App.GetService<IDesktopIamAuthService>();
            if (auth is not null)
                await auth.LogoutAsync();
            Environment.Exit(0);
        }
    }
}
