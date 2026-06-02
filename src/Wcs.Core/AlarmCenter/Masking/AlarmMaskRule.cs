namespace Wcs.Core.AlarmCenter.Masking;

/// <summary>
/// 报警屏蔽规则 — 用于设备维修等场景下动态关闭特定报警
/// </summary>
public class AlarmMaskRule
{
    /// <summary>规则唯一标识</summary>
    public string MaskId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>目标设备 ID（null=全局屏蔽）</summary>
    public string? DeviceId { get; set; }

    /// <summary>目标报警代码（null=该设备所有报警）</summary>
    public string? AlarmCode { get; set; }

    /// <summary>屏蔽原因</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>屏蔽开始时间</summary>
    public DateTime StartTime { get; set; } = DateTime.UtcNow;

    /// <summary>屏蔽结束时间（null=永久有效）</summary>
    public DateTime? EndTime { get; set; }

    /// <summary>是否启用</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>操作员</summary>
    public string? CreatedBy { get; set; }

    /// <summary>创建时间</summary>
    public DateTime CreatedTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 检查屏蔽规则当前是否有效
    /// </summary>
    public bool IsActive()
    {
        if (!Enabled) return false;
        var now = DateTime.UtcNow;
        if (now < StartTime) return false;
        if (EndTime.HasValue && now > EndTime.Value) return false;
        return true;
    }
}
