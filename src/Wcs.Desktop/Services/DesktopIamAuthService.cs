using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Wcs.Desktop.Models;

namespace Wcs.Desktop.Services;

public sealed class DesktopIamOptions
{
    public bool Enabled { get; set; }
    public string Authority { get; set; } = "http://localhost:5202";
    public string ClientId { get; set; } = "industrial-desktop";
    public string RedirectUri { get; set; } = "http://127.0.0.1:5210/callback";
    public string Scope { get; set; } = "openid profile offline_access industrial-platform";
    public string? Tenant { get; set; }
}

public sealed record DesktopIamLoginResult(
    bool Success,
    string? AccessToken = null,
    string? UserName = null,
    string? DisplayName = null,
    string? GlobalUserId = null,
    string? Error = null);

public interface IDesktopIamAuthService
{
    Task<DesktopIamLoginResult> LoginAsync(CancellationToken cancellationToken = default);
    Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default);
    Task LogoutAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// System-browser Authorization Code + PKCE client for WCS Desktop. Tokens stay only in
/// this process. A refresh token renews the short-lived access token without restarting
/// WCS or asking the operator to sign in again.
/// </summary>
public sealed class DesktopIamAuthService : IDesktopIamAuthService, IDisposable
{
    private readonly DesktopIamOptions _options;
    private readonly IAuthState _authState;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset _expiresAt;

    public DesktopIamAuthService(IOptions<DesktopIamOptions> options, IAuthState authState)
    {
        _options = options.Value;
        _authState = authState;
        _http = new HttpClient
        {
            BaseAddress = new Uri(_options.Authority.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public async Task<DesktopIamLoginResult> LoginAsync(CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(_options.RedirectUri, UriKind.Absolute, out var redirect)
            || !IPAddress.TryParse(redirect.Host, out var address)
            || !IPAddress.IsLoopback(address)
            || redirect.Port <= 0)
            return new(false, Error: "Desktop RedirectUri 必须是带端口的 127.0.0.1 回调地址。");

        var state = RandomBase64Url(24);
        var verifier = RandomBase64Url(64);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var listener = new TcpListener(address, redirect.Port);

        try
        {
            listener.Start();
            var signIn = new UriBuilder(new Uri(_http.BaseAddress!, "desktop-auth/signin"))
            {
                Query = BuildQuery(new Dictionary<string, string>
                {
                    ["state"] = state,
                    ["codeChallenge"] = challenge
                })
            };
            Process.Start(new ProcessStartInfo(signIn.Uri.AbsoluteUri) { UseShellExecute = true });

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            var callback = await ReceiveCallbackAsync(listener, redirect, timeout.Token);
            var query = ParseQuery(callback.Query);
            if (!query.TryGetValue("state", out var returnedState)
                || !FixedTimeEquals(state, returnedState))
                return new(false, Error: "IAM 登录状态校验失败，请重新登录。");
            if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
                return new(false, Error: query.TryGetValue("error_description", out var description)
                    ? description
                    : "IAM 未返回授权码。");

            using var tokenResponse = await _http.PostAsync("connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = _options.ClientId,
                ["code"] = code,
                ["redirect_uri"] = _options.RedirectUri,
                ["code_verifier"] = verifier
            }), cancellationToken);
            using var tokenJson = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(cancellationToken));
            if (!tokenResponse.IsSuccessStatusCode || !TryApplyTokens(tokenJson.RootElement))
                return new(false, Error: ReadError(tokenJson.RootElement) ?? "IAM Token 获取失败。");

            using var profileRequest = new HttpRequestMessage(HttpMethod.Get, "api/iam/users/me");
            profileRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            using var profileResponse = await _http.SendAsync(profileRequest, cancellationToken);
            using var profileJson = JsonDocument.Parse(await profileResponse.Content.ReadAsStringAsync(cancellationToken));
            if (!profileResponse.IsSuccessStatusCode)
            {
                ClearTokens();
                return new(false, Error: ReadError(profileJson.RootElement) ?? "IAM 用户信息读取失败。");
            }

            var root = profileJson.RootElement;
            var userName = GetString(root, "userName") ?? GetString(root, "name") ?? "IAM User";
            var displayName = GetString(root, "displayName") ?? userName;
            _authState.Token = _accessToken;
            _authState.UserName = userName;
            return new(true, _accessToken, userName, displayName, GetString(root, "id"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, Error: "等待浏览器完成 IAM 登录超时。");
        }
        catch (SocketException ex)
        {
            return new(false, Error: $"无法监听 Desktop 登录回调端口 {redirect.Port}：{ex.Message}");
        }
        catch (Exception ex)
        {
            return new(false, Error: $"IAM 登录异常：{ex.Message}");
        }
        finally
        {
            listener.Stop();
        }
    }

    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            return _authState.Token;
        if (!string.IsNullOrWhiteSpace(_accessToken) && _expiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            return _accessToken;
        if (string.IsNullOrWhiteSpace(_refreshToken))
            return null;

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_accessToken) && _expiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
                return _accessToken;

