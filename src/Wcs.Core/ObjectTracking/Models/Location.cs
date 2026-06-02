namespace Wcs.Core.ObjectTracking.Models;

/// <summary>
/// 层级位置模型 — 将扁平字符串位置解析为 Zone→Conveyor→Position 三层结构
/// "AISLE_1.CV_101.POS_05" → Zone="AISLE_1", Conveyor="CV_101", Position="POS_05"
/// </summary>
public record Location
{
    /// <summary>区域（如 "AISLE_1"）</summary>
    public string Zone { get; init; } = string.Empty;

    /// <summary>输送线段（如 "CV_101"）</summary>
    public string Conveyor { get; init; } = string.Empty;

    /// <summary>具体位置（如 "POS_05"）</summary>
    public string Position { get; init; } = string.Empty;

    /// <summary>完整路径键</summary>
    public string PathKey => $"{Zone}.{Conveyor}.{Position}";

    /// <summary>
    /// 从点分字符串解析 Location
    /// </summary>
    public static Location FromString(string pathKey)
    {
        var parts = pathKey.Split('.', 3);
        return new Location
        {
            Zone = parts.Length > 0 ? parts[0] : "",
            Conveyor = parts.Length > 1 ? parts[1] : "",
            Position = parts.Length > 2 ? parts[2] : (parts.Length > 0 ? parts[0] : "")
        };
    }

    /// <summary>
    /// 获取区域路径（用于空间索引查询）
    /// </summary>
    public string ZoneKey => Zone;

    /// <summary>
    /// 获取线段路径（用于空间索引查询）
    /// </summary>
    public string ConveyorKey => $"{Zone}.{Conveyor}";
}
