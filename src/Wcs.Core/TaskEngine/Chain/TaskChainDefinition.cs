namespace Wcs.Core.TaskEngine.Chain;

/// <summary>
/// 序列化友好的版本号
/// </summary>
public class Version
{
    public int Major { get; set; } = 1;
    public int Minor { get; set; } = 0;

    public Version() { }
    public Version(int major, int minor) { Major = major; Minor = minor; }

    public override string ToString() => $"{Major}.{Minor}";
}

/// <summary>
/// 任务链版本化定义
/// </summary>
public class TaskChainDefinition
{
    public string DefinitionId { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = string.Empty;
    public Version Version { get; set; } = new(1, 0);
    public string Description { get; init; } = string.Empty;
    public DateTime CreatedTime { get; init; } = DateTime.UtcNow;
    public DateTime? LastModified { get; set; }
    public bool IsBreakingChange { get; set; }
    public TaskGraph? Graph { get; set; }
}
