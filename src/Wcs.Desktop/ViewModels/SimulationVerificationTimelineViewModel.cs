namespace Wcs.Desktop.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

public partial class SimulationVerificationViewModel
{
    private static readonly JsonSerializerOptions TimelineJsonOptions = new() { WriteIndented = true };
    private const int TimelineTemplateSchemaVersion = 1;

    public ObservableCollection<SimulationTimelineEditorItem> TimelineItems { get; } = [];

    [ObservableProperty] private SimulationTimelineEditorItem? _selectedTimelineItem;
    [ObservableProperty] private string _timelineAppendOffsetMsText = "0";
    [ObservableProperty] private string _timelineTemplateName = "my-scenario";
    [ObservableProperty] private string _timelineTemplateNamesText = "点击“刷新模板”查看本机模板。";
    [ObservableProperty] private string _timelineStatusText =
        "可把 PLC/RGV/Traffic/External 等当前 Scenario 追加到同一时间轴；最终仍生成严格 S1 DSL 并经过 S0 Manifest/SHA-256 治理。";

    [RelayCommand]
    private void NewTimelineScenario()
    {
        TimelineItems.Clear();
        SelectedTimelineItem = null;
        ScenarioId = "visual-timeline-scenario";
        ScenarioVersion = "1.0.0";
        ScenarioFile = "visual-timeline-scenario.json";
        ScenarioSource = "Wcs.Desktop Multi-Fault Timeline Editor";
        ScenarioApprovedBy = "simulation-operator";
        TimelineAppendOffsetMsText = "0";
        TimelineStatusText = "已创建空白多故障时间轴。可新增 Action/Assertion，或先在其它仿真面板生成场景后“追加当前 Scenario”。";
        StatusText = TimelineStatusText;
    }

    [RelayCommand]
    private void AddTimelineAction()
    {
        var item = new SimulationTimelineEditorItem
        {
            ItemType = "Action",
            Id = UniqueTimelineId("action"),
            AtMillisecondsText = NextTimelineOffset().ToString(CultureInfo.InvariantCulture),
            DurationMillisecondsText = "0",
            Kind = "state.set",
            Target = "state.demo",
            BodyJson = "{\"Value\":1}"
        };
        TimelineItems.Add(item);
        ReindexTimeline();
        SelectedTimelineItem = item;
        TimelineStatusText = "已新增 Action。修改 Kind / Target / Payload / 时间后生成 Scenario。";
    }

    [RelayCommand]
    private void AddTimelineAssertion()
    {
        var item = new SimulationTimelineEditorItem
        {
            ItemType = "Assertion",
            Id = UniqueTimelineId("assert"),
            AtMillisecondsText = NextTimelineOffset().ToString(CultureInfo.InvariantCulture),
            DurationMillisecondsText = "0",
            Kind = "state.equals",
            Target = "state.demo",
            BodyJson = "1"
        };
        TimelineItems.Add(item);
        ReindexTimeline();
        SelectedTimelineItem = item;
        TimelineStatusText = "已新增 Assertion。Body JSON 表示 Expected，可为对象、字符串、数字、布尔值或 null。";
    }

    [RelayCommand]
    private void DeleteSelectedTimelineItem()
    {
        if (SelectedTimelineItem is null)
        {
            TimelineError("请先选择一条时间轴记录。");
            return;
        }
        var index = TimelineItems.IndexOf(SelectedTimelineItem);
        TimelineItems.Remove(SelectedTimelineItem);
        ReindexTimeline();
        SelectedTimelineItem = TimelineItems.Count == 0 ? null : TimelineItems[Math.Clamp(index, 0, TimelineItems.Count - 1)];
        TimelineStatusText = "已删除选中记录。";
    }

    [RelayCommand]
    private void MoveSelectedTimelineItemUp()
    {
        if (SelectedTimelineItem is null)
        {
            TimelineError("请先选择一条时间轴记录。");
            return;
        }
        var index = TimelineItems.IndexOf(SelectedTimelineItem);
        if (index <= 0)
            return;
        TimelineItems.Move(index, index - 1);
        ReindexTimeline();
        TimelineStatusText = "已上移选中记录；Order 已重新计算。";
    }

