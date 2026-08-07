using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Wcs.Desktop.Services;

public sealed class DesktopIamOptions
{
    public bool Enabled { get; set; }
    public string Authority { get; set; } = "http://localhost:5202";
    public string ClientId { get; set; } = "industrial-desktop";
    public string RedirectUri { get; set; } = "industrial-platform://desktop/callback";
    public string Scope { get; set; } = "openid profile industrial-platform";
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
    Task<DesktopIamLoginResult> LoginAsync(string userName, string password, CancellationToken cancellationToken = default);
    Task LogoutAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Native desktop Authorization Code + PKCE client. The desktop first establishes the
/// IAM session in an isolated CookieContainer, then executes the public-client OIDC
/// authorization flow with redirects disabled. Because redirects are intercepted before
/// navigation, the registered custom-scheme callback does not require OS protocol handling
/// in this HTTP-driven desktop flow.
/// </summary>
public sealed class DesktopIamAuthService : IDesktopIamAuthService, IDisposable
{
    private readonly DesktopIamOptions _options;
    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _http;

    public DesktopIamAuthService(IOptions<DesktopIamOptions> options)
    {
        _options = options.Value;
        var handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
            AllowAutoRedirect = false
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(_options.Authority.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(20)
        };
    }

    public async Task<DesktopIamLoginResult> LoginAsync(
        string userName,
        string password,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var login = await _http.PostAsJsonAsync("account/login", new
            {
                userName,
                password,
                tenant = string.IsNullOrWhiteSpace(_options.Tenant) ? null : _options.Tenant
            }, cancellationToken);
            if (!login.IsSuccessStatusCode)
                return new(false, Error: "IAM 用户名或密码错误。");

            var state = RandomBase64Url(24);
            var verifier = RandomBase64Url(64);
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var authorize = new UriBuilder(new Uri(_http.BaseAddress!, "connect/authorize"));
            authorize.Query = BuildQuery(new Dictionary<string, string>
            {
                ["client_id"] = _options.ClientId,
                ["response_type"] = "code",
                ["redirect_uri"] = _options.RedirectUri,
                ["scope"] = _options.Scope,
                ["state"] = state,
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256"
            });

            using var authorization = await _http.GetAsync(authorize.Uri, cancellationToken);
            if ((int)authorization.StatusCode is < 300 or >= 400 || authorization.Headers.Location is null)
                return new(false, Error: $"IAM 授权失败：HTTP {(int)authorization.StatusCode}。");

            var callback = authorization.Headers.Location.IsAbsoluteUri
                ? authorization.Headers.Location
                : new Uri(_http.BaseAddress!, authorization.Headers.Location);
            if (!SameRedirect(callback, _options.RedirectUri))
                return new(false, Error: "IAM 返回了未注册的 Desktop RedirectUri。");

            var callbackQuery = ParseQuery(callback.Query);
            if (!callbackQuery.TryGetValue("state", out var returnedState)
                || !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(state),
                    Encoding.UTF8.GetBytes(returnedState)))
                return new(false, Error: "IAM 登录状态校验失败。");
            if (!callbackQuery.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
                return new(false, Error: callbackQuery.TryGetValue("error_description", out var description)
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
            if (!tokenResponse.IsSuccessStatusCode
                || !tokenJson.RootElement.TryGetProperty("access_token", out var accessTokenElement))
                return new(false, Error: ReadError(tokenJson.RootElement) ?? "IAM Token 获取失败。");

            var accessToken = accessTokenElement.GetString();
            if (string.IsNullOrWhiteSpace(accessToken))
                return new(false, Error: "IAM 返回了空 AccessToken。");

            using var profileRequest = new HttpRequestMessage(HttpMethod.Get, "api/iam/users/me");
            profileRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            using var profileResponse = await _http.SendAsync(profileRequest, cancellationToken);
            using var profileJson = JsonDocument.Parse(await profileResponse.Content.ReadAsStringAsync(cancellationToken));
            if (!profileResponse.IsSuccessStatusCode)
                return new(false, Error: ReadError(profileJson.RootElement) ?? "IAM 用户信息读取失败。");

            var root = profileJson.RootElement;
            var globalUserId = GetString(root, "id");
            var iamUserName = GetString(root, "userName") ?? userName;
            var displayName = GetString(root, "displayName") ?? iamUserName;
            return new(true, accessToken, iamUserName, displayName, globalUserId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, Error: "IAM 登录请求超时。");
        }
        catch (Exception ex)
        {
            return new(false, Error: $"IAM 登录异常：{ex.Message}");
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.PostAsync("account/logout", content: null, cancellationToken);
        }
        catch
        {
            // Desktop clears its bearer token independently. IAM availability must not
            // block local logout.
        }
    }

    public void Dispose() => _http.Dispose();

    private static string RandomBase64Url(int byteLength)
        => Base64Url(RandomNumberGenerator.GetBytes(byteLength));

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool SameRedirect(Uri actual, string expected)
    {
        if (!Uri.TryCreate(expected, UriKind.Absolute, out var expectedUri)) return false;
        return string.Equals(actual.Scheme, expectedUri.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(actual.Host, expectedUri.Host, StringComparison.OrdinalIgnoreCase)
            && actual.Port == expectedUri.Port
            && string.Equals(actual.AbsolutePath.TrimEnd('/'), expectedUri.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildQuery(IReadOnlyDictionary<string, string> values)
        => string.Join('&', values.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

    private static Dictionary<string, string> ParseQuery(string query)
        => query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split('=', 2))
            .Where(x => x.Length == 2)
            .ToDictionary(
                x => Uri.UnescapeDataString(x[0]),
                x => Uri.UnescapeDataString(x[1]),
                StringComparer.OrdinalIgnoreCase);

    private static string? ReadError(JsonElement root)
        => GetString(root, "error_description") ?? GetString(root, "error");

    private static string? GetString(JsonElement root, string property)
        => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}