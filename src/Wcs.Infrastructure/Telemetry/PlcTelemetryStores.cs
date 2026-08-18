namespace Wcs.Infrastructure.Telemetry;

using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using SqlSugar;
using Wcs.Core.Telemetry;

internal sealed class DisabledPlcTelemetryStore : IPlcTelemetryStore
{
    public string ProviderName => "Disabled";

    public Task WriteBatchAsync(
        IReadOnlyList<PlcTelemetryPoint> points,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class SqlServerPlcTelemetryStore : IPlcTelemetryStore
{
    private readonly string _connectionString;

    public SqlServerPlcTelemetryStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    public string ProviderName => "SqlServer";

    public async Task WriteBatchAsync(
        IReadOnlyList<PlcTelemetryPoint> points,
        CancellationToken cancellationToken = default)
    {
        if (points.Count == 0) return;

        using var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _connectionString,
            DbType = DbType.SqlServer,
            IsAutoCloseConnection = true
        });

        var entities = points.Select(ToEntity).ToList();
        var eventIds = entities.Select(static item => item.EventId).ToArray();
        var existing = await db.Queryable<PlcTelemetryEntity>()
            .Where(item => eventIds.Contains(item.EventId))
            .Select(static item => item.EventId)
            .ToListAsync();

        if (existing.Count > 0)
        {
            var existingSet = existing.ToHashSet(StringComparer.Ordinal);
            entities.RemoveAll(item => existingSet.Contains(item.EventId));
        }

        if (entities.Count > 0)
            await db.Insertable(entities).ExecuteCommandAsync(cancellationToken);
    }

    private static PlcTelemetryEntity ToEntity(PlcTelemetryPoint point) => new()
    {
        Sequence = point.Sequence,
        TimestampUnixNanoseconds = point.TimestampUnixNanoseconds,
        TimestampUtc = point.TimestampUtc,
        EventId = point.EventId,
        Site = point.Site,
        PlcName = point.PlcName,
        DbBlock = point.DbBlock,
        DeviceId = point.DeviceId,
        SignalName = point.SignalName,
        OldValue = point.OldValue,
        NewValue = point.NewValue,
        ValueKind = (int)point.ValueKind,
        BoolValue = point.BoolValue,
        NumericValue = point.NumericValue.HasValue ? (decimal?)point.NumericValue.Value : null,
        TextValue = point.TextValue,
        Quality = point.Quality,
        ValidatorPassed = point.ValidatorPassed,
        ValidatorReason = point.ValidatorReason,
        DomainEventType = point.DomainEventType,
        Source = point.Source
    };
}

internal sealed class InfluxDbPlcTelemetryStore : IPlcTelemetryStore
{
    private readonly HttpClient _httpClient;
    private readonly PlcTelemetryOptions _options;

    public InfluxDbPlcTelemetryStore(HttpClient httpClient, PlcTelemetryOptions options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    public string ProviderName => "InfluxDb";

    public async Task WriteBatchAsync(
        IReadOnlyList<PlcTelemetryPoint> points,
        CancellationToken cancellationToken = default)
    {
        if (points.Count == 0) return;

        var payload = BuildLineProtocol(points);
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildWriteUri());

        if (_options.InfluxDb.ApiVersion == InfluxDbApiVersion.V2)
            request.Headers.Authorization = new AuthenticationHeaderValue("Token", _options.InfluxDb.Token);
        else if (!string.IsNullOrWhiteSpace(_options.InfluxDb.Token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.InfluxDb.Token);

        if (_options.InfluxDb.Gzip)
        {
            var uncompressed = Encoding.UTF8.GetBytes(payload);
            await using var output = new MemoryStream();
            await using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
                await gzip.WriteAsync(uncompressed, cancellationToken);

            request.Content = new ByteArrayContent(output.ToArray());
            request.Content.Headers.ContentEncoding.Add("gzip");
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain")
            {
                CharSet = "utf-8"
            };
        }
        else
        {
            request.Content = new StringContent(payload, Encoding.UTF8, "text/plain");
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"InfluxDB write failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}; {body}");
        }
    }

    private Uri BuildWriteUri()
    {
        var baseUrl = _options.InfluxDb.Url.TrimEnd('/');
        string path;
        if (_options.InfluxDb.ApiVersion == InfluxDbApiVersion.V2)
        {
            path = "/api/v2/write" +
                $"?org={Uri.EscapeDataString(_options.InfluxDb.Organization)}" +
                $"&bucket={Uri.EscapeDataString(_options.InfluxDb.Bucket)}" +
                "&precision=ns";
        }
        else
        {
            path = "/api/v3/write_lp" +
                $"?db={Uri.EscapeDataString(_options.InfluxDb.Database)}" +
                "&precision=nanosecond&accept_partial=false&no_sync=false";
        }

        return new Uri(baseUrl + path, UriKind.Absolute);
    }

    private string BuildLineProtocol(IReadOnlyList<PlcTelemetryPoint> points)
    {
        var builder = new StringBuilder(points.Count * 180);
        foreach (var point in points)
        {
            builder
                .Append(EscapeMeasurement(_options.Measurement))
                .Append(",site=").Append(EscapeTag(point.Site))
                .Append(",plc=").Append(EscapeTag(point.PlcName))
                .Append(",device=").Append(EscapeTag(point.DeviceId))
                .Append(",signal=").Append(EscapeTag(point.SignalName))
                .Append(' ')
                .Append("sequence=").Append(point.Sequence).Append('i')
                .Append(",db_block=").Append(point.DbBlock).Append('i')
                .Append(",quality=").Append(point.Quality).Append('i')
                .Append(",validator_passed=").Append(point.ValidatorPassed ? "true" : "false")
                .Append(",event_id=\"").Append(EscapeFieldString(point.EventId)).Append('"');

            switch (point.ValueKind)
            {
                case PlcTelemetryValueKind.Boolean when point.BoolValue.HasValue:
                    builder.Append(",value_bool=").Append(point.BoolValue.Value ? "true" : "false");
                    break;
                case PlcTelemetryValueKind.Numeric when point.NumericValue.HasValue:
                    builder.Append(",value_num=")
                        .Append(point.NumericValue.Value.ToString("R", CultureInfo.InvariantCulture));
                    break;
                default:
                    builder.Append(",value_text=\"")
                        .Append(EscapeFieldString(point.TextValue ?? point.NewValue ?? string.Empty))
                        .Append('"');
                    break;
            }

            AppendOptionalStringField(builder, "old_value", point.OldValue);
            AppendOptionalStringField(builder, "new_value", point.NewValue);
            AppendOptionalStringField(builder, "validator_reason", point.ValidatorReason);
            AppendOptionalStringField(builder, "domain_event_type", point.DomainEventType);
            AppendOptionalStringField(builder, "source", point.Source);

            builder.Append(' ').Append(point.TimestampUnixNanoseconds).Append('\n');
        }
        return builder.ToString();
    }

    private static void AppendOptionalStringField(StringBuilder builder, string name, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        builder.Append(',').Append(name).Append("=\"")
            .Append(EscapeFieldString(value)).Append('"');
    }

    private static string EscapeMeasurement(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace(" ", "\\ ", StringComparison.Ordinal);

    private static string EscapeTag(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("=", "\\=", StringComparison.Ordinal)
            .Replace(" ", "\\ ", StringComparison.Ordinal);

    private static string EscapeFieldString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
}
