namespace Wcs.Core.PlcSubsystem.SignalMapper.Validation.Examples;

/// <summary>
/// 路径合理性验证器 — 检查目标设备是否有空位
///
/// 场景：CV01 托盘到位后，在生成运输任务前确认目标设备是否已满。
/// 数据从 ValidatorContext.StateCenter 获取。
/// </summary>
[SignalValidator("RoutePathCheck")]
public class RoutePathValidator : ISignalValidator
{
    public string ValidatorId => "RoutePathCheck";
    public string? DeviceId => null;
    public string? SignalId => null;

    public SignalValidationResult? Validate(ValidatorContext ctx)
    {
        // 只验证 PalletArrived 信号
        if (!ctx.Definition.SignalId.Contains("Arrived"))
            return null;

        var deviceId = ctx.Definition.PropertyMappings.GetValueOrDefault("DeviceId");
        if (string.IsNullOrEmpty(deviceId))
            return null;

        // 查目标设备的状态
        var targetState = ctx.StateCenter.GetDeviceState(deviceId);
        if (targetState != null && targetState.Status == StateCenter.Models.DeviceStatusEnum.Error)
        {
            return SignalValidationResult.Reject(
                $"目标设备 {deviceId} 处于故障状态（Error），拒绝新任务");
        }

        return null;
    }
}
