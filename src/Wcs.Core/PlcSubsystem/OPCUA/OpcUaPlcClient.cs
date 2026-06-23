using Wcs.Core.PlcSubsystem.Abstractions;

namespace Wcs.Core.PlcSubsystem.OpcUa;

/// <summary>
/// OPC UA 标签客户端 — 将标签名作为 NodeId，通过 OpcUaConnection 通信
///
/// 标签名即 OPC UA 节点 ID，例如 "ns=2;s=CV01.Speed"
/// OpcUaConnection.ReadAsync() 已自动处理类型转换（int/float/bool/string → byte[]）
/// </summary>
public class OpcUaPlcClient : IPlcClient
{
    private readonly OpcUaConnection _connection;

    public OpcUaPlcClient(OpcUaConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    public async Task<object?> ReadAsync(string tagName, int timeoutMs = 3000)
    {
        // OpcUaConnection.ReadAsync 返回 byte[]，需转回目标类型
        var data = await _connection.ReadAsync(tagName, 1);
        if (data == null) return null;

        return data.Length switch
        {
            1 => (int)data[0],
            2 => (short)((data[0] << 8) | data[1]),
            4 => BitConverter.ToInt32(data),
            8 => BitConverter.ToInt64(data),
            _ => data
        };
    }

    public async Task WriteAsync(string tagName, object? value, int timeoutMs = 3000)
    {
        if (value == null) return;
        var data = value switch
        {
            int i => BitConverter.GetBytes(i),
            short s => new[] { (byte)(s >> 8), (byte)s },
            float f => BitConverter.GetBytes(f),
            bool b => new[] { (byte)(b ? 1 : 0) },
            string s => System.Text.Encoding.UTF8.GetBytes(s),
            byte[] b => b,
            _ => BitConverter.GetBytes(Convert.ToInt32(value))
        };
        await _connection.WriteAsync(tagName, data);
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
}