    [RelayCommand]
    private void MoveSelectedTimelineItemDown()
    {
        if (SelectedTimelineItem is null)
        {
            TimelineError("请先选择一条时间轴记录。");
            return;
        }
        var index = TimelineItems.IndexOf(SelectedTimelineItem);
        if (index < 0 || index >= TimelineItems.Count - 1)
            return;
        TimelineItems.Move(index, index + 1);
        ReindexTimeline();
        TimelineStatusText = "已下移选中记录；Order 已重新计算。";
    }

    [RelayCommand]
    private void ClearTimeline()
    {
        TimelineItems.Clear();
        SelectedTimelineItem = null;
        TimelineStatusText = "时间轴已清空。";
    }

    [RelayCommand]
    private void ReplaceTimelineFromCurrentScenario() => ImportCurrentScenario(replace: true);

    [RelayCommand]
    private void AppendCurrentScenarioToTimeline() => ImportCurrentScenario(replace: false);

    [RelayCommand]
    private void AutoGenerateTimelineAssertions()
    {
        var added = EnsureAutomaticAssertions();
        TimelineStatusText = added == 0
            ? "没有发现需要补充、且当前尚未覆盖的已知断言。"
            : $"已自动补充 {added} 条已知安全断言；可继续手工调整。";
        StatusText = TimelineStatusText;
    }

