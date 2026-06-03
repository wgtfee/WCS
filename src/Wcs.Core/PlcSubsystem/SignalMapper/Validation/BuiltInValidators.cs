namespace Wcs.Core.PlcSubsystem.SignalMapper.Validation;

using Wcs.Core.StateCenter.Interfaces;

/// <summary>
/// 设备忙闲验证器 — 设备 Running 时拒绝新的到位信号
/// </summary>
public class DeviceBusyValidator : ISignalValidator
{
    public string ValidatorId => "DeviceBusy";
    public string? DeviceId { get; }
    public string? SignalId => null;

    private readonly string _targetDevice;

    public DeviceBusyValidator(string targetDevice)
    {
        _targetDevice = targetDevice;
        DeviceId = targetDevice;
    }

    public SignalValidationResult? Validate(ValidatorContext ctx)
    {
        var state = ctx.StateCenter.GetDeviceState(_targetDevice);
        if (state != null && state.Status == StateCenter.Models.DeviceStatusEnum.Running)
        {
            return SignalValidationResult.Reject(
                $"[{ValidatorId}] {_targetDevice} 正在运行，拒绝信号 {ctx.Definition.SignalId}");
        }
        return null;
    }
}
