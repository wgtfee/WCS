namespace Wcs.Host.BackgroundServices;

using Wcs.Core.CommandCenter;
using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;

/// <summary>
/// 信号响应服务 — 订阅 CommandRequestedEvent 自动写入 PLC
///
/// 完整链路：
///   验证器 Pass + WithCommand(cmd)
///   → EventDetector 发布 CommandRequestedEvent
///   → SignalResponseService 接收
///   → CommandCenter.SendTagCommandAsync() 写回 PLC
///
/// 验证器只需要 return Pass("理由").WithCommand(cmd, "CmdType", deviceId);
/// 不再需要手写 if/else 分发。
/// </summary>
public class SignalResponseService : BackgroundService
{
    private readonly IEventBus _eventBus;
    private readonly ICommandCenter _commandCenter;
    private readonly ILogger<SignalResponseService> _logger;

    public SignalResponseService(
        IEventBus eventBus,
        ICommandCenter commandCenter,
        ILogger<SignalResponseService> logger)
    {
        _eventBus = eventBus;
        _commandCenter = commandCenter;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _eventBus.Subscribe<CommandRequestedEvent>(async (evt, ct) =>
        {
            try
            {
                _logger.LogInformation("[SignalResponse] ⚡ {Type} → {Device}",
                    evt.CommandType, evt.DeviceId);

                await _commandCenter.SendTagCommandAsync(
                    evt.DeviceId,
                    evt.CommandType,
                    evt.Command,
                    evt.TaskId,
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SignalResponse] ❌ {Type} → {Device}",
                    evt.CommandType, evt.DeviceId);
            }
        });

        _logger.LogInformation("SignalResponseService 已启动 — 监听 CommandRequestedEvent → 自动写入");
        return Task.CompletedTask;
    }
}
