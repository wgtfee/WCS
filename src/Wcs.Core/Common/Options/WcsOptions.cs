namespace Wcs.Core.Common.Options;

using Wcs.Core.StateCenter.Models;

/// <summary>
/// WCS 配置模型 - 对应 appsettings.json 中的 WcsOptions 节
/// </summary>
public class WcsOptions
{
    public const string SectionName = "WcsOptions";

    public PlcPollingOptions PlcPolling { get; set; } = new();
    public PersistenceOptions Persistence { get; set; } = new();
    public SnapshotOptions Snapshot { get; set; } = new();
    public AlarmMonitorOptions AlarmMonitor { get; set; } = new();
    public List<AlarmRule> AlarmRules { get; set; } = new();
}

public class PlcPollingOptions
{
    public bool Enabled { get; set; } = true;
    public int IntervalMs { get; set; } = 100;
}

public class PersistenceOptions
{
    public int IntervalSeconds { get; set; } = 10;
}

public class SnapshotOptions
{
    public int IntervalSeconds { get; set; } = 5;
    public int MaxSnapshots { get; set; } = 100;
}

public class AlarmMonitorOptions
{
    public int IntervalSeconds { get; set; } = 10;
}