    [RelayCommand]
    private void BumpTimelineScenarioVersion()
    {
        var parts = (ScenarioVersion ?? string.Empty).Split('.');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch) ||
            patch == int.MaxValue)
        {
            TimelineError("Version 必须是可递增的 major.minor.patch，例如 1.0.0。 ");
            return;
        }
        ScenarioVersion = $"{major}.{minor}.{patch + 1}";
        ScenarioFile = $"{Slug(ScenarioId)}-{ScenarioVersion}.json";
        TimelineStatusText = $"Scenario Version 已递增到 {ScenarioVersion}；注册后会形成新的不可变版本。";
    }

    [RelayCommand]
    private void GenerateTimelineScenario() => TryGenerateTimelineScenario();

    [RelayCommand]
    private async Task GenerateAndRegisterTimelineScenarioAsync()
    {
        if (TryGenerateTimelineScenario())
            await RegisterScenarioAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RefreshTimelineTemplatesAsync()
    {
        try
        {
            var directory = TimelineTemplateDirectory();
            if (!Directory.Exists(directory))
            {
                TimelineTemplateNamesText = "本机还没有保存时间轴模板。";
                return;
            }
            var names = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            TimelineTemplateNamesText = names.Length == 0 ? "本机还没有保存时间轴模板。" : string.Join(" / ", names);
            await Task.CompletedTask;
        }
        catch (Exception exception)
        {
            TimelineError($"刷新本机模板失败：{exception.Message}");
        }
    }

    [RelayCommand]
    private async Task SaveTimelineTemplateAsync()
    {
        if (!TryTemplatePath(TimelineTemplateName, out var path, out var normalizedName))
            return;
        try
        {
            Directory.CreateDirectory(TimelineTemplateDirectory());
            var template = new TimelineTemplateDocument
            {
                SchemaVersion = TimelineTemplateSchemaVersion,
                Name = normalizedName,
                ScenarioId = ScenarioId,
                ScenarioVersion = ScenarioVersion,
                ScenarioSeedText = ScenarioSeedText,
                ScenarioStartUtcText = VisualScenarioStartUtcText,
                Items = TimelineItems.Select(item => item.Clone()).ToList()
            };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(template, TimelineJsonOptions)).ConfigureAwait(true);
            TimelineTemplateName = normalizedName;
            await RefreshTimelineTemplatesAsync().ConfigureAwait(true);
            TimelineStatusText = $"模板 {normalizedName} 已保存到 Desktop 本机应用数据目录；未写生产 SQL。";
            StatusText = TimelineStatusText;
        }
        catch (Exception exception)
        {
            TimelineError($"保存本机模板失败：{exception.Message}");
        }
    }

    [RelayCommand]
    private async Task LoadTimelineTemplateAsync()
    {
        if (!TryTemplatePath(TimelineTemplateName, out var path, out var normalizedName))
            return;
        try
        {
            if (!File.Exists(path))
            {
                TimelineError($"本机模板 {normalizedName} 不存在。 ");
                return;
            }
            var json = await File.ReadAllTextAsync(path).ConfigureAwait(true);
            var template = JsonSerializer.Deserialize<TimelineTemplateDocument>(json)
                ?? throw new InvalidOperationException("模板 JSON 为空。");
            if (template.SchemaVersion != TimelineTemplateSchemaVersion)
                throw new InvalidOperationException($"不支持模板 SchemaVersion={template.SchemaVersion}。 ");

            TimelineItems.Clear();
            foreach (var item in template.Items ?? [])
                TimelineItems.Add(item.Clone());
            ReindexTimeline();
            SelectedTimelineItem = TimelineItems.FirstOrDefault();
            ScenarioId = string.IsNullOrWhiteSpace(template.ScenarioId) ? "visual-timeline-scenario" : template.ScenarioId;
            ScenarioVersion = string.IsNullOrWhiteSpace(template.ScenarioVersion) ? "1.0.0" : template.ScenarioVersion;
            ScenarioSeedText = string.IsNullOrWhiteSpace(template.ScenarioSeedText) ? ScenarioSeedText : template.ScenarioSeedText;
            VisualScenarioStartUtcText = string.IsNullOrWhiteSpace(template.ScenarioStartUtcText) ? VisualScenarioStartUtcText : template.ScenarioStartUtcText;
            ScenarioFile = $"{Slug(ScenarioId)}-{ScenarioVersion}.json";
            TimelineTemplateName = normalizedName;
            TimelineStatusText = $"已载入本机模板 {normalizedName}：{TimelineItems.Count} 条记录。";
            StatusText = TimelineStatusText;
        }
        catch (Exception exception)
        {
            TimelineError($"加载本机模板失败：{exception.Message}");
        }
    }

    [RelayCommand]
    private async Task DeleteTimelineTemplateAsync()
    {
        if (!TryTemplatePath(TimelineTemplateName, out var path, out var normalizedName))
            return;
        try
        {
            if (File.Exists(path))
                File.Delete(path);
            await RefreshTimelineTemplatesAsync().ConfigureAwait(true);
            TimelineStatusText = $"已删除本机模板 {normalizedName}。";
        }
        catch (Exception exception)
        {
            TimelineError($"删除本机模板失败：{exception.Message}");
        }
    }

    private void ImportCurrentScenario(bool replace)
    {
        if (!TryLong(TimelineAppendOffsetMsText, "追加 Offset(ms)", 0, long.MaxValue / 2, out var appendOffset))
            return;
        try
        {
            using var document = JsonDocument.Parse(ScenarioJson ?? string.Empty, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("当前 Scenario JSON 根节点必须是对象。 ");

            if (replace)
            {
                TimelineItems.Clear();
                appendOffset = 0;
                if (root.TryGetProperty("ScenarioId", out var scenarioId) && scenarioId.ValueKind == JsonValueKind.String)
                    ScenarioId = scenarioId.GetString() ?? ScenarioId;
                if (root.TryGetProperty("Version", out var version) && version.ValueKind == JsonValueKind.String)
                    ScenarioVersion = version.GetString() ?? ScenarioVersion;
                if (root.TryGetProperty("Seed", out var seed) && seed.TryGetInt64(out var seedValue))
                    ScenarioSeedText = seedValue.ToString(CultureInfo.InvariantCulture);
                if (root.TryGetProperty("StartTimeUtc", out var start) && start.ValueKind == JsonValueKind.String)
                    VisualScenarioStartUtcText = start.GetString() ?? VisualScenarioStartUtcText;
            }

            var prefix = replace ? string.Empty : $"m{TimelineItems.Count + 1}-";
            var added = 0;
            if (root.TryGetProperty("Actions", out var actions) && actions.ValueKind == JsonValueKind.Array)
            {
                foreach (var action in actions.EnumerateArray())
                {
                    var originalAt = action.GetProperty("AtMilliseconds").GetInt64();
                    var kind = action.GetProperty("Kind").GetString() ?? string.Empty;
                    var body = action.TryGetProperty("Payload", out var payload) ? payload.GetRawText() : "{}";
                    var at = checked(originalAt + appendOffset);
                    var duration = InferDurationMilliseconds(kind, at, body);
                    if (duration > 0 && IsFaultKind(kind))
                        at = InferFaultStartMilliseconds(kind, at, body, appendOffset);
                    TimelineItems.Add(new SimulationTimelineEditorItem
                    {
                        ItemType = "Action",
                        Id = UniqueTimelineId(prefix + (action.GetProperty("Id").GetString() ?? "action")),
                        AtMillisecondsText = at.ToString(CultureInfo.InvariantCulture),
                        DurationMillisecondsText = duration.ToString(CultureInfo.InvariantCulture),
                        Kind = kind,
                        Target = action.GetProperty("Target").GetString() ?? string.Empty,
                        BodyJson = body,
                        Order = action.TryGetProperty("Order", out var order) && order.TryGetInt32(out var orderValue) ? orderValue : 0
                    });
                    added++;
                }
            }
            if (root.TryGetProperty("Assertions", out var assertions) && assertions.ValueKind == JsonValueKind.Array)
            {
                foreach (var assertion in assertions.EnumerateArray())
                {
                    TimelineItems.Add(new SimulationTimelineEditorItem
                    {
                        ItemType = "Assertion",
                        Id = UniqueTimelineId(prefix + (assertion.GetProperty("Id").GetString() ?? "assert")),
                        AtMillisecondsText = checked(assertion.GetProperty("AtMilliseconds").GetInt64() + appendOffset).ToString(CultureInfo.InvariantCulture),
                        DurationMillisecondsText = "0",
                        Kind = assertion.GetProperty("Kind").GetString() ?? string.Empty,
                        Target = assertion.GetProperty("Target").GetString() ?? string.Empty,
                        BodyJson = assertion.TryGetProperty("Expected", out var expected) ? expected.GetRawText() : "null",
                        Order = assertion.TryGetProperty("Order", out var order) && order.TryGetInt32(out var orderValue) ? orderValue : 0
                    });
                    added++;
                }
            }
            SortTimeline();
            SelectedTimelineItem = TimelineItems.FirstOrDefault();
            TimelineStatusText = $"已{(replace ? "载入" : "追加")}当前 Scenario：新增 {added} 条记录，Offset={appendOffset}ms。";
            StatusText = TimelineStatusText;
        }
        catch (Exception exception)
        {
            TimelineError($"导入当前 Scenario 失败：{exception.Message}");
        }
    }

    private bool TryGenerateTimelineScenario()
    {
        if (!TryGetVisualCommon(out var seed, out var start))
            return false;
        if (TimelineItems.Count == 0)
            return TimelineError("时间轴为空。请先新增记录或导入当前 Scenario。 ");
        if (!TryRequired(ScenarioId, "ScenarioId", out var scenarioId) || !TryRequired(ScenarioVersion, "Version", out var version))
            return false;

        EnsureAutomaticAssertions();
        var actions = new List<object>();
        var assertions = new List<object>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var parsed = new List<ParsedTimelineItem>();

        foreach (var item in TimelineItems)
        {
            if (!TryParseTimelineItem(item, ids, out var parsedItem))
                return false;
            parsed.Add(parsedItem);
        }

        foreach (var item in parsed.OrderBy(item => item.AtMilliseconds)
                     .ThenBy(item => item.IsAssertion ? 1 : 0)
                     .ThenBy(item => item.Order)
                     .ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            if (item.IsAssertion)
            {
                assertions.Add(Assertion(item.Id, item.AtMilliseconds, item.Order, item.Kind, item.Target, item.Body));
                continue;
            }

            var payload = item.Body as JsonObject
                ?? throw new InvalidOperationException($"Action {item.Id} Payload 必须是 JSON 对象。 ");
            ApplyDurationToKnownAction(item.Kind, item.AtMilliseconds, item.DurationMilliseconds, payload);
            actions.Add(Action(item.Id, item.AtMilliseconds, item.Order, item.Kind, item.Target, payload));
            AppendKnownRecoveryAction(actions, parsed, item, payload);
        }

        var maxOffset = parsed.Max(item => checked(item.AtMilliseconds + item.DurationMilliseconds));
        var duration = checked(Math.Max(1_000L, maxOffset + 1_000L));
        var scenarioDocument = new Dictionary<string, object?>
        {
            ["SchemaVersion"] = 1,
            ["ScenarioId"] = scenarioId,
            ["Version"] = version,
            ["Seed"] = seed,
            ["StartTimeUtc"] = start,
            ["DurationMilliseconds"] = duration,
            ["StopOnAssertionFailure"] = true,
            ["Actions"] = actions,
            ["Assertions"] = assertions
        };

        ScenarioFile = $"{Slug(scenarioId)}-{version}.json";
        ScenarioSource = "Wcs.Desktop Multi-Fault Timeline Editor";
        ScenarioApprovedBy = "simulation-operator";
        ScenarioJson = JsonSerializer.Serialize(scenarioDocument, TimelineJsonOptions);
        ScenarioSeedText = seed.ToString(CultureInfo.InvariantCulture);
        SpeedFactorText = "1";
        CheckpointHash = "-";
        CheckpointStateText = $"Timeline：Actions={actions.Count}, Assertions={assertions.Count}, Duration={duration}ms, Version={version}";
        TimelineStatusText = $"已生成严格 Scenario DSL：Actions={actions.Count}，Assertions={assertions.Count}，Duration={duration}ms。下一步可生成并注册或到场景治理页检查。";
        StatusText = TimelineStatusText;
        return true;
    }

    private bool TryParseTimelineItem(SimulationTimelineEditorItem item, HashSet<string> ids, out ParsedTimelineItem parsed)
    {
        parsed = default;
        if (!TryRequired(item.Id, "Timeline Id", out var id) || !ids.Add(id))
        {
            if (ids.Contains(id))
                TimelineError($"Timeline Id 重复：{id}。 ");
            return false;
        }
        var isAssertion = string.Equals(item.ItemType, "Assertion", StringComparison.OrdinalIgnoreCase);
        if (!isAssertion && !string.Equals(item.ItemType, "Action", StringComparison.OrdinalIgnoreCase))
            return TimelineError($"{id} ItemType 只支持 Action / Assertion。 ");
        if (!TryRequired(item.Kind, $"{id} Kind", out var kind) || !TryRequired(item.Target, $"{id} Target", out var target) ||
            !TryLong(item.AtMillisecondsText, $"{id} AtMilliseconds", 0, long.MaxValue / 2, out var at) ||
            !TryLong(item.DurationMillisecondsText, $"{id} Duration", 0, long.MaxValue / 2, out var duration))
            return false;
        try
        {
            var body = JsonNode.Parse(item.BodyJson ?? string.Empty, new JsonNodeOptions { PropertyNameCaseInsensitive = false },
                new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            if (body is null)
                body = JsonValue.Create((string?)null);
            if (!isAssertion && body is not JsonObject)
                return TimelineError($"Action {id} Payload 必须是 JSON 对象。 ");
            parsed = new ParsedTimelineItem(isAssertion, id, at, duration, item.Order, kind, target, body);
            return true;
        }
        catch (JsonException exception)
        {
            return TimelineError($"{id} Body JSON 无效：{exception.Message}");
        }
    }

    private int EnsureAutomaticAssertions()
    {
        var existing = new HashSet<string>(TimelineItems.Where(item => item.IsAssertion).Select(item => item.Id), StringComparer.Ordinal);
        var additions = new List<SimulationTimelineEditorItem>();
        foreach (var action in TimelineItems.Where(item => !item.IsAssertion).ToArray())
        {
            if (!long.TryParse(action.AtMillisecondsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var at) || at < 0)
                continue;
            _ = long.TryParse(action.DurationMillisecondsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var duration);
            JsonNode? body;
            try { body = JsonNode.Parse(action.BodyJson ?? "{}"); }
            catch { continue; }
            if (body is not JsonObject payload)
                continue;

            void Add(string suffix, long assertionAt, string kind, string target, JsonNode? expected)
            {
                var id = $"auto-{action.Id}-{suffix}";
                if (!existing.Add(id))
                    return;
                additions.Add(new SimulationTimelineEditorItem
                {
                    ItemType = "Assertion",
                    Id = id,
                    AtMillisecondsText = Math.Max(0, assertionAt).ToString(CultureInfo.InvariantCulture),
                    DurationMillisecondsText = "0",
                    Kind = kind,
                    Target = target,
                    BodyJson = expected?.ToJsonString() ?? "null"
                });
            }

            switch (action.Kind)
            {
                case "plc.connection.set" when payload["Connected"] is JsonValue connected && connected.TryGetValue<bool>(out var isConnected):
                    Add("connected", at + 1, "plc.connected", action.Target, JsonValue.Create(isConnected));
                    if (!isConnected && duration > 0)
                        Add("recovered", at + duration + 1, "plc.connected", action.Target, JsonValue.Create(true));
                    break;
                case "plc.block.write":
                    if (payload["Offset"] is JsonValue offset && offset.TryGetValue<int>(out var writeOffset) && payload["DataBase64"] is JsonValue data && data.TryGetValue<string>(out var base64))
                        Add("write", at + 1, "plc.block.equals", action.Target, new JsonObject { ["Offset"] = writeOffset, ["DataBase64"] = base64 });
                    break;
                case "external.fault.apply":
                    Add("fault-active", at + 1, "external.fault.active", action.Target, JsonValue.Create(true));
                    if (duration > 0)
                        Add("fault-cleared", at + duration + 1, "external.fault.active", action.Target, JsonValue.Create(false));
                    break;
                case "external.circuit.reset":
                    Add("circuit", at + 1, "external.circuit.state", action.Target, JsonValue.Create("Closed"));
                    break;
                case "traffic.deadlock.detect":
                    Add("deadlock", at + 1, "traffic.deadlock.exists", "all", JsonValue.Create(true));
                    break;
                case "traffic.deadlock.resolve":
                    Add("cleared", at + 1, "traffic.deadlock.exists", "all", JsonValue.Create(false));
                    break;
                case "rgv.vehicle.unload":
                    Add("unloaded", at + 1, "rgv.vehicle.load.equals", action.Target, null);
                    break;
            }
        }
        foreach (var item in additions)
            TimelineItems.Add(item);
        if (additions.Count > 0)
            SortTimeline();
        return additions.Count;
    }

    private static void ApplyDurationToKnownAction(string kind, long at, long duration, JsonObject payload)
    {
        if (duration <= 0)
            return;
        switch (kind)
        {
            case "plc.fault.apply":
                payload["StartMilliseconds"] = at;
                payload["EndMilliseconds"] = checked(at + duration);
                break;
            case "external.fault.apply":
                payload["StartsAtOffsetMilliseconds"] = at;
                payload["EndsAtOffsetMilliseconds"] = checked(at + duration);
                break;
        }
    }

    private static void AppendKnownRecoveryAction(List<object> actions, IReadOnlyList<ParsedTimelineItem> allItems, ParsedTimelineItem item, JsonObject payload)
    {
        if (item.DurationMilliseconds <= 0)
            return;
        var end = checked(item.AtMilliseconds + item.DurationMilliseconds);
        bool HasAction(string kind, string target) => allItems.Any(candidate => !candidate.IsAssertion && candidate.AtMilliseconds == end &&
            string.Equals(candidate.Kind, kind, StringComparison.Ordinal) && string.Equals(candidate.Target, target, StringComparison.Ordinal));

        switch (item.Kind)
        {
            case "plc.connection.set" when payload["Connected"] is JsonValue connected && connected.TryGetValue<bool>(out var plcConnected) && !plcConnected:
                if (!HasAction("plc.connection.set", item.Target))
                    actions.Add(Action($"auto-recover-{item.Id}", end, item.Order + 10_000, "plc.connection.set", item.Target, Payload(("Connected", true))));
                break;
            case "rgv.vehicle.online.set" when payload["IsOnline"] is JsonValue online && online.TryGetValue<bool>(out var isOnline) && !isOnline:
                if (!HasAction("rgv.vehicle.online.set", item.Target))
                    actions.Add(Action($"auto-recover-{item.Id}", end, item.Order + 10_000, "rgv.vehicle.online.set", item.Target, Payload(("IsOnline", true))));
                break;
            case "plc.fault.apply":
                var plcFaultId = payload["Id"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(plcFaultId) && !HasAction("plc.fault.clear", plcFaultId))
                    actions.Add(Action($"auto-clear-{item.Id}", end, item.Order + 10_000, "plc.fault.clear", plcFaultId, Payload()));
                break;
            case "external.fault.apply":
                if (!HasAction("external.fault.clear", item.Target))
                    actions.Add(Action($"auto-clear-{item.Id}", end, item.Order + 10_000, "external.fault.clear", item.Target, Payload()));
                break;
        }
    }

    private static long InferDurationMilliseconds(string kind, long at, string bodyJson)
    {
        if (!IsFaultKind(kind))
            return 0;
        try
        {
            var payload = JsonNode.Parse(bodyJson) as JsonObject;
            if (payload is null)
                return 0;
            long? start = kind == "plc.fault.apply" ? payload["StartMilliseconds"]?.GetValue<long?>() : payload["StartsAtOffsetMilliseconds"]?.GetValue<long?>();
            long? end = kind == "plc.fault.apply" ? payload["EndMilliseconds"]?.GetValue<long?>() : payload["EndsAtOffsetMilliseconds"]?.GetValue<long?>();
            var resolvedStart = start ?? at;
            return end.HasValue && end.Value > resolvedStart ? end.Value - resolvedStart : 0;
        }
        catch { return 0; }
    }

    private static long InferFaultStartMilliseconds(string kind, long fallbackAt, string bodyJson, long appendOffset)
    {
        try
        {
            var payload = JsonNode.Parse(bodyJson) as JsonObject;
            long? start = kind == "plc.fault.apply" ? payload?["StartMilliseconds"]?.GetValue<long?>() : payload?["StartsAtOffsetMilliseconds"]?.GetValue<long?>();
            return start.HasValue ? checked(start.Value + appendOffset) : fallbackAt;
        }
        catch { return fallbackAt; }
    }

    private static bool IsFaultKind(string kind) => kind is "plc.fault.apply" or "external.fault.apply";

    private long NextTimelineOffset()
    {
        var max = 0L;
        foreach (var item in TimelineItems)
            if (long.TryParse(item.AtMillisecondsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                max = Math.Max(max, value);
        return checked(max + (TimelineItems.Count == 0 ? 0 : 1_000));
    }

    private string UniqueTimelineId(string baseId)
    {
        var clean = Slug(string.IsNullOrWhiteSpace(baseId) ? "item" : baseId);
        var used = new HashSet<string>(TimelineItems.Select(item => item.Id), StringComparer.Ordinal);
        if (!used.Contains(clean))
            return clean;
        for (var index = 2; index < 100_000; index++)
        {
            var candidate = $"{clean}-{index}";
            if (!used.Contains(candidate))
                return candidate;
        }
        return $"{clean}-{Guid.NewGuid():N}";
    }

    private void SortTimeline()
    {
        var selected = SelectedTimelineItem;
        var ordered = TimelineItems
            .OrderBy(item => ParseOffset(item.AtMillisecondsText))
            .ThenBy(item => item.IsAssertion ? 1 : 0)
            .ThenBy(item => item.Order)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        TimelineItems.Clear();
        foreach (var item in ordered)
            TimelineItems.Add(item);
        ReindexTimeline();
        SelectedTimelineItem = selected is not null && TimelineItems.Contains(selected) ? selected : TimelineItems.FirstOrDefault();
    }

    private void ReindexTimeline()
    {
        for (var index = 0; index < TimelineItems.Count; index++)
            TimelineItems[index].Order = index;
    }

    private static long ParseOffset(string text) => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : long.MaxValue;

    private bool TimelineError(string message)
    {
        TimelineStatusText = message;
        StatusText = message;
        return false;
    }

    private bool TryTemplatePath(string? rawName, out string path, out string normalizedName)
    {
        normalizedName = SanitizeTemplateName(rawName);
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedName))
            return TimelineError("模板名称不能为空。 ");
        path = Path.Combine(TimelineTemplateDirectory(), normalizedName + ".json");
        return true;
    }

    private static string TimelineTemplateDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Wcs.Desktop", "SimulationTemplates");

    private static string SanitizeTemplateName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;
        var characters = raw.Trim().Take(80)
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-')
            .ToArray();
        return new string(characters).Trim('-');
    }

    private readonly record struct ParsedTimelineItem(
        bool IsAssertion,
        string Id,
        long AtMilliseconds,
        long DurationMilliseconds,
        int Order,
        string Kind,
        string Target,
        JsonNode Body);

    private sealed class TimelineTemplateDocument
    {
        public int SchemaVersion { get; set; }
        public string Name { get; set; } = string.Empty;
        public string ScenarioId { get; set; } = string.Empty;
        public string ScenarioVersion { get; set; } = "1.0.0";
        public string ScenarioSeedText { get; set; } = string.Empty;
        public string ScenarioStartUtcText { get; set; } = string.Empty;
        public List<SimulationTimelineEditorItem>? Items { get; set; }
    }
}
