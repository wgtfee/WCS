using CommunityToolkit.Mvvm.ComponentModel;

namespace Wcs.Desktop.Controls;

public partial class ClosableTabItem : ObservableObject
{
    [ObservableProperty]
    private string _header = string.Empty;

    /// <summary>页签顶部的一行用途说明，帮助用户理解当前页面职责。</summary>
    [ObservableProperty]
    private string? _description;

    [ObservableProperty]
    private object? _content;

    [ObservableProperty]
    private bool _canClose = true;

    [ObservableProperty]
    private bool _isSelected;
}
