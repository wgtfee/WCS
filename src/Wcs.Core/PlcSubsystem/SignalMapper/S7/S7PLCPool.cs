using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Wcs.Core.PlcSubsystem.SignalMapper.S7
{
    public class S7PLCPool
    {
        #region Fields
        private static readonly ConcurrentDictionary<string, S7PLCPool> _plcInstances = new ConcurrentDictionary<string, S7PLCPool>();
        private readonly string _plcName;
        private readonly string _ipAddress;
        private readonly int _rack;
        private readonly int _slot;
        private readonly Snap7.S7Client _client;
        private readonly ConcurrentQueue<WriteRequest> _writeQueue;
        private readonly ConcurrentQueue<ReadRequest> _readQueue;
        private readonly Timer _queueProcessTimer;
        private readonly SemaphoreSlim _operationSemaphore;
        private const int QUEUE_PROCESS_INTERVAL = 50; // 队列处理间隔(ms)
        private const int OPERATION_TIMEOUT = 5000;    // 操作超时时间(ms)
        private long _totalWriteCount;
        private long _totalReadCount;
        private long _totalWriteTime;
        private long _totalReadTime;
        private int _writeErrorCount;
        private int _readErrorCount;
        private bool _isFirstOperation = true;  // 添加标记字段
        #endregion

        #region Inner Classes
        private class WriteRequest
        {
            public int Db { get; set; }
            public int StartByteAdr { get; set; }
            public int Count { get; set; }
            public object Data { get; set; }
            public DateTime RequestTime { get; set; }
            public TaskCompletionSource<(int Result, string ErrorText)> CompletionSource { get; set; }
        }

        private class ReadRequest
        {
            public int Db { get; set; }
            public int StartByteAdr { get; set; }
            public int Count { get; set; }
            public DateTime RequestTime { get; set; }
            public TaskCompletionSource<(byte[] Data, int Result, string ErrorText)> CompletionSource { get; set; }
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
            _writeQueue = new ConcurrentQueue<WriteRequest>();
            _readQueue = new ConcurrentQueue<ReadRequest>();
            _operationSemaphore = new SemaphoreSlim(1, 1);

            // 使用单个定时器处理所有队列
            _queueProcessTimer = new Timer(ProcessQueues, null, QUEUE_PROCESS_INTERVAL, QUEUE_PROCESS_INTERVAL);
        }
        #endregion

        #region Public Methods
        public static S7PLCPool GetInstance(string plcName, string ipAddress, int rack = 0, int slot = 0)
        {
            return _plcInstances.GetOrAdd(plcName, _ => new S7PLCPool(plcName, ipAddress, rack, slot));
        }

        public static bool RemoveInstance(string plcName)
        {
            if (_plcInstances.TryRemove(plcName, out var instance))
            {
                instance.Dispose();
                return true;
            }
            return false;
        }

        public static IEnumerable<string> GetAllInstanceNames()
        {
            return _plcInstances.Keys;
        }

        public int ConnectPLC(out string errorText)
        {
            errorText = string.Empty;
            if (_client == null) return 1;

            int result = _client.ConnectTo(_ipAddress, _rack, _slot);
            errorText = _client.ErrorText(result);

            if (result == 0) return 0;

            if (_client.Connect() == 0)
            {
                result = _client.Connect();
            }

            return result;
        }

        public void DisconnectPLC()
        {
            if (_client?.Connected() == true)
            {
                _client.Disconnect();
            }
        }

        public async Task<(int Result, string ErrorText)> WritePLCDataAsync(int db, int startByteAdr, int count, object writeData)
        {
            var request = new WriteRequest
            {
                Db = db,
                StartByteAdr = startByteAdr,
                Count = count,
                Data = writeData,
                RequestTime = DateTime.Now,
                CompletionSource = new TaskCompletionSource<(int Result, string ErrorText)>()
            };

            _writeQueue.Enqueue(request);
            return await request.CompletionSource.Task;
        }

        public async Task<(byte[] Data, int Result, string ErrorText)> ReadPLCDataAsync(int db, int startByteAdr, int count)
        {
            var request = new ReadRequest
            {
                Db = db,
                StartByteAdr = startByteAdr,
                Count = count,
                RequestTime = DateTime.Now,
                CompletionSource = new TaskCompletionSource<(byte[] Data, int Result, string ErrorText)>()
            };

            _readQueue.Enqueue(request);
            return await request.CompletionSource.Task;
        }

        public class PLCStatistics
        {
            public int WriteQueueCount { get; set; }
            public int ReadQueueCount { get; set; }
            public long TotalWriteCount { get; set; }
            public long TotalReadCount { get; set; }
            public double AverageWriteTime { get; set; }
            public double AverageReadTime { get; set; }
            public int WriteErrorCount { get; set; }
            public int ReadErrorCount { get; set; }
        }

        public PLCStatistics GetStatistics()
        {
            return new PLCStatistics
            {
                WriteQueueCount = _writeQueue.Count,
                ReadQueueCount = _readQueue.Count,
                TotalWriteCount = _totalWriteCount,
                TotalReadCount = _totalReadCount,
                AverageWriteTime = _totalWriteCount > 0 ? (double)_totalWriteTime / _totalWriteCount : 0,
                AverageReadTime = _totalReadCount > 0 ? (double)_totalReadTime / _totalReadCount : 0,
                WriteErrorCount = _writeErrorCount,
                ReadErrorCount = _readErrorCount
            };
        }
        #endregion

        #region Private Methods
        private async void ProcessQueues(object state)
        {
            if (_writeQueue.IsEmpty && _readQueue.IsEmpty) return;

            bool semaphoreAcquired = false;
            try
            {
                semaphoreAcquired = await _operationSemaphore.WaitAsync(OPERATION_TIMEOUT);
                if (!semaphoreAcquired) return;

                // 第一次操作时，确保先读后写
                if (_isFirstOperation)
                {
                    _isFirstOperation = false;

                    // 先处理所有读取请求
                    while (_readQueue.TryDequeue(out var readRequest))
                    {
                        var startTime = DateTime.Now;
                        await ProcessReadRequest(readRequest);
                        UpdateReadStatistics(DateTime.Now - startTime);
                    }

                    // 再处理所有写入请求
                    while (_writeQueue.TryDequeue(out var writeRequest))
                    {
                        var startTime = DateTime.Now;
                        await ProcessWriteRequest(writeRequest);
                        UpdateWriteStatistics(DateTime.Now - startTime);
                    }
                }
                else
                {
                    // 后续操作按照队列顺序处理
                    var readRequests = new List<(DateTime Time, ReadRequest Request)>();
                    var writeRequests = new List<(DateTime Time, WriteRequest Request)>();

                    // 收集所有请求
                    while (_readQueue.TryDequeue(out var readRequest))
                    {
                        readRequests.Add((readRequest.RequestTime, readRequest));
                    }
                    while (_writeQueue.TryDequeue(out var writeRequest))
                    {
                        writeRequests.Add((writeRequest.RequestTime, writeRequest));
                    }

                    // 按时间顺序合并和处理请求
                    var allRequests = new List<(DateTime Time, Action Process)>();
                    foreach (var read in readRequests)
                    {
                        allRequests.Add((read.Time, async () =>
                        {
                            var startTime = DateTime.Now;
                            await ProcessReadRequest(read.Request);
                            UpdateReadStatistics(DateTime.Now - startTime);
                        }
                        ));
                    }
                    foreach (var write in writeRequests)
                    {
                        allRequests.Add((write.Time, async () =>
                        {
                            var startTime = DateTime.Now;
                            await ProcessWriteRequest(write.Request);
                            UpdateWriteStatistics(DateTime.Now - startTime);
                        }
                        ));
                    }

                    // 按请求时间顺序处理
                    foreach (var request in allRequests.OrderBy(r => r.Time))
                    {
                        await Task.Run(request.Process);
                    }
                }
            }
            finally
            {
                if (semaphoreAcquired)
                {
                    _operationSemaphore.Release();
                }
            }
        }

        private void UpdateReadStatistics(TimeSpan duration)
        {
            Interlocked.Increment(ref _totalReadCount);
            Interlocked.Add(ref _totalReadTime, (long)duration.TotalMilliseconds);
        }

        private void UpdateWriteStatistics(TimeSpan duration)
        {
            Interlocked.Increment(ref _totalWriteCount);
            Interlocked.Add(ref _totalWriteTime, (long)duration.TotalMilliseconds);
        }

        private async Task ProcessWriteRequest(WriteRequest request)
        {
            try
            {
                if (!_client.Connected())
                {
                    request.CompletionSource.SetResult((-1, "PLC未连接"));
                    return;
                }

                await Task.Run(() =>
                {
                    try
                    {
                        byte[] bytes = Struct.ToBytes(request.Data, request.StartByteAdr, request.Count);
                        int result = _client.WriteArea(0x84, request.Db, request.StartByteAdr,
                            request.Count - request.StartByteAdr, 0x2, bytes);

                        string errorText = $"{_client.ErrorText(result)}\0返回数值{result}";

                        if (result != 0 && _client.Connect() == 0)
                        {
                            result = _client.WriteArea(0x84, request.Db, request.StartByteAdr,
                                request.Count - request.StartByteAdr, 0x2, bytes);
                            errorText = $"{_client.ErrorText(result)}\0返回数值{result}";
                        }

                        request.CompletionSource.SetResult((result, errorText));
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _writeErrorCount);
                        request.CompletionSource.SetResult((-3, $"处理写入请求异常: {ex.Message}"));
                    }
                });
            }
            catch (Exception ex)
            {
                request.CompletionSource.SetResult((-3, $"处理写入请求异常: {ex.Message}"));
            }
        }

        private async Task ProcessReadRequest(ReadRequest request)
        {
            try
            {
                if (!_client.Connected())
                {
                    request.CompletionSource.SetResult((new byte[request.Count], -1, "PLC未连接"));
                    return;
                }

                await Task.Run(() =>
                {
                    try
                    {
                        var bytes = new byte[request.Count];
                        int result = _client.ReadArea(0x84, request.Db, request.StartByteAdr,
                            request.Count, 0x2, bytes);

                        string errorText = $"{_client.ErrorText(result)}\0返回数值{result}";

                        if (result != 0 && _client.Connect() == 0)
                        {
                            result = _client.ReadArea(0x84, request.Db, request.StartByteAdr,
                                request.Count, 0x2, bytes);
                            errorText = $"{_client.ErrorText(result)}\0返回数值{result}";
                        }

                        request.CompletionSource.SetResult((bytes, result, errorText));
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _readErrorCount);
                        request.CompletionSource.SetResult((new byte[request.Count], -3, $"处理读取请求异常: {ex.Message}"));
                    }
                });
            }
            catch (Exception ex)
            {
                request.CompletionSource.SetResult((new byte[request.Count], -3, $"处理读取请求异常: {ex.Message}"));
            }
        }

        private void Dispose()
        {
            DisconnectPLC();
            _queueProcessTimer?.Dispose();
            _operationSemaphore?.Dispose();
        }
        #endregion
    }
}
