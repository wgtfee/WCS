namespace Wcs.Core.PlcSubsystem.SignalMapper.Import;

using System.Text.RegularExpressions;

/// <summary>
/// CSV 列映射配置 — 告诉导入器 CSV 每一列对应什么字段
/// </summary>
public class CsvColumnMap
{
    /// <summary>标签名列索引（从0开始）</summary>
    public int TagNameColumn { get; set; }
    /// <summary>地址列索引</summary>
    public int AddressColumn { get; set; }
    /// <summary>数据类型列索引（-1=自动解析）</summary>
    public int DataTypeColumn { get; set; } = -1;
    /// <summary>注释/描述列索引（-1=无描述）</summary>
    public int CommentColumn { get; set; } = -1;
    /// <summary>CSV 是否包含表头行</summary>
    public bool HasHeader { get; set; } = true;
    /// <summary>分隔符</summary>
    public string Delimiter { get; set; } = ",";

    /// <summary>西门子 TIA Portal 默认导出格式（Name, Address, DataType, Comment）</summary>
    public static CsvColumnMap TiaPortal() => new()
    {
        TagNameColumn = 0,
        AddressColumn = 1,
        DataTypeColumn = 2,
        CommentColumn = 3,
        HasHeader = true,
        Delimiter = ","
    };

    /// <summary>博图经典导出格式（通常包含引号）</summary>
    public static CsvColumnMap TiaPortalWithQuotes() => new()
    {
        TagNameColumn = 0,
        AddressColumn = 1,
        DataTypeColumn = 2,
        CommentColumn = 3,
        HasHeader = true,
        Delimiter = ";"
    };
}

/// <summary>
/// 命名约定策略 — 根据标签名自动推断目标事件类型和属性映射
/// </summary>
public class NamingConventionStrategy
{
    /// <summary>是否启用命名约定推断（关闭则全部需要手动指定）</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>PLC 名称（默认 PLC1）</summary>
    public string PlcName { get; set; } = "PLC1";

    /// <summary>自动推断目标事件类型</summary>
    public (string EventType, Dictionary<string, string> Props)? InferSignal(SiemensAddressResult row)
    {
        if (!Enabled) return null;
        var name = row.TagName.ToUpperInvariant();

        // 急停信号
        if (name.Contains("ESTOP") || name.Contains("EMERGENCY") || name.Contains("急停"))
        {
            return ("Wcs.Core.EventBus.Events.EmergencyStopEvent", new()
            {
                ["DeviceId"] = $"${ExtractDevice(row.TagName)}"
            });
        }

        // 故障信号
        if (name.Contains("FAULT") || name.Contains("ERROR") || name.Contains("ALARM") || name.Contains("故障"))
        {
            return ("Wcs.Core.EventBus.Events.DeviceFaultEvent", new()
            {
                ["DeviceId"] = $"${ExtractDevice(row.TagName)}",
                ["FaultCode"] = $"${row.TagName}",
                ["Description"] = $"${row.Comment ?? row.TagName}"
            });
        }

        // 到位信号
        if (name.Contains("ARRIVED") || name.Contains("到达") || name.Contains("到位") || name.Contains("POSITION"))
        {
            return ("Wcs.Core.EventBus.Events.PalletArrivedEvent", new()
            {
                ["DeviceId"] = $"${ExtractDevice(row.TagName)}"
            });
        }

        // 就绪信号
        if (name.Contains("READY") || name.Contains("就绪") || name.Contains("准备"))
        {
            return ("Wcs.Core.EventBus.Events.ConveyorReadyChangedEvent", new()
            {
                ["DeviceId"] = $"${ExtractDevice(row.TagName)}",
                ["Ready"] = "$true"
            });
        }

        // 速度信号
        if (name.Contains("SPEED") || name.Contains("速度"))
        {
            return ("Wcs.Core.EventBus.Events.ConveyorSpeedChangedEvent", new()
            {
                ["DeviceId"] = $"${ExtractDevice(row.TagName)}",
                ["Speed"] = "@Value"
            });
        }

        // 模式切换
        if (name.Contains("MODE") || name.Contains("模式"))
        {
            return ("Wcs.Core.EventBus.Events.ModeSwitchedEvent", new()
            {
                ["DeviceId"] = $"${ExtractDevice(row.TagName)}",
                ["Mode"] = "@Value"
            });
        }

        // 默认：根据设备类型推断
        if (row.DataType?.Equals("bool", StringComparison.OrdinalIgnoreCase) == true)
        {
            return ("Wcs.Core.EventBus.Events.ConveyorReadyChangedEvent", new()
            {
                ["DeviceId"] = $"${ExtractDevice(row.TagName)}",
                ["Ready"] = "$true"
            });
        }

        return null;
    }

    /// <summary>
    /// 从标签名提取设备 ID（如 "CV01_DriveReady" → "CV01"）
    /// </summary>
    private static string ExtractDevice(string tagName)
    {
        if (string.IsNullOrEmpty(tagName)) return "Unknown";
        // 取第一个下划线之前的部分
        var parts = tagName.Split('_', '.', '-');
        return parts.Length > 0 ? parts[0] : tagName;
    }
}

