namespace Wcs.Infrastructure.Persistence;

using SqlSugar;

[SugarTable("Wcs_TransportPlcSignalMap")]
public sealed class TransportPlcSignalMapEntity
{
    [SugarColumn(IsPrimaryKey = true)]
    public string VehicleId { get; set; } = string.Empty;
    public string DriverId { get; set; } = string.Empty;
    public int VehicleKind { get; set; }
    public int DriverMode { get; set; }
    public bool Enabled { get; set; }
    public long Version { get; set; }
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string PayloadJson { get; set; } = string.Empty;
    [SugarColumn(IsNullable = true)]
    public string? UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
