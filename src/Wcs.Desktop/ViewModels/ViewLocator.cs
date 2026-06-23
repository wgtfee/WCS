using Avalonia.Controls;
using Avalonia.Controls.Templates;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;

namespace Wcs.Desktop.ViewModels;

public class ViewLocator : IDataTemplate
{
    private readonly IServiceProvider _serviceProvider;

    public ViewLocator(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public Control? Build(object? param)
    {
        if (param is null) return null;

        var name = param.GetType().FullName!.Replace("ViewModel", "View");
        var type = Type.GetType(name);
        Console.WriteLine(type == null? "没找到": "找到了");
        if (type is not null)
        {
            // 使用 DI 容器创建 View（支持有参构造函数）
            var view = (Control)ActivatorUtilities.CreateInstance(_serviceProvider, type);
            view.DataContext = param; // 显式绑定 DataContext
            return view;
        }

        return new TextBlock { Text = $"View not found: {name}" };
    }

    public bool Match(object? data) => data is ObservableObject;
}