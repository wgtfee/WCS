namespace Wcs.Core.AlarmCenter.Engine;

using System.Collections.Concurrent;
using Wcs.Core.AlarmCenter.Models;
using Wcs.Core.StateCenter.Models;

/// <summary>
/// 报警防抖引擎 — 为每个 AlarmCode 提供可配置的 DelayRaise / DelayRecover
/// PLC 信号在 DelayRaiseMs 窗口内抖动会重置计时器，稳定后才确认报警
/// </summary>
public sealed class AlarmDebounceEngine : IDisposable
{
    private readonly ConcurrentDictionary<string, DebounceEntry> _entries = new();
    private readonly Action<string> _onConfirmedRaise;   // alarmCode → pending→active
    private readonly Action<string> _onConfirmedRecover; // alarmCode → pendingRecover→recovered
    private readonly Action<string> _onCanceledRaise;    // alarmCode → pendingRaise→normal
    private readonly Action<string> _onRebounce;         // alarmCode → pendingRecover→active

    public AlarmDebounceEngine(
        Action<string> onConfirmedRaise,
        Action<string> onConfirmedRecover,
        Action<string> onCanceledRaise,
        Action<string> onRebounce)
    {
        _onConfirmedRaise = onConfirmedRaise ?? throw new ArgumentNullException(nameof(onConfirmedRaise));
        _onConfirmedRecover = onConfirmedRecover ?? throw new ArgumentNullException(nameof(onConfirmedRecover));
        _onCanceledRaise = onCanceledRaise ?? throw new ArgumentNullException(nameof(onCanceledRaise));
        _onRebounce = onRebounce ?? throw new ArgumentNullException(nameof(onRebounce));
    }

    /// <summary>
    /// 报警信号到达 — 开始/重置 DelayRaise 计时器
    /// </summary>
    public void SignalRaise(string alarmCode, AlarmRule rule)
    {
        var entry = _entries.GetOrAdd(alarmCode, _ => new DebounceEntry(rule));

        lock (entry.Lock)
        {
            entry.ResetRaiseTimer(() =>
            {
                // DelayRaise 到期 → 确认报警
                _onConfirmedRaise(alarmCode);
            });
        }
    }

    /// <summary>
    /// 恢复信号到达 — 开始/重置 DelayRecover 计时器
    /// </summary>
    public void SignalRecover(string alarmCode, AlarmRule rule)
    {
        var entry = _entries.GetOrAdd(alarmCode, _ => new DebounceEntry(rule));

        lock (entry.Lock)
        {
            entry.ResetRecoverTimer(() =>
            {
                // DelayRecover 到期 → 确认恢复
                _onConfirmedRecover(alarmCode);
            });
        }
    }

    /// <summary>
    /// 取消未确认的 PendingRaise（信号在 DelayRaise 窗口内消失）
    /// </summary>
    public void CancelRaise(string alarmCode)
    {
        var entry = _entries.GetOrAdd(alarmCode, _ => new DebounceEntry(new AlarmRule()));

        lock (entry.Lock)
        {
            entry.CancelRaiseTimer();
            _onCanceledRaise(alarmCode);
        }
    }

    /// <summary>
    /// PendingRecover 期间信号重新触发（反弹）
    /// </summary>
    public void Rebounce(string alarmCode, AlarmRule rule)
    {
        var entry = _entries.GetOrAdd(alarmCode, _ => new DebounceEntry(rule));

        lock (entry.Lock)
        {
            entry.CancelRecoverTimer();
            _onRebounce(alarmCode);
        }
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values)
        {
            entry.Dispose();
        }
        _entries.Clear();
    }

    private sealed class DebounceEntry : IDisposable
    {
        private readonly AlarmRule _rule;
        private Timer? _raiseTimer;
        private Timer? _recoverTimer;
        private readonly object _lock = new();
        private bool _disposed;

        public object Lock => _lock;

        public DebounceEntry(AlarmRule rule)
        {
            _rule = rule;
        }

        public void ResetRaiseTimer(Action onElapsed)
        {
            CancelRaiseTimer();
            _raiseTimer = new Timer(_ =>
            {
                lock (_lock)
                {
                    onElapsed();
                }
            }, null, _rule.DelayRaiseMs, Timeout.Infinite);
        }

        public void CancelRaiseTimer()
        {
            _raiseTimer?.Dispose();
            _raiseTimer = null;
        }

        public void ResetRecoverTimer(Action onElapsed)
        {
            CancelRecoverTimer();
            _recoverTimer = new Timer(_ =>
            {
                lock (_lock)
                {
                    onElapsed();
                }
            }, null, _rule.DelayRecoverMs, Timeout.Infinite);
        }

        public void CancelRecoverTimer()
        {
            _recoverTimer?.Dispose();
            _recoverTimer = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CancelRaiseTimer();
            CancelRecoverTimer();
        }
    }
}
