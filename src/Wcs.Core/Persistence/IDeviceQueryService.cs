using Wcs.Core.PlcSubsystem.Examples;

namespace Wcs.Core.Persistence;

/// <summary>
/// 设备数据库查询接口 — 从 Wcs_DeviceRuntime 读取设备运行时数据
/// </summary>
public interface IDeviceQueryService
{
    /// <summary>从 Wcs_DeviceRuntime 读取所有持久化的设备状态</summary>
    Task<List<DeviceRuntimeEntity>> GetDeviceRuntimesAsync(CancellationToken ct = default);
}
