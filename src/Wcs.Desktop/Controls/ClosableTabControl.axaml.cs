using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;

namespace Wcs.Desktop.Controls;

public partial class ClosableTabControl : UserControl
{
    public ClosableTabControl()
    {
        InitializeComponent();

        SelectTabCommand = new RelayCommand<ClosableTabItem>(SelectTab);

        CloseTabCommand = new RelayCommand<ClosableTabItem>(CloseTab);
    }

    public ICommand SelectTabCommand { get; }

    public ICommand CloseTabCommand { get; }

    public ObservableCollection<ClosableTabItem>? Tabs
    {
        get => GetValue(TabsProperty);
        set => SetValue(TabsProperty, value);
    }

    public static readonly StyledProperty<ObservableCollection<ClosableTabItem>?>
        TabsProperty =
            AvaloniaProperty.Register<
                ClosableTabControl,
                ObservableCollection<ClosableTabItem>?>(
                nameof(Tabs));

    public ClosableTabItem? SelectedTab
    {
        get => GetValue(SelectedTabProperty);
        set => SetValue(SelectedTabProperty, value);
    }

    public static readonly StyledProperty<ClosableTabItem?>
        SelectedTabProperty =
            AvaloniaProperty.Register<
                ClosableTabControl,
                ClosableTabItem?>(
                nameof(SelectedTab));

    private void SelectTab(ClosableTabItem? tab)
    {
        if (tab == null)
            return;

        SelectedTab = tab;
    }

    private void CloseTab(ClosableTabItem? tab)
    {
        if (tab == null)
            return;

        if (tab.CanClose == false)
            return;

        Tabs?.Remove(tab);

        if (SelectedTab == tab)
        {
            SelectedTab = Tabs?.FirstOrDefault();
        }
    }
}