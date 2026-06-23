using System.Collections.Concurrent;
using S7CommPlusDriver;
using S7CommPlusDriver.ClientApi;
using Wcs.Core.PlcSubsystem.Abstractions;
using Wcs.Core.PlcSubsystem.Label;

namespace Wcs.Core.PlcSubsystem.S7.S7CommPlus;

/// <summary>
/// S7 Communication Plus 标签式客户端 — 基于西门子 S7-1500 扩展协议的符号标签读写
///
/// 工作原理：
///   1. 通过 S7CommPlusConnection 连接 PLC（TLS 加密）
///   2. 调用 getPlcTagBySymbol() 按变量名解析标签，获取 ItemAddress 和数据类型
///   3. 使用 ReadTags() / WriteTags() 进行批量读写（原生 MultiVariables 请求）
///   4. 已解析的标签缓存到 _tagCache，避免重复浏览 PLC
///
/// 适用场景：
///   - Siemens S7-1500 系列 PLC
///   - 真正的符号标签访问（按变量名读写，无需偏移量配置）
///   - 支持 TLS 加密通信
///
/// 配置示例（appsettings.json）：
/// ```json
/// {
///   "S7CommPlus": {
///     "Address": "192.168.1.1",
///     "Password": "",
///     "TimeoutMs": 5000
///   }
/// }
/// ```
/// </summary>
public class S7CommPlusPlcClient : IPlcClient
{
    private readonly PlcTagRegistry _registry;
    private readonly S7CommPlusConfig _config;
    private S7CommPlusConnection? _connection;
    private readonly ConcurrentDictionary<string, PlcTag> _tagCache = new();
    private bool _disposed;

    public S7CommPlusPlcClient(PlcTagRegistry registry, S7CommPlusConfig config)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>读取单个标签的值</summary>
    public Task<object?> ReadAsync(string tagName, int timeoutMs = 3000)
    {
        var plcTag = ResolveTag(tagName);
        var result = GetConnection().ReadTags(new[] { plcTag });
        if (result != 0)
            throw new InvalidOperationException(
                $"读取标签 '{tagName}' 失败: S7CommPlus 错误码 {result}");

        if (plcTag.Quality != PlcTagQC.TAG_QUALITY_GOOD)
            throw new InvalidOperationException(
                $"标签 '{tagName}' 数据质量异常: Quality={plcTag.Quality}");

        return Task.FromResult(ExtractValue(plcTag));
    }

    /// <summary>写入单个标签的值</summary>
    public Task WriteAsync(string tagName, object? value, int timeoutMs = 3000)
    {
        var template = ResolveTag(tagName);

        // 根据模板类型创建带值的写入标签
        var writeTag = CreateTagWithValue(tagName, value,
            template.Address, template.Datatype);

        var result = GetConnection().WriteTags(new[] { writeTag });
        if (result != 0)
            throw new InvalidOperationException(
                $"写入标签 '{tagName}' 失败: S7CommPlus 错误码 {result}");

        return Task.CompletedTask;
    }

    /// <summary>批量读取多个标签 — 使用 ReadTags 原生批量请求</summary>
    public Task<object?[]> ReadBatchAsync(string[] tagNames, int timeoutMs = 3000)
    {
        var tags = tagNames.Select(ResolveTag).ToList();
        var conn = GetConnection();

        var result = conn.ReadTags(tags);
        if (result != 0)
            throw new InvalidOperationException(
                $"批量读取失败: S7CommPlus 错误码 {result}");

        var values = tags.Select(t =>
            t.Quality == PlcTagQC.TAG_QUALITY_GOOD ? ExtractValue(t) : null)
            .ToArray();

        return Task.FromResult(values!);
    }

    /// <summary>批量写入多个标签 — 使用 WriteTags 原生批量请求</summary>
    public Task WriteBatchAsync(IEnumerable<(string Name, object? Value)> writes, int timeoutMs = 3000)
    {
        var writeTags = new List<PlcTag>();
        foreach (var (name, value) in writes)
        {
            var template = ResolveTag(name);
            writeTags.Add(CreateTagWithValue(name, value,
                template.Address, template.Datatype));
        }

        var result = GetConnection().WriteTags(writeTags);
        if (result != 0)
            throw new InvalidOperationException(
                $"批量写入失败: S7CommPlus 错误码 {result}");

        return Task.CompletedTask;
    }

    /// <summary>清理连接</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _tagCache.Clear();
            try { _connection?.Disconnect(); } catch { }
            _connection = null;
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    #region 内部实现

    /// <summary>获取或创建连接</summary>
    private S7CommPlusConnection GetConnection()
    {
        if (_connection == null)
        {
            var conn = new S7CommPlusConnection();
            var result = conn.Connect(
                _config.Address,
                _config.Password ?? "",
                _config.Username ?? "",
                _config.TimeoutMs > 0 ? _config.TimeoutMs : 5000);

            if (result != 0)
                throw new InvalidOperationException(
                    $"S7CommPlus 连接 '{_config.Address}' 失败: 错误码 {result}");

            _connection = conn;
        }
        return _connection;
    }

