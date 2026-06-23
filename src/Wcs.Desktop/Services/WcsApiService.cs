using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using Wcs.Core.Recovery;
using Wcs.Core.StateCenter.Models;
using Wcs.Core.TaskEngine.Context;
using Wcs.Desktop.Models;
using Wcs.Entity;

namespace Wcs.Desktop.Services;

/// <summary>
/// REST API 客户端 - 基于 HttpClient 的实现
/// </summary>
public class WcsApiService : IWcsApiService
{
    private readonly HttpClient _http;

    public WcsApiService(HttpClient http, IOptions<WcsDesktopOptions> options)
    {
        _http = http;
        _http.BaseAddress = new Uri(options.Value.ServerUrl);
    }

    public async Task<SystemOverview?> GetOverviewAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<SystemOverview>("/api/overview", ct);

    public async Task<List<DeviceState>> GetDevicesAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<List<DeviceState>>("/api/devices", ct) ?? [];

    public async Task<DeviceState?> GetDeviceAsync(string deviceId, CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<DeviceState>($"/api/devices/{deviceId}", ct);

    public async Task<List<TaskContext>> GetActiveTasksAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<List<TaskContext>>("/api/tasks", ct) ?? [];

    public async Task<TaskContext?> CreateTaskAsync(string deviceId, string routeId,
        int priority = 2, Dictionary<string, object>? parameters = null,
        CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/tasks",
            new { deviceId, routeId, priority, parameters }, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TaskContext>(ct);
    }

    public async Task<bool> CancelTaskAsync(string taskId, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"/api/tasks/{taskId}/cancel", null, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<bool>(ct);
    }

    public async Task<List<AlarmState>> GetAlarmsAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<List<AlarmState>>("/api/alarms", ct) ?? [];

    public async Task AckAlarmAsync(string alarmId, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"/api/alarms/{alarmId}/ack", null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task RecoverAlarmAsync(string alarmCode, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"/api/alarms/{alarmCode}/recover", null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<List<ObjectState>> GetObjectsAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<List<ObjectState>>("/api/objects", ct) ?? [];

    public async Task<RecoveryResult?> RecoverAsync(CancellationToken ct = default)
    {
        var response = await _http.PostAsync("/api/system/recover", null, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RecoveryResult>(ct);
    }

    public async Task<List<MenuItemDto>> GetMenusAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<MenuItemDto>>("/api/menus", ct) ?? [];

    // ---- 数据库查询 ----

    public async Task<List<AlarmState>> GetAlarmsFromDbAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<List<AlarmState>>("/api/alarms/db", ct) ?? [];

    public async Task<List<AlarmState>> GetAlarmHistoryAsync(
        DateTime? from = null, DateTime? to = null,
        string? level = null, int page = 1, int pageSize = 50,
        CancellationToken ct = default)
    {
        var query = BuildQuery("/api/alarms/history", ("from", from?.ToString("O")),
            ("to", to?.ToString("O")), ("level", level),
            ("page", page.ToString()), ("pageSize", pageSize.ToString()));
        return await _http.GetFromJsonAsync<List<AlarmState>>(query, ct) ?? [];
    }

    public async Task<List<TaskContext>> GetTasksFromDbAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<List<TaskContext>>("/api/tasks/db", ct) ?? [];

    public async Task<List<TaskContext>> GetTaskHistoryAsync(
        DateTime? from = null, DateTime? to = null,
        string? status = null, int page = 1, int pageSize = 50,
        CancellationToken ct = default)
    {
        var query = BuildQuery("/api/tasks/history", ("from", from?.ToString("O")),
            ("to", to?.ToString("O")), ("status", status),
            ("page", page.ToString()), ("pageSize", pageSize.ToString()));
        return await _http.GetFromJsonAsync<List<TaskContext>>(query, ct) ?? [];
    }

    /// <summary>构建查询字符串，跳过 null/空参数</summary>
    private static string BuildQuery(string basePath, params (string Name, string? Value)[] parameters)
    {
        var parts = parameters
            .Where(p => !string.IsNullOrEmpty(p.Value))
            .Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value!)}");
        var joined = string.Join("&", parts);
        return string.IsNullOrEmpty(joined) ? basePath : $"{basePath}?{joined}";
    }
}
