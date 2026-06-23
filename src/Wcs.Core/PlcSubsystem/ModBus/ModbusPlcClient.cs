using Wcs.Core.PlcSubsystem.Abstractions;
using Wcs.Core.PlcSubsystem.Pools;

namespace Wcs.Core.PlcSubsystem.Modbus;

/// <summary>
/// Modbus 标签客户端 — 将标签名解析为 Modbus 地址，通过 ModbusConnection 通信
///
/// 标签名格式：{RegisterType}:{Offset}
///   例如 "HR:0" → 保持寄存器0, "IR:10" → 输入寄存器10
/// </summary>
public class ModbusPlcClient : IPlcClient
{
    private readonly ModbusConnection _connection;

    public ModbusPlcClient(ModbusConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    public async Task<object?> ReadAsync(string tagName, int timeoutMs = 3000)
    {
        var (addr, count) = ParseAddress(tagName);
        var data = await _connection.ReadAsync(addr, (ushort)count);
        if (data == null) return null;

        // Modbus 寄存器 16-bit，取前 2 字节转 short（大端序）
        if (data.Length >= 2)
            return (short)((data[0] << 8) | data[1]);
        return data.Length > 0 ? (int)data[0] : null;
    }

    public async Task WriteAsync(string tagName, object? value, int timeoutMs = 3000)
    {
        var (addr, count) = ParseAddress(tagName);

        // 将值转为 byte[]（大端序 short）
        var shortVal = Convert.ToInt16(value);
        var data = new byte[] { (byte)(shortVal >> 8), (byte)shortVal };

        await _connection.WriteAsync(addr, data);
    }

    public async Task<object?[]> ReadBatchAsync(string[] tagNames, int timeoutMs = 3000)
    {
        var results = new object?[tagNames.Length];
        for (int i = 0; i < tagNames.Length; i++)
            results[i] = await ReadAsync(tagNames[i], timeoutMs);
        return results;
    }

    public async Task WriteBatchAsync(IEnumerable<(string Name, object? Value)> writes, int timeoutMs = 3000)
    {
        foreach (var (name, value) in writes)
            await WriteAsync(name, value, timeoutMs);
    }

    private static (string addr, int count) ParseAddress(string tagName)
    {
        var parts = tagName.Split(':');
        if (parts.Length < 2) return (tagName, 1);
        var offset = int.TryParse(parts[1], out var o) ? o : 0;
        return ($"{parts[0]}:{offset}", 1);
    }
}
