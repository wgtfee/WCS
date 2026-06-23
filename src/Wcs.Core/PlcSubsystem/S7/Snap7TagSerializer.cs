using System.Reflection;
using Wcs.Core.PlcSubsystem.Abstractions;

namespace Wcs.Core.PlcSubsystem.S7;

/// <summary>
/// Snap7 标签序列化器 — 适配现有 Snap7 系统到 ITagSerializer 接口
///
/// 写入委托给 PlcWriter.WriteStructAsync()，读取暂不支持（走 S7PollingService）
///
/// 用法：
///   var writer = new TagWriter(new Snap7TagSerializer(plcWriter), db, logger);
///   await writer.WriteAsync(new ConveyorControlCommand { StartStation1 = true });
/// </summary>
public class Snap7TagSerializer : ITagSerializer
{
    private readonly PlcWriter _writer;

    public Snap7TagSerializer(PlcWriter writer)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    /// <summary>
    /// Snap7 写入 — 委托给 PlcWriter.WriteStructAsync()
    /// 支持 [PlcBlock] + [PlcOffset] 标注的 struct
    /// </summary>
    public async Task WriteAsync(object obj)
    {
        var type = obj.GetType();
        var blockAttr = type.GetCustomAttribute<PlcBlockAttribute>();
        if (blockAttr == null)
            throw new InvalidOperationException($"类型 '{type.Name}' 缺少 [PlcBlock] 特性");

        // 通过反射调用 WriteStructAsync<T> 泛型方法
        var method = typeof(PlcWriter).GetMethod("WriteStructAsync")?
            .MakeGenericMethod(type);

        if (method == null)
            throw new InvalidOperationException("找不到 PlcWriter.WriteStructAsync 方法");

        var task = (Task<bool>)method.Invoke(_writer, [obj, null, null, null])!;
        var success = await task;
        if (!success)
            throw new InvalidOperationException($"Snap7 写入 '{type.Name}' 失败");
    }

    /// <summary>
    /// Snap7 读取暂不支持 — Snap7 的数据读取通过 S7PollingService 完成
    /// </summary>
    public Task ReadAsync(object obj)
    {
        throw new NotSupportedException(
            "Snap7 不支持通过 ITagSerializer 读取，请使用 S7PollingService");
    }

    public Task<bool> CheckHealthAsync()
    {
        // Snap7 连接由 Pool 管理，写入时自动重连
        return Task.FromResult(true);
    }
}
