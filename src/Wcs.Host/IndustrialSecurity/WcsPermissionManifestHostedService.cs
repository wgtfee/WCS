using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Industrial.Security.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace Wcs.Host.IndustrialSecurity;

/// <summary>
/// Generates the IAM permission manifest from the WcsPermissionResource table
/// (the real permission catalog) instead of a hand-written static file. The shared
/// Security SDK watches the generated file and synchronizes it to IAM.
/// </summary>
public sealed class WcsPermissionManifestHostedService(
    ISqlSugarClient db,
    IConfiguration configuration,
    ILogger<WcsPermissionManifestHostedService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "permission-manifest.json");
        var refreshSeconds = Math.Max(15, configuration.GetValue("Security:ResourceSync:CatalogRefreshSeconds", 60));
        var delay = TimeSpan.FromSeconds(refreshSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var manifest = await BuildAsync(stoppingToken);
                await WriteIfChangedAsync(manifestPath, manifest, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Failed to generate WCS permission manifest; the previous manifest is preserved.");
            }

            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task<PermissionManifestRequest> BuildAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var rows = await db.Ado.SqlQueryAsync<WcsPermissionResourceRow>(
            "SELECT Code, Name, Type, ParentCode, Route, ApiPath, HttpMethod, Sort, Enabled FROM WcsPermissionResource ORDER BY Sort, Code");
        var resources = rows.Select(row => new PermissionResourceDto(
            row.Code,
            row.Name,
            row.Type,
            ParentCode: row.ParentCode,
            Route: row.Route,
            ApiPath: row.ApiPath,
            HttpMethod: row.HttpMethod,
            Sort: row.Sort,
            Enabled: row.Enabled,
            MetadataJson: JsonSerializer.Serialize(new { source = "WcsPermissionResource", provider = "SqlSugar" }, JsonOptions)))
            .ToList();

        var ordered = resources.OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase).ToArray();
        var sourceBytes = JsonSerializer.SerializeToUtf8Bytes(
            ordered.Select(x => new { x.Code, x.Name, x.Type, x.ParentCode, x.Route, x.ApiPath, x.HttpMethod, x.Enabled, x.MetadataJson }),
            JsonOptions);
        var sourceHash = Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant();
        var version = $"sys-menu-{sourceHash[..12]}";
        var manifestHash = PermissionManifestHasher.Compute(IndustrialSystemCodes.Wcs, version, ordered);

        return new PermissionManifestRequest(
            new PermissionManifestSystem(IndustrialSystemCodes.Wcs, "WCS", "Generated from WcsPermissionResource"),
            version,
            manifestHash,
            ordered);
    }

    private sealed class WcsPermissionResourceRow
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string? ParentCode { get; set; }
        public string? Route { get; set; }
        public string? ApiPath { get; set; }
        public string? HttpMethod { get; set; }
        public int Sort { get; set; }
        public bool Enabled { get; set; }
    }

    private static async Task WriteIfChangedAsync(string path, PermissionManifestRequest manifest, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        var bytes = Encoding.UTF8.GetBytes(json);
        var existing = File.Exists(path) ? await File.ReadAllBytesAsync(path, ct) : null;
        if (existing is not null && existing.AsSpan().SequenceEqual(bytes))
            return;
        var tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes, ct);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (Exception) when (attempt < 4 && !ct.IsCancellationRequested)
            {
                await Task.Delay(500, ct);
            }
        }
    }
}
