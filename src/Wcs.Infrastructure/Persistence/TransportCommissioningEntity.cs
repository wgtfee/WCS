namespace Wcs.Infrastructure.Persistence;

using SqlSugar;

[SugarTable("Wcs_TransportCommissioning")]
public sealed class TransportCommissioningEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 300)]
    public string StateKey { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false)]
    public int Category { get; set; }

    [SugarColumn(Length = 200, IsNullable = false)]
    public string RecordId { get; set; } = string.Empty;

    [SugarColumn(ColumnDataType = "nvarchar(max)", IsNullable = false)]
    public string PayloadJson { get; set; } = "{}";

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