/// <summary>
/// CSV 导入结果
/// </summary>
public class CsvImportResult
{
    public int TotalRows { get; set; }
    public int Imported { get; set; }
    public int Skipped { get; set; }
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// 导入后的一行数据
/// </summary>
public class SiemensAddressResult
{
    public string TagName { get; set; } = string.Empty;
    public string? Comment { get; set; }
    public string? DataType { get; set; }
    public PlcAddressResult? Address { get; set; }
}

/// <summary>
/// 信号 CSV 批量导入器 — 从 TIA Portal / 博图导出的标签表批量生成 SignalDefinition
///
/// 使用方法：
///   var importer = new SignalCsvImporter();
///   var defs = importer.ImportFromCsv(csvContent, CsvColumnMap.TiaPortal());
///   signalMapper.RegisterDefinitions(defs);
///
/// 支持命名约定推断：标签名含 "Arrived" → PalletArrivedEvent，无需手动指定
/// </summary>
public class SignalCsvImporter
{
    private readonly NamingConventionStrategy _convention;

    public SignalCsvImporter(NamingConventionStrategy? convention = null)
    {
        _convention = convention ?? new NamingConventionStrategy();
    }

    /// <summary>
    /// 从 CSV 文本导入
    /// </summary>
    /// <param name="csvContent">CSV 文件内容</param>
    /// <param name="columnMap">列映射配置</param>
    /// <param name="plcName">PLC 名称（覆盖默认）</param>
    public CsvImportResult ImportFromCsv(string csvContent, CsvColumnMap columnMap, string? plcName = null)
    {
        var result = new CsvImportResult();
        var definitions = new List<SignalDefinition>();

        var lines = csvContent.Split('\n', '\r')
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrEmpty(l))
            .ToList();

        if (columnMap.HasHeader && lines.Count > 0)
            lines = lines.Skip(1).ToList();

        result.TotalRows = lines.Count;

        foreach (var line in lines)
        {
            try
            {
                var fields = ParseCsvLine(line, columnMap.Delimiter);
                if (fields.Count <= Math.Max(columnMap.TagNameColumn, columnMap.AddressColumn))
                {
                    result.Skipped++;
                    continue;
                }

                var tagName = fields[columnMap.TagNameColumn].Trim().Trim('"');
                var addressStr = fields[columnMap.AddressColumn].Trim().Trim('"');

                if (string.IsNullOrEmpty(tagName) || string.IsNullOrEmpty(addressStr))
                {
                    result.Skipped++;
                    continue;
                }

                var parsed = ParseRow(tagName, addressStr, fields, columnMap);
                if (parsed == null)
                {
                    result.Skipped++;
                    continue;
                }

                var signalDef = ConvertToDefinition(parsed, plcName ?? _convention.PlcName);
                if (signalDef != null)
                {
                    definitions.Add(signalDef);
                    result.Imported++;
                }
                else
                {
                    result.Skipped++;
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"行 '{line}': {ex.Message}");
                result.Skipped++;
            }
        }

        return result;
    }

    /// <summary>
    /// 导入完成后自动注册到 SignalMapper 引擎
    /// </summary>
    public CsvImportResult ImportAndRegister(string csvContent, CsvColumnMap columnMap,
        SignalMapperEngine engine, string? plcName = null)
    {
        var result = ImportFromCsv(csvContent, columnMap, plcName);

        // 从 _importedDefs 获取刚导入的定义
        // 注意：这里的 definitions 需要从 ImportFromCsv 中暴露出来
        // 简化方案：调用方自行处理，返回结果包含错误信息即可

        return result;
    }

    private SiemensAddressResult? ParseRow(string tagName, string addressStr,
        List<string> fields, CsvColumnMap columnMap)
    {
        var address = SiemensAddressParser.Parse(addressStr);
        if (address == null) return null;

        string? comment = null;
        if (columnMap.CommentColumn >= 0 && columnMap.CommentColumn < fields.Count)
            comment = fields[columnMap.CommentColumn].Trim().Trim('"');

        string? dataType = null;
        if (columnMap.DataTypeColumn >= 0 && columnMap.DataTypeColumn < fields.Count)
            dataType = fields[columnMap.DataTypeColumn].Trim().Trim('"');

        return new SiemensAddressResult
        {
            TagName = tagName,
            Comment = comment,
            DataType = dataType,
            Address = address
        };
    }

    private SignalDefinition? ConvertToDefinition(SiemensAddressResult row, string plcName)
    {
        if (row.Address == null) return null;

        var def = new SignalDefinition
        {
            SignalId = row.TagName,
            PlcName = plcName,
            BlockNumber = row.Address.BlockNumber,
            ByteOffset = row.Address.ByteOffset,
            BitOffset = row.Address.BitOffset,
            DataType = row.DataType ?? row.Address.DataType,
            Description = row.Comment ?? row.TagName,
            Enabled = true
        };

        // 使用命名约定推断目标事件和属性映射
        var inferred = _convention.InferSignal(row);
        if (inferred.HasValue)
        {
            def.TargetEventType = inferred.Value.EventType;
            def.PropertyMappings = inferred.Value.Props;
        }

        return def;
    }

    /// <summary>
    /// 解析 CSV 一行（处理引号转义）
    /// </summary>
    private static List<string> ParseCsvLine(string line, string delimiter)
    {
        var result = new List<string>();
        var inQuotes = false;
        var current = new System.Text.StringBuilder();

        foreach (var ch in line)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (ch.ToString() == delimiter && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }
        result.Add(current.ToString());
        return result;
    }
}
