using System.Collections.Concurrent;
using Snap7;

namespace Wcs.Core.PlcSubsystem.Pools;

public class ReadPool : IDisposable
{
    private readonly ConcurrentDictionary<string, ReadConnection> _connections = new();
    public ReadConnection GetOrCreate(string plcName, string address, int rack = 0, int slot = 0)
        => _connections.GetOrAdd(plcName, _ => new ReadConnection(plcName, address, rack, slot));
    public ReadConnection? Get(string plcName) => _connections.TryGetValue(plcName, out var c) ? c : null;
    public IEnumerable<ReadConnection> GetAll() => _connections.Values;
    public void Dispose() { foreach (var c in _connections.Values) c.Dispose(); _connections.Clear(); }
}

public class ReadConnection : IDisposable
{
    private readonly S7Client _client = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _plcName, _address;
    private readonly int _rack, _slot;
    private long _readCount, _errorCount;
    public string PlcName => _plcName;

    public ReadConnection(string plcName, string address, int rack = 0, int slot = 0)
    { _plcName = plcName; _address = address; _rack = rack; _slot = slot; }

    public int Connect(out string error)
    {
        error = ""; var r = _client.ConnectTo(_address, _rack, _slot);
        if (r == 0) return 0; error = _client.ErrorText(r);
        r = _client.Connect();
        if (r == 0) return 0; error = _client.ErrorText(r);
        return r;
    }

    public async Task<(byte[] Data, int Result, string Error)> ReadAsync(int db, int start, int count)
    {
        await _lock.WaitAsync();
        try
        {
            EnsureConnected(); var data = new byte[count];
            var r = _client.ReadArea(0x84, db, start, count, 0x2, data);
            Interlocked.Increment(ref _readCount);
            if (r != 0) { Interlocked.Increment(ref _errorCount); return (data, r, _client.ErrorText(r)); }
            return (data, 0, "");
        }
        finally { _lock.Release(); }
    }

    private void EnsureConnected() { if (_client.Connected()) return;
        _client.ConnectTo(_address, _rack, _slot); if (!_client.Connected()) _client.Connect(); }

    public long ReadCount => Interlocked.Read(ref _readCount);
    public long ErrorCount => Interlocked.Read(ref _errorCount);
    public void Dispose() { _lock.Dispose(); try { _client.Disconnect(); } catch { } }
}
