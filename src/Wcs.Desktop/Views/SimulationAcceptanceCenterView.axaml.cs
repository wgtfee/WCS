namespace Wcs.Desktop.Views;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AtomTabControl = AtomUI.Desktop.Controls.TabControl;
using AtomTabItem = AtomUI.Desktop.Controls.TabItem;

public partial class SimulationAcceptanceCenterView : UserControl
{
    public SimulationAcceptanceCenterView()
    {
        InitializeComponent();
    }

    private void OnOpenAdvancedEditorClick(object? sender, RoutedEventArgs e) =>
        NavigateToTab("多故障时间轴");

    private void OnOpenAcceptanceDetailsClick(object? sender, RoutedEventArgs e) =>
        NavigateToTab("一键验收");

    private void NavigateToTab(string header)
    {
        var tabs = this.GetVisualAncestors().OfType<AtomTabControl>().FirstOrDefault();
        if (tabs is null)
            return;

        foreach (var item in tabs.Items)
        {
            if (item is not AtomTabItem tab ||
                !string.Equals(tab.Header?.ToString(), header, StringComparison.Ordinal))
                continue;

            tabs.SelectedItem = tab;
            return;
        }
    }
}
