namespace Wcs.Infrastructure.S7;

using Wcs.Core.PlcSubsystem;
using S7net = global::S7.Net;

public class S7RealClient : IS7Connection
{
    private readonly S7ConnectionConfig _config;
    private readonly PlcConnectionStatus _status;
    private S7net.Plc? _plc;
    private readonly object _lock = new();

    public string PlcName => _config.PlcName;
    public bool IsConnected
    {
        get { lock (_lock) return _plc?.IsConnected ?? false; }
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
            if (_plc?.IsConnected == true) return true;
            _status.Status = PlcConnectionStatusEnum.Connecting;
        }

        try
        {
            _plc = new S7net.Plc(
                S7net.CpuType.S71500,
                _config.Address,
                (short)_config.Rack,
                (short)_config.Slot);

            await Task.Run(() => _plc.Open(), cancellationToken);

            lock (_lock)
            {
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
                _status.Status = PlcConnectionStatusEnum.Failed;
                _status.LastError = ex.Message;
                _status.FailureCount++;
            }
            return false;
        }
    }

    public Task<bool> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_plc?.IsConnected != true) return Task.FromResult(true);
            _status.Status = PlcConnectionStatusEnum.Disconnecting;
        }

        try
        {
            _plc?.Close();
            lock (_lock)
            {
                _status.Status = PlcConnectionStatusEnum.Disconnected;
            }
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            lock (_lock) { _status.LastError = ex.Message; }
            return Task.FromResult(false);
        }
    }

    public Task<byte[]?> ReadBlockAsync(int blockNumber, int length, CancellationToken ct = default)
    {
        if (_plc?.IsConnected != true) return Task.FromResult<byte[]?>(null);

        try
        {
            var result = _plc.ReadBytes(S7net.DataType.DataBlock, blockNumber, 0, length);
            lock (_lock)
            {
                _status.ReadCount++;
                _status.LastHeartbeat = DateTime.UtcNow;
            }
            return Task.FromResult<byte[]?>(result);
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
        if (_plc?.IsConnected != true) return Task.FromResult(false);

        try
        {
            _plc.WriteBytes(S7net.DataType.DataBlock, blockNumber, 0, data);
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
}

public interface IS7ConnectionFactory
{
    IEnumerable<IS7Connection> CreateAll();
}

public class S7ConnectionFactory : IS7ConnectionFactory
{
    private readonly List<S7ConnectionConfig> _configs;

    public S7ConnectionFactory(IEnumerable<S7ConnectionConfig> configs)
    {
        _configs = configs.ToList();
    }

    public IEnumerable<IS7Connection> CreateAll()
    {
        return _configs.Select(c => new S7RealClient(c)).ToList();
    }
}
