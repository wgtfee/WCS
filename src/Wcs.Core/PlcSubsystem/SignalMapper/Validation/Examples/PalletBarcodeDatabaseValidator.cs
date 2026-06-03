namespace Wcs.Core.PlcSubsystem.SignalMapper.Validation.Examples;

using System.Collections.Concurrent;
using Wcs.Core.EventBus.Events;

/// <summary>
/// 条码数据库验证器 — 查数据库确认托盘条码是否已注册
///
/// 场景：托盘到站后 PLC 发来条码，WCS 需要查数据库确认：
///   1. 这个条码是否在系统中注册过
///   2. 是否已经被处理过（防重复处理）
///   3. 物料类型是否匹配当前工位的工艺要求
///
/// 这种验证 JSON 配置完全做不到——需要查数据库。
/// 但写代码实现后，注册只需一行（或被特性自动发现）。
/// </summary>
[SignalValidator("PalletBarcodeCheck", DeviceId = "CV01")]
public class PalletBarcodeDatabaseValidator : ISignalValidator
{
    public string ValidatorId => "PalletBarcodeCheck";
    public string? DeviceId => "CV01";
    public string? SignalId => "CV01_PalletArrived";

    // 模拟数据库中已注册的条码
    private static readonly HashSet<string> KnownBarcodes = new()
    {
        "PALLET_000001", "PALLET_000002", "PALLET_000003",
        "PALLET_000004", "PALLET_000005"
    };

    // 模拟已处理的条码（防重复）
    private static readonly ConcurrentDictionary<string, bool> ProcessedBarcodes = new();

    public PalletBarcodeDatabaseValidator()
    {
    }

    public SignalValidationResult? Validate(ValidatorContext ctx)
    {
        // 从信号属性中读取条码
        // 实际场景中条码可能来自 PLC 数据块的字符串区域
        var barcode = ctx.Definition.PropertyMappings.GetValueOrDefault("Barcode");
        if (string.IsNullOrEmpty(barcode))
        {
            // 没有条码信息，可能是简单的到位信号，不拦截
            return null;
        }

        // 1. 查数据库：条码是否已注册
        if (!KnownBarcodes.Contains(barcode))
        {
            return SignalValidationResult.Reject(
                $"条码 {barcode} 未在系统中注册，拒绝处理");
        }

        // 2. 防重复处理
        if (ProcessedBarcodes.ContainsKey(barcode))
        {
            return SignalValidationResult.Reject(
                $"条码 {barcode} 已被处理过，拒绝重复处理");
        }

        // 3. 标记已处理
        ProcessedBarcodes.TryAdd(barcode, true);

        return SignalValidationResult.Pass($"条码 {barcode} 验证通过");
    }
}
