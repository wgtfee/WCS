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
                Header = "仿真验收中心",
                Content = new SimulationAcceptanceCenterView()
            });
            tabs.Items.Add(new AtomTabItem
            {
                Header = "Traffic / External",
                Content = new SimulationTrafficExternalOperationsView()
            });
            tabs.Items.Add(new AtomTabItem
            {
                Header = "多故障时间轴",
                Content = new SimulationTimelineEditorView()
            });
            tabs.Items.Add(new AtomTabItem
            {
                Header = "一键验收",
                Content = new SimulationAcceptanceView()
            });
        }
    }
}
