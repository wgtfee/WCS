// ====================================================================
// EventBus 事件示例 — 定义、发布、订阅
// ====================================================================
//
// 事件链路：
//   PLC 信号变化 → EventDetector 检测 → 验证器通过
//   → EventBus.PublishAsync(event)
//   → 订阅者收到 → 执行业务逻辑
//
// 一、自定义事件
// 二、发布事件
// 三、订阅事件（BackgroundService）
// 四、订阅事件（EventHandler 类）
// ====================================================================

namespace Wcs.Core.EventBus.Events;

// ====================================================================
// 一、自定义事件
// ====================================================================
//
// 1. 继承 EventBase
// 2. 设置优先级
// 3. 加业务字段
//
// 示例：物料到达事件

/// <summary>物料到达事件</summary>
public class MaterialArrivedEvent : EventBase
{
    public override EventPriority Priority => EventPriority.High;
    public string DeviceId { get; set; } = string.Empty;
    public string MaterialId { get; set; } = string.Empty;
    public string? Barcode { get; set; }
    public int Quantity { get; set; }
}

/// <summary>报警确认事件</summary>
public class AlarmAcknowledgedEvent : EventBase
{
    public override EventPriority Priority => EventPriority.Medium;
    public string AlarmId { get; set; } = string.Empty;
    public string OperatorName { get; set; } = string.Empty;
    public DateTime AcknowledgedTime { get; set; } = DateTime.UtcNow;
}

// ====================================================================
// 二、发布事件（在业务代码中）
// ====================================================================
//
//    await _eventBus.PublishAsync(new MaterialArrivedEvent
//    {
//        DeviceId = "CV01",
//        MaterialId = "MAT_001",
//        Barcode = "BARCODE_123",
//        Quantity = 1
//    });
//
// EventDetector 也会自动发布事件：
//   PalletArrivedEvent / DeviceFaultEvent / ConveyorReadyChangedEvent ...
//   CommandRequestedEvent（验证器 WithCommand 时自动发）

// ====================================================================
// 三、订阅事件（BackgroundService 方式，推荐）
// ====================================================================
//
// 在 BackgroundService.ExecuteAsync 中订阅：
//
//    protected override Task ExecuteAsync(CancellationToken stoppingToken)
//    {
//        _eventBus.Subscribe<MaterialArrivedEvent>(async (evt, ct) =>
//        {
//            _logger.LogInformation("物料 {MaterialId} 到达 {DeviceId}",
//                evt.MaterialId, evt.DeviceId);
//
//            // 处理业务逻辑...
//            await SomeBusinessLogic(evt, ct);
//        });
//
//        _eventBus.Subscribe<TaskCompletedEvent>(async (evt, ct) =>
//        {
//            if (evt.Success)
//                await _commandCenter.SendTagCommandAsync(evt.DeviceId, "CompleteAck",
//                    new TagControlCommand { StartStation1 = true });
//        });
//
//        return Task.CompletedTask;
//    }

// ====================================================================
// 四、订阅事件（EventHandler 类方式，适用于复杂逻辑）
// ====================================================================
//
// 实现 IEventHandler<T> 接口：
//
//    public class MaterialHandler : IEventHandler<MaterialArrivedEvent>
//    {
//        private readonly ILogger _logger;
//
//        public async Task HandleAsync(MaterialArrivedEvent @event, CancellationToken ct)
//        {
//            // 事件处理逻辑
//        }
//    }
//
// 然后注册到 EventBus：
//   eventBus.Subscribe(new MaterialHandler());

// ====================================================================
// 五、常用模式：验证器 → 事件 → 写入
// ====================================================================
//
// 验证器里：
//   return SignalValidationResult.Pass("物料到达，允许处理")
//       .WithCommand(new TagControlCommand { StartStation1 = true },
//           "ProcessMaterial", deviceId: "CV01");
//
// EventDetector 自动发布 CommandRequestedEvent
//   → SignalResponseService 接收
//   → SendTagCommandAsync 写入 PLC
//
// 不需要写任何事件代码。

// ====================================================================
// 六、优先级说明
// ====================================================================
//
// EventPriority.Critical  → 设备故障、急停（优先处理）
// EventPriority.High      → 托盘到位、任务完成（正常业务）
// EventPriority.Medium    → 状态变化、信息更新
// EventPriority.Low       → 日志、统计、归档
