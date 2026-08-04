using SqlSugar;

namespace Wcs.Infrastructure.Persistence;

/// <summary>业务系统的 Shadow User 映射。只保存 IAM 身份和本地映射，不保存 IAM 密码。</summary>
[SugarTable("Wcs_ShadowUser")]
public sealed class WcsShadowUserEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 36)] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [SugarColumn(Length = 36, IsNullable = false)] public string IamUserId { get; set; } = string.Empty;
    [SugarColumn(Length = 100, IsNullable = false)] public string LocalUserId { get; set; } = string.Empty;
    [SugarColumn(Length = 100, IsNullable = true)] public string? UserName { get; set; }
    [SugarColumn(Length = 200, IsNullable = true)] public string? DisplayName { get; set; }
    [SugarColumn(Length = 30, IsNullable = false)] public string Status { get; set; } = "Active";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
