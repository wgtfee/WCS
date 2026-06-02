namespace Wcs.Infrastructure.S7;

using Wcs.Core.PlcSubsystem;

/// <summary>
/// 真实 S7 连接 - 使用 Sharp7 或 S7netplus
/// 当前为预留骨架，生产环境需引用 S7netplus 包
/// </summary>
public class S7RealClient : IS7Connection
{
    private readonly S7ConnectionConfig _config;
    private readonly PlcConnectionStatus _status;
    private readonly object _lock = new();
    private bool _disposed;

    // 生产环境: private Plc _plc;
    private bool _isConnected;

    public string PlcName => _config.PlcName;
    public bool IsConnected
    {
        get { lock (_lock) return _isConnected; }
    }

    public S7RealClient(S7ConnectionConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _status = new PlcConnectionStatus
        {
            PlcName = config.PlcName,
            Status = PlcConnectionStatusEnum.Disconnected
        };
    }

    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_isConnected) return true;
            _status.Status = PlcConnectionStatusEnum.Connecting;
        }

        try
        {
            // 生产环境:
            // _plc = new Plc(CpuType.S71500, _config.Address, _config.Rack, _config.Slot);
            // _plc.Timeout = _config.Timeout;
            // await Task.Run(() => _plc.Open(), cancellationToken);

            await Task.Delay(50, cancellationToken);

            lock (_lock)
            {
                _isConnected = true;
                _status.Status = PlcConnectionStatusEnum.Connected;
                _status.LastConnectTime = DateTime.UtcNow;
                _status.LastHeartbeat = DateTime.UtcNow;
                _status.FailureCount = 0;
            }
            return true;
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _isConnected = false;
                _status.Status = PlcConnectionStatusEnum.Failed;
                _status.LastError = ex.Message;
                _status.FailureCount++;
            }
            return false;
        }
    }

    public async Task<bool> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_isConnected) return true;
            _status.Status = PlcConnectionStatusEnum.Disconnecting;
        }

        try
        {
            // _plc?.Close();
            await Task.Delay(30, cancellationToken);

            lock (_lock)
            {
                _isConnected = false;
                _status.Status = PlcConnectionStatusEnum.Disconnected;
            }
            return true;
        }
        catch (Exception ex)
        {
            lock (_lock) { _status.LastError = ex.Message; }
            return false;
        }
    }

    public Task<byte[]?> ReadBlockAsync(int blockNumber, int length, CancellationToken ct = default)
    {
        if (!IsConnected) return Task.FromResult<byte[]?>(null);

        try
        {
            // 生产环境: var result = _plc.ReadBytes(DataType.DataBlock, blockNumber, 0, length);
            var data = new byte[length];
            lock (_lock)
            {
                _status.ReadCount++;
                _status.LastHeartbeat = DateTime.UtcNow;
            }
            return Task.FromResult<byte[]?>(data);
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _status.LastError = ex.Message;
                _status.FailureCount++;
            }
            return Task.FromResult<byte[]?>(null);
        }
    }

    public Task<bool> WriteBlockAsync(int blockNumber, byte[] data, CancellationToken ct = default)
    {
        if (!IsConnected) return Task.FromResult(false);

        try
        {
            // _plc.WriteBytes(DataType.DataBlock, blockNumber, 0, data);
            lock (_lock)
            {
                _status.WriteCount++;
                _status.LastHeartbeat = DateTime.UtcNow;
            }
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _status.LastError = ex.Message;
                _status.FailureCount++;
            }
            return Task.FromResult(false);
        }
    }

    public PlcConnectionStatus GetStatus()
    {
        lock (_lock)
        {
            return new PlcConnectionStatus
            {
                PlcName = _status.PlcName,
                Status = _status.Status,
                LastConnectTime = _status.LastConnectTime,
                LastHeartbeat = _status.LastHeartbeat,
                FailureCount = _status.FailureCount,
                LastError = _status.LastError,
                ReadCount = _status.ReadCount,
                WriteCount = _status.WriteCount
            };
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        // _plc?.Dispose();
        _disposed = true;
    }
}
