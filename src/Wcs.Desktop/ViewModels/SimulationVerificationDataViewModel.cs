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
            ScenarioDataStatusText = "当前 Scenario DSL 为空。";
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
                        DataLabel = "Payload",
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
                        Phase = "断言",
                        Id = ReadString(assertion, "Id"),
                        AtMilliseconds = ReadInt64(assertion, "AtMilliseconds"),
                        Order = ReadInt32(assertion, "Order"),
                        Kind = ReadString(assertion, "Kind"),
                        Target = ReadString(assertion, "Target"),
                        DataLabel = "Expected",
                        Data = ReadJson(assertion, "Expected")
                    });
                }
            }

            ScenarioActionCount = rows.Count(x => x.Phase == "动作");
            ScenarioAssertionCount = rows.Count(x => x.Phase == "断言");
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
                $"{scenarioId ?? "Scenario"}@{version ?? "?"} · {ScenarioActionCount} 个动作 · {ScenarioAssertionCount} 个断言 · Duration {ScenarioDurationMilliseconds} ms";
        }
        catch (JsonException exception)
        {
            ScenarioDataStatusText = $"Scenario JSON 尚不可解析：{exception.Message}";
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
                Type = "Raw",
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
                    rows.Add(new SimulationStateDataRow { Path = path, Type = "Object", Value = "{}" });
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
                    rows.Add(new SimulationStateDataRow { Path = path, Type = "Array", Value = "[]" });
                break;
            }
            default:
                rows.Add(new SimulationStateDataRow
                {
                    Path = path,
                    Type = element.ValueKind.ToString(),
                    Value = Truncate(PrimitiveText(element), 2000)
                });
                break;
        }
    }

    private static string PrimitiveText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Null => "null",
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
}

public sealed class SimulationStateDataRow
{
    public string Path { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
