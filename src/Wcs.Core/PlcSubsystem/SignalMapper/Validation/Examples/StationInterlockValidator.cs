namespace Wcs.Core.PlcSubsystem.SignalMapper.Validation.Examples;

using Wcs.Core.StateCenter.Interfaces;

/// <summary>
/// 工位互锁验证器 — 查上下游设备状态决定是否接收信号
///
/// 场景：CV03 接受到位信号前，必须确保：
///   1. 上游 CV02 已完成（不是 Running）
///   2. 下游 LIFT01 空闲（Idle）
///   3. 目标 ASRS01 空闲（Idle）
///
/// 这是一个典型的工位互锁（Station Interlock）验证。
/// 用 JSON 配置也可以表达简单的 AND/OR，但这里的逻辑涉及
/// 运行时上下文的组合判断，用代码更清晰、可调试。
/// </summary>
[SignalValidator("CV03_StationInterlock", DeviceId = "CV03")]
public class StationInterlockValidator : ISignalValidator
{
    public string ValidatorId => "CV03_StationInterlock";
    public string? DeviceId => "CV03";
    public string? SignalId => null; // CV03 的所有信号都验证

    private readonly IStateCenter _state;

    public StationInterlockValidator(IStateCenter state) => _state = state;

    public SignalValidationResult? Validate(ValidatorContext ctx)
    {
        // 只验证到位类信号
        if (!ctx.Definition.SignalId.Contains("Arrived") && !ctx.Definition.SignalId.Contains("Pallet"))
            return null;

        // 查上游 CV02
        var upState = _state.GetDeviceState("CV02");
        if (upState != null && upState.Status == StateCenter.Models.DeviceStatusEnum.Running)
        {
            return SignalValidationResult.Reject(
                $"上游 CV02 正在运行（{upState.Status}），CV03 不能接收新托盘");
        }

        // 查下游 LIFT01
        var liftState = _state.GetDeviceState("LIFT01");
        if (liftState != null && liftState.Status != StateCenter.Models.DeviceStatusEnum.Idle)
        {
            return SignalValidationResult.Reject(
                $"下游 LIFT01 状态={liftState.Status}（期望 Idle），拒绝信号");
        }

        // 查目标 ASRS01
        var asrsState = _state.GetDeviceState("ASRS01");
        if (asrsState != null && asrsState.Status != StateCenter.Models.DeviceStatusEnum.Idle)
        {
            return SignalValidationResult.Defer(
                $"ASRS01 忙（{asrsState.Status}），延迟处理", retryAfterMs: 3000);
        }

        return SignalValidationResult.Pass("上下游就绪，允许到位");
    }
}
