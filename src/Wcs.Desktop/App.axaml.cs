using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Desktop.Interface;
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
            // Avoid duplicate validations from both Avalonia and CommunityToolkit
            //BindingPlugins.DataValidators.RemoveAt(0);
            // 注册 ViewLocator 到全局 DataTemplates
            DataTemplates.Add(new ViewLocator(_services!));

            var vm = _services!.GetRequiredService<MainWindowViewModel>();
            //vm.InitializeAsync().GetAwaiter().GetResult();

            desktop.MainWindow = new MainWindow
            {
                DataContext = vm
            };
            _ = Task.Run(async () =>
            {
                try
                {
                    await vm.InitializeAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                }
            });
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        services.AddWcsDesktop();
        services.AddSingleton<LoadService>();
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
