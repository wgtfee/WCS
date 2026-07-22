namespace Wcs.Core.TransportScheduling;

using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

public interface ITransportPointTableImporter
{
    TransportPointTableImportResult Import(byte[] content, string fileName);
}

/// <summary>
/// 支持 JSON、Excel 兼容 CSV 和最小 OpenXML XLSX 点位表。
/// XLSX 只读取第一个工作表，不执行公式、不加载宏，适合现场点位清单导入。
/// </summary>
public sealed class TransportPointTableImporter : ITransportPointTableImporter
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public TransportPointTableImportResult Import(byte[] content, string fileName)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0)
            return Failed("文件", "导入文件为空");
        if (content.Length > 10 * 1024 * 1024)
            return Failed("文件", "点位表不能超过 10MB");

        try
        {
            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            var rows = extension switch
            {
                ".json" => ParseJson(content),
                ".csv" => ParseCsv(Encoding.UTF8.GetString(content)),
                ".xlsx" => ParseXlsx(content),
                _ => throw new NotSupportedException("仅支持 .json、.csv 和 .xlsx 点位表")
            };
            return ValidateAndMap(rows);
        }
        catch (Exception ex)
        {
            return Failed("文件", ex.Message);
        }
    }

    private static IReadOnlyList<TransportPointTableRow> ParseJson(byte[] content) =>
        JsonSerializer.Deserialize<TransportPointTableRow[]>(content, JsonOptions)
        ?? Array.Empty<TransportPointTableRow>();

    private static IReadOnlyList<TransportPointTableRow> ParseCsv(string content)
    {
        var lines = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            return Array.Empty<TransportPointTableRow>();

        var headers = ParseCsvLine(lines[0]);
        var rows = new List<TransportPointTableRow>();
        for (var i = 1; i < lines.Length; i++)
        {
            var values = ParseCsvLine(lines[i]);
            if (values.All(string.IsNullOrWhiteSpace))
                continue;
            rows.Add(BindRow(headers, values, i + 1));
        }
        return rows;
    }

    private static IReadOnlyList<TransportPointTableRow> ParseXlsx(byte[] content)
    {
        using var memory = new MemoryStream(content, writable: false);
        using var archive = new ZipArchive(memory, ZipArchiveMode.Read, leaveOpen: false);
        var sharedStrings = ReadSharedStrings(archive);
        var sheet = archive.GetEntry("xl/worksheets/sheet1.xml")
            ?? throw new InvalidDataException("XLSX 未找到第一个工作表 xl/worksheets/sheet1.xml");

        using var stream = sheet.Open();
        var document = XDocument.Load(stream);
        var ns = document.Root?.Name.Namespace ?? XNamespace.None;
        var matrix = new List<IReadOnlyList<string>>();
        foreach (var row in document.Descendants(ns + "row"))
        {
            var values = new SortedDictionary<int, string>();
            foreach (var cell in row.Elements(ns + "c"))
            {
                var reference = (string?)cell.Attribute("r") ?? string.Empty;
                var column = ColumnIndex(reference);
                var type = (string?)cell.Attribute("t");
                string value;
                if (string.Equals(type, "inlineStr", StringComparison.Ordinal))
                {
                    value = string.Concat(cell.Descendants(ns + "t").Select(x => x.Value));
                }
                else
                {
                    value = cell.Element(ns + "v")?.Value ?? string.Empty;
                    if (string.Equals(type, "s", StringComparison.Ordinal) &&
                        int.TryParse(value, out var sharedIndex) &&
                        sharedIndex >= 0 && sharedIndex < sharedStrings.Count)
                    {
                        value = sharedStrings[sharedIndex];
                    }
                }
                values[column] = value;
            }

            var length = values.Count == 0 ? 0 : values.Keys.Max() + 1;
            var materialized = Enumerable.Range(0, length)
                .Select(index => values.GetValueOrDefault(index) ?? string.Empty)
                .ToArray();
            if (materialized.Any(x => !string.IsNullOrWhiteSpace(x)))
                matrix.Add(materialized);
        }

        if (matrix.Count == 0)
            return Array.Empty<TransportPointTableRow>();
        var headers = matrix[0];
        return matrix
            .Skip(1)
            .Select((values, index) => BindRow(headers, values, index + 2))
            .ToArray();
    }

    private static IReadOnlyList<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
            return Array.Empty<string>();
        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        var ns = document.Root?.Name.Namespace ?? XNamespace.None;
        return document
            .Descendants(ns + "si")
            .Select(item => string.Concat(item.Descendants(ns + "t").Select(x => x.Value)))
            .ToArray();
    }

    private static int ColumnIndex(string cellReference)
    {
        var letters = new string(cellReference.TakeWhile(char.IsLetter).ToArray());
        var result = 0;
        foreach (var letter in letters.ToUpperInvariant())
            result = result * 26 + (letter - 'A' + 1);
        return Math.Max(0, result - 1);
    }

    private static TransportPointTableRow BindRow(
        IReadOnlyList<string> headers,
        IReadOnlyList<string> values,
        int rowNumber)
    {
        var row = new TransportPointTableRow();
        var type = typeof(TransportPointTableRow);
        for (var i = 0; i < headers.Count; i++)
        {
            var header = headers[i].Trim();
            if (string.IsNullOrWhiteSpace(header))
                continue;
            var property = type.GetProperty(
                header,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property is null || !property.CanWrite)
                continue;
            var text = i < values.Count ? values[i].Trim() : string.Empty;
            try
            {
                property.SetValue(row, ConvertText(text, property.PropertyType));
            }
            catch (Exception ex)
            {
                throw new FormatException($"第 {rowNumber} 行字段 {header} 格式错误：{ex.Message}", ex);
            }
        }
        return row;
    }

    private static object? ConvertText(string text, Type type)
    {
        if (type == typeof(string))
            return text;
        if (type == typeof(bool))
        {
            if (string.Equals(text, "1", StringComparison.Ordinal) ||
                string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "是", StringComparison.Ordinal))
                return true;
            if (string.Equals(text, "0", StringComparison.Ordinal) ||
                string.Equals(text, "no", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "否", StringComparison.Ordinal))
                return false;
            return bool.Parse(text);
        }
        if (type == typeof(int))
            return string.IsNullOrWhiteSpace(text) ? 0 : int.Parse(text);
        if (type == typeof(long))
            return string.IsNullOrWhiteSpace(text) ? 0L : long.Parse(text);
        if (type.IsEnum)
        {
            if (int.TryParse(text, out var numeric))
                return Enum.ToObject(type, numeric);
            return Enum.Parse(type, text, ignoreCase: true);
        }
        return Convert.ChangeType(text, type);
    }

    private static string[] ParseCsvLine(string line)
    {
        var values = new List<string>();
        var builder = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var current = line[i];
            if (current == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    builder.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (current == ',' && !quoted)
            {
                values.Add(builder.ToString());
                builder.Clear();
            }
            else
            {
                builder.Append(current);
            }
        }
        if (quoted)
            throw new FormatException("CSV 存在未闭合的双引号");
        values.Add(builder.ToString());
        return values.ToArray();
    }

    private static TransportPointTableImportResult ValidateAndMap(
        IReadOnlyList<TransportPointTableRow> rows)
    {
        var issues = new List<TransportPointTableIssue>();
        var maps = new List<TransportPlcSignalMap>();
        if (rows.Count == 0)
            issues.Add(Issue(0, "文件", "点位表没有数据行"));

        var duplicates = rows
            .Where(x => !string.IsNullOrWhiteSpace(x.VehicleId))
            .GroupBy(x => x.VehicleId, StringComparer.Ordinal)
            .Where(x => x.Count() > 1)
            .Select(x => x.Key)
            .ToHashSet(StringComparer.Ordinal);

        for (var index = 0; index < rows.Count; index++)
        {
            var rowNumber = index + 2;
            var row = rows[index];
            if (string.IsNullOrWhiteSpace(row.VehicleId))
                issues.Add(Issue(rowNumber, nameof(row.VehicleId), "VehicleId 不能为空"));
            if (string.IsNullOrWhiteSpace(row.DriverId))
                issues.Add(Issue(rowNumber, nameof(row.DriverId), "DriverId 不能为空"));
            if (duplicates.Contains(row.VehicleId))
                issues.Add(Issue(rowNumber, nameof(row.VehicleId), "同一文件中 VehicleId 重复"));
            if (row.PollIntervalMs <= 0)
                issues.Add(Issue(rowNumber, nameof(row.PollIntervalMs), "轮询周期必须大于 0"));
            if (row.HeartbeatTimeoutMs <= 0)
                issues.Add(Issue(rowNumber, nameof(row.HeartbeatTimeoutMs), "心跳超时必须大于 0"));

            if (row.Mode == TransportDriverMode.PlcTag)
            {
                Require(rowNumber, nameof(row.HeartbeatTag), row.HeartbeatTag, issues);
                Require(rowNumber, nameof(row.CurrentNodeTag), row.CurrentNodeTag, issues);
                Require(rowNumber, nameof(row.OperatingStateTag), row.OperatingStateTag, issues);
                Require(rowNumber, nameof(row.StateSequenceTag), row.StateSequenceTag, issues);
                Require(rowNumber, nameof(row.CommandSequenceTag), row.CommandSequenceTag, issues);
                Require(rowNumber, nameof(row.CommandCodeTag), row.CommandCodeTag, issues);
                Require(rowNumber, nameof(row.CommandRequestTag), row.CommandRequestTag, issues);
                Require(rowNumber, nameof(row.AcknowledgedSequenceTag), row.AcknowledgedSequenceTag, issues);
                Require(rowNumber, nameof(row.CommandAcceptedTag), row.CommandAcceptedTag, issues);
                Require(rowNumber, nameof(row.CommandCompletedTag), row.CommandCompletedTag, issues);
            }

            try
            {
                maps.Add(ToMap(row));
            }
            catch (Exception ex)
            {
                issues.Add(Issue(rowNumber, "代码映射", ex.Message));
            }
        }

        return new TransportPointTableImportResult
        {
            Rows = rows,
            Maps = maps,
            Issues = issues
        };
    }

    private static TransportPlcSignalMap ToMap(TransportPointTableRow row) => new()
    {
        VehicleId = row.VehicleId,
        DriverId = row.DriverId,
        Kind = row.Kind,
        Mode = row.Mode,
        Enabled = row.Enabled,
        PollIntervalMs = row.PollIntervalMs,
        HeartbeatTimeoutMs = row.HeartbeatTimeoutMs,
        HeartbeatTag = row.HeartbeatTag,
        DeviceOnlineTag = row.DeviceOnlineTag,
        CurrentNodeTag = row.CurrentNodeTag,
        OperatingStateTag = row.OperatingStateTag,
        BatteryPercentTag = row.BatteryPercentTag,
        FaultCodeTag = row.FaultCodeTag,
        FaultMessageTag = row.FaultMessageTag,
        StateSequenceTag = row.StateSequenceTag,
        ActiveCommandIdTag = row.ActiveCommandIdTag,
        LoadPresentTag = row.LoadPresentTag,
        CommandIdTag = row.CommandIdTag,
        CommandSequenceTag = row.CommandSequenceTag,
        CommandCodeTag = row.CommandCodeTag,
        TargetNodeTag = row.TargetNodeTag,
        CommandRequestTag = row.CommandRequestTag,
        AcknowledgedCommandIdTag = row.AcknowledgedCommandIdTag,
        AcknowledgedSequenceTag = row.AcknowledgedSequenceTag,
        CommandAcceptedTag = row.CommandAcceptedTag,
        CommandCompletedTag = row.CommandCompletedTag,
        CommandErrorTag = row.CommandErrorTag,
        NodeCodeMap = ParseDictionary<int, string>(row.NodeCodeMapJson, nameof(row.NodeCodeMapJson)),
        TargetNodeCodeMap = ParseDictionary<string, int>(row.TargetNodeCodeMapJson, nameof(row.TargetNodeCodeMapJson)),
        OperatingStateMap = ParseDictionary<int, TransportVehicleOperatingState>(row.OperatingStateMapJson, nameof(row.OperatingStateMapJson)),
        CommandCodeMap = ParseDictionary<TransportExecutionCommandType, int>(row.CommandCodeMapJson, nameof(row.CommandCodeMapJson)),
        Version = row.ExpectedVersion
    };

    private static IReadOnlyDictionary<TKey, TValue> ParseDictionary<TKey, TValue>(
        string json,
        string fieldName)
        where TKey : notnull
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<TKey, TValue>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<TKey, TValue>>(json, JsonOptions)
                ?? new Dictionary<TKey, TValue>();
        }
        catch (Exception ex)
        {
            throw new FormatException($"{fieldName} 不是有效 JSON：{ex.Message}", ex);
        }
    }

    private static void Require(
        int rowNumber,
        string field,
        string value,
        ICollection<TransportPointTableIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
            issues.Add(Issue(rowNumber, field, $"PLC 模式必须配置 {field}"));
    }

    private static TransportPointTableIssue Issue(int row, string field, string message) => new()
    {
        RowNumber = row,
        Field = field,
        Level = TransportPointTableIssueLevel.Error,
        Message = message
    };

    private static TransportPointTableImportResult Failed(string field, string message) => new()
    {
        Issues = new[] { Issue(0, field, message) }
    };

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