    /// <summary>
    /// 解析标签名 — 通过 getPlcTagBySymbol 向 PLC 查询符号地址，
    /// 之后缓存结果避免重复查询。
    /// </summary>
    private PlcTag ResolveTag(string tagName)
    {
        if (_tagCache.TryGetValue(tagName, out var cached))
            return cached;

        var conn = GetConnection();
        var plcTag = conn.getPlcTagBySymbol(tagName);
        if (plcTag == null)
            throw new KeyNotFoundException(
                $"标签 '{tagName}' 在 PLC '{_config.Address}' 中未找到，请确认变量名正确");

        _tagCache[tagName] = plcTag;
        return plcTag;
    }

    /// <summary>从 PlcTag 中提取 .NET 值</summary>
    private static object? ExtractValue(PlcTag tag) => tag switch
    {
        PlcTagBool b     => b.Value,
        PlcTagByte b     => b.Value,
        PlcTagSInt si    => si.Value,
        PlcTagUSInt usi  => usi.Value,
        PlcTagInt i      => i.Value,
        PlcTagUInt ui    => ui.Value,
        PlcTagDInt di    => di.Value,
        PlcTagUDInt udi  => udi.Value,
        PlcTagLInt li    => li.Value,
        PlcTagULInt uli  => uli.Value,
        PlcTagWord w     => w.Value,
        PlcTagDWord dw   => dw.Value,
        PlcTagLWord lw   => lw.Value,
        PlcTagReal r     => r.Value,
        PlcTagLReal lr   => lr.Value,
        PlcTagString s   => s.Value,
        PlcTagWString ws => ws.Value,
        PlcTagChar c     => c.Value,
        PlcTagWChar wc   => wc.Value,
        PlcTagDate d     => d.Value,
        PlcTagTimeOfDay t => t.Value,
        PlcTagTime t     => t.Value,
        PlcTagDateAndTime dt => dt.Value,
        PlcTagS5Time s5  => null, // S5Time 结构复杂，暂不自动转换
        PlcTagPointer p  => p.Value,
        PlcTagAny a      => a.Value,
        _ => null
    };

    /// <summary>
    /// 根据模板 PlcTag 创建带写入值的标签。
    /// 使用 TagFactory 创建与模板相同类型的 PlcTag，然后设置值。
    /// </summary>
    private static PlcTag CreateTagWithValue(string name, object? value,
        ItemAddress address, uint datatype)
    {
        var tag = PlcTags.TagFactory(name, address, datatype);
        if (tag == null)
            throw new NotSupportedException($"无法为标签 '{name}' 创建 PlcTag，未知的数据类型 {datatype}");

        SetTagValue(tag, value);
        return tag;
    }

    /// <summary>设置 PlcTag 的值</summary>
    private static void SetTagValue(PlcTag tag, object? value)
    {
        if (value == null) return;

        switch (tag)
        {
            case PlcTagBool b:     b.Value = (bool)value; break;
            case PlcTagByte b:     b.Value = (byte)value; break;
            case PlcTagSInt si:    si.Value = (sbyte)value; break;
            case PlcTagUSInt usi:  usi.Value = (byte)value; break;
            case PlcTagInt i:      i.Value = (short)value; break;
            case PlcTagUInt ui:    ui.Value = (ushort)value; break;
            case PlcTagDInt di:    di.Value = (int)value; break;
            case PlcTagUDInt udi:  udi.Value = (uint)value; break;
            case PlcTagLInt li:    li.Value = (long)value; break;
            case PlcTagULInt uli:  uli.Value = (ulong)value; break;
            case PlcTagWord w:     w.Value = (ushort)value; break;
            case PlcTagDWord dw:   dw.Value = (uint)value; break;
            case PlcTagLWord lw:   lw.Value = (ulong)value; break;
            case PlcTagReal r:     r.Value = (float)value; break;
            case PlcTagLReal lr:   lr.Value = (double)value; break;
            case PlcTagString s:   s.Value = (string)value; break;
            case PlcTagWString ws: ws.Value = (string)value; break;
            case PlcTagChar c:     c.Value = (char)value; break;
            case PlcTagWChar wc:   wc.Value = (char)value; break;
            case PlcTagDate d:     d.Value = (DateTime)value; break;
            case PlcTagTimeOfDay t: t.Value = (uint)value; break;
            case PlcTagTime t:     t.Value = (int)value; break;
            case PlcTagDateAndTime dt: dt.Value = (DateTime)value; break;
            case PlcTagPointer p:  p.Value = (byte[])value; break;
            case PlcTagAny a:      a.Value = (byte[])value; break;
        }
    }

    #endregion
}

/// <summary>S7CommPlus 连接配置</summary>
public class S7CommPlusConfig
{
    /// <summary>PLC 的 IP 地址</summary>
    public string Address { get; set; } = string.Empty;
    /// <summary>PLC 密码（可选）</summary>
    public string? Password { get; set; }
    /// <summary>用户名（可选）</summary>
    public string? Username { get; set; }
    /// <summary>连接超时（毫秒，默认 5000）</summary>
    public int TimeoutMs { get; set; } = 5000;
}
