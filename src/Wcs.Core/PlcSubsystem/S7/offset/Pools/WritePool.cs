using System.Collections.Concurrent;
using Snap7;

namespace Wcs.Core.PlcSubsystem.Pools;

/// <summary>写连接池 — 专用于 PLC 写入，与读连接池物理隔离</summary>
public class WritePool : IDisposable
{
    private readonly ConcurrentDictionary<string, WriteConnection> _connections = new();
    public WriteConnection GetOrCreate(string plcName, string address, int rack = 0, int slot = 0)
        => _connections.GetOrAdd(plcName, _ => new WriteConnection(plcName, address, rack, slot));
    public WriteConnection? Get(string plcName) => _connections.TryGetValue(plcName, out var c) ? c : null;
    public void Dispose() { foreach (var c in _connections.Values) c.Dispose(); _connections.Clear(); }
}

public class WriteConnection : IDisposable
{
    private readonly S7Client _client = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _plcName, _address;
    private readonly int _rack, _slot;
    private long _writeCount, _errorCount;
    public string PlcName => _plcName;

    public WriteConnection(string plcName, string address, int rack = 0, int slot = 0)
    { _plcName = plcName; _address = address; _rack = rack; _slot = slot; }

    public int Connect(out string error)
    {
        error = ""; var r = _client.ConnectTo(_address, _rack, _slot);
        if (r == 0) return 0; error = _client.ErrorText(r);
        r = _client.Connect();
        if (r == 0) return 0; error = _client.ErrorText(r);
        return r;
    }

    public async Task<(int Result, string Error)> WriteAsync(int db, int start, byte[] data)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureConnected();
            var r = _client.WriteArea(0x84, db, start, data.Length, 0x2, data);
            Interlocked.Increment(ref _writeCount);
            if (r != 0) { Interlocked.Increment(ref _errorCount); return (r, _client.ErrorText(r)); }
            return (0, "");
        }
        finally { _lock.Release(); }
    }

    private void EnsureConnected() { if (_client.Connected()) return;
        _client.ConnectTo(_address, _rack, _slot); if (!_client.Connected()) _client.Connect(); }

    public long WriteCount => Interlocked.Read(ref _writeCount);
    public long ErrorCount => Interlocked.Read(ref _errorCount);
    public void Dispose() { _lock.Dispose(); try { _client.Disconnect(); } catch { } }
}
