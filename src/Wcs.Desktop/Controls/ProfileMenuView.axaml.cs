using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Wcs.Desktop.Services;
using Wcs.Desktop.Views;

namespace Wcs.Desktop.Controls;

public partial class ProfileMenuView : UserControl
{
    private ThemeService _theme => App.GetService<ThemeService>()!;

    public ProfileMenuView()
    {
        InitializeComponent();
        UpdateThemeButtonText();
    }

    private MainWindow? GetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow as MainWindow;
        return null;
    }

    private void UpdateThemeButtonText()
    {
        ThemeButton.Content = _theme.Current == ThemeVariant.Dark ? "🎨 浅色主题" : "🎨 深色主题";
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
        _theme.Toggle();
        UpdateThemeButtonText();
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