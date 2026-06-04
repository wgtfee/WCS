namespace Wcs.Core.PlcSubsystem.Validation.Examples;

/// <summary>
/// 默认通过验证器 — 所有信号都放行，不做任何检查
/// 注册此验证器后可以看到完整链路（不因验证拒绝而中断）
/// </summary>
public class AlwaysPassValidator : ISignalValidator
{
    public string ValidatorId => "AlwaysPass";
    public string? DeviceId => null;
    public string? SignalId => null;

    public SignalValidationResult? Validate(ValidatorContext ctx)
    {
        return SignalValidationResult.Pass("[AlwaysPass] 默认通过");
    }
}

/// <summary>
/// 只拒绝故障信号的验证器 — 用于测试验证器拒绝分支
/// 其他全部放行
/// </summary>
public class RejectOnFaultOnlyValidator : ISignalValidator
{
    public string ValidatorId => "RejectOnFaultOnly";
    public string? DeviceId => null;
    public string? SignalId => null;

    public SignalValidationResult? Validate(ValidatorContext ctx)
    {
        // 检查 RawStruct 的所有 bool 字段，含 Fault 且为 true 则拒绝
        if (ctx.RawStruct != null)
        {
            foreach (var f in ctx.RawStruct.GetType().GetFields())
            {
                if (f.Name.Contains("Fault") && f.GetValue(ctx.RawStruct) is bool b && b)
                    return SignalValidationResult.Reject($"故障: {f.Name}=true");
            }
        }
        return SignalValidationResult.Pass("[RejectOnFaultOnly] 通过");
    }
}
