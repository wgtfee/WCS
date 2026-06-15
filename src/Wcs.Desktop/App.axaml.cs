using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Desktop.Interface;
using Wcs.Desktop.Models;
using Wcs.Desktop.Services;
using Wcs.Desktop.ViewModels;
using Wcs.Desktop.Views;
using Wcs.Service;

namespace Wcs.Desktop;

public partial class App : Application
{
    private IServiceProvider? _services;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        _services = ConfigureServices();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            DataTemplates.Add(new ViewLocator(_services!));

            var loginVm = _services!.GetRequiredService<LoginViewModel>();
            var loginView = new LoginView { DataContext = loginVm };

            var window = new Window
            {
                Content = loginView,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                CanResize = false,
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#F5F5F5"))
            };

            loginVm.LoginSuccess += () =>
            {
                var mainVm = _services!.GetRequiredService<MainWindowViewModel>();
                var mainWin = new MainWindow
                {
                    DataContext = mainVm
                };

                desktop.MainWindow = mainWin;
                mainWin.Show();
                window.Close();

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await mainVm.InitializeAsync();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex);
                    }
                });
            };

            desktop.MainWindow = window;
        }
 
        base.OnFrameworkInitializationCompleted();
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        services.AddWcsDesktop();
        services.AddSingleton<LoadService>();
        services.AddSingleton<IAuthState, AuthState>();
                services.AddSingleton<IDataProvider, ApiDataProvider>();
        return services.BuildServiceProvider();
    }

    /// <summary>获取 DI 容器中的服务</summary>
    public static T? GetService<T>() where T : class
    {
        if (Current is App app && app._services != null)
            return app._services.GetService<T>();
        return null;
    }
}
