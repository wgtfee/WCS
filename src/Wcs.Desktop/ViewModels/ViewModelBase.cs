using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wcs.Desktop.Interface;

namespace Wcs.Desktop.ViewModels
{
    /// <summary>
    /// ViewModel 基类。
    /// 实现 IDisposable：瞬态 ViewModel 订阅了单例实时服务的事件，
    /// 页签关闭时必须统一退订，否则旧实例被事件引用链钉住无法回收。
    /// 内置轮询循环助手：页面释放时自动停止。
    /// </summary>
    public abstract class ViewModelBase : ObservableObject, IAsyncInitializable, IDisposable
    {
        private CancellationTokenSource? _pollCts;

        public bool IsInitialized { get; private set; }

        public async Task InitializeAsync()
        {
            if (IsInitialized)
                return;

            await OnInitializeAsync();

            IsInitialized = true;
        }

        protected virtual Task OnInitializeAsync()
        {
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (IsDisposed)
                return;

            IsDisposed = true;
            StopPollingLoop();
            OnDispose();
            GC.SuppressFinalize(this);
        }

        protected bool IsDisposed { get; private set; }

        /// <summary>子类在此退订实时服务事件、释放定时器等资源。</summary>
        protected virtual void OnDispose()
        {
        }

        /// <summary>
        /// 启动后台轮询循环（页面释放时自动停止）。
        /// 回调在启动时的同步上下文上执行，可安全更新可观察集合。
        /// </summary>
        protected void StartPollingLoop(TimeSpan interval, Func<CancellationToken, Task> tick)
        {
            StopPollingLoop();
            _pollCts = new CancellationTokenSource();
            var ignored = PollLoopAsync(_pollCts.Token, interval, tick);
        }

        protected void StopPollingLoop()
        {
            _pollCts?.Cancel();
            _pollCts?.Dispose();
            _pollCts = null;
        }

        private static async Task PollLoopAsync(
            CancellationToken cancellationToken,
            TimeSpan interval,
            Func<CancellationToken, Task> tick)
        {
            try
            {
                using var timer = new PeriodicTimer(interval);
                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(true))
                {
                    await tick(cancellationToken).ConfigureAwait(true);
                }
            }
            catch (OperationCanceledException)
            {
                // 页面关闭
            }
            catch (ObjectDisposedException)
            {
                // 页面释放竞态
            }
        }
    }
}
