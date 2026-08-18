namespace Wcs.Desktop.Views;

using Avalonia.Controls;
using Avalonia.LogicalTree;
using System.Collections;
using System.Reflection;

/// <summary>
/// 仅翻译桌面仿真页中写死的展示文案，不修改任何绑定值或协议值。
/// </summary>
internal static class SimulationChineseUiLocalizer
{
    private static readonly IReadOnlyDictionary<string, string> TextMap = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["S2/S3 受治理设备操作"] = "S2/S3 受治理设备仿真操作",
        ["这里不是直接设备控制：所有操作先转换成现有 S2/S3 Scenario DSL，再经过 S0 Manifest + SHA-256 与 S1 Run 隔离。Production、真实 PLC/RGV/HIL 不在此控制面。"] = "这里不是直接设备控制：所有操作先转换成现有受治理场景，再经过场景注册、内容摘要校验和隔离运行。生产环境、真实控制器、真实轨道车和真实硬件在环不在此控制面。",
        ["S2 · PLC DB / Fault Injection"] = "S2 · 控制器数据块与异常模拟",
        ["真实 S2 actions：plc.block.define / write / read / plc.fault.apply / clear。"] = "使用现有 S2 受治理动作完成数据块定义、写入、读取、异常模拟和恢复。",
        ["Block Key"] = "数据块标识",
        ["Block Size"] = "数据块大小",
        ["Initial Base64"] = "初始数据（Base64）",
        ["Write Offset"] = "写入偏移",
        ["Write Base64"] = "写入数据（Base64）",
        ["Read Offset"] = "读取偏移",
        ["Read Count"] = "读取长度",
        ["Fault Id"] = "异常编号",
        ["Fault Kind"] = "异常类型",
        ["Fault Start(ms)"] = "异常开始时间（毫秒）",
        ["Fault End(ms)"] = "异常结束时间（毫秒）",
        ["Offset"] = "偏移量",
        ["Length"] = "长度",
        ["BitIndex"] = "位序号",
        ["Jitter Min"] = "抖动最小值",
        ["Jitter Max"] = "抖动最大值",
        ["Replacement Base64"] = "替换数据（Base64）",
        ["生成 PLC DSL"] = "生成控制器场景",
        ["S3 · RGV Route / Motion / Online / Load"] = "S3 · 轨道车路线、运行、在线与载荷",
        ["真实 S3 actions：segment/vehicle define、route.assign、vehicle.advance、vehicle.online.set、load/unload。"] = "使用现有 S3 受治理动作完成区段与车辆定义、路线分配、车辆前进、在线状态切换以及装载和卸载。",
        ["VehicleId"] = "轨道车编号",
        ["Source"] = "起点",
        ["Middle"] = "中间节点",
        ["Destination"] = "终点",
        ["Segment A"] = "区段一",
        ["Segment B"] = "区段二",
        ["Length(mm)"] = "区段长度（毫米）",
        ["Speed(mm/s)"] = "运行速度（毫米/秒）",
        ["Battery %"] = "电量（百分比）",
        ["LoadId（空=不装载）"] = "载荷编号（留空表示不装载）",
        ["Offline Duration(ms)"] = "离线持续时间（毫秒）",
        ["生成 RGV DSL"] = "生成轨道车场景",
        ["Scenario"] = "场景编号",
        ["File"] = "场景文件",
        ["Summary"] = "场景摘要",
        ["所有可视化场景最终仍生成严格 Scenario DSL，并经过 S0 Manifest + Content SHA-256 治理；不会直接调用生产 PLC/RGV/Traffic/HIL。"] = "所有可视化场景最终仍生成严格的受治理场景数据，并经过场景注册和内容摘要校验；不会直接调用生产控制器、轨道车、交通控制或真实硬件在环。",
        ["Seed（非 0 Int64）"] = "随机种子（非零整数）",
        ["StartTimeUtc"] = "虚拟开始时间",
        ["生成 DSL"] = "生成场景",
        ["S2 · PLC 断线 / 自动恢复"] = "S2 · 控制器断线与自动恢复",
        ["PLC Id"] = "控制器编号",
        ["断线时间(ms)"] = "断线时间（毫秒）",
        ["持续时间(ms)"] = "持续时间（毫秒）",
        ["S3/S4 · 双 RGV 路权死锁"] = "S3/S4 · 双轨道车路权死锁",
        ["车辆 A"] = "轨道车一",
        ["车辆 B"] = "轨道车二",
        ["区段 A"] = "区段一",
        ["区段 B"] = "区段二",
        ["路权租约(ms)"] = "路权占用时长（毫秒）",
        ["死锁检测时间(ms)"] = "死锁检测时间（毫秒）",
        ["S5 · MES / 外部接口超时恢复"] = "S5 · 制造执行系统与外部接口超时恢复",
        ["Endpoint"] = "接口编号",
        ["Operation"] = "调用操作标识",
        ["故障窗口(ms)"] = "异常窗口（毫秒）",
        ["Timeout(ms)"] = "请求超时（毫秒）",
        ["Retry Delay(ms)"] = "重试延迟（毫秒）",
        ["S6 · 电机/设备 Health + RUL 退化"] = "S6 · 电机/设备健康与剩余寿命退化",
        ["Asset Id"] = "设备编号",
        ["退化时长(h)"] = "退化时长（小时）",
        ["目标 Health Score"] = "目标健康评分",
        ["目标 Fusion Risk"] = "目标融合风险",
        ["目标 RUL Median(h)"] = "目标剩余寿命中位数（小时）",
        ["S7 · 全链任务恢复 / 幂等"] = "S7 · 全链任务恢复与幂等",
        ["MissionId"] = "任务编号",
        ["PLC Block"] = "控制器数据块",
        ["LoadId"] = "载荷编号",
        ["Source Node"] = "起点",
        ["Middle Node"] = "中间节点",
        ["Destination Node"] = "终点",
        ["External Endpoint"] = "外部接口",
        ["Health Asset"] = "健康设备",
        ["预置场景 / 分层检查"] = "预置场景与分层检查",
        ["无需手写 DSL：载入模板后仍必须通过场景治理注册。"] = "无需手写场景数据：载入模板后仍必须通过既有场景治理注册。",
        ["S8 继续保持 Capacity/HIL-readiness 只读证据层。"] = "S8 继续保持容量与硬件在环就绪度的只读证据层。",
        ["S2 PLC 断线恢复"] = "S2 控制器断线恢复",
        ["S3/S4 双 RGV 死锁"] = "S3/S4 双轨道车死锁",
        ["S6 Health/RUL 72h"] = "S6 健康与剩余寿命 72 小时",
        ["S7 全链恢复/幂等"] = "S7 全链恢复与幂等",
        ["Stage"] = "阶段",
        ["Run"] = "运行编号",
        ["Run Status"] = "运行状态",
        ["S2 PLC"] = "S2 控制器",
        ["Blocks / Faults / Audit"] = "数据块 / 异常 / 审计",
        ["S3 RGV"] = "S3 轨道车",
        ["Vehicles / Occupancy / Audit"] = "车辆 / 占用 / 审计",
        ["S4 Traffic"] = "S4 交通管制",
        ["Reservations / Deadlocks / Audit"] = "预约 / 死锁 / 审计",
        ["S5 External"] = "S5 外部接口",
        ["Requests / Faults / Audit"] = "请求 / 异常 / 审计",
        ["S6 Health"] = "S6 健康状态",
        ["Assets / Health Audit"] = "设备 / 健康审计",
        ["S7 Recovery"] = "S7 恢复",
        ["Missions / Recovery Audit"] = "任务 / 恢复审计",
        ["S8 Capacity"] = "S8 容量",
        ["8h / 24h Readiness Status"] = "8 小时 / 24 小时就绪状态",
        ["Inspection JSON 扁平视图"] = "分层检查数据扁平视图",
        ["Path"] = "数据路径",
        ["Value"] = "数据值",
        ["Traffic / External"] = "交通管制与外部接口"
    };

    public static void Apply(Control root)
    {
        TranslateObject(root);
        foreach (var control in root.GetLogicalDescendants().OfType<Control>())
        {
            TranslateObject(control);
            TranslateColumns(control);
        }
    }

    private static void TranslateObject(object target)
    {
        if (target is TextBlock textBlock && textBlock.Text is { Length: > 0 } text && TextMap.TryGetValue(text, out var translatedText))
            textBlock.Text = translatedText;

        TranslateStringProperty(target, "Header");
        TranslateStringProperty(target, "Content");
    }

    private static void TranslateColumns(object target)
    {
        var property = target.GetType().GetProperty("Columns", BindingFlags.Instance | BindingFlags.Public);
        if (property?.GetValue(target) is not IEnumerable columns)
            return;

        foreach (var column in columns)
            if (column is not null)
                TranslateStringProperty(column, "Header");
    }

    private static void TranslateStringProperty(object target, string propertyName)
    {
        var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (property is null || !property.CanRead || !property.CanWrite || property.PropertyType != typeof(object) && property.PropertyType != typeof(string))
            return;

        if (property.GetValue(target) is string value && TextMap.TryGetValue(value, out var translated))
            property.SetValue(target, translated);
    }
}
