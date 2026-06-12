using CommunityToolkit.Mvvm.ComponentModel;

namespace Wcs.Desktop.Controls;

public partial class ClosableTabItem : ObservableObject
{
    [ObservableProperty]
    private string _header = string.Empty;

    [ObservableProperty]
    private object? _content;

    [ObservableProperty]
    private bool _canClose = true;

    [ObservableProperty]
    private bool _isSelected;
}
