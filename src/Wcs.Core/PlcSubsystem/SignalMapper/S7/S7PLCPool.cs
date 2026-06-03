using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Wcs.Core.PlcSubsystem.SignalMapper.S7
{
    /// <summary>
    /// S7 PLC 连接池 — 带队列和重连，单一线程按序处理读写
    /// 修复：async void 丢失、重连IP错误、请求堆积
    /// </summary>
    public class S7PLCPool
    {
        #region Fields
        private static readonly ConcurrentDictionary<string, S7PLCPool> _plcInstances = new();
        private readonly string _plcName;
        private readonly string _ipAddress;
        private readonly int _rack;
        private readonly int _slot;
        private readonly Snap7.S7Client _client;
        private readonly Channel<ReadRequest> _readChannel = new();
        private readonly Channel<WriteRequest> _writeChannel = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _operationSemaphore = new(1, 1);
        private long _totalReads, _totalWrites, _totalReadTime, _totalWriteTime;
        private int _readErrors, _writeErrors;
        private bool _disposed;

        private const int MAX_RETRIES = 2;
        #endregion

        #region Request Types
        private class ReadRequest
        {
            public int Db { get; set; }
            public int StartByteAdr { get; set; }
            public int Count { get; set; }
            public TaskCompletionSource<(byte[] Data, int Result, string ErrorText)> Tcs { get; set; } = new();
        }

        private class WriteRequest
        {
            public int Db { get; set; }
            public int StartByteAdr { get; set; }
            public int Count { get; set; }
            public object Data { get; set; } = null!;
            public TaskCompletionSource<(int Result, string ErrorText)> Tcs { get; set; } = new();
        }

        /// <summary>优先级通道 — 写优先于读（确保命令先到达）</summary>
        private class Channel<T>
        {
            private readonly ConcurrentQueue<T> _high = new();
            private readonly ConcurrentQueue<T> _normal = new();

            public void Enqueue(T item, bool highPriority = false)
            {
                if (highPriority) _high.Enqueue(item);
                else _normal.Enqueue(item);
            }

            public bool TryDequeue(out T? item)
            {
                if (_high.TryDequeue(out item)) return true;
                return _normal.TryDequeue(out item);
            }

            public int Count => _high.Count + _normal.Count;
        }
        #endregion

        #region Constructor
        private S7PLCPool(string plcName, string ipAddress, int rack = 0, int slot = 0)
        {
            _plcName = plcName;
            _ipAddress = ipAddress;
            _rack = rack;
            _slot = slot;
            _client = new Snap7.S7Client();

            // 启动后台处理线程（单一线程，按序处理）
            Task.Run(() => ProcessLoopAsync(_cts.Token));
        }

        public static S7PLCPool GetInstance(string plcName, string ipAddress, int rack = 0, int slot = 0)
            => _plcInstances.GetOrAdd(plcName, _ => new S7PLCPool(plcName, ipAddress, rack, slot));

        public static bool RemoveInstance(string plcName)
        {
            if (_plcInstances.TryRemove(plcName, out var inst))
            {
                inst.Dispose();
                return true;
            }
            return false;
        }

        public static IEnumerable<string> GetAllInstanceNames() => _plcInstances.Keys;
        #endregion

        #region Connect / Disconnect

        public int ConnectPLC(out string errorText)
        {
            errorText = string.Empty;
            if (_client == null) return 1;

            var result = _client.ConnectTo(_ipAddress, _rack, _slot);
            errorText = _client.ErrorText(result);

            if (result == 0) return 0;

            // ConnectTo 失败时尝试 Connect（无参Connect连的是之前设置过的地址）
            result = _client.Connect();
            errorText = _client.ErrorText(result);
            return result;
        }

        public void DisconnectPLC()
        {
            if (_client?.Connected() == true)
                _client.Disconnect();
        }

        /// <summary>确保已连接，断开时自动重连</summary>
        private int EnsureConnected()
        {
            if (_client == null) return 1;
            if (_client.Connected()) return 0;

            // 重连
            var result = _client.ConnectTo(_ipAddress, _rack, _slot);
            if (result == 0) return 0;

            result = _client.Connect();
            return result;
        }
        #endregion

        #region Public API

        public async Task<(byte[] Data, int Result, string ErrorText)> ReadPLCDataAsync(
            int db, int startByteAdr, int count)
        {
            var req = new ReadRequest { Db = db, StartByteAdr = startByteAdr, Count = count };
            _readChannel.Enqueue(req);
            return await req.Tcs.Task.ConfigureAwait(false);
        }

        public async Task<(int Result, string ErrorText)> WritePLCDataAsync(
            int db, int startByteAdr, int count, object writeData)
        {
            var req = new WriteRequest
            {
                Db = db,
                StartByteAdr = startByteAdr,
                Count = count,
                Data = writeData
            };
            _writeChannel.Enqueue(req, highPriority: true); // 写优先
            return await req.Tcs.Task.ConfigureAwait(false);
        }
        #endregion

        #region Background Processing Loop

        /// <summary>
        /// 单一线程后台循环 — 按序处理读写请求
        /// 修复：原版 async void Timer + Action 导致请求丢失
        /// </summary>
        private async Task ProcessLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // 写优先（确保命令先到达 PLC）
                    if (_writeChannel.TryDequeue(out var writeReq))
                    {
                        await _operationSemaphore.WaitAsync(ct);
                        try { await ExecuteWriteAsync(writeReq); }
                        finally { _operationSemaphore.Release(); }
                        continue;
                    }

                    // 再处理读
                    if (_readChannel.TryDequeue(out var readReq))
                    {
                        await _operationSemaphore.WaitAsync(ct);
                        try { await ExecuteReadAsync(readReq); }
                        finally { _operationSemaphore.Release(); }
                        continue;
                    }

                    // 队列空，等一段时间再轮询
                    await Task.Delay(10, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    // 防止后台循环意外退出
                    await Task.Delay(100, ct);
                }
            }
        }

        private async Task ExecuteReadAsync(ReadRequest req)
        {
            var start = DateTime.Now;
            var bytes = new byte[req.Count];

            for (int retry = 0; retry <= MAX_RETRIES; retry++)
            {
                var connResult = EnsureConnected();
                if (connResult != 0)
                {
                    if (retry == MAX_RETRIES)
                    {
                        Interlocked.Increment(ref _readErrors);
                        req.Tcs.SetResult((bytes, -1, "PLC连接失败"));
                        return;
                    }
                    await Task.Delay(100 * (retry + 1));
                    continue;
                }

                var result = _client.ReadArea(0x84, req.Db, req.StartByteAdr, req.Count, 0x2, bytes);
                if (result == 0)
                {
                    UpdateStats(ref _totalReads, ref _totalReadTime, start);
                    req.Tcs.SetResult((bytes, 0, ""));
                    return;
                }

                if (retry == MAX_RETRIES)
                {
                    Interlocked.Increment(ref _readErrors);
                    req.Tcs.SetResult((bytes, result, _client.ErrorText(result)));
                }
            }
        }

        private async Task ExecuteWriteAsync(WriteRequest req)
        {
            var start = DateTime.Now;

            for (int retry = 0; retry <= MAX_RETRIES; retry++)
            {
                var connResult = EnsureConnected();
                if (connResult != 0)
                {
                    if (retry == MAX_RETRIES)
                    {
                        Interlocked.Increment(ref _writeErrors);
                        req.Tcs.SetResult((-1, "PLC连接失败"));
                        return;
                    }
                    await Task.Delay(100 * (retry + 1));
                    continue;
                }

                byte[] bytes;
                try
                {
                    bytes = Struct.ToBytes(req.Data, req.StartByteAdr, req.Count);
                }
                catch (Exception ex)
                {
                    req.Tcs.SetResult((-3, $"Struct序列化失败: {ex.Message}"));
                    return;
                }

                var result = _client.WriteArea(0x84, req.Db, req.StartByteAdr,
                    req.Count - req.StartByteAdr, 0x2, bytes);

                if (result == 0)
                {
                    UpdateStats(ref _totalWrites, ref _totalWriteTime, start);
                    req.Tcs.SetResult((0, ""));
                    return;
                }

                if (retry == MAX_RETRIES)
                {
                    Interlocked.Increment(ref _writeErrors);
                    req.Tcs.SetResult((result, _client.ErrorText(result)));
                }
            }
        }

        private static void UpdateStats(ref long count, ref long totalTime, DateTime start)
        {
            Interlocked.Increment(ref count);
            Interlocked.Add(ref totalTime, (long)(DateTime.Now - start).TotalMilliseconds);
        }
        #endregion

        #region Statistics

        public class PLCStatistics
        {
            public int WriteQueueCount { get; set; }
            public int ReadQueueCount { get; set; }
            public long TotalReads { get; set; }
            public long TotalWrites { get; set; }
            public double AverageReadTime { get; set; }
            public double AverageWriteTime { get; set; }
            public int ReadErrors { get; set; }
            public int WriteErrors { get; set; }
        }

        public PLCStatistics GetStatistics()
        {
            return new PLCStatistics
            {
                WriteQueueCount = _writeChannel.Count,
                ReadQueueCount = _readChannel.Count,
                TotalReads = Interlocked.Read(ref _totalReads),
                TotalWrites = Interlocked.Read(ref _totalWrites),
                AverageReadTime = _totalReads > 0 ? (double)_totalReadTime / _totalReads : 0,
                AverageWriteTime = _totalWrites > 0 ? (double)_totalWriteTime / _totalWrites : 0,
                ReadErrors = _readErrors,
                WriteErrors = _writeErrors
            };
        }
        #endregion

        #region Dispose

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            _cts.Dispose();
            DisconnectPLC();
            _operationSemaphore.Dispose();
        }
        #endregion
    }
}
