namespace Wcs.Core.AlarmCenter.Engine;

using System.Collections.Concurrent;
using Wcs.Core.StateCenter.Models;

/// <summary>
/// 报警风暴防护 — 基于滑动窗口的速率限制
/// 每个 AlarmCode 独立计数 + 全局总量双重限制
/// 超过阈值进入 StormMode，批量抑制并通知
/// </summary>
public sealed class AlarmStormGuard
{
    private readonly ConcurrentDictionary<string, SlidingWindowCounter> _perCodeCounters = new();
    private readonly SlidingWindowCounter _globalCounter;
    private readonly int _globalMaxPerWindow;
    private readonly int _windowSizeMs;
    private volatile bool _isInStormMode;
    private DateTime _stormStartTime;
    private int _totalSuppressed;

    /// <summary>
    /// 是否处于风暴模式
    /// </summary>
    public bool IsInStormMode => _isInStormMode;

    /// <summary>
    /// 当前窗口内全局报警数
    /// </summary>
    public int CurrentGlobalCount => _globalCounter.GetCount();

    /// <summary>
    /// 该风暴周期内被抑制的报警总数
    /// </summary>
    public int TotalSuppressed => _totalSuppressed;

    public event Action? StormStarted;   // 风暴开始时通知
    public event Action? StormEnded;     // 风暴结束时通知

    public AlarmStormGuard(int windowSeconds = 60, int globalMaxPerWindow = 1000)
    {
        _windowSizeMs = windowSeconds * 1000;
        _globalMaxPerWindow = globalMaxPerWindow;
        _globalCounter = new SlidingWindowCounter(_windowSizeMs);
    }

    /// <summary>
    /// 检查报警是否应被抑制，返回 true 表示通过（不被抑制），false 表示应该抑制
    /// </summary>
    public bool CheckAndCount(string alarmCode, AlarmRule rule)
    {
        var effectiveThreshold = rule.SuppressionThreshold;
        var codeCounter = _perCodeCounters.GetOrAdd(alarmCode,
            _ => new SlidingWindowCounter(rule.SuppressionWindowSec * 1000));

        int codeCount = codeCounter.Increment();
        int globalCount = _globalCounter.Increment();

        // 检查是否超过阈值
        bool codeExceeded = codeCount > effectiveThreshold;
        bool globalExceeded = globalCount > _globalMaxPerWindow;

        if (codeExceeded || globalExceeded)
        {
            if (!_isInStormMode)
            {
                _isInStormMode = true;
                _stormStartTime = DateTime.UtcNow;
                _totalSuppressed = 0;
                StormStarted?.Invoke();
            }

            Interlocked.Increment(ref _totalSuppressed);
            return false; // 抑制
        }

        // 窗口过期后自动退出风暴模式
        if (_isInStormMode && codeCount <= effectiveThreshold && globalCount <= _globalMaxPerWindow / 2)
        {
            _isInStormMode = false;
            StormEnded?.Invoke();
        }

        return true; // 通过
    }

    public void Reset()
    {
        _perCodeCounters.Clear();
        _globalCounter.Reset();
        _isInStormMode = false;
        _totalSuppressed = 0;
    }

    private sealed class SlidingWindowCounter
    {
        private readonly int _windowMs;
        private readonly ConcurrentQueue<DateTime> _timestamps = new();
        private readonly object _cleanLock = new();

        public SlidingWindowCounter(int windowMs)
        {
            _windowMs = windowMs;
        }

        public int Increment()
        {
            var now = DateTime.UtcNow;
            _timestamps.Enqueue(now);
            CleanOld(now);
            return _timestamps.Count;
        }

        public int GetCount()
        {
            CleanOld(DateTime.UtcNow);
            return _timestamps.Count;
        }

        public void Reset()
        {
            lock (_cleanLock)
            {
                _timestamps.Clear();
            }
        }

        private void CleanOld(DateTime now)
        {
            lock (_cleanLock)
            {
                while (_timestamps.TryPeek(out var ts) && (now - ts).TotalMilliseconds > _windowMs)
                {
                    _timestamps.TryDequeue(out _);
                }
            }
        }
    }
}
