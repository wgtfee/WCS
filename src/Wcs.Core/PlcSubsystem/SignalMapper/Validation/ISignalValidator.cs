namespace Wcs.Core.PlcSubsystem.SignalMapper.Validation;

using Wcs.Core.EventBus.Events;

/// <summary>
/// 信号验证器接口 — 对特定工位/设备的信号做业务验证
///
/// 例如：CV03 只有在提升机就绪时才接受 Arrived 信号
///       堆垛机 ASRS01 在 Busy 时拒绝新的 Store 信号
///       称重台必须在数值稳定后才发布重量信号
/// </summary>
public interface ISignalValidator
{
    /// <summary>签名标识</summary>
    string ValidatorId { get; }

    /// <summary>目标设备 ID（null=全局验证器，对所有设备生效）</summary>
    string? DeviceId { get; }

    /// <summary>目标信号 ID（null=该设备所有信号）</summary>
    string? SignalId { get; }

    /// <summary>
    /// 尝试验证信号
    /// </summary>
    /// <param name="definition">触发的信号定义</param>
    /// <param name="diff">原始 PLC 块差异数据</param>
    /// <param name="generatedEvents">已生成的业务事件列表</param>
    /// <returns>验证结果：通过/拒绝/忽略（null=此验证器不处理此信号）</returns>
    SignalValidationResult? Validate(
        SignalDefinition definition,
        PlcBlockDiff diff,
        IReadOnlyList<IEvent> generatedEvents);
}

/// <summary>
/// 验证结果
/// </summary>
public enum SignalValidationAction
{
    /// <summary>验证通过，事件正常发布</summary>
    Pass,
    /// <summary>拒绝此信号，不发布事件</summary>
    Reject,
    /// <summary>延迟处理，等待条件满足</summary>
    Defer
}

/// <summary>
/// 信号验证结果
/// </summary>
public class SignalValidationResult
{
    /// <summary>结果动作</summary>
    public SignalValidationAction Action { get; set; } = SignalValidationAction.Pass;

    /// <summary>拒绝/延迟原因</summary>
    public string? Reason { get; set; }

    /// <summary>延迟重试时间（毫秒）</summary>
    public int? RetryAfterMs { get; set; }

    /// <summary>验证通过</summary>
    public static SignalValidationResult Pass(string? reason = null) =>
        new() { Action = SignalValidationAction.Pass, Reason = reason };

    /// <summary>拒绝</summary>
    public static SignalValidationResult Reject(string reason) =>
        new() { Action = SignalValidationAction.Reject, Reason = reason };

    /// <summary>延迟</summary>
    public static SignalValidationResult Defer(string reason, int retryAfterMs = 1000) =>
        new() { Action = SignalValidationAction.Defer, Reason = reason, RetryAfterMs = retryAfterMs };
}
