namespace Wcs.Desktop.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Text.Json;

public partial class SimulationVerificationViewModel
{
    private const int MaximumVisibleStateRows = 500;
    private static readonly JsonSerializerOptions PrettyJsonOptions = new() { WriteIndented = true };

    public ObservableCollection<SimulationScenarioDataRow> ScenarioDataRows { get; } = [];
    public ObservableCollection<SimulationStateDataRow> CheckpointStateRows { get; } = [];

    [ObservableProperty] private string _scenarioDataStatusText = "等待场景数据";
    [ObservableProperty] private int _scenarioActionCount;
    [ObservableProperty] private int _scenarioAssertionCount;
    [ObservableProperty] private long _scenarioDurationMilliseconds;
    [ObservableProperty] private string _checkpointStateJson = "{}";
    [ObservableProperty] private int _checkpointStateEntryCount;

    partial void OnScenarioJsonChanged(string value) => RebuildScenarioDataPreview(value);

    private void RebuildScenarioDataPreview(string? json)
    {
        ScenarioDataRows.Clear();
        ScenarioActionCount = 0;
        ScenarioAssertionCount = 0;
        ScenarioDurationMilliseconds = 0;

        if (string.IsNullOrWhiteSpace(json))
        {
            ScenarioDataStatusText = "当前场景数据为空。";
            OnPropertyChanged(nameof(BeginnerAcceptanceResultDescription));
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("DurationMilliseconds", out var duration) && duration.TryGetInt64(out var durationMs))
                ScenarioDurationMilliseconds = durationMs;

            var rows = new List<SimulationScenarioDataRow>();
            if (root.TryGetProperty("Actions", out var actions) && actions.ValueKind == JsonValueKind.Array)
            {
                foreach (var action in actions.EnumerateArray())
                {
                    rows.Add(new SimulationScenarioDataRow
                    {
                        Phase = "动作",
                        Id = ReadString(action, "Id"),
                        AtMilliseconds = ReadInt64(action, "AtMilliseconds"),
                        Order = ReadInt32(action, "Order"),
                        Kind = ReadString(action, "Kind"),
                        Target = ReadString(action, "Target"),
                        DataLabel = "输入数据",
                        Data = ReadJson(action, "Payload")
                    });
                }
            }

            if (root.TryGetProperty("Assertions", out var assertions) && assertions.ValueKind == JsonValueKind.Array)
            {
                foreach (var assertion in assertions.EnumerateArray())
                {
                    rows.Add(new SimulationScenarioDataRow
                    {
                        Phase = "预期结果",
                        Id = ReadString(assertion, "Id"),
                        AtMilliseconds = ReadInt64(assertion, "AtMilliseconds"),
                        Order = ReadInt32(assertion, "Order"),
                        Kind = ReadString(assertion, "Kind"),
                        Target = ReadString(assertion, "Target"),
                        DataLabel = "预期数据",
                        Data = ReadJson(assertion, "Expected")
                    });
                }
            }

            ScenarioActionCount = rows.Count(x => x.Phase == "动作");
            ScenarioAssertionCount = rows.Count(x => x.Phase == "预期结果");
            foreach (var row in rows
                         .OrderBy(x => x.AtMilliseconds)
                         .ThenBy(x => x.Order)
                         .ThenBy(x => x.Phase, StringComparer.Ordinal)
                         .ThenBy(x => x.Id, StringComparer.Ordinal))
            {
                ScenarioDataRows.Add(row);
            }

            var scenarioId = root.TryGetProperty("ScenarioId", out var id) ? id.GetString() : null;
            var version = root.TryGetProperty("Version", out var versionNode) ? versionNode.GetString() : null;
            ScenarioDataStatusText =
                $"场景 {scenarioId ?? "-"} · 版本 {version ?? "-"} · {ScenarioActionCount} 个动作 · {ScenarioAssertionCount} 个预期结果 · 总时长 {ScenarioDurationMilliseconds} 毫秒";
            OnPropertyChanged(nameof(BeginnerAcceptanceResultDescription));
        }
        catch (JsonException exception)
        {
            ScenarioDataStatusText = $"场景数据暂时无法解析：{exception.Message}";
            OnPropertyChanged(nameof(BeginnerAcceptanceResultDescription));
        }
    }

    private void ApplyCheckpointStatePreview(string? stateJson)
    {
        CheckpointStateRows.Clear();
        CheckpointStateEntryCount = 0;
        CheckpointStateJson = string.IsNullOrWhiteSpace(stateJson) ? "{}" : stateJson;

        if (string.IsNullOrWhiteSpace(stateJson))
            return;

        try
        {
            using var document = JsonDocument.Parse(stateJson);
            CheckpointStateJson = JsonSerializer.Serialize(document.RootElement, PrettyJsonOptions);
            FlattenState(document.RootElement, "$", CheckpointStateRows);
            CheckpointStateEntryCount = CheckpointStateRows.Count;
        }
        catch (JsonException)
        {
            CheckpointStateRows.Add(new SimulationStateDataRow
            {
                Path = "$",
                Type = "原始数据",
                Value = Truncate(stateJson, 2000)
            });
            CheckpointStateEntryCount = 1;
        }
    }

    private void ClearCheckpointStatePreview()
    {
        CheckpointStateRows.Clear();
        CheckpointStateEntryCount = 0;
        CheckpointStateJson = "{}";
    }

    private static void FlattenState(
        JsonElement element,
        string path,
        ObservableCollection<SimulationStateDataRow> rows)
    {
        if (rows.Count >= MaximumVisibleStateRows)
            return;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var any = false;
                foreach (var property in element.EnumerateObject())
                {
                    any = true;
                    FlattenState(property.Value, $"{path}.{property.Name}", rows);
                    if (rows.Count >= MaximumVisibleStateRows)
                        break;
                }
                if (!any)
                    rows.Add(new SimulationStateDataRow { Path = path, Type = "对象", Value = "{}" });
                break;
            }
            case JsonValueKind.Array:
            {
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    FlattenState(item, $"{path}[{index}]", rows);
                    index++;
                    if (rows.Count >= MaximumVisibleStateRows)
                        break;
                }
                if (index == 0)
                    rows.Add(new SimulationStateDataRow { Path = path, Type = "数组", Value = "[]" });
                break;
            }
            default:
                rows.Add(new SimulationStateDataRow
                {
                    Path = path,
                    Type = ChineseValueKind(element.ValueKind),
                    Value = Truncate(PrimitiveText(element), 2000)
                });
                break;
        }
    }

    private static string ChineseValueKind(JsonValueKind kind) => kind switch
    {
        JsonValueKind.String => "文本",
        JsonValueKind.Number => "数字",
        JsonValueKind.True or JsonValueKind.False => "布尔值",
        JsonValueKind.Null => "空值",
        _ => "值"
    };

    private static string PrimitiveText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Null => "空",
        JsonValueKind.Undefined => string.Empty,
        _ => element.GetRawText()
    };

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static long ReadInt64(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : 0;

    private static int ReadInt32(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;

    private static string ReadJson(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? Truncate(value.GetRawText(), 2000) : "-";

    private static string Truncate(string value, int maxCharacters) =>
        value.Length <= maxCharacters ? value : value[..maxCharacters] + " …";
}

public sealed class SimulationScenarioDataRow
{
    public string Phase { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public long AtMilliseconds { get; set; }
    public int Order { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string DataLabel { get; set; } = string.Empty;
    public string Data { get; set; } = string.Empty;

    public string TimeText => $"{AtMilliseconds} 毫秒";
    public string OperationText => SimulationScenarioChineseFormatter.Operation(Kind, Phase);
    public string DataSummary => SimulationScenarioChineseFormatter.DataSummary(Data);
}

public sealed class SimulationStateDataRow
{
    public string Path { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

internal static class SimulationScenarioChineseFormatter
{
    public static string Operation(string kind, string phase)
    {
        var value = kind?.Trim() ?? string.Empty;
        var lower = value.ToLowerInvariant();

        if (phase == "预期结果")
            return AssertionOperation(lower);
        if (lower.Contains("fault.apply", StringComparison.Ordinal)) return "注入故障";
        if (lower.Contains("fault.clear", StringComparison.Ordinal)) return "清除故障";
        if (lower.Contains("block.define", StringComparison.Ordinal)) return "定义数据块";
        if (lower.Contains("write", StringComparison.Ordinal)) return "写入数据";
        if (lower.Contains("read", StringComparison.Ordinal)) return "读取数据";
        if (lower.Contains("segment.define", StringComparison.Ordinal)) return "定义区段";
        if (lower.Contains("vehicle.define", StringComparison.Ordinal)) return "定义轨道车";
        if (lower.Contains("route.assign", StringComparison.Ordinal)) return "分配运行路线";
        if (lower.Contains("vehicle.advance", StringComparison.Ordinal)) return "轨道车前进";
        if (lower.Contains("online.set", StringComparison.Ordinal)) return "设置在线状态";
        if (lower.Contains("unload", StringComparison.Ordinal)) return "卸载物料";
        if (lower.Contains("load", StringComparison.Ordinal)) return "装载物料";
        if (lower.Contains("deadlock.detect", StringComparison.Ordinal)) return "检测交通死锁";
        if (lower.Contains("deadlock.resolve", StringComparison.Ordinal)) return "解除交通死锁";
        if (lower.Contains("rolling.release", StringComparison.Ordinal)) return "释放滚动预约";
        if (lower.Contains("rolling.reserve", StringComparison.Ordinal)) return "建立滚动预约";
        if (lower.Contains("release", StringComparison.Ordinal)) return "释放占用";
        if (lower.Contains("reserve", StringComparison.Ordinal)) return "申请占用";
        if (lower.Contains("expire", StringComparison.Ordinal)) return "处理过期占用";
        if (lower.Contains("zone.define", StringComparison.Ordinal)) return "定义交通区域";
        if (lower.Contains("endpoint.define", StringComparison.Ordinal)) return "定义外部接口";
        if (lower.Contains("request.invoke", StringComparison.Ordinal)) return "调用外部接口";
        if (lower.Contains("circuit.reset", StringComparison.Ordinal)) return "复位接口熔断";
        if (lower.Contains("health", StringComparison.Ordinal) || lower.Contains("rul", StringComparison.Ordinal)) return "更新健康状态";
        if (lower.Contains("mission", StringComparison.Ordinal)) return "执行运输任务";
        return "执行场景动作";
    }

    public static string DataSummary(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "-")
            return "无附加数据";

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return TranslateValue(root);

            var parts = root.EnumerateObject()
                .Take(8)
                .Select(property => $"{TranslateKey(property.Name)}：{TranslateValue(property.Value)}")
                .ToArray();
            return parts.Length == 0 ? "无附加数据" : string.Join("；", parts);
        }
        catch (JsonException)
        {
            return "包含结构化测试数据";
        }
    }

    private static string AssertionOperation(string lower)
    {
        if (lower.Contains("state", StringComparison.Ordinal)) return "检查状态是否符合预期";
        if (lower.Contains("value", StringComparison.Ordinal)) return "检查数据值是否符合预期";
        if (lower.Contains("route", StringComparison.Ordinal)) return "检查路线结果";
        if (lower.Contains("occup", StringComparison.Ordinal) || lower.Contains("reserve", StringComparison.Ordinal)) return "检查交通占用结果";
        if (lower.Contains("health", StringComparison.Ordinal) || lower.Contains("rul", StringComparison.Ordinal)) return "检查健康预测结果";
        return "检查结果是否符合预期";
    }

    private static string TranslateKey(string key) => key switch
    {
        "BlockKey" => "数据块",
        "BlockNumber" => "数据块编号",
        "Offset" => "偏移量",
        "Value" => "数值",
        "OldValue" => "原值",
        "NewValue" => "新值",
        "FaultId" => "故障编号",
        "FaultKind" => "故障类型",
        "StartMilliseconds" => "开始时间",
        "EndMilliseconds" => "结束时间",
        "VehicleId" => "轨道车编号",
        "LoadId" => "载荷编号",
        "SourceNodeId" => "起点",
        "MiddleNodeId" => "中间节点",
        "DestinationNodeId" => "终点",
        "NodeId" => "节点",
        "SegmentId" => "区段",
        "SegmentIds" => "区段列表",
        "LengthMm" => "区段长度",
        "SpeedMmPerSecond" => "运行速度",
        "BatteryPercent" => "电量",
        "Online" => "在线状态",
        "ZoneId" => "交通区域",
        "LeaseMilliseconds" => "占用时长",
        "EndpointId" => "接口编号",
        "SystemKind" => "外部系统",
        "Operation" => "调用操作",
        "AssetId" => "设备编号",
        "DurationHours" => "持续小时数",
        "MissionId" => "任务编号",
        "Expected" => "预期值",
        "Actual" => "实际值",
        "Status" => "状态",
        "Result" => "结果",
        _ => "参数"
    };

    private static string TranslateValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
            return TranslateToken(value.GetString() ?? string.Empty);
        if (value.ValueKind == JsonValueKind.True) return "是";
        if (value.ValueKind == JsonValueKind.False) return "否";
        if (value.ValueKind == JsonValueKind.Null) return "空";
        if (value.ValueKind == JsonValueKind.Array)
        {
            var values = value.EnumerateArray().Take(6).Select(TranslateValue).ToArray();
            return values.Length == 0 ? "空" : string.Join("、", values);
        }
        if (value.ValueKind == JsonValueKind.Object)
            return "结构化数据";
        return value.GetRawText();
    }

    private static string TranslateToken(string value) => value switch
    {
        "Disconnect" => "断线",
        "Timeout" => "超时",
        "ReadFailure" => "读取失败",
        "WriteFailure" => "写入失败",
        "Stuck" => "数据卡住",
        "BitFlip" => "位翻转",
        "Jitter" => "数据抖动",
        "OutOfRange" => "数据越界",
        "Completed" => "已完成",
        "Failed" => "失败",
        "Paused" => "已暂停",
        "Running" => "运行中",
        "Online" => "在线",
        "Offline" => "离线",
        _ => value
    };
}