            using var response = await _http.PostAsync("connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = _options.ClientId,
                ["refresh_token"] = _refreshToken!
            }), cancellationToken);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!response.IsSuccessStatusCode || !TryApplyTokens(json.RootElement))
            {
                ClearTokens();
                return null;
            }
            _authState.Token = _accessToken;
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        var token = _refreshToken;
        ClearTokens();
        if (string.IsNullOrWhiteSpace(token)) return;
        try
        {
            using var response = await _http.PostAsync("connect/revocation", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _options.ClientId,
                ["token"] = token,
                ["token_type_hint"] = "refresh_token"
            }), cancellationToken);
        }
        catch
        {
            // Local logout must still complete when IAM is temporarily unavailable.
        }
    }

    public void Dispose()
    {
        _tokenLock.Dispose();
        _http.Dispose();
    }

    private bool TryApplyTokens(JsonElement root)
    {
        var accessToken = GetString(root, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken)) return false;
        _accessToken = accessToken;
        _refreshToken = GetString(root, "refresh_token") ?? _refreshToken;
        var expiresIn = root.TryGetProperty("expires_in", out var value) && value.TryGetInt32(out var seconds)
            ? Math.Max(60, seconds)
            : 300;
        _expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
        return true;
    }

    private void ClearTokens()
    {
        _accessToken = null;
        _refreshToken = null;
        _expiresAt = DateTimeOffset.MinValue;
        _authState.Token = null;
        _authState.UserName = null;
    }

    private static async Task<Uri> ReceiveCallbackAsync(TcpListener listener, Uri redirect, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync(cancellationToken);
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken))) { }

        var target = requestLine?.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1);
        var callback = target is null ? null : new Uri(redirect, target);
        var valid = callback is not null
            && string.Equals(callback.AbsolutePath.TrimEnd('/'), redirect.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        var html = valid
            ? "<!doctype html><meta charset=\"utf-8\"><title>WCS 登录完成</title><h2>登录完成</h2><p>可以关闭此页面并返回 WCS Desktop。</p>"
            : "<!doctype html><meta charset=\"utf-8\"><title>WCS 登录失败</title><h2>登录回调无效</h2>";
        var body = Encoding.UTF8.GetBytes(html);
        var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {(valid ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        return valid ? callback! : throw new InvalidOperationException("Desktop IAM 回调路径无效。");
    }

    private static string RandomBase64Url(int byteLength) => Base64Url(RandomNumberGenerator.GetBytes(byteLength));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static bool FixedTimeEquals(string left, string right) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
    private static string BuildQuery(IReadOnlyDictionary<string, string> values) => string.Join('&', values.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    private static Dictionary<string, string> ParseQuery(string query) => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=', 2)).Where(x => x.Length == 2).ToDictionary(x => Uri.UnescapeDataString(x[0]), x => Uri.UnescapeDataString(x[1]), StringComparer.OrdinalIgnoreCase);
    private static string? ReadError(JsonElement root) => GetString(root, "error_description") ?? GetString(root, "error");
    private static string? GetString(JsonElement root, string property) => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
