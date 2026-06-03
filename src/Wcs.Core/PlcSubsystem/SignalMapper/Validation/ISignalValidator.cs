namespace Wcs.Core.PlcSubsystem.SignalMapper.Validation;

using Wcs.Core.EventBus.Events;

/// <summary>
/// 信号验证器接口 — 对特定工位/设备的信号做业务验证
///
/// 验证器通过 ValidatorContext 访问 StateCenter、数据库、RouteCenter 等系统状态。
///
/// 简单规则用 JSON 配置（appsettings.json → ValidationRules），
/// 复杂业务逻辑实现此接口（一个工位一个类，互不干扰）。
/// </summary>
public interface ISignalValidator
{
    /// <summary>验证器唯一标识</summary>
    string ValidatorId { get; }

    /// <summary>目标设备 ID（null=全局验证器，对所有设备生效）</summary>
    string? DeviceId { get; }

    /// <summary>目标信号 ID（null=该设备所有信号）</summary>
    string? SignalId { get; }

    /// <summary>
    /// 验证信号。ValidatorContext 提供了验证所需的全部上下文：
    /// - StateCenter：设备/任务/报警状态
    /// - ObjectTracking：物料位置、预留
    /// - RouteCenter：路径规划、拥塞
    /// - RawDiff：原始 PLC 数据
    /// </summary>
    SignalValidationResult? Validate(ValidatorContext context);
}

/// <summary>
/// 验证结果动作
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
    public SignalValidationAction Action { get; set; } = SignalValidationAction.Pass;
    public string? Reason { get; set; }
    public int? RetryAfterMs { get; set; }

    public static SignalValidationResult Pass(string? reason = null) =>
        new() { Action = SignalValidationAction.Pass, Reason = reason };

    public static SignalValidationResult Reject(string reason) =>
        new() { Action = SignalValidationAction.Reject, Reason = reason };

    public static SignalValidationResult Defer(string reason, int retryAfterMs = 1000) =>
        new() { Action = SignalValidationAction.Defer, Reason = reason, RetryAfterMs = retryAfterMs };
}

/// <summary>
/// 标记验证器应自动注册到验证管道中。
/// 只需在类上加上此特性，无需在 Program.cs 中手动注册。
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class SignalValidatorAttribute : Attribute
{
    /// <summary>验证器名称（用于日志）</summary>
    public string Name { get; }

    /// <summary>目标设备 ID（可选）</summary>
    public string? DeviceId { get; set; }

    /// <summary>目标信号 ID（可选）</summary>
    public string? SignalId { get; set; }

    public SignalValidatorAttribute(string name) => Name = name;
}
