namespace Wcs.Desktop.ViewModels;

using CommunityToolkit.Mvvm.Input;

public partial class SimulationVerificationViewModel
{
    [RelayCommand]
    private void SelectDevicePlcFaultKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind) ||
            !SupportedPlcFaultKinds.Contains(kind, StringComparer.OrdinalIgnoreCase))
        {
            DevicePanelStatusText = $"PLC Fault Kind 只支持：{PlcFaultKindsText}。";
            return;
        }

        DevicePlcFaultKind = SupportedPlcFaultKinds.First(x =>
            string.Equals(x, kind, StringComparison.OrdinalIgnoreCase));
        DevicePanelStatusText = $"已选择 PLC Fault：{DevicePlcFaultKind}。填写参数后生成受治理 Scenario DSL。";
    }
}
