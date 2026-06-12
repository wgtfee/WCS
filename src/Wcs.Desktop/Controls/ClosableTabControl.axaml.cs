using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Wcs.Desktop.Controls;

public partial class ClosableTabControl : UserControl
{
    public ClosableTabControl()
    {
        InitializeComponent();

        SelectedTabProperty.Changed.AddClassHandler<ClosableTabControl>((ctrl, args) =>
        {
            if (args.OldValue is ClosableTabItem oldTab)
                oldTab.IsSelected = false;
            if (args.NewValue is ClosableTabItem newTab)
                newTab.IsSelected = true;
        });
    }

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
        if (tab == null) return;
        SelectedTab = tab;
    }

    private void CloseTab(ClosableTabItem? tab)
    {
        if (tab == null || !tab.CanClose) return;

        var wasSelected = tab == SelectedTab;
        Tabs?.Remove(tab);

        if (wasSelected)
            SelectFallback();
    }

    private void CloseLeftTabs(ClosableTabItem? tab)
    {
        if (tab == null || Tabs == null) return;

        bool selectionRemoved = false;
        var toRemove = new List<ClosableTabItem>();
        foreach (var t in Tabs)
        {
            if (t == tab) break;
            if (t.CanClose) toRemove.Add(t);
        }

        foreach (var t in toRemove)
        {
            if (t == SelectedTab) selectionRemoved = true;
            Tabs.Remove(t);
        }

        if (selectionRemoved)
            SelectTab(tab);
    }

    private void CloseRightTabs(ClosableTabItem? tab)
    {
        if (tab == null || Tabs == null) return;

        var idx = Tabs.IndexOf(tab);
        if (idx < 0) return;

        bool selectionRemoved = false;
        var toRemove = Tabs.Skip(idx + 1).Where(t => t.CanClose).ToList();

        foreach (var t in toRemove)
        {
            if (t == SelectedTab) selectionRemoved = true;
            Tabs.Remove(t);
        }

        if (selectionRemoved)
            SelectTab(tab);
    }

    private void CloseAllTabs()
    {
        if (Tabs == null) return;

        var toRemove = Tabs.Where(t => t.CanClose).ToList();
        foreach (var t in toRemove)
            Tabs.Remove(t);

        SelectFallback();
    }

    private void CloseAllButThis(ClosableTabItem? tab)
    {
        if (tab == null || Tabs == null) return;

        var toRemove = Tabs.Where(t => t != tab && t.CanClose).ToList();
        foreach (var t in toRemove)
            Tabs.Remove(t);

        SelectTab(tab);
    }

    private void SelectFallback()
    {
        if (Tabs == null || Tabs.Count == 0) return;
        SelectTab(Tabs[0]);
    }

    // ── 事件处理 ──

    private static ClosableTabItem? GetTabFromSender(object? sender)
        => sender is Control c && c.DataContext is ClosableTabItem tab ? tab : null;

    private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var tab = GetTabFromSender(sender);
        if (tab != null) SelectTab(tab);
    }

    private void OnCloseButtonClick(object? sender, RoutedEventArgs e)
        => CloseTab(GetTabFromSender(sender));

    private void OnCloseMenuItemClick(object? sender, RoutedEventArgs e)
        => CloseTab(GetTabFromSender(sender));

    private void OnCloseLeftMenuItemClick(object? sender, RoutedEventArgs e)
        => CloseLeftTabs(GetTabFromSender(sender));

    private void OnCloseRightMenuItemClick(object? sender, RoutedEventArgs e)
        => CloseRightTabs(GetTabFromSender(sender));

    private void OnCloseAllMenuItemClick(object? sender, RoutedEventArgs e)
        => CloseAllTabs();

    private void OnCloseAllButThisMenuItemClick(object? sender, RoutedEventArgs e)
        => CloseAllButThis(GetTabFromSender(sender));
}