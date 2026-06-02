namespace Wcs.Core.ObjectTracking.Topology;

/// <summary>
/// 区域 — 逻辑分区（如 "AISLE_1", "BAY_A"）
/// </summary>
public record Zone
{
    public string ZoneId { get; init; } = string.Empty;
    public string? DisplayName { get; init; }
    public Dictionary<string, string> Properties { get; init; } = new();
}
