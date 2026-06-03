namespace Wcs.Core.DeviceCenter.Capability;

/// <summary>
/// 设备能力枚举 — 描述设备的运输/处理能力
///
/// 纯 WCS 边界：只包含设备的物理运输和处理能力
/// 不含：CanStore（库位决策属于 WMS）、CanAllocate、CanReserveLocation
/// </summary>
[Flags]
public enum DeviceCapability
{
    /// <summary>无特殊能力</summary>
    None = 0,
    /// <summary>可输送（输送线）</summary>
    CanConvey = 1 << 0,
    /// <summary>可提升（提升机）</summary>
    CanLift = 1 << 1,
    /// <summary>可旋转（转台/旋转台）</summary>
    CanRotate = 1 << 2,
    /// <summary>可分拣（分拣机）</summary>
    CanSort = 1 << 3,
    /// <summary>可抓取（机器人/机械手）</summary>
    CanGrip = 1 << 4,
    /// <summary>可扫描（条码/RFID 读取）</summary>
    CanScan = 1 << 5,
    /// <summary>可转移（与其他设备交接）</summary>
    CanTransfer = 1 << 6,
    /// <summary>可称重（称重台）</summary>
    CanWeigh = 1 << 7,
    /// <summary>可测量尺寸（测量站）</summary>
    CanMeasure = 1 << 8
}

/// <summary>
/// 设备能力记录 — 设备 ID → 能力集合
/// </summary>
public class DeviceCapabilityRecord
{
    /// <summary>设备 ID</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>能力集合</summary>
    public DeviceCapability Capabilities { get; set; } = DeviceCapability.CanConvey;

    /// <summary>自定义能力标签</summary>
    public Dictionary<string, string> CustomTags { get; set; } = new();

    /// <summary>最大负载（kg）</summary>
    public double MaxLoadKg { get; set; }

    /// <summary>最大速度（m/s）</summary>
    public double MaxSpeedMps { get; set; }

    /// <summary>能力描述</summary>
    public string? Description { get; set; }
}

/// <summary>
/// 设备能力中心接口 — 统一管理设备能力查询与匹配
/// 任务只需声明需要什么能力，不关心具体设备
/// </summary>
public interface IDeviceCapabilityCenter
{
    /// <summary>
    /// 注册设备能力
    /// </summary>
    void RegisterCapability(string deviceId, DeviceCapability capabilities);

    /// <summary>
    /// 注册完整设备能力记录
    /// </summary>
    void RegisterCapabilityRecord(DeviceCapabilityRecord record);

    /// <summary>
    /// 获取设备能力
    /// </summary>
    DeviceCapability? GetCapability(string deviceId);

    /// <summary>
    /// 获取设备能力记录
    /// </summary>
    DeviceCapabilityRecord? GetCapabilityRecord(string deviceId);

    /// <summary>
    /// 查找具备指定能力的设备
    /// </summary>
    IEnumerable<string> FindDevices(DeviceCapability requiredCapability);

    /// <summary>
    /// 查找具备全部指定能力的设备
    /// </summary>
    IEnumerable<string> FindDevicesAll(DeviceCapability requiredCapabilities);

    /// <summary>
    /// 检查设备是否具备指定能力
    /// </summary>
    bool HasCapability(string deviceId, DeviceCapability capability);

    /// <summary>
    /// 移除设备能力
    /// </summary>
    bool RemoveCapability(string deviceId);

    /// <summary>
    /// 获取所有设备能力
    /// </summary>
    IReadOnlyList<DeviceCapabilityRecord> GetAllCapabilities();

    /// <summary>
    /// 获取设备能力统计
    /// </summary>
    DeviceCapabilityStats GetStats();
}

/// <summary>
/// 设备能力统计
/// </summary>
public class DeviceCapabilityStats
{
    public int TotalDevices { get; set; }
    public int Conveyors { get; set; }
    public int Lifts { get; set; }
    public int Robots { get; set; }
    public int Sorters { get; set; }
}
