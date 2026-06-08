using Avalonia.Controls;
using Wcs.Desktop.ViewModels;
using Wcs.Entity;

namespace Wcs.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>左侧菜单选中项变化 → 打开对应标签页</summary>
    private async void OnTreeViewSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (e.AddedItems.Count == 0) return;
        if (e.AddedItems[0] is MenuItemDto menu)
        {
            // 叶子节点才打开页面
            if (menu.Children == null || menu.Children.Count == 0)
            {
                e.Handled = true;
                await vm.OpenPageFromMenu(menu);
            }
        }
    }
}
