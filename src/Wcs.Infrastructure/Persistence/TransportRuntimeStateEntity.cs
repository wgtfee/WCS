namespace Wcs.Infrastructure.Persistence;

using SqlSugar;

[SugarTable("Wcs_TransportRuntimeState")]
public sealed class TransportRuntimeStateEntity
{
    [SugarColumn(IsPrimaryKey = true)]
    public string StateKey { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string RecordId { get; set; } = string.Empty;
    [SugarColumn(ColumnDataType = "nvarchar(max)")]
    public string PayloadJson { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; }
}
