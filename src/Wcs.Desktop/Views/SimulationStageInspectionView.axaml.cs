namespace Wcs.Desktop.Views;

using Avalonia.Controls;
using AtomTabControl = AtomUI.Desktop.Controls.TabControl;
using AtomTabItem = AtomUI.Desktop.Controls.TabItem;

public partial class SimulationStageInspectionView : UserControl
{
    public SimulationStageInspectionView()
    {
        InitializeComponent();

        if (Content is AtomTabControl tabs)
        {
            tabs.Items.Add(new AtomTabItem
            {
                Header = "Traffic / External",
                Content = new SimulationTrafficExternalOperationsView()
            });
        }
    }
}
