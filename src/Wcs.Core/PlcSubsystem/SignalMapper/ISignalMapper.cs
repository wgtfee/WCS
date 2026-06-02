namespace Wcs.Core.PlcSubsystem.SignalMapper;

using Wcs.Core.EventBus.Events;

/// <summary>
/// 信号映射器接口 — 将 PLC 块数据变化转换为业务信号事件
/// </summary>
public interface ISignalMapper
{
    /// <summary>
    /// 注册信号映射定义
    /// </summary>
    void RegisterDefinition(SignalDefinition definition);

    /// <summary>
    /// 批量注册信号映射定义
    /// </summary>
    void RegisterDefinitions(IEnumerable<SignalDefinition> definitions);

    /// <summary>
    /// 移除信号映射定义
    /// </summary>
    bool RemoveDefinition(string signalId);

    /// <summary>
    /// 获取所有信号映射定义
    /// </summary>
    IReadOnlyList<SignalDefinition> GetDefinitions();

    /// <summary>
    /// 解析 PLC 块变化，生成业务信号事件
    /// </summary>
    IReadOnlyList<IEvent> Resolve(PlcBlockDiff diff);

    /// <summary>
    /// 启用/禁用指定信号
    /// </summary>
    void SetEnabled(string signalId, bool enabled);

    /// <summary>
    /// 获取信号映射定义数量
    /// </summary>
    int DefinitionCount { get; }
}